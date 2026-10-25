import { randomBytes } from "node:crypto";
import { expect, request, test, type Browser, type Page } from "@playwright/test";
import { apiAs, env, login, prepare } from "./helpers";

// Settings → Users & roles, end to end against the real API and database:
// a real self-registered account is promoted and demoted by the seeded Admin
// through the UI, the change persists across reloads, and the person's own
// session picks it up (menus and allowed actions change). Nothing is stubbed.
test.describe.configure({ mode: "serial" });

const email = `role-change-${randomBytes(4).toString("hex")}@e2e.test`;
const password = `pw-${randomBytes(9).toString("base64url")}`;
let userId = "";

async function signIn(page: Page, next = "/app") {
  await page.goto(`/login?next=${encodeURIComponent(next)}`);
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password", { exact: true }).fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL((u) => u.pathname === next);
}

async function adminPage(browser: Browser) {
  const context = await browser.newContext();
  const page = await context.newPage();
  await prepare(page);
  await login(page, "admin", "/app");
  await page.goto("/app/settings?section=users");
  await expect(page.getByTestId("users-table")).toBeVisible();
  return { context, page };
}

const row = (page: Page) => page.locator(`[data-testid=user-row][data-email="${email}"]`);

/**
 * Make the person's open session pick up a role change: non-Admins press
 * "Check for role changes"; an Admin (who sees the user table instead) reloads,
 * which restores the session from the refresh cookie with the current role.
 */
async function checkRole(page: Page, before: string, expected: string) {
  // Client-side navigation (no reload), so only the button can pick the change up.
  await page.getByTestId("nav-settings").click();
  await page.waitForURL((u) => u.pathname === "/app/settings");
  await page.getByRole("button", { name: /Users & roles/ }).click();
  const button = page.getByTestId("check-role");
  await expect(button.or(page.getByTestId("users-table"))).toBeVisible();
  await expect(page.getByTestId("session-role")).toHaveText(before);
  if (await button.isVisible()) await button.click();
  else await page.reload();
  await expect(page.getByTestId("session-role")).toHaveText(expected);
  if (expected !== "Admin") await expect(page.getByTestId("my-role")).toHaveText(expected);
}

test.beforeAll(async () => {
  const ctx = await request.newContext({ baseURL: env.baseURL });
  const res = await ctx.post("/api/auth/register", { data: { email, password } });
  expect(res.status()).toBe(201);
  userId = (await res.json()).user.id;
  await ctx.dispose();
});

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

test("a new account is a Viewer and can't run operational actions", async ({ page }) => {
  await signIn(page, "/app/settings");
  await page.goto("/app/settings?section=users");
  await expect(page.getByTestId("my-role")).toHaveText("Viewer");
  await page.goto("/app/agents");
  await expect(page.getByTestId("agent-run-blocked").first()).toBeVisible();
  await expect(page.getByTestId("nav-audit")).toHaveCount(0);
});

test("the Admin promotes it to Operator; the change persists and reaches the person's session", async ({ page, browser }) => {
  await signIn(page, "/app");
  const admin = await adminPage(browser);
  await expect(row(admin.page).getByRole("combobox")).toHaveValue("Viewer");
  const patched = admin.page.waitForResponse((r) => r.url().endsWith(`/api/users/${userId}/role`) && r.request().method() === "PATCH");
  await row(admin.page).getByRole("combobox").selectOption("Operator");
  expect((await patched).status()).toBe(200);
  await expect(admin.page.getByTestId("users-notice")).toContainText("Role updated");

  // Persisted on the server: still Operator after a full reload.
  await admin.page.reload();
  await expect(row(admin.page).getByRole("combobox")).toHaveValue("Operator");
  await admin.context.close();

  // The person's already-open session picks it up without signing out…
  await checkRole(page, "Viewer", "Operator");
  // …and the new permission works: Operators can run agents.
  await page.goto("/app/agents");
  await expect(page.getByTestId("agent-run-line-throughput-agent")).toBeVisible();
  // A reload restores the session from the refresh cookie, still as Operator.
  await page.reload();
  await expect(page.getByTestId("session-role")).toHaveText("Operator");
  await expect(page.getByTestId("agent-run-line-throughput-agent")).toBeVisible();
});

test("granting and removing Admin need confirmation and change what the person can open", async ({ page, browser }) => {
  await signIn(page, "/app");
  const admin = await adminPage(browser);

  await row(admin.page).getByRole("combobox").selectOption("Admin");
  const dialog = admin.page.getByTestId("role-confirm");
  await expect(dialog).toContainText("Make this person an Admin?");
  await dialog.getByTestId("role-confirm-confirm").click();
  await expect(row(admin.page).getByRole("combobox")).toHaveValue("Admin");

  await checkRole(page, "Operator", "Admin");
  await expect(page.getByTestId("nav-audit")).toBeVisible();
  await expect(page.getByTestId("users-table")).toBeVisible();
  // Even as an Admin, the person can't change their own role.
  await expect(row(page).getByRole("combobox")).toBeDisabled();

  await row(admin.page).getByRole("combobox").selectOption("Viewer");
  await expect(dialog).toContainText("Remove Admin access?");
  await dialog.getByTestId("role-confirm-confirm").click();
  await expect(row(admin.page).getByRole("combobox")).toHaveValue("Viewer");
  await admin.context.close();

  await checkRole(page, "Admin", "Viewer");
  await expect(page.getByTestId("nav-audit")).toHaveCount(0);
  await expect(page.getByTestId("users-table")).toHaveCount(0);
});

test("the server refuses self-promotion and an Admin changing their own role, and audits every change", async () => {
  const user = await request.newContext({ baseURL: env.baseURL });
  const loginRes = await user.post("/api/auth/login", { data: { email, password } });
  const { accessToken } = await loginRes.json();
  const self = await user.patch(`/api/users/${userId}/role`, { headers: { Authorization: `Bearer ${accessToken}` }, data: { role: "Admin" } });
  expect(self.status()).toBe(403);
  await user.dispose();

  const admin = await apiAs("admin");
  const me = await (await admin.get("/api/auth/me")).json();
  const own = await admin.patch(`/api/users/${me.id}/role`, { data: { role: "Viewer" } });
  expect(own.status()).toBe(409);
  const bad = await admin.patch(`/api/users/${userId}/role`, { data: { role: "Root" } });
  expect(bad.status()).toBe(400);

  const audit = (await (await admin.get(`/api/audit?entityType=user&entityId=${userId}`)).json()) as Array<{ action: string; fromState: string; toState: string; actorRole: string }>;
  const changes = audit.filter((a) => a.action === "user.role_changed").map((a) => `${a.fromState}→${a.toState}`);
  expect(changes).toEqual(expect.arrayContaining(["Viewer→Operator", "Operator→Admin", "Admin→Viewer"]));
  expect(audit.filter((a) => a.action === "user.role_changed").every((a) => a.actorRole === "Admin")).toBe(true);
  await admin.dispose();
});
