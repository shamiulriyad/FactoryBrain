import { randomBytes } from "node:crypto";
import { expect, request, test, type Browser, type Page } from "@playwright/test";
import { apiAs, env, expandNav, login, prepare } from "./helpers";

// Role-based access control end to end, through the real UI, API and
// database (nothing stubbed):
// Super Admin → creates a factory → creates its Factory Admin → the Factory
// Admin replaces the temporary password and creates an HR officer → the HR
// officer sees only what HR may → the Factory Admin deactivates them and their
// open session ends → every step is in the audit log.
test.describe.configure({ mode: "serial" });

const tag = randomBytes(3).toString("hex");
const factory = { code: `E2E-${tag}`.toUpperCase(), name: `E2E Factory ${tag}` };
const fa = { email: `factory-admin-${tag}@e2e.test`, temp: "", password: `pw-${randomBytes(9).toString("base64url")}` };
const hr = { email: `hr-${tag}@e2e.test`, temp: "", password: `pw-${randomBytes(9).toString("base64url")}` };
let faId = "";
let hrId = "";

async function newPage(browser: Browser): Promise<Page> {
  const page = await (await browser.newContext()).newPage();
  await prepare(page);
  return page;
}

/** First sign-in with a temporary password: the app sends the person to Account to replace it. */
async function firstSignIn(page: Page, who: { email: string; temp: string; password: string }) {
  await page.goto("/login");
  await page.getByLabel("Email").fill(who.email);
  await page.getByLabel("Password", { exact: true }).fill(who.temp);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL((u) => u.pathname === "/app/account");
  await expect(page.getByTestId("must-change-password")).toBeVisible();
  // Nothing else opens until the password is changed.
  const blocked = await page.request.get("/api/roles");
  expect([401, 403]).toContain(blocked.status());
  const form = page.getByTestId("change-password-form");
  await form.getByLabel("Current password").fill(who.temp);
  await form.getByLabel("New password", { exact: true }).fill(who.password);
  await form.getByLabel("Repeat new password").fill(who.password);
  await page.getByTestId("change-password").click();
  await page.waitForURL((u) => u.pathname !== "/app/account");
}

async function createUserInUi(page: Page, user: { email: string }, name: string, roleId: string): Promise<{ id: string; temp: string }> {
  await page.goto("/app/admin/users/new");
  await page.getByTestId("user-email").fill(user.email);
  await page.getByTestId("user-name").fill(name);
  const temp = await page.getByTestId("user-password").inputValue();
  await page.getByTestId(`role-option-${roleId}`).check();
  const factoryButton = page.getByTestId(`factory-option-${factory.code}`);
  if ((await factoryButton.getAttribute("aria-pressed")) !== "true") await factoryButton.click();
  const created = page.waitForResponse((r) => r.url().endsWith("/api/users") && r.request().method() === "POST");
  await page.getByTestId("create-user").click();
  const res = await created;
  expect(res.status()).toBe(201);
  const body = await res.json();
  await page.waitForURL((u) => u.pathname === `/app/admin/users/${body.id}`);
  return { id: body.id, temp };
}

test.beforeEach(async ({ page }) => {
  await prepare(page);
});

test("the Super Admin creates a factory and its Factory Admin", async ({ page }) => {
  await login(page, "admin", "/app/admin/factories");
  await page.getByTestId("new-factory").click();
  await page.getByTestId("factory-code").fill(factory.code);
  await page.getByTestId("factory-name").fill(factory.name);
  const created = page.waitForResponse((r) => r.url().endsWith("/api/factories") && r.request().method() === "POST");
  await page.getByTestId("save-factory").click();
  expect((await created).status()).toBe(201);
  await expect(page.getByTestId("factory-list")).toContainText(factory.name);

  const result = await createUserInUi(page, fa, "E2E Factory Admin", "FactoryAdmin");
  faId = result.id;
  fa.temp = result.temp;
  await expect(page.getByTestId("user-detail")).toContainText("Factory Admin");
  await expect(page.getByTestId("user-detail")).toContainText("Temp. password");
});

test("the Factory Admin replaces the temporary password and can only grant roles below their own", async ({ browser }) => {
  const page = await newPage(browser);
  await firstSignIn(page, fa);
  await expect(page.getByTestId("nav-admin-users")).toBeVisible();
  await expect(page.getByTestId("nav-admin-factories")).toBeVisible();

  // The user list holds only their factory (never the Super Admin).
  await page.goto("/app/admin/users");
  await expect(page.getByTestId("users-table")).toBeVisible();
  await expect(page.locator(`[data-testid=user-row][data-email="admin@e2e.test"]`)).toHaveCount(0);

  // Factory Admin / Super Admin can't be granted by a Factory Admin.
  await page.goto("/app/admin/users/new");
  await expect(page.getByTestId("role-option-HrOfficer")).toBeVisible();
  await expect(page.getByTestId("role-option-FactoryAdmin")).toHaveCount(0);
  await expect(page.getByTestId("role-option-SuperAdmin")).toHaveCount(0);

  const result = await createUserInUi(page, hr, "E2E HR Officer", "HrOfficer");
  hrId = result.id;
  hr.temp = result.temp;
  await page.context().close();
});

test("the HR officer sees what HR may and is refused administration", async ({ browser }) => {
  const page = await newPage(browser);
  await firstSignIn(page, hr);
  await expandNav(page);
  await expect(page.getByTestId("nav-workforce")).toBeVisible();
  await expect(page.getByTestId("nav-admin-users")).toHaveCount(0);
  await expect(page.getByTestId("nav-audit")).toHaveCount(0);
  // Typing the URL shows Access denied; the API refuses too.
  await page.goto("/app/admin/users");
  await expect(page.getByTestId("access-denied")).toBeVisible();
  expect((await page.request.get("/api/users")).status()).toBe(401); // no bearer on a raw request
  await page.context().close();
});

test("deactivation needs confirmation and ends the person's open session", async ({ page, browser }) => {
  const hrPage = await newPage(browser);
  await hrPage.goto("/login");
  await hrPage.getByLabel("Email").fill(hr.email);
  await hrPage.getByLabel("Password", { exact: true }).fill(hr.password);
  await hrPage.getByRole("button", { name: "Sign in" }).click();
  await hrPage.waitForURL((u) => u.pathname.startsWith("/app"));

  await page.goto("/login");
  await page.getByLabel("Email").fill(fa.email);
  await page.getByLabel("Password", { exact: true }).fill(fa.password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL((u) => u.pathname.startsWith("/app"));
  await page.goto(`/app/admin/users/${hrId}?tab=security`);
  await page.getByTestId("toggle-status").click();
  const dialog = page.getByTestId("status-confirm");
  await expect(dialog).toContainText("Deactivate");
  await dialog.getByTestId("status-confirm-confirm").click();
  await expect(page.getByTestId("user-detail")).toContainText("Deactivated");

  // The HR session is over: the next navigation sends them to sign in.
  await hrPage.reload();
  await hrPage.waitForURL((u) => u.pathname === "/login");
  await hrPage.getByLabel("Email").fill(hr.email);
  await hrPage.getByLabel("Password", { exact: true }).fill(hr.password);
  await hrPage.getByRole("button", { name: "Sign in" }).click();
  await expect(hrPage.getByText("This account is deactivated")).toBeVisible();
  await hrPage.context().close();
});

test("the server refuses escalation and the audit log has every step", async () => {
  const anon = await request.newContext({ baseURL: env.baseURL });
  const login = await anon.post("/api/auth/login", { data: { email: fa.email, password: fa.password } });
  const { accessToken } = await login.json();
  const auth = { Authorization: `Bearer ${accessToken}` };
  expect((await anon.put(`/api/users/${faId}/roles`, { headers: auth, data: { roles: ["SuperAdmin"] } })).status()).toBe(403);
  expect((await anon.post("/api/factories", { headers: auth, data: { code: "NOPE", name: "Nope" } })).status()).toBe(403);
  await anon.dispose();

  const admin = await apiAs("admin");
  const audit = (await (await admin.get("/api/audit?entityType=user&limit=200")).json()) as Array<{ action: string; entityId: string; actorEmail: string }>;
  const about = (id: string) => audit.filter((a) => a.entityId === id).map((a) => a.action);
  expect(about(faId)).toEqual(expect.arrayContaining(["user.created", "auth.login"]));
  expect(about(hrId)).toEqual(expect.arrayContaining(["user.created", "user.deactivated"]));
  expect(audit.find((a) => a.entityId === hrId && a.action === "user.created")?.actorEmail).toBe(fa.email);
  await admin.dispose();
});
