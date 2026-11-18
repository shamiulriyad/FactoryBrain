import { expect, test } from "@playwright/test";
import { login, prepare } from "./helpers";

// Workforce runs on server data only: headcount, attendance, leave, overtime
// and workforce analytics from the API. No browser demo dataset.

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

test("Workforce shows server figures in every tab and no demo data", async ({ page }) => {
  await login(page, "admin", "/app/workforce");

  await expect(page.getByTestId("workforce-page")).toBeVisible();
  await expect(page.getByTestId("demo-data-banner")).toHaveCount(0);
  const nav = page.getByTestId("nav-workforce");
  if (await nav.count()) await expect(nav).not.toContainText("Demo");

  // Overview: live headcount and today's attendance.
  await expect(page.getByTestId("workforce-overview")).toBeVisible();
  await expect(page.getByTestId("wf-active")).toBeVisible();
  await expect(page.getByTestId("wf-present")).toBeVisible();

  // Workers: the directory table (or its empty message) from GET /api/workers.
  const workers = page.waitForResponse((r) => r.url().includes("/api/workers?") && r.request().method() === "GET");
  await page.getByTestId("workforce-tab-workers").click();
  await expect(page).toHaveURL(/tab=workers/);
  expect((await workers).ok()).toBeTruthy();
  await expect(page.getByTestId("workforce-workers")).toBeVisible();

  await page.getByTestId("workforce-tab-attendance").click();
  await expect(page.getByTestId("workforce-attendance")).toBeVisible();

  await page.getByTestId("workforce-tab-leave").click();
  await expect(page.getByTestId("workforce-leave")).toBeVisible();

  const analytics = page.waitForResponse((r) => r.url().includes("/api/analytics/workforce"));
  await page.getByTestId("workforce-tab-performance").click();
  expect((await analytics).ok()).toBeTruthy();
  await expect(page.getByTestId("workforce-performance")).toBeVisible();
});

test("an old Workforce worker link opens the worker directory profile", async ({ page }) => {
  await login(page, "admin", "/app/workers");
  await page.goto("/app/workforce/workers/00000000-0000-0000-0000-000000000000");
  await expect(page).toHaveURL(/\/app\/workers\/00000000-0000-0000-0000-000000000000/);
});
