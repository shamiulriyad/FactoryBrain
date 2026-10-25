import { randomBytes } from "node:crypto";
import { expect, test, type Page } from "@playwright/test";
import { apiAs, creds, env, login, prepare } from "./helpers";

// Pages wired to the API in the frontend integration pass: role-aware
// navigation, the audit log, the knowledge base, agents, floor alerts and
// session expiry. Runs after approvals.spec.ts (file order), so the audit log
// already holds the approval decisions made there.
test.describe.configure({ mode: "serial" });

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

const nav = (page: Page, id: string) => page.getByTestId(`nav-${id}`);

test("navigation follows the signed-in role, and Admin pages refuse other roles", async ({ page, browser }) => {
  await login(page, "viewer", "/app");
  await expect(nav(page, "knowledge")).toBeVisible();
  await expect(nav(page, "approvals")).toBeVisible();
  await expect(nav(page, "audit")).toHaveCount(0);

  // A deep link to an Admin page shows an explanation instead of the page.
  await page.goto("/app/audit");
  await expect(page.getByTestId("access-denied")).toContainText("needs the Admin role");
  await expect(page.getByTestId("audit-table")).toHaveCount(0);

  const adminContext = await browser.newContext();
  const admin = await adminContext.newPage();
  await prepare(admin);
  await login(admin, "admin", "/app");
  await expect(nav(admin, "audit")).toBeVisible();
  await adminContext.close();
});

test("the audit log shows the recorded decisions and filters by entity", async ({ page }) => {
  // Sign in on Overview: the audit table itself lists the admin's email.
  await login(page, "admin", "/app");
  await page.goto("/app/audit");
  const rows = page.getByTestId("audit-row");
  await expect(rows.first()).toBeVisible();

  await page.getByLabel("Entity", { exact: true }).selectOption("approval");
  await expect(page).toHaveURL(/entityType=approval/);
  await expect(rows.first()).toBeVisible();
  for (const action of await rows.evaluateAll((els) => els.map((e) => e.getAttribute("data-action")))) {
    expect(action).toMatch(/^approval\./);
  }

  await rows.first().click();
  await expect(page.getByTestId("audit-detail")).toContainText("approval");
});

test("approval details show the server audit trail to an Admin", async ({ page }) => {
  await login(page, "admin", "/app/approvals");
  await page.getByLabel("Status").selectOption({ label: "All" });
  const card = page.locator(`[data-approval-card="${env.approveId}"]`);
  await card.getByRole("button", { name: /Details/ }).click();
  const detail = page.getByTestId("approval-detail");
  await expect(detail.getByTestId("approval-audit")).toContainText("approval.approved");
  await expect(detail.getByTestId("approval-audit")).toContainText(creds("operator").email);
});

test("knowledge base: a Viewer browses, an Admin ingests a document the API confirms", async ({ page, browser }) => {
  await login(page, "viewer", "/app/knowledge");
  await expect(page.getByTestId("kb-readonly")).toBeVisible();
  await expect(page.getByTestId("kb-add-document")).toHaveCount(0);
  await expect(page.getByTestId("kb-row").first()).toBeVisible();

  const adminContext = await browser.newContext();
  const admin = await adminContext.newPage();
  await prepare(admin);
  await login(admin, "admin", "/app/knowledge");
  await admin.getByTestId("kb-add-document").click();
  const dialog = admin.getByTestId("kb-ingest-dialog");

  // Client-side validation before anything is sent.
  await dialog.getByTestId("kb-ingest-submit").click();
  await expect(dialog).toContainText("A title is required.");

  const title = `E2E spreader tension SOP ${randomBytes(3).toString("hex")}`;
  await dialog.getByTestId("kb-title").fill(title);
  await dialog.getByTestId("kb-source").selectOption("sop");
  await dialog.getByTestId("kb-content").fill(
    "Spreader tension check.\n\nBefore each lay, check the fabric tension on the spreader. Loose fabric causes size variation; tight fabric shrinks after cutting. Record the setting on the lay sheet."
  );
  const ingested = admin.waitForResponse((r) => r.url().endsWith("/api/rag/ingest") && r.request().method() === "POST");
  await dialog.getByTestId("kb-ingest-submit").click();
  const res = await ingested;
  expect(res.status()).toBe(201);
  const body = await res.json();
  await expect(dialog.getByTestId("kb-ingest-result")).toContainText(body.documentId);
  await expect(dialog.getByTestId("kb-ingest-result")).toContainText(`${body.chunks} chunk`);
  await dialog.getByRole("button", { name: "Done" }).click();

  await admin.getByTestId("kb-search").fill(title);
  await expect(admin.getByTestId("kb-row")).toHaveCount(1);
  await expect(admin.getByTestId("kb-row")).toContainText(title);

  // The API lists it too.
  const api = await apiAs("viewer");
  const docs = (await (await api.get("/api/rag/documents?source=sop")).json()) as Array<{ id: string; title: string }>;
  expect(docs.some((d) => d.id === body.documentId && d.title === title)).toBe(true);
  await api.dispose();

  // Reindex is review-first: either nothing to do, or a plan that needs an explicit acknowledgement.
  await admin.getByTestId("kb-review-reindex").click();
  const reindex = admin.getByTestId("kb-reindex-dialog");
  await expect(reindex.getByTestId("kb-reindex-nothing").or(reindex.getByTestId("reindex-plan"))).toBeVisible();
  if (await reindex.getByTestId("reindex-plan").isVisible()) {
    await expect(reindex.getByTestId("kb-reindex-confirm")).toBeDisabled();
  }
  await adminContext.close();
});

test("agents come from the server; an Operator runs one and a Viewer can't", async ({ page, browser }) => {
  await login(page, "viewer", "/app/agents");
  await expect(page.locator('[data-agent-id="line-throughput-agent"]')).toBeVisible();
  await expect(page.getByTestId("agent-run-blocked").first()).toBeVisible();
  await expect(page.getByTestId("agent-run-line-throughput-agent")).toHaveCount(0);

  const opContext = await browser.newContext();
  const op = await opContext.newPage();
  await prepare(op);
  await login(op, "operator", "/app/agents");
  const ran = op.waitForResponse((r) => r.url().endsWith("/api/agents/line-throughput-agent/run"));
  await op.getByTestId("agent-run-line-throughput-agent").click();
  expect((await ran).status()).toBe(200);
  const result = op.locator('[data-agent-id="line-throughput-agent"]').getByTestId("agent-run-result");
  await expect(result).toContainText("Rule-based demo run");
  await opContext.close();
});

test("floor alerts: an Operator acknowledges one; a Viewer only reads", async ({ page, browser }) => {
  await login(page, "viewer", "/app/activity");
  await page.getByTestId("alerts-tab-all").click();
  await expect(page.getByTestId("floor-alert").first()).toBeVisible();
  await expect(page.getByTestId("floor-alert-toggle")).toHaveCount(0);

  const opContext = await browser.newContext();
  const op = await opContext.newPage();
  await prepare(op);
  await login(op, "operator", "/app/activity");
  await op.getByTestId("alerts-tab-all").click();
  const first = op.getByTestId("floor-alert").first();
  const before = await first.getAttribute("data-read");
  const patched = op.waitForResponse((r) => r.url().includes("/api/floor-alerts/") && r.request().method() === "PATCH");
  await first.getByTestId("floor-alert-toggle").click();
  expect((await patched).status()).toBe(200);
  await expect(op.getByTestId("floor-alert").first()).not.toHaveAttribute("data-read", before ?? "");
  await opContext.close();
});

test("an expired session returns to sign-in and says why", async ({ page, context }) => {
  await login(page, "viewer", "/app");
  // The refresh cookie is gone and the API now refuses the access token.
  await context.clearCookies();
  await page.route("**/api/**", (route) => route.fulfill({ status: 401, contentType: "application/problem+json", body: "{}" }));
  await page.getByRole("button", { name: "Refresh overview" }).click();
  await page.waitForURL(/\/login\?.*reason=expired/);
  await expect(page.getByTestId("session-expired")).toContainText("Your session expired");
  expect(new URL(page.url()).searchParams.get("next")).toBe("/app");
});

test("phone layout: navigation drawer and no horizontal scrolling", async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
  const page = await context.newPage();
  await prepare(page);
  const { email, password } = creds("operator");
  await page.goto(`/login?next=${encodeURIComponent("/app/approvals")}`);
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL((u) => u.pathname === "/app/approvals");
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();

  for (const path of ["/app", "/app/approvals", "/app/knowledge", "/app/settings"]) {
    await page.goto(path);
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    expect(new URL(page.url()).pathname, "still signed in").toBe(path);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow, `horizontal overflow on ${path}`).toBeLessThanOrEqual(1);
  }

  await page.getByRole("button", { name: "Open navigation" }).click();
  const drawer = page.getByRole("dialog", { name: "Navigation" });
  await expect(drawer.getByText(email, { exact: true })).toBeVisible();
  await drawer.getByTestId("nav-ask").click();
  await page.waitForURL((u) => u.pathname === "/app/ask");
  await expect(drawer).toHaveCount(0);
  await context.close();
});
