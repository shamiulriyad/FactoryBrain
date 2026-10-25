import { expect, request, type APIRequestContext, type Page } from "@playwright/test";

export type Role = "admin" | "operator" | "viewer";

function need(name: string): string {
  const v = process.env[name];
  if (!v) throw new Error(`${name} is not set (run via npm run e2e)`);
  return v;
}

export const creds = (role: Role) => ({
  email: need(`E2E_${role.toUpperCase()}_EMAIL`),
  password: need(`E2E_${role.toUpperCase()}_PASSWORD`),
});

export const env = {
  baseURL: need("E2E_BASE_URL"),
  fakeLlmUrl: need("E2E_FAKE_LLM_URL"),
  approveId: need("E2E_APPROVE_ID"),
  rejectId: need("E2E_REJECT_ID"),
  highRiskId: need("E2E_HIGH_RISK_ID"),
  fakeLlmFinding: need("E2E_FAKE_LLM_FINDING"),
  forceLlmFailure: need("E2E_FORCE_LLM_FAILURE"),
};

/**
 * Skip onboarding and the guided tour (both persisted in localStorage) and
 * pin the UI to English, so tests exercise the app itself.
 */
export async function prepare(page: Page) {
  await page.addInitScript(() => {
    const set = (k: string, v: unknown) => window.localStorage.setItem(k, JSON.stringify(v));
    set("bunonbrain:onboardingComplete", true);
    set("bunonbrain:onboardedAt", new Date().toISOString());
    set("bunonbrain:tourCompleted", true);
    set("bunonbrain:tourDismissed", true);
    set("bunonbrain:locale", "en");
  });
}

/** Sign in through the real login page and wait for the app shell. */
export async function login(page: Page, role: Role, next = "/app") {
  const { email, password } = creds(role);
  await page.goto(`/login?next=${encodeURIComponent(next)}`);
  await page.getByLabel("Email").fill(email);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await page.waitForURL((u) => u.pathname === next || u.pathname.startsWith(next + "/"));
  // The top bar shows who is signed in once the session is established.
  await expect(page.getByText(email, { exact: true })).toBeVisible();
}

/**
 * API client through the Next.js server (same rewrites and admin proxy the
 * browser uses), authenticated as `role` with a fresh access token.
 */
export async function apiAs(role: Role): Promise<APIRequestContext> {
  const anon = await request.newContext({ baseURL: env.baseURL });
  const res = await anon.post("/api/auth/login", { data: creds(role) });
  expect(res.status(), `API login as ${role}`).toBe(200);
  const { accessToken } = await res.json();
  await anon.dispose();
  return request.newContext({ baseURL: env.baseURL, extraHTTPHeaders: { Authorization: `Bearer ${accessToken}` } });
}

export async function fakeLlmStats(): Promise<{
  chat: number;
  failures: number;
  other: number;
  lastModel: string | null;
  lastKeySha256: string | null;
}> {
  const ctx = await request.newContext();
  const res = await ctx.get(`${env.fakeLlmUrl}/__stats`);
  const body = await res.json();
  await ctx.dispose();
  return body;
}
