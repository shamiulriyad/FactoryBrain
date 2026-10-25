import { createHash, randomBytes } from "node:crypto";
import { expect, test, type Browser, type Page } from "@playwright/test";
import { apiAs, fakeLlmStats, login, prepare } from "./helpers";

// Settings changed through the real Settings UI reach Ask on the next
// question, with no API restart. The only model is the harness's local fake
// (LLM_BASE_URL); the key typed here is test-only and never leaves 127.0.0.1.
test.describe.configure({ mode: "serial" });

const sha256 = (s: string) => createHash("sha256").update(s).digest("hex");

async function openAdvanced(page: Page) {
  await page.goto("/app/settings?section=advanced");
  await expect(page.getByTestId("llm-panel")).toBeVisible();
  await expect(page.getByTestId("llm-persistence")).toBeVisible(); // status loaded
}

async function askAsViewer(browser: Browser, question: string) {
  const context = await browser.newContext();
  const page = await context.newPage();
  await prepare(page);
  await login(page, "viewer", "/app/ask");
  const answered = page.waitForResponse((r) => r.url().endsWith("/api/ask") && r.request().method() === "POST");
  await page.getByTestId("ask-input").fill(question);
  await page.getByTestId("ask-input").press("Enter");
  const body = await (await answered).json();
  const chip = page.getByTestId("ask-source-chip");
  await expect(chip).toBeVisible();
  const mode = await chip.getAttribute("data-mode");
  await context.close();
  return { body, mode };
}

test.afterAll(async () => {
  const admin = await apiAs("admin");
  await admin.post("/api/settings/llm", { data: { clearAll: true } });
  await admin.dispose();
});

test("Admin changes the model in the Settings UI and a viewer's Ask follows Live / Demo without a restart", async ({ page, browser }) => {
  const key = `e2e-ui-key-${randomBytes(9).toString("base64url")}`;
  const model = `e2e-ui-model-${randomBytes(3).toString("hex")}`;

  // Record every settings response the browser sees, to prove the key is never sent back.
  const settingsBodies: string[] = [];
  page.on("response", async (r) => {
    if (r.url().includes("/api/settings/")) settingsBodies.push(await r.text().catch(() => ""));
  });

  await prepare(page);
  await login(page, "admin", "/app");
  await openAdvanced(page);
  await expect(page.getByTestId("llm-mode-chip")).toHaveAttribute("data-mode", "demo");

  await page.locator("#llm-provider").selectOption("openai");
  await page.locator("#llm-api-key").fill(key);
  await page.locator("#llm-model").fill(model);
  await page.getByTestId("llm-save").click();

  await expect(page.getByTestId("llm-mode-chip")).toHaveAttribute("data-mode", "live");
  await expect(page.getByTestId("llm-persistence")).toHaveAttribute("data-persistence", "database");
  await expect(page.locator("#llm-api-key")).toHaveValue(""); // the draft is discarded after saving

  // The viewer's very next question is answered by the model the Admin chose, with the Admin's key.
  const before = await fakeLlmStats();
  const live = await askAsViewer(browser, "needle breakage procedure");
  expect(live.body.mode).toBe("live");
  expect(live.mode).toBe("live");
  const after = await fakeLlmStats();
  expect(after.chat).toBe(before.chat + 1);
  expect(after.lastModel).toBe(model);
  expect(after.lastKeySha256).toBe(sha256(key));

  // Clear it in the UI: the next answer is Demo and the model is not called.
  await page.getByTestId("llm-clear").click();
  await page.getByTestId("llm-clear-confirm").click();
  await expect(page.getByTestId("llm-mode-chip")).toHaveAttribute("data-mode", "demo");

  const beforeDemo = await fakeLlmStats();
  const demo = await askAsViewer(browser, "needle breakage procedure");
  expect(demo.body.mode).toBe("demo");
  expect(demo.mode).toBe("demo");
  expect((await fakeLlmStats()).chat).toBe(beforeDemo.chat);

  // The key never came back to the browser, and isn't anywhere in the page.
  expect(settingsBodies.length).toBeGreaterThan(0);
  for (const b of settingsBodies) expect(b).not.toContain(key);
  expect(await page.content()).not.toContain(key);
});

test("a stale Settings page can't overwrite a newer save (409, latest values shown)", async ({ page }) => {
  await prepare(page);
  await login(page, "admin", "/app");
  await openAdvanced(page);

  // Someone else saves after this page loaded.
  const other = await apiAs("admin");
  expect((await other.post("/api/settings/llm", { data: { provider: "openai", model: "saved-elsewhere" } })).status()).toBe(200);
  await other.dispose();

  await page.locator("#llm-model").fill("stale-page-model");
  await page.getByTestId("llm-save").click();
  await expect(page.getByTestId("llm-notice")).toContainText("Someone else changed these settings");
  await expect(page.locator("#llm-model")).toHaveValue("saved-elsewhere");
});

test("embedding changes show a plan first and change nothing until confirmed", async ({ page }) => {
  const embedding = async () => {
    const api = await apiAs("viewer");
    const s = await (await api.get("/api/settings/embedding")).json();
    await api.dispose();
    return s as { provider: string; dimensions: number; columnDimensions: number };
  };

  await prepare(page);
  await login(page, "admin", "/app");
  await openAdvanced(page);
  await expect(page.getByTestId("embedding-current")).toHaveText("local / hash-md5 / 384");

  // Review: the plan reports the scope, the resize and the cost (local: free).
  await page.locator("#emb-dims").fill("256");
  await page.getByTestId("emb-review").click();
  await expect(page.getByTestId("emb-plan")).toBeVisible();
  await expect(page.getByTestId("emb-plan-column")).toHaveText("vector(384) → vector(256)");
  await expect(page.getByTestId("emb-plan-docs")).toHaveText(/^[1-9]\d* \/ [1-9]\d*$/);
  await expect(page.getByTestId("emb-plan-cost")).toContainText("no provider calls");

  // Cancel: nothing changed on the server.
  await page.getByRole("button", { name: "Cancel" }).click();
  await expect(page.getByTestId("emb-plan")).toHaveCount(0);
  expect(await embedding()).toMatchObject({ provider: "local", dimensions: 384, columnDimensions: 384 });

  // Review + confirm: applied.
  await page.getByTestId("emb-review").click();
  await page.getByTestId("emb-confirm").click();
  await expect(page.getByTestId("emb-notice")).toContainText("embeddings updated");
  await expect(page.getByTestId("embedding-current")).toHaveText("local / hash-md5 / 256");
  expect(await embedding()).toMatchObject({ provider: "local", dimensions: 256, columnDimensions: 256 });

  // Put it back the same way (keeps the rest of the suite on 384).
  await page.locator("#emb-dims").fill("384");
  await page.getByTestId("emb-review").click();
  await page.getByTestId("emb-confirm").click();
  await expect(page.getByTestId("embedding-current")).toHaveText("local / hash-md5 / 384");
  expect(await embedding()).toMatchObject({ dimensions: 384, columnDimensions: 384 });
});

test("a Viewer sees the settings but cannot change them", async ({ page }) => {
  await prepare(page);
  await login(page, "viewer", "/app");
  await openAdvanced(page);
  await expect(page.getByTestId("embedding-panel")).toContainText("Only an Admin can change embedding settings");
  await expect(page.getByTestId("emb-review")).toHaveCount(0);

  // The form is read-only for a Viewer: nothing to submit, and it says why.
  await expect(page.getByTestId("llm-readonly")).toContainText("Only an Admin account can change these settings");
  await expect(page.locator("#llm-model")).toBeDisabled();
  await expect(page.locator("#llm-api-key")).toBeDisabled();
  await expect(page.getByTestId("llm-save")).toBeDisabled();

  // …and the API refuses the same change made directly.
  const viewerApi = await apiAs("viewer");
  const direct = await viewerApi.post("/api/settings/llm", { data: { provider: "openai", model: "viewer-attempt" } });
  expect(direct.status()).toBe(403);
  await viewerApi.dispose();
});
