import { expect, test } from "@playwright/test";
import { creds, login, prepare } from "./helpers";

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

test("protected routes send an anonymous visitor to login, preserving the destination", async ({ page }) => {
  await page.goto("/app/approvals");
  await page.waitForURL(/\/login\?next=/);
  expect(new URL(page.url()).searchParams.get("next")).toBe("/app/approvals");
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();
});

test("a wrong password is refused with a generic message", async ({ page }) => {
  await page.goto("/login");
  await page.getByLabel("Email").fill(creds("viewer").email);
  await page.getByLabel("Password").fill("definitely-not-the-password");
  await page.getByRole("button", { name: "Sign in" }).click();
  // Scoped to the form: Next.js also renders an (empty) role="alert" route announcer.
  await expect(page.locator("form").getByRole("alert")).toHaveText("Email or password is incorrect.");
  expect(new URL(page.url()).pathname).toBe("/login");
});

test("login returns to the requested page and authenticated navigation works", async ({ page }) => {
  await login(page, "viewer", "/app/approvals");
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  // Signed-in identity and role from the API.
  const chip = page.getByText(creds("viewer").email, { exact: true }).locator("..");
  await expect(chip).toContainText("Viewer");

  // Client-side navigation through the sidebar to another protected page.
  await page.locator('a[href="/app/ask"]').first().click();
  await page.waitForURL("**/app/ask");
  await expect(page.getByTestId("ask-input")).toBeVisible();

  // A full reload keeps the session: the HttpOnly refresh cookie restores it.
  await page.reload();
  await expect(page.getByText(creds("viewer").email, { exact: true })).toBeVisible();
  expect(new URL(page.url()).pathname).toBe("/app/ask");
});

test("the refresh cookie is HttpOnly and scoped to /api/auth; the access token is not stored in the browser", async ({ page, context }) => {
  await login(page, "viewer");
  const cookies = await context.cookies();
  const refresh = cookies.find((c) => c.name === "fb_refresh");
  expect(refresh, "fb_refresh cookie").toBeTruthy();
  expect(refresh!.httpOnly).toBe(true);
  expect(refresh!.path).toBe("/api/auth");
  expect(refresh!.sameSite).toBe("Lax");
  // Not readable from page script…
  expect(await page.evaluate(() => document.cookie)).not.toContain("fb_refresh");
  // …and no JWT anywhere in web storage.
  const stored = await page.evaluate(() => JSON.stringify({ ...window.localStorage, ...window.sessionStorage }));
  expect(stored).not.toMatch(/eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\./);
});

test("an external next= target is ignored after login (no open redirect)", async ({ page }) => {
  const { email, password } = creds("viewer");
  await page.goto("/login?next=" + encodeURIComponent("https://evil.example/phish"));
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL((u) => u.pathname === "/app");
  expect(new URL(page.url()).origin).toBe(new URL(process.env.E2E_BASE_URL!).origin);
  expect(page.url()).not.toContain("evil.example");
});

test("logout ends the session server-side and protected routes require login again", async ({ page, context }) => {
  await login(page, "operator", "/app/ask");
  const before = (await context.cookies()).find((c) => c.name === "fb_refresh")?.value;
  expect(before).toBeTruthy();

  await page.getByRole("button", { name: "Sign out" }).click();
  await page.waitForURL("**/login**");
  expect((await context.cookies()).find((c) => c.name === "fb_refresh")).toBeUndefined();

  // Protected pages now bounce to login, even after a reload.
  await page.goto("/app/approvals");
  await page.waitForURL(/\/login\?next=/);

  // The old refresh token was revoked on the server, not just deleted locally.
  const replay = await page.request.post("/api/auth/refresh", { headers: { Cookie: `fb_refresh=${before}` } });
  expect(replay.status()).toBe(401);
});
