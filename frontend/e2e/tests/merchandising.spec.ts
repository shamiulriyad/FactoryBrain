import { expect, test, type Page } from "@playwright/test";
import { apiAs, login, prepare } from "./helpers";

// Sector 3 — merchandising & document automation, end to end in the browser
// against the real API and a fresh database. The harness runs the API with
// MERCH_EXTRACTION_PROVIDER=rules: the deterministic label parser (not AI)
// reads labelled values from real uploaded files, so these tests exercise the
// real upload → parse → extract → review → confirm workflow without a model.
test.describe.configure({ mode: "serial" });

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

const suffix = Date.now().toString(36).toUpperCase();
const po = `E2E-PO-${suffix}`;
let orderId = "";

const poText = `PURCHASE ORDER
PO No: ${po}
Buyer: Nordic Wear AB
Style No: ST-1001
Description: Men's knit polo shirt
Total Qty (pcs): 1,200
Ex-Factory Date: 15-Jan-2027
Currency: USD
Unit Price: USD 4.25

Colour      S      M      L
Navy        200    200    200
White       200    200    200
`;

async function upload(page: Page, file: { name: string; mimeType: string; buffer: Buffer }, type?: string) {
  await page.getByTestId("merch-upload").click();
  const form = page.getByTestId("merch-upload-form");
  if (type) await form.getByTestId("merch-upload-type").selectOption(type);
  await form.getByTestId("merch-upload-file").setInputFiles(file);
  await form.getByTestId("merch-upload-save").click();
}

test("a Viewer reads merchandising pages but has no write controls, and the API refuses writes", async ({ page }) => {
  await login(page, "viewer", "/app/merchandising");
  await expect(page.getByRole("heading", { name: "Merchandising" })).toBeVisible();
  await expect(page.getByTestId("merch-extraction-status")).toContainText("rule-based label parser (not AI)");
  await expect(page.getByTestId("merch-register")).toHaveCount(0);
  await expect(page.getByTestId("merch-stat-active")).toBeVisible();

  const api = await apiAs("viewer");
  expect((await api.post("/api/merchandising/orders", { data: { buyer: "x", buyerOrderNumber: "y" } })).status()).toBe(403);
  const config = await (await api.get("/api/merchandising/config")).json();
  expect(config.extraction.mode).toBe("rules");
  expect(config.ocrConfigured).toBe(false);
  expect(config.emailIntegrationConfigured).toBe(false);
  await api.dispose();
});

test("an Operator registers an order, uploads the PO, reviews the values against their source and confirms them", async ({ page }) => {
  await login(page, "operator", "/app/merchandising");
  await page.getByTestId("merch-register").click();
  const form = page.getByTestId("merch-order-form");
  await form.getByTestId("merch-buyer").fill("Nordic Wear AB");
  await form.getByTestId("merch-po").fill(po);
  await form.getByTestId("merch-milestone-add").click();
  await form.getByTestId("merch-milestone-name").fill("Lab dip approval");
  await form.getByTestId("merch-milestone-due").fill("2027-01-05");
  await form.getByTestId("merch-order-save").click();

  await page.waitForURL(/\/app\/merchandising\/orders\/[0-9a-f-]{36}$/);
  orderId = page.url().split("/").pop()!;
  await expect(page.getByTestId("merch-order-title")).toContainText(po);
  await expect(page.locator('[data-testid="merch-required-doc"][data-state="missing"]')).toHaveCount(2);
  await expect(page.locator('[data-testid="merch-attention-item"][data-kind="missing_document"]').first()).toBeVisible();
  await expect(page.getByTestId("merch-milestone")).toContainText("Lab dip approval");

  // An Excel workbook is refused with an explanation; nothing is stored.
  await upload(page, { name: "sizes.xlsx", mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", buffer: Buffer.from("PK\u0003\u0004not really") });
  await expect(page.getByTestId("merch-upload-form").getByTestId("form-error")).toBeVisible();
  await page.getByTestId("merch-upload-form").getByRole("button", { name: "Cancel" }).click();
  await expect(page.getByTestId("merch-no-documents")).toBeVisible();

  await upload(page, { name: "purchase-order.txt", mimeType: "text/plain", buffer: Buffer.from(poText) }, "purchase_order");
  await page.getByTestId("merch-document-link").first().click();
  await page.waitForURL(/\/app\/merchandising\/versions\//);
  await expect(page.getByTestId("merch-version")).toHaveAttribute("data-status", "needs_review", { timeout: 30_000 });
  await expect(page.getByTestId("merch-extractor")).toContainText("rules / label-parser");

  // Values with their evidence; the source is highlighted in the document text.
  const qty = page.locator('[data-testid="merch-field"][data-key="quantity"]');
  await expect(qty.getByTestId("merch-field-value")).toContainText("1200");
  await expect(qty.getByTestId("merch-field-snippet")).toHaveText("“Total Qty (pcs): 1,200”");
  await qty.getByTestId("merch-show-source").click();
  await expect(page.getByTestId("merch-evidence-highlight")).toHaveText("Total Qty (pcs): 1,200");
  await expect(page.locator('[data-testid="merch-field"][data-key="size_color_breakdown"]')).toContainText("6 row(s), total 1,200");
  await expect(page.locator('[data-testid="merch-field"]').filter({ hasText: "confidence" })).toHaveCount(0); // the parser reports none

  // Confirming without deciding every value is refused.
  await page.getByTestId("merch-confirm").click();
  await expect(page.getByTestId("merch-confirm-errors")).toContainText("Decide every value");

  // Correct the delivery date, reject the price, accept the rest.
  const delivery = page.locator('[data-testid="merch-field"][data-key="delivery_date"]');
  await delivery.getByTestId("merch-decide-corrected").click();
  await delivery.getByTestId("merch-correct-value").fill("2027-01-20");
  await page.locator('[data-testid="merch-field"][data-key="unit_price"]').getByTestId("merch-decide-rejected").click();
  await page.getByTestId("merch-accept-valid").click();
  await page.getByTestId("merch-review-notes").fill("Checked against the signed PO");
  await page.getByTestId("merch-confirm").click();
  await expect(page.getByTestId("merch-version")).toHaveAttribute("data-status", "confirmed");
  await expect(page.getByTestId("merch-review")).toContainText("operator@e2e.test");
  await expect(delivery).toHaveAttribute("data-decision", "corrected");

  // The order now carries the confirmed values and says where they came from.
  await page.goto(`/app/merchandising/orders/${orderId}`);
  await expect(page.locator('[data-testid="merch-value"][data-key="quantity"]').getByTestId("merch-value-display")).toHaveText("1200");
  await expect(page.locator('[data-testid="merch-value"][data-key="delivery_date"]')).toContainText("2027-01-20");
  await expect(page.locator('[data-testid="merch-value"][data-key="quantity"]')).toContainText("Confirmed from a document");
  await expect(page.locator('[data-testid="merch-value"][data-key="unit_price"]').getByTestId("merch-value-display")).toHaveText("not confirmed");
  await expect(page.getByTestId("merch-summary-headline")).toContainText(`Nordic Wear AB PO ${po}: 1,200 pcs, style ST-1001, delivery 2027-01-20`);
  await expect(page.locator('[data-testid="merch-required-doc"][data-state="confirmed"]')).toHaveCount(1);
});

test("a conflicting tech pack is flagged with both sources and the Operator resolves it", async ({ page }) => {
  // Upload the tech pack as the Admin through the API (the Operator's browser session stays signed in below).
  const admin = await apiAs("admin");
  const res = await admin.post(`/api/merchandising/orders/${orderId}/documents`, {
    multipart: {
      type: "tech_pack",
      file: { name: "techpack.txt", mimeType: "text/plain", buffer: Buffer.from(`TECH PACK\nStyle No: ST 1001\nTotal Qty: 1,150\n`) },
    },
  });
  expect(res.status()).toBe(201);
  await admin.dispose();

  await login(page, "operator", `/app/merchandising/orders/${orderId}`);
  const conflict = page.locator('[data-testid="merch-conflict"][data-field="quantity"]');
  await expect(conflict).toBeVisible({ timeout: 30_000 });
  await expect(conflict.getByTestId("merch-conflict-value")).toHaveText(["1200", "1150"]); // purchase order, then tech pack
  await expect(conflict).toContainText("Total Qty: 1,150");
  await expect(page.locator('[data-testid="merch-conflict"][data-field="style"]')).toHaveCount(0); // "ST-1001" = "ST 1001"
  await expect(page.locator('[data-testid="merch-attention-item"][data-kind="conflict"][data-priority="high"]')).toBeVisible();

  await conflict.getByTestId("merch-conflict-resolve").click();
  const form = page.getByTestId("merch-resolve-form");
  await form.locator("label", { hasText: "1200" }).getByTestId("merch-resolve-option").check();
  await form.getByTestId("merch-resolve-note").fill("Buyer confirmed 1,200 pieces by email");
  await form.getByTestId("merch-resolve-save").click();
  await expect(page.getByTestId("merch-conflict")).toHaveCount(0);
  await page.getByText(/^History \(/).click();
  await expect(page.getByTestId("merch-conflict-history")).toContainText("resolved as 1200 by operator@e2e.test");
  await expect(page.locator('[data-testid="merch-value"][data-key="quantity"]')).toContainText("Chosen when resolving a conflict");
});

test("a scanned image is kept but reported as needing OCR, and the overview counts come from the API", async ({ page }) => {
  await login(page, "operator", `/app/merchandising/orders/${orderId}`);
  const png = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64");
  await upload(page, { name: "buyer-note.png", mimeType: "image/png", buffer: png }, "buyer_instruction");
  await expect(page.getByTestId("merch-upload-form")).toHaveCount(0);
  await page.locator('[data-testid="merch-document"][data-type="buyer_instruction"]').getByTestId("merch-document-link").click();
  await expect(page.getByTestId("merch-version")).toHaveAttribute("data-status", "failed", { timeout: 30_000 });
  await expect(page.getByTestId("merch-failure")).toContainText("ocr_required");
  await expect(page.getByTestId("merch-retry")).toHaveCount(0);
  await expect(page.getByTestId("merch-no-text")).toBeVisible();

  await page.goto("/app/merchandising");
  const api = await apiAs("viewer");
  const overview = await (await api.get("/api/merchandising/overview")).json();
  await api.dispose();
  await expect(page.getByTestId("merch-stat-active")).toContainText(String(overview.activeOrders));
  await expect(page.getByTestId("merch-stat-review")).toContainText(String(overview.documentsAwaitingReview));
  await expect(page.getByTestId("merch-stat-failed")).toContainText(String(overview.failedDocuments));
  expect(overview.failedDocuments).toBeGreaterThanOrEqual(1);
  expect(overview.documentsAwaitingReview).toBeGreaterThanOrEqual(1); // the tech pack
  await expect(page.getByTestId("merch-deadline").filter({ hasText: "Lab dip approval" })).toHaveCount(0); // outside the 14-day window

  await page.getByTestId("merch-tab-orders").click();
  await page.getByTestId("merch-search").fill(po);
  await expect(page.getByTestId("merch-order-row")).toHaveCount(1);
  await page.getByTestId("merch-filter-attention").selectOption("failed");
  await expect(page.getByTestId("merch-order-row")).toHaveCount(1);
  await page.getByTestId("merch-filter-attention").selectOption("conflicts");
  await expect(page.getByTestId("merch-orders-empty")).toBeVisible();
});

test("an Admin sees the order's audit trail of uploads, extraction, confirmation and resolution", async ({ page }) => {
  await login(page, "admin", `/app/merchandising/orders/${orderId}`);
  const audit = page.getByTestId("merch-audit");
  for (const action of ["merch_order.created", "merch_document.uploaded", "merch_document.processed", "merch_document.confirmed", "merch_conflict.detected", "merch_conflict.resolved"])
    await expect(audit).toContainText(action);
  await expect(audit).toContainText("system:merch-worker");
});
