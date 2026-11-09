import { expect, test } from "@playwright/test";
import { login, prepare } from "./helpers";

test.describe.configure({ mode: "serial" });

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

test("Viewer sees Workforce Demo pill, aggregates and directory basics, but no wages, phones, leave reasons or grievance details", async ({
  page
}) => {
  await login(page, "viewer", "/app/workforce");

  const navItem = page.getByTestId("nav-workforce");
  await expect(navItem).toBeVisible();
  await expect(navItem).toContainText("Demo");

  await expect(page.getByTestId("demo-data-banner")).toBeVisible();
  await expect(page.getByTestId("wf-stat-headcount")).toBeVisible();
  await expect(page.getByTestId("workforce-reset-demo-btn")).toHaveCount(0);

  // Workers directory: Viewer cannot add workers or see phone/wage columns
  await page.getByTestId("workforce-tab-workers").click();
  await expect(page).toHaveURL(/tab=workers/);
  await expect(page.getByTestId("wf-viewer-privacy-callout")).toBeVisible();
  await expect(page.getByTestId("wf-add-worker-btn")).toHaveCount(0);
  await expect(page.getByTestId("worker-phone-W-0001")).toHaveCount(0);
  await expect(page.getByTestId("worker-wage-W-0001")).toHaveCount(0);

  // Leave tab: Viewer cannot see individual leave reasons or approve buttons
  await page.getByTestId("workforce-tab-leave").click();
  await expect(page.getByTestId("leave-reason-hidden-LR-1001")).toBeVisible();
  await expect(page.getByTestId("leave-approve-LR-1001")).toHaveCount(0);

  // Grievances tab: Viewer sees category counts only, no individual ticket table
  await page.getByTestId("workforce-tab-grievances").click();
  await expect(page.getByTestId("wf-grievance-category-counts")).toBeVisible();
  await expect(page.getByTestId("wf-grievance-viewer-only")).toBeVisible();
  await expect(page.getByTestId("grievance-row")).toHaveCount(0);

  // Worker profile: Viewer cannot see Notes tab or wage box
  await page.goto("/app/workforce/workers/W-0001");
  await expect(page.getByTestId("worker-profile-page")).toBeVisible();
  await expect(page.getByTestId("worker-subtab-notes")).toHaveCount(0);
  await expect(page.getByTestId("profile-wage-hidden")).toBeVisible();
  await expect(page.getByTestId("profile-admin-wage-box")).toHaveCount(0);
});

test("Operator marks attendance and approves leave with Undo, while wage amounts remain Admin-only", async ({
  page
}) => {
  await login(page, "operator", "/app/workforce");

  // Attendance roll-call: mark W-0001 as late, then Undo from toast
  await page.getByTestId("workforce-tab-attendance").click();
  await expect(page.getByTestId("wf-attendance-manual-callout")).toBeVisible();
  await page.getByTestId("attendance-status-W-0001-late").click();
  await expect(page.getByTestId("attendance-current-W-0001")).toContainText("Late");
  await expect(page.getByTestId("att-local-W-0001")).toBeVisible();

  const toast = page.getByTestId("toast").first();
  await expect(toast).toBeVisible();
  await toast.getByRole("button", { name: "Undo" }).click();
  await expect(page.getByTestId("attendance-current-W-0001")).toContainText("Present");

  // Leave queue: approve LR-1001, then Undo
  await page.getByTestId("workforce-tab-leave").click();
  await expect(page.getByTestId("leave-approve-LR-1001")).toBeVisible();
  await page.getByTestId("leave-approve-LR-1001").click();

  const leaveToast = page.getByTestId("toast").first();
  await expect(leaveToast).toBeVisible();
  await leaveToast.getByRole("button", { name: "Undo" }).click();
  await expect(page.getByTestId("leave-approve-LR-1001")).toBeVisible();

  // Performance tab: Operator sees neutral leaderboard, but BDT bonus table is hidden
  await page.getByTestId("workforce-tab-performance").click();
  await expect(page.getByTestId("wf-performance-table")).toBeVisible();
  await expect(page.getByTestId("wf-bonus-hidden-callout")).toBeVisible();
  await expect(page.getByTestId("wf-bonus-table")).toHaveCount(0);
});

test("Wellbeing tab enforces non-negotiable privacy rules and the k >= 5 anonymity threshold", async ({
  page
}) => {
  await login(page, "operator", "/app/workforce?tab=wellbeing");

  await expect(page.getByTestId("wf-wellbeing-never-do-panel")).toContainText(
    "No individual 'mental health score'"
  );

  // Line 1 has >= 5 responses (sufficient), while Line 6 starts with 3 responses (< 5)
  await expect(page.getByTestId("wf-pulse-line-line-1")).toHaveAttribute(
    "data-sufficient",
    "true"
  );
  await expect(page.getByTestId("wf-pulse-line-line-6")).toHaveAttribute(
    "data-sufficient",
    "false"
  );
  await expect(page.getByTestId("wf-pulse-insufficient-line-6")).toContainText(
    "Not enough responses to show safely"
  );

  // Submit 2 anonymous pulse responses on Line 6 to reach k = 5 and unlock the aggregate
  await page.getByTestId("wf-pulse-line-select").selectOption("line-6");
  await page.getByTestId("wf-pulse-submit-btn").click();
  await expect(page.getByTestId("wf-pulse-line-line-6")).toHaveAttribute(
    "data-sufficient",
    "false"
  );
  await page.getByTestId("wf-pulse-submit-btn").click();
  await expect(page.getByTestId("wf-pulse-line-line-6")).toHaveAttribute(
    "data-sufficient",
    "true"
  );
  await expect(page.getByTestId("wf-pulse-insufficient-line-6")).toHaveCount(0);
});

test("Admin sees wage and bonus BDT figures, policy constants and can reset demo data", async ({
  page
}) => {
  await login(page, "admin", "/app/workforce");

  await expect(page.getByTestId("wf-admin-policy-panel")).toBeVisible();
  await expect(page.getByTestId("workforce-reset-demo-btn")).toBeVisible();

  await page.getByTestId("workforce-tab-performance").click();
  await expect(page.getByTestId("wf-bonus-table")).toBeVisible();

  await page.getByTestId("workforce-reset-demo-btn").click();
  const dialog = page.getByTestId("workforce-reset-confirm-modal");
  await expect(dialog).toBeVisible();
  await dialog.getByTestId("workforce-reset-confirm-modal-confirm").click();
  await expect(page.getByTestId("toast").first()).toContainText(
    "Workforce demo data restored to seed."
  );
});
