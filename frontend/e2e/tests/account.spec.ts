import { randomBytes } from "node:crypto";
import { expect, request, test, type Page } from "@playwright/test";
import { env, login, prepare } from "./helpers";

// Sign-up, forgot / reset password and role management through the real
// pages. Reset emails go through the API's real SMTP client to the harness's
// fake SMTP server on 127.0.0.1 and are read back from it.
test.describe.configure({ mode: "serial" });

const email = `new-user-${randomBytes(4).toString("hex")}@e2e.test`;
const firstPassword = `first-${randomBytes(8).toString("base64url")}`;
const newPassword = `second-${randomBytes(8).toString("base64url")}`;

async function signInForm(page: Page, user: string, password: string) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(user);
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
}

async function latestResetLink(to: string): Promise<string> {
  const ctx = await request.newContext();
  let link: string | null = null;
  for (let i = 0; i < 50 && !link; i++) {
    const mails = (await (await ctx.get(`${env.fakeLlmUrl}/__mail?to=${encodeURIComponent(to)}`)).json()) as { body: string }[];
    const last = mails.at(-1)?.body ?? "";
    link = last.match(/https?:\/\/\S+\/reset-password\?token=[A-Za-z0-9_-]{43}/)?.[0] ?? null;
    if (!link) await new Promise((r) => setTimeout(r, 200));
  }
  await ctx.dispose();
  expect(link, `reset email for ${to}`).toBeTruthy();
  return link!;
}

test("the sign-in page explains a bad email and links to sign-up and password reset", async ({ page }) => {
  await prepare(page);
  await page.goto("/login");
  await page.getByLabel("Email").fill("test");
  await page.getByLabel("Password", { exact: true }).fill("whatever-123");
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.locator("form [role=alert]")).toHaveText("Enter your full email address, for example name@company.com.");

  await page.getByTestId("link-forgot").click();
  await expect(page).toHaveURL(/\/forgot-password$/);
  await page.goto("/login");
  await page.getByTestId("link-register").click();
  await expect(page).toHaveURL(/\/register$/);
});

test("anyone can sign up and lands in the app as a Viewer", async ({ page }) => {
  await prepare(page);
  await page.goto("/register");
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password", { exact: true }).fill("short");
  await page.getByLabel("Confirm password").fill("short");
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page.locator("form [role=alert]")).toContainText("at least 10 characters");

  await page.getByLabel("Password", { exact: true }).fill(firstPassword);
  await page.getByLabel("Confirm password").fill(firstPassword + "x");
  await page.getByRole("button", { name: "Create account" }).click();
  await expect(page.locator("form [role=alert]")).toHaveText("The two passwords don't match.");

  await page.getByLabel("Confirm password").fill(firstPassword);
  await page.getByRole("button", { name: "Create account" }).click();
  await page.waitForURL((u) => u.pathname === "/app");
  await expect(page.getByText(email, { exact: true })).toBeVisible();
  await expect(page.getByTestId("session-role")).toHaveText("Viewer");

  // The same email can't be registered twice.
  const ctx = await request.newContext({ baseURL: env.baseURL });
  const dupe = await ctx.post("/api/auth/register", { data: { email, password: firstPassword } });
  expect(dupe.status()).toBe(409);
  await ctx.dispose();
});

test("forgot password emails a one-time link that sets a new password", async ({ page }) => {
  await prepare(page);
  await page.goto("/forgot-password");
  await page.getByLabel("Email").fill(email);
  await page.getByRole("button", { name: "Send reset link" }).click();
  await expect(page.getByTestId("forgot-sent")).toContainText("If an account exists for that email");

  const link = await latestResetLink(email);
  expect(new URL(link).origin).toBe(new URL(env.baseURL).origin);

  await page.goto(link);
  await expect(page.getByRole("heading", { name: "Choose a new password" })).toBeVisible();
  await expect(page).toHaveURL(/\/reset-password$/); // the token is removed from the address bar
  await page.getByLabel("New password").fill(newPassword);
  await page.getByLabel("Confirm password").fill(newPassword);
  await page.getByRole("button", { name: "Set new password" }).click();
  await expect(page.getByTestId("reset-done")).toContainText("Your password has been changed");

  // Old password no longer works; the new one does.
  await signInForm(page, email, firstPassword);
  await expect(page.locator("form [role=alert]")).toHaveText("Email or password is incorrect.");
  await signInForm(page, email, newPassword);
  await page.waitForURL((u) => u.pathname === "/app");

  // The link works once.
  await page.goto(link);
  await page.getByLabel("New password").fill(`third-${randomBytes(8).toString("base64url")}`);
  await page.getByLabel("Confirm password").fill(await page.getByLabel("New password").inputValue());
  await page.getByRole("button", { name: "Set new password" }).click();
  await expect(page.getByTestId("reset-bad-link")).toContainText("invalid, has expired, or was already used");
});

test("an unknown email gets the same answer and no email", async ({ page }) => {
  const nobody = `nobody-${randomBytes(4).toString("hex")}@e2e.test`;
  await prepare(page);
  await page.goto("/forgot-password");
  await page.getByLabel("Email").fill(nobody);
  await page.getByRole("button", { name: "Send reset link" }).click();
  await expect(page.getByTestId("forgot-sent")).toContainText("If an account exists for that email");
  await page.waitForTimeout(1000);
  const ctx = await request.newContext();
  const mails = await (await ctx.get(`${env.fakeLlmUrl}/__mail?to=${encodeURIComponent(nobody)}`)).json();
  await ctx.dispose();
  expect(mails).toEqual([]);
});

test("an Admin promotes the new account in Settings → Users", async ({ page }) => {
  await prepare(page);
  await login(page, "admin", "/app/settings");
  await page.goto("/app/settings?section=users");
  const row = page.locator(`[data-testid=user-row][data-email="${email}"]`);
  await expect(row).toBeVisible();
  await row.getByRole("combobox").selectOption("Operator");
  await expect(page.getByTestId("users-notice")).toContainText("Role updated");
  await expect(row.getByRole("combobox")).toHaveValue("Operator");
  // Your own role can't be changed here.
  await expect(page.locator(`[data-testid=user-row][data-email="${process.env.E2E_ADMIN_EMAIL}"]`).getByRole("combobox")).toBeDisabled();
});

test("a Viewer sees their own access but not the user list, and the API agrees", async ({ page }) => {
  await prepare(page);
  await login(page, "viewer", "/app");
  await page.goto("/app/settings?section=users");
  await expect(page.getByTestId("my-access")).toBeVisible();
  await expect(page.getByTestId("my-role")).toHaveText("Viewer");
  await expect(page.getByTestId("users-table")).toHaveCount(0);
  await expect(page.getByRole("combobox", { name: /Role:/ })).toHaveCount(0);

  const ctx = await request.newContext({ baseURL: env.baseURL });
  const login2 = await ctx.post("/api/auth/login", { data: { email: process.env.E2E_VIEWER_EMAIL, password: process.env.E2E_VIEWER_PASSWORD } });
  const { accessToken, user } = await login2.json();
  const auth = { Authorization: `Bearer ${accessToken}` };
  expect((await ctx.get("/api/users", { headers: auth })).status()).toBe(403);
  // Nobody can grant themselves a role.
  expect((await ctx.patch(`/api/users/${user.id}/role`, { headers: auth, data: { role: "Admin" } })).status()).toBe(403);
  await ctx.dispose();
});
