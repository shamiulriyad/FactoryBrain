import { expect, test, type Browser, type Page } from "@playwright/test";
import { apiAs, login, prepare } from "./helpers";

// Sector 2 — production planning & line efficiency, end to end in the
// browser against the real API and a fresh database: an Admin configures the
// plant, an Operator creates an order and a plan, the plan goes live only
// after an approval in the queue, output is recorded and imported, the
// overview and line pages show the API's figures, a correction replaces a
// record, a live plan's change waits for approval, and a line-balance
// proposal becomes the layout only after an Admin approves it. The harness
// runs the API with PRODUCTION_TIME_ZONE unset, so plant time is UTC.
test.describe.configure({ mode: "serial" });

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

const day = (offset: number) => new Date(Date.now() + offset * 86_400_000).toISOString().slice(0, 10);
const yesterday = day(-1);
const lineId = "line-e2e";
const orderNumber = "E2E-PO-1";
let styleId = "";
let orderId = "";
let planId = "";

async function as(browser: Browser, role: "admin" | "operator" | "viewer", next: string) {
  const context = await browser.newContext();
  const page = await context.newPage();
  await prepare(page);
  await login(page, role, next);
  return { context, page };
}

/** Sign in on the production page, then open one of its tabs. */
async function loginTo(page: Page, role: "admin" | "operator" | "viewer", tab: string) {
  await login(page, role, "/app/production");
  await page.goto(`/app/production?tab=${tab}`);
}

// Each user has one refresh-token slot: an API login as the user whose browser
// session is open would sign that session out. Reads therefore go through the
// Viewer, and a user's own approvals through their browser session.
async function planDetail() {
  const api = await apiAs("viewer");
  const res = await api.get(`/api/production/plans/${planId}`);
  expect(res.status()).toBe(200);
  const body = await res.json();
  await api.dispose();
  return body;
}

async function adminApproves(approvalId: string) {
  const api = await apiAs("admin");
  const res = await api.post(`/api/approvals/${approvalId}/approve`, { data: {} });
  const status = res.status();
  await api.dispose();
  return status;
}

/** Approve from the approvals page, in this browser session. */
async function approveInBrowser(page: Page, approvalId: string) {
  await page.goto("/app/approvals");
  const card = page.locator(`[data-approval-card="${approvalId}"]`);
  await card.getByTestId("approval-approve").click();
  await expect(card).toHaveCount(0);
}

test("a Viewer reads production pages but has no write controls, and the API refuses writes", async ({ page }) => {
  await login(page, "viewer", "/app/production");
  await expect(page.getByRole("heading", { name: "Production planning" })).toBeVisible();
  await expect(page.getByTestId("production-setup-gaps")).toBeVisible();
  await expect(page.getByTestId("output-record")).toHaveCount(0);
  await page.getByTestId("prod-tab-setup").click();
  await expect(page.getByText("Only an Admin can change the plant configuration.")).toBeVisible();
  await expect(page.getByTestId("line-new")).toHaveCount(0);
  await expect(page.getByTestId("production-sources")).toContainText("integration: not connected");

  const api = await apiAs("viewer");
  expect((await api.post("/api/production/output", { data: {} })).status()).toBe(403);
  expect((await api.post("/api/production/lines", { data: { id: "x", name: "x", workstations: 1 } })).status()).toBe(403);
  await api.dispose();
});

test("an Admin sets up a line, a shift and a style with its operation bulletin", async ({ page }) => {
  await loginTo(page, "admin", "setup");
  await page.getByTestId("line-new").click();
  await page.getByTestId("line-id").fill(lineId);
  await page.getByTestId("line-name").fill("E2E Line");
  await page.getByTestId("line-workstations").fill("4");
  await page.getByTestId("line-save").click();
  await expect(page.getByTestId("lines-table")).toContainText("E2E Line");

  await page.getByTestId("shift-new").click();
  await page.getByTestId("shift-code").fill("e2a");
  await page.getByTestId("shift-name").fill("E2E day");
  await page.getByTestId("shift-start").fill("06:00");
  await page.getByTestId("shift-end").fill("14:00");
  await page.getByTestId("shift-break-start").fill("");
  await page.getByTestId("shift-break").fill("0");
  await page.getByTestId("shift-save").click();
  await expect(page.getByTestId("shifts-table")).toContainText("E2E day");

  // An overlapping shift is refused with the API's reason.
  await page.getByTestId("shift-new").click();
  await page.getByTestId("shift-code").fill("e2x");
  await page.getByTestId("shift-name").fill("E2E overlap");
  await page.getByTestId("shift-start").fill("12:00");
  await page.getByTestId("shift-end").fill("15:00");
  await page.getByTestId("shift-break-start").fill("");
  await page.getByTestId("shift-break").fill("0");
  await page.getByTestId("shift-save").click();
  await expect(page.getByTestId("shift-form").getByText(/overlaps e2a/).first()).toBeVisible();
  await page.getByTestId("shift-form").getByRole("button", { name: "Cancel" }).click();

  await page.getByTestId("style-new").click();
  await page.getByTestId("style-code").fill("E2E-ST");
  await page.getByTestId("style-name").fill("E2E Polo");
  await page.getByTestId("style-smv").fill("2.6");
  const ops: Array<[string, string, string]> = [["Join shoulder", "0.5", ""], ["Attach collar", "0.8", "1"], ["Side seam", "0.6", "1"], ["Hem", "0.7", "2, 3"]];
  for (const [i, [name, smv, preds]] of ops.entries()) {
    await page.getByTestId("style-add-op").click();
    await page.getByTestId("op-name").nth(i).fill(name);
    await page.getByTestId("op-smv").nth(i).fill(smv);
    await page.getByTestId("op-preds").nth(i).fill(preds);
  }
  await page.getByTestId("style-save").click();
  await expect(page.getByTestId("styles-table")).toContainText("E2E-ST");

  const api = await apiAs("admin");
  const styles = await (await api.get("/api/production/styles")).json();
  const style = styles.find((s: { code: string }) => s.code === "E2E-ST");
  expect(style.operations).toHaveLength(4);
  expect(style.smvBasis).toBe("approved");
  styleId = style.id;
  // A second style without a bulletin, for the missing-inputs case.
  expect((await api.post("/api/production/styles", { data: { code: "E2E-EMPTY", name: "No bulletin", approvedSmv: 5 } })).status()).toBe(201);
  await api.dispose();
});

test("an Operator creates an order and a plan; it goes live only after approval and activation", async ({ page, browser }) => {
  await loginTo(page, "operator", "orders");
  await page.getByTestId("order-new").click();
  await page.getByTestId("order-number").fill(orderNumber);
  await page.getByTestId("order-style").selectOption(styleId);
  await page.getByTestId("order-quantity").fill("2000");
  await page.getByTestId("order-due").fill(day(3));
  await page.getByTestId("order-save").click();
  const row = page.getByTestId("order-row").filter({ hasText: orderNumber });
  await expect(row).toContainText("Not enough data");
  await row.getByRole("link", { name: orderNumber }).click();
  await page.waitForURL(/\/app\/production\/orders\//);
  orderId = page.url().split("/").pop()!;
  await expect(page.getByTestId("order-risk")).toContainText("An active production plan for the order");

  await page.getByTestId("order-plan").click();
  await page.getByTestId("plan-line").selectOption(lineId);
  await page.getByTestId("plan-start").fill(yesterday);
  await page.getByTestId("plan-end").fill(day(3));
  await page.getByTestId("plan-quantity").fill("2000");
  await page.getByTestId("plan-operators").fill("20");
  await page.getByTestId("plan-efficiency").fill("60");
  await expect(page.getByTestId("plan-shift-e2a")).toBeChecked();
  await page.getByTestId("plan-save").click();
  const planRow = page.getByTestId("plan-row").first();
  await expect(planRow).toContainText("Draft");
  await planRow.getByRole("link").first().click();
  await page.waitForURL(/\/app\/production\/plans\//);
  planId = page.url().split("/").pop()!;
  // 20 operators × 60 × 60 % ÷ 2.6 SMV = 276.9 pieces/hour, calculated by the API.
  await expect(page.getByTestId("plan-targets")).toContainText("276.9 pcs/h");

  await page.getByTestId("plan-submit").click();
  await page.getByTestId("plan-confirm-confirm").click();
  await expect(page.getByTestId("plan-status")).toHaveText("Waiting for approval");
  await expect(page.getByTestId("plan-activate")).toHaveCount(0);

  // An Admin approves it in the approval queue.
  const approvalId = (await planDetail()).activationApproval.id;
  const admin = await as(browser, "admin", "/app/approvals");
  const card = admin.page.locator(`[data-approval-card="${approvalId}"]`);
  await expect(card).toContainText("Activate plan");
  await card.getByTestId("approval-approve").click();
  await expect(card).toHaveCount(0);
  await admin.context.close();

  await page.reload();
  await page.getByTestId("plan-activate").click();
  await expect(page.getByTestId("plan-status")).toHaveText("Active");
});

test("output is recorded and imported, duplicates are refused, and the overview shows the API's figures", async ({ page }) => {
  await login(page, "operator", "/app/production");
  const record = async (start: string, end: string, qty: string) => {
    await page.getByTestId("output-record").click();
    await page.getByTestId("output-line").selectOption(lineId);
    await page.getByTestId("output-order").selectOption(orderId);
    await page.getByTestId("output-date").fill(yesterday);
    await page.getByTestId("output-start").fill(start);
    await page.getByTestId("output-end").fill(end);
    await page.getByTestId("output-quantity").fill(qty);
    await page.getByTestId("output-operators").fill("20");
    await page.getByTestId("output-save").click();
  };
  await record("08:00", "09:00", "50");
  await expect(page.getByTestId("output-form")).toHaveCount(0);
  await record("08:30", "09:30", "10");
  await expect(page.getByTestId("form-error")).toContainText("already recorded");
  await page.getByTestId("output-form").getByRole("button", { name: "Cancel" }).click();
  await record("09:00", "10:00", "55");
  await expect(page.getByTestId("output-form")).toHaveCount(0);

  const csv = [
    "source_ref,line_id,order_number,stage,interval_start,interval_end,quantity,operators_present",
    `e2e-1,${lineId},${orderNumber},sewing_output,${yesterday} 10:00,${yesterday} 11:00,60,20`,
    `e2e-2,${lineId},${orderNumber},sewing_output,${yesterday} 11:00,${yesterday} 12:00,65,20`,
  ].join("\n");
  const importFile = async () => {
    await page.getByTestId("output-import").click();
    await page.getByTestId("import-file").setInputFiles({ name: "shift.csv", mimeType: "text/csv", buffer: Buffer.from(csv) });
    await expect(page.getByTestId("import-preview")).toContainText("2 data rows");
    await page.getByTestId("import-save").click();
  };
  await importFile();
  await expect(page.getByTestId("import-report")).toContainText("2 created · 0 already imported");
  await page.getByTestId("import-form").getByRole("button", { name: "Done" }).click();
  await importFile();
  await expect(page.getByTestId("import-report")).toContainText("0 created · 2 already imported");
  await page.getByTestId("import-form").getByRole("button", { name: "Done" }).click();

  await page.getByTestId("overview-line").selectOption(lineId);
  // 230 good pieces; planned 06:00–12:00 at 276.9/h = 1661; efficiency 230 × 2.6 ÷ (20 × 240) = 12.5 %.
  await expect(page.getByTestId("stat-actual")).toContainText("230");
  await expect(page.getByTestId("stat-actual")).toContainText("planned 1,661");
  await expect(page.getByTestId("stat-achievement")).toContainText("13.8 %");
  await expect(page.getByTestId("stat-efficiency")).toContainText("12.5 %");
  await expect(page.getByTestId("overview-daily-chart")).toBeVisible();
  await expect(page.getByTestId("overview-risks")).toContainText(orderNumber);
  await expect(page.getByTestId("production-definitions")).toBeVisible();
});

test("the line page drills into the records and a correction replaces one without double counting", async ({ page }) => {
  await login(page, "operator", `/app/production/lines/${lineId}`);
  await page.getByTestId("line-date").fill(yesterday);
  await expect(page.getByTestId("line-timeline")).toBeVisible();
  await expect(page.getByTestId("line-actual")).toContainText("230");
  const rows = page.getByTestId("history-row");
  await expect(rows).toHaveCount(4);
  await rows.first().getByTestId("history-correct").click();
  await page.getByTestId("correct-quantity").fill("67");
  await page.getByTestId("correct-reason").fill("Recount at end of hour");
  await page.getByTestId("correct-save").click();
  await expect(page.getByTestId("line-actual")).toContainText("232");
  await expect(rows).toHaveCount(4);
  await page.getByTestId("history-superseded").check();
  await expect(rows).toHaveCount(5);
});

test("a change to the live plan waits for approval and is then applied as a revision", async ({ page }) => {
  await login(page, "operator", `/app/production/plans/${planId}`);
  await page.getByTestId("plan-edit").click();
  await page.getByTestId("plan-operators").fill("22");
  await page.getByTestId("plan-reason").fill("Two more operators from line 2");
  await page.getByTestId("plan-save").click();
  await expect(page.getByTestId("plan-message")).toContainText("waiting for approval");
  await expect(page.getByTestId("plan-change").filter({ hasText: "revision · pending" })).toContainText("operators: 20 → 22");

  // Medium risk, so the same Operator may approve it in the queue, but the submitter's own approval…
  const pending = (await planDetail()).changes.find((c: { status: string }) => c.status === "pending");
  await approveInBrowser(page, pending.approval.id);
  await page.goto(`/app/production/plans/${planId}`);
  await page.getByTestId("plan-resolve-change").click();
  // …can't make it live: separation of duties.
  await expect(page.getByText(/same person who submitted/)).toBeVisible();
  expect((await planDetail()).values.operators).toBe(20);

  // Withdraw it, ask again, and let an Admin approve this time.
  await page.getByTestId("plan-withdraw-change").click();
  await expect(page.getByTestId("plan-message")).toContainText("withdrawn");
  await page.getByTestId("plan-edit").click();
  await page.getByTestId("plan-operators").fill("22");
  await page.getByTestId("plan-reason").fill("Two more operators from line 2");
  await page.getByTestId("plan-save").click();
  const again = (await planDetail()).changes.find((c: { status: string }) => c.status === "pending");
  expect(await adminApproves(again.approval.id)).toBe(200);
  await page.reload();
  await page.getByTestId("plan-resolve-change").click();
  await expect(page.getByTestId("plan-message")).toContainText("Revision 1 is live.");
  expect((await planDetail()).values.operators).toBe(22);
});

test("a line-balance proposal needs an Admin's approval before it becomes the recorded layout", async ({ page }) => {
  await loginTo(page, "operator", "balancing");
  await page.getByTestId("balance-line").selectOption(lineId);
  await page.getByTestId("balance-style").selectOption({ label: "E2E-EMPTY (0 ops)" });
  await page.getByTestId("balance-run").click();
  await expect(page.getByTestId("balance-missing")).toContainText("operation bulletin");

  await page.getByTestId("balance-style").selectOption(styleId);
  await page.getByTestId("balance-workstations").fill("4");
  await page.getByTestId("balance-run").click();
  await page.waitForURL(/\/app\/production\/balancing\//);
  const proposalId = page.url().split("/").pop()!;
  await expect(page.getByTestId("proposal-comparison")).toContainText("No current layout is recorded");
  await expect(page.getByTestId("proposed-bars")).toBeVisible();
  await page.getByTestId("proposal-submit").click();
  await expect(page.getByTestId("proposal-status")).toHaveText("Waiting for approval");

  const api = await apiAs("viewer");
  const approvalId = (await (await api.get(`/api/production/balance/proposals/${proposalId}`)).json()).approval.id;
  await api.dispose();
  // High risk: the Operator can't decide it (the API's 403 is covered by the backend tests)…
  await page.goto("/app/approvals");
  await expect(page.locator(`[data-approval-card="${approvalId}"]`).getByTestId("approval-blocked")).toHaveText("High-risk approvals need an Admin.");
  // …an Admin can.
  expect(await adminApproves(approvalId)).toBe(200);
  await page.goto(`/app/production/balancing/${proposalId}`);
  await page.getByTestId("proposal-apply").click();
  await expect(page.getByTestId("proposal-status")).toHaveText("Applied");

  await page.goto(`/app/production/lines/${lineId}`);
  await expect(page.getByTestId("line-layout-bars")).toBeVisible();
  await expect(page.getByText(/Applied from an approved balance proposal/)).toBeVisible();
});

test("the history tab filters and pages through persisted records", async ({ page }: { page: Page }) => {
  await loginTo(page, "viewer", "history");
  await page.getByTestId("history-line").selectOption(lineId);
  await expect(page.getByTestId("history-row")).toHaveCount(4);
  await page.getByTestId("history-stage").selectOption("cutting");
  await expect(page.getByText("No output records match")).toBeVisible();
  await expect(page.getByTestId("history-correct")).toHaveCount(0);
});
