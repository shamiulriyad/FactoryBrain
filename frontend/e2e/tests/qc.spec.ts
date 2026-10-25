import { deflateSync, crc32 } from "node:zlib";
import { expect, test, type Page } from "@playwright/test";
import { apiAs, env, login, prepare } from "./helpers";

// Sector 1 — quality inspection, end to end: upload in the browser, analysis
// by the API's worker through QC_VISION_PROVIDER=http, review, history and
// metrics. The vision provider is the harness's deterministic TEST detector
// (model name E2E_FAKE_QC_MODEL); these tests verify the workflow, not a
// real model's accuracy.
test.describe.configure({ mode: "serial" });

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

const fakeModel = process.env.E2E_FAKE_QC_MODEL ?? "";

/** A real, decodable RGB PNG. */
function png(width: number, height: number): Buffer {
  const row = Buffer.alloc(1 + width * 3);
  for (let x = 0; x < width; x++) row.set([180 + (x % 40), 160, 140], 1 + x * 3);
  const raw = Buffer.concat(Array.from({ length: height }, () => row));
  const chunk = (type: string, data: Buffer) => {
    const len = Buffer.alloc(4);
    len.writeUInt32BE(data.length);
    const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
    const crc = Buffer.alloc(4);
    crc.writeUInt32BE(crc32(body) >>> 0);
    return Buffer.concat([len, body, crc]);
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8;
  ihdr[9] = 2;
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk("IHDR", ihdr),
    chunk("IDAT", deflateSync(raw)),
    chunk("IEND", Buffer.alloc(0))
  ]);
}

let inspectionUrl = "";
let reference = "";

async function finding(page: Page, label: string) {
  return page.getByTestId("qc-predictions").getByTestId("qc-finding").filter({ hasText: label });
}

test("a Viewer can browse but not submit or review", async ({ page }) => {
  await login(page, "viewer", "/app/vision");
  await expect(page.getByTestId("qc-submit-forbidden")).toBeVisible();
  await expect(page.getByTestId("qc-submit")).toBeDisabled();
  await page.goto("/app/qc");
  await expect(page.getByRole("heading", { name: "Quality inspections" })).toBeVisible();
  await expect(page.getByTestId("qc-new-inspection")).toHaveCount(0);

  // The API enforces it too, whatever the UI shows.
  const api = await apiAs("viewer");
  const res = await api.post("/api/qc/inspections", {
    multipart: { productType: "Polo", stage: "final", image: { name: "x.png", mimeType: "image/png", buffer: png(8, 8) } }
  });
  expect(res.status()).toBe(403);
  await api.dispose();
});

test("the upload form refuses files that are not images before sending them", async ({ page }) => {
  await login(page, "operator", "/app/vision");
  await expect(page.getByTestId("qc-provider-configured")).toContainText("http");
  await page.getByTestId("qc-file").setInputFiles({ name: "invoice.png", mimeType: "image/png", buffer: Buffer.from("<html><script>alert(1)</script></html>") });
  await expect(page.getByText("contents don't match")).toBeVisible();
  await page.getByTestId("qc-file").setInputFiles({ name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("hello") });
  await expect(page.getByText("Only JPEG, PNG and WebP images are accepted.")).toBeVisible();
  await page.getByTestId("qc-submit").click();
  await expect(page.getByText("Enter the product type.")).toBeVisible();
});

test("an Operator submits a garment photo, sees the model's located predictions and records a corrected review", async ({ page }) => {
  await login(page, "operator", "/app/vision");
  await page.getByTestId("qc-file").setInputFiles({ name: "shoulder-seam.png", mimeType: "image/png", buffer: png(96, 64) });
  await expect(page.getByTestId("qc-file-info")).toContainText("shoulder-seam.png");
  await page.getByTestId("qc-product").fill("Polo shirt");
  await page.getByTestId("qc-line").fill("line-2");
  await page.getByTestId("qc-order").fill("PO-E2E-77");
  await page.getByTestId("qc-submit").click();

  await page.waitForURL(/\/app\/qc\/[0-9a-f-]{36}$/);
  inspectionUrl = new URL(page.url()).pathname;
  const detail = page.getByTestId("qc-detail");
  // The API's background worker analyses it; the page polls until it is done.
  await expect(detail).toHaveAttribute("data-status", "completed", { timeout: 45_000 });
  reference = (await page.getByRole("heading", { level: 1 }).innerText()).trim();
  expect(reference).toMatch(/^QC-\d{8}-[0-9A-F]{8}$/);

  await expect(page.getByTestId("qc-model")).toContainText(fakeModel);
  await expect(page.getByTestId("qc-image").locator("img")).toBeVisible();
  // Below-threshold "hole" (5%) is not stored; two of the three predictions carry a box.
  await expect(page.getByTestId("qc-predictions").getByTestId("qc-finding")).toHaveCount(3);
  await expect(page.getByTestId("qc-overlay-box")).toHaveCount(2);
  await expect(await finding(page, "Skipped stitch")).toContainText("Confidence 91%");
  await expect(await finding(page, "chalk_mark")).toContainText("not in the taxonomy");

  // Saving with predictions still undecided is refused with a clear reason.
  await page.getByTestId("qc-submit-review").click();
  await expect(page.getByTestId("qc-review-errors")).toContainText("3 left");

  await (await finding(page, "Skipped stitch")).getByTestId("qc-decide-accepted").click();
  await (await finding(page, "Stain")).getByTestId("qc-decide-rejected").click();
  const chalk = await finding(page, "chalk_mark");
  await chalk.getByTestId("qc-decide-corrected").click();
  await chalk.getByTestId("qc-correct-category").selectOption({ label: "Untrimmed thread" });
  await page.getByTestId("qc-add-defect").click();
  await page.getByTestId("qc-addition").getByTestId("qc-addition-category").selectOption({ label: "Open seam" });
  await page.getByTestId("qc-outcome-rework").click();
  await page.getByTestId("qc-review-notes").fill("Re-stitch the shoulder seam");
  await page.getByTestId("qc-submit-review").click();

  await expect(page.getByTestId("qc-status")).toHaveText("Reviewed");
  await expect(page.getByTestId("qc-outcome")).toHaveText("Rework");
  await expect(page.getByTestId("qc-review")).toContainText("3 verified defect(s)");

  // Persisted: a fresh load shows the same review, and the API agrees.
  await page.reload();
  await expect(page.getByTestId("qc-detail")).toHaveAttribute("data-status", "reviewed");
  const api = await apiAs("operator");
  const body = await (await api.get(`/api/qc/inspections/${inspectionUrl.split("/").pop()}`)).json();
  expect(body.review.outcome).toBe("rework");
  expect(body.findings.filter((f: { verified: boolean }) => f.verified)).toHaveLength(3);
  expect(body.findings.find((f: { rawLabel?: string }) => f.rawLabel === "chalk_mark").effectiveCategoryCode).toBe("loose_thread");
  await api.dispose();
});

test("history and quality metrics show the persisted review", async ({ page }) => {
  await login(page, "operator", "/app/qc");
  await page.getByTestId("qc-search").fill(reference);
  const rows = page.getByTestId("qc-inspection-row");
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toHaveAttribute("data-status", "reviewed");
  await expect(rows.first()).toContainText("Rework");

  await page.getByTestId("qc-tab-summary").click();
  await expect(page.getByTestId("qc-stat-inspections")).not.toContainText("—");
  await expect(page.getByTestId("qc-stat-defective")).toContainText("%");
  await expect(page.getByTestId("qc-by-category")).toContainText("Open seam");
  await expect(page.getByTestId("qc-by-line")).toContainText("line-2");
});

test("a Viewer opens the reviewed inspection without any review controls", async ({ page }) => {
  await login(page, "viewer", "/app/qc");
  await page.goto(inspectionUrl);
  await expect(page.getByTestId("qc-detail")).toHaveAttribute("data-status", "reviewed");
  await expect(page.getByTestId("qc-image").locator("img")).toBeVisible();
  await expect(page.getByTestId("qc-review")).toBeVisible();
  await expect(page.getByTestId("qc-start-review")).toHaveCount(0);
  await expect(page.getByTestId("qc-review-form")).toHaveCount(0);
  await expect(page.getByTestId("qc-audit")).toHaveCount(0); // the audit trail is Admin-only
});

test("an Admin sees the inspection's audit trail", async ({ page }) => {
  await login(page, "admin", "/app/qc");
  await page.goto(inspectionUrl);
  const trail = page.getByTestId("qc-audit");
  await expect(trail).toContainText("qc_inspection.created");
  await expect(trail).toContainText("qc_inspection.analyzed");
  await expect(trail).toContainText("qc_inspection.reviewed");
  expect(env.baseURL).toBeTruthy();
});
