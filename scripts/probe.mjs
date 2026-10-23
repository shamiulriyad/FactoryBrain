// Probes every key route and reports the HTTP status.
// Helps confirm there are no compile errors anywhere in the app tree.

const ROUTES = [
  "/",
  "/onboarding/welcome",
  "/onboarding/profile",
  "/onboarding/connect",
  "/onboarding/understanding",
  "/onboarding/ready",
  "/app",
  "/app/ask",
  "/app/brain",
  "/app/insights",
  "/app/agents",
  "/app/agents/sales-analyst",
  "/app/approvals",
  "/app/activity",
  "/app/integrations",
  "/app/settings",
  "/app/architecture"
];

const base = process.env.BASE ?? "http://localhost:3000";

async function probe(path) {
  const url = base + path;
  try {
    const res = await fetch(url, { redirect: "manual" });
    const text = await res.text();
    // Heuristic for compile errors. Only trigger on strings that prove a real build/runtime failure:
    //   - "Failed to compile" (Next.js dev overlay)
    //   - "Module not found" / "SyntaxError" (real build failures)
    //   - `next-error` meta tag pointing to a non-not-found status
    // The bare word "Error:" appears in dev-tool CSS and Next source (false positive) so we exclude it.
    const status = res.status;
    const hasError =
      /Failed to compile|SyntaxError|Module not found/i.test(text) ||
      (status >= 500 && !/id="__next_error__"/.test(text));
    return { path, status, bytes: text.length, hasError };
  } catch (e) {
    return { path, status: "ERR", bytes: 0, hasError: true, error: String(e) };
  }
}

const results = await Promise.all(ROUTES.map(probe));
for (const r of results) {
  const flag = r.hasError ? "[COMPILE ERR]" : "ok";
  console.log(`${flag.padEnd(14)} ${String(r.status).padEnd(5)} ${String(r.bytes).padStart(7)} bytes  ${r.path}`);
}
const ok = results.every((r) => !r.hasError && (r.status === 200 || r.status === 307 || r.status === 308));
console.log(ok ? "\nAll routes compiled cleanly." : "\nSome routes failed.");
process.exit(ok ? 0 : 1);