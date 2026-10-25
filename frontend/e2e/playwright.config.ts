import { defineConfig, devices } from "@playwright/test";

// Run through `npm run e2e` (e2e/run-e2e.mjs), which starts the disposable
// database, the API, the fake model and the Next.js server, and passes their
// URLs and the test-only credentials in E2E_* variables.
const baseURL = process.env.E2E_BASE_URL;
if (!baseURL) {
  throw new Error("E2E_BASE_URL is not set: run the suite with `npm run e2e`, not `playwright test` directly.");
}

export default defineConfig({
  testDir: "./tests",
  // One worker: the tests share server state (approvals, LLM settings) and
  // each user has a single refresh-token slot.
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  reporter: [["list"]],
  outputDir: "./.artifacts/test-results",
  use: {
    baseURL,
    ...devices["Desktop Chrome"],
    locale: "en-US",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
});
