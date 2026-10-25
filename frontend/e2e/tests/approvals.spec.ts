import { expect, test, type Page } from "@playwright/test";
import { apiAs, creds, env, login, prepare } from "./helpers";

// These run in file order: they decide the scenario approvals the harness
// created (two medium-risk agent-run approvals and one high-risk one).
test.describe.configure({ mode: "serial" });

const card = (page: Page, id: string) => page.locator(`[data-approval-card="${id}"]`);

async function openApprovals(page: Page, status: "Pending" | "Approved" | "Rejected" | "All" = "Pending") {
  if (!page.url().includes("/app/approvals")) await page.goto("/app/approvals");
  await page.getByLabel("Status").selectOption({ label: status });
}

async function auditFor(approvalId: string) {
  const admin = await apiAs("admin");
  const res = await admin.get(`/api/audit?entityType=approval&entityId=${encodeURIComponent(approvalId)}`);
  expect(res.status()).toBe(200);
  const entries = await res.json();
  await admin.dispose();
  return entries as Array<{ action: string; actorEmail: string; actorRole: string; fromState: string; toState: string; reason: string | null }>;
}

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

test("the queue is served by the API and a viewer cannot decide", async ({ page }) => {
  await login(page, "viewer", "/app/approvals");
  for (const id of [env.approveId, env.rejectId, env.highRiskId]) {
    await expect(card(page, id)).toBeVisible();
  }
  const c = card(page, env.approveId);
  await expect(c.getByTestId("approval-approve")).toBeDisabled();
  await expect(c.getByTestId("approval-reject")).toBeDisabled();
  await expect(c.getByTestId("approval-blocked")).toHaveText("Your role can't do this. Ask an administrator.");
  await expect(page.getByTestId("approvals-counts")).toContainText("pending on the server");
});

test("an operator approves a medium-risk item and the audit log records it", async ({ page }) => {
  await login(page, "operator", "/app/approvals");
  const c = card(page, env.approveId);
  await expect(c).toBeVisible();
  await c.getByTestId("approval-approve").click();

  // Gone from the pending view once the server has decided it…
  await expect(card(page, env.approveId)).toHaveCount(0);
  // …and shown under Approved with who decided it.
  await openApprovals(page, "Approved");
  await expect(card(page, env.approveId).getByTestId("approval-decision")).toContainText(`Approved by ${creds("operator").email}`);

  const audit = await auditFor(env.approveId);
  expect(audit).toHaveLength(1);
  expect(audit[0]).toMatchObject({
    action: "approval.approved",
    actorEmail: creds("operator").email,
    actorRole: "Operator",
    fromState: "pending_approval",
    toState: "approved",
  });
});

test("rejecting needs a reason, which is shown and audited", async ({ page }) => {
  await login(page, "operator", "/app/approvals");
  const c = card(page, env.rejectId);
  await c.getByTestId("approval-reject").click();
  const confirm = c.getByTestId("approval-confirm-reject");
  await expect(confirm).toBeDisabled();               // no reason yet
  await c.getByTestId("approval-reason").fill("   ");
  await expect(confirm).toBeDisabled();               // whitespace is not a reason
  const reason = "Duplicate of work order WO-1042 (e2e).";
  await c.getByTestId("approval-reason").fill(reason);
  await confirm.click();

  await expect(card(page, env.rejectId)).toHaveCount(0);
  await openApprovals(page, "Rejected");
  await expect(card(page, env.rejectId).getByTestId("approval-decision")).toContainText(reason);

  const audit = await auditFor(env.rejectId);
  expect(audit).toHaveLength(1);
  expect(audit[0]).toMatchObject({ action: "approval.rejected", toState: "rejected", reason, actorRole: "Operator" });
});

test("a high-risk approval needs an Admin, in the UI and on the server", async ({ page, browser }) => {
  // Operator: the UI disables the action and says why…
  await login(page, "operator", "/app/approvals");
  const c = card(page, env.highRiskId);
  await expect(c.getByTestId("approval-approve")).toBeDisabled();
  await expect(c.getByTestId("approval-blocked")).toHaveText("High-risk approvals need an Admin.");

  // …and the server refuses the same call made directly, so the UI is not the only guard.
  const operatorApi = await apiAs("operator");
  const direct = await operatorApi.post(`/api/approvals/${env.highRiskId}/approve`, { data: {} });
  expect(direct.status()).toBe(403);
  expect((await direct.json()).detail).toBe("High-risk approvals can only be decided by an Admin.");
  await operatorApi.dispose();
  expect(await auditFor(env.highRiskId)).toHaveLength(0);

  // Admin, in a separate browser session, approves it.
  const adminContext = await browser.newContext();
  const adminPage = await adminContext.newPage();
  await prepare(adminPage);
  await login(adminPage, "admin", "/app/approvals");
  const adminCard = card(adminPage, env.highRiskId);
  await expect(adminCard.getByTestId("approval-blocked")).toHaveCount(0);
  await adminCard.getByTestId("approval-approve").click();
  // High-risk decisions are final: the UI asks once more before sending.
  const dialog = adminPage.getByTestId("approval-high-confirm");
  await expect(dialog).toContainText("Approve a high-risk action?");
  await dialog.getByTestId("approval-high-confirm-confirm").click();
  await expect(card(adminPage, env.highRiskId)).toHaveCount(0);
  await adminContext.close();

  const audit = await auditFor(env.highRiskId);
  expect(audit).toHaveLength(1);
  expect(audit[0]).toMatchObject({ action: "approval.approved", actorRole: "Admin", actorEmail: creds("admin").email });
});

test("a decided approval cannot be decided again", async () => {
  const admin = await apiAs("admin");
  const again = await admin.post(`/api/approvals/${env.approveId}/reject`, { data: { reason: "too late" } });
  expect(again.status()).toBe(409);
  await admin.dispose();
  expect(await auditFor(env.approveId)).toHaveLength(1);
});
