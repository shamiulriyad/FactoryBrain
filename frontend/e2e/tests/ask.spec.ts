import { expect, test, type Page } from "@playwright/test";
import { apiAs, env, fakeLlmStats, login, prepare } from "./helpers";

// Every state below is produced by the real API for that request; nothing is
// stubbed in the browser. "Live" uses the harness's local fake model, which
// an Admin enables through the real Settings API (LLM_BASE_URL points at it).
test.describe.configure({ mode: "serial" });

async function ask(page: Page, question: string) {
  const answered = page.waitForResponse((r) => r.url().endsWith("/api/ask") && r.request().method() === "POST");
  await page.getByTestId("ask-input").fill(question);
  await page.getByTestId("ask-input").press("Enter");
  const res = await answered;
  expect(res.status()).toBe(200);
  const body = await res.json();
  await expect(page.getByTestId("ask-finding")).toBeVisible();
  return body as { mode: string; warning?: string | null; finding: string; citations: unknown[] };
}

const chip = (page: Page) => page.getByTestId("ask-source-chip");

async function setModel(enabled: boolean) {
  const admin = await apiAs("admin");
  const res = await admin.post("/api/settings/llm", {
    data: enabled
      ? { provider: "openai", apiKey: "e2e-fake-key-not-a-real-key", model: "e2e-chat-model" }
      : { clearAll: true },
  });
  expect(res.status()).toBe(200);
  const status = await res.json();
  expect(status.mode).toBe(enabled ? "live" : "demo");
  expect(JSON.stringify(status)).not.toContain("e2e-fake-key"); // the key is never echoed
  await admin.dispose();
}

test.beforeEach(async ({ page }) => {
  await prepare(page);
  await login(page, "viewer", "/app/ask");
});

test.afterAll(async () => {
  await setModel(false);
});

test("no model configured: the answer is labelled Demo, cites sources and shows no confidence", async ({ page }) => {
  const body = await ask(page, "needle breakage procedure");
  expect(body.mode).toBe("demo");
  await expect(chip(page)).toHaveAttribute("data-mode", "demo");
  await expect(chip(page)).toHaveText("Demo");
  await expect(page.getByTestId("ask-finding")).toContainText("Demo mode");
  await expect(page.getByTestId("ask-citations")).toBeVisible();
  await expect(page.getByText("Not a generated answer: no confidence score")).toBeVisible();
  await expect(page.getByTestId("ask-notice")).toHaveCount(0);
});

test("nothing relevant retrieved: the answer is labelled No source", async ({ page }) => {
  const body = await ask(page, "zzqx purple volcano tax");
  expect(body.mode).toBe("no_source");
  await expect(chip(page)).toHaveAttribute("data-mode", "no_source");
  await expect(chip(page)).toHaveText("No source");
  await expect(page.getByTestId("ask-finding")).toContainText("No confident source was found");
  await expect(page.getByTestId("ask-citations")).toHaveCount(0);
});

test("a model answer is labelled Live and shows its confidence", async ({ page }) => {
  await setModel(true);
  const before = await fakeLlmStats();

  const body = await ask(page, "needle breakage procedure");
  expect(body.mode).toBe("live");
  await expect(chip(page)).toHaveAttribute("data-mode", "live");
  await expect(chip(page)).toHaveText("Live");
  await expect(page.getByTestId("ask-finding")).toContainText(env.fakeLlmFinding);
  await expect(page.getByText(/^Confidence \d+%$/)).toBeVisible();
  await expect(page.getByTestId("ask-citations")).toBeVisible();

  const after = await fakeLlmStats();
  expect(after.chat).toBe(before.chat + 1); // answered by the local fake, nothing else
});

test("a failed model call falls back to Demo and says why", async ({ page }) => {
  await setModel(true);
  const before = await fakeLlmStats();

  const body = await ask(page, `needle breakage ${env.forceLlmFailure}`);
  expect(body.mode).toBe("demo");
  expect(body.warning).toContain("language model call failed");
  await expect(chip(page)).toHaveText("Demo");
  await expect(page.getByTestId("ask-notice")).toContainText("language model call failed");
  await expect(page.getByText("Not a generated answer: no confidence score")).toBeVisible();

  const after = await fakeLlmStats();
  expect(after.failures).toBe(before.failures + 1);
});

test("with the model switched off again, answers are Demo, not Live", async ({ page }) => {
  await setModel(false);
  const body = await ask(page, "AQL 1.5 sampling plan normal inspection");
  expect(body.mode).toBe("demo");
  await expect(chip(page)).toHaveText("Demo");
});
