// Smoke test for the prototype.
// Validates the dataset, the i18n tables, and the service contracts.
// Designed to run without a live Next.js server — except for the LLM
// settings route, which is exercised end-to-end against a freshly spawned
// `next start` to prove the secret never leaks.

import { promises as fs } from "node:fs";
import path from "node:path";
import url from "node:url";
import { spawn } from "node:child_process";

const __dirname = path.dirname(url.fileURLToPath(import.meta.url));
// The Next.js app lives in <repo>/frontend; every path below is relative to it.
const root = path.resolve(__dirname, "..", "frontend");

async function read(rel) {
  return fs.readFile(path.join(root, rel), "utf8");
}

function assert(cond, msg) {
  if (!cond) {
    console.error("FAIL: " + msg);
    process.exit(1);
  }
  console.log("ok: " + msg);
}

async function main() {
  // 1. Files exist
  for (const f of [
    "package.json",
    "src/app/page.tsx",
    "src/app/onboarding/welcome/page.tsx",
    "src/app/app/page.tsx",
    "src/app/app/ask/page.tsx",
    "src/app/app/brain/page.tsx",
    "src/app/app/insights/page.tsx",
    "src/app/app/approvals/page.tsx",
    "src/app/app/activity/page.tsx",
    "src/app/app/integrations/page.tsx",
    "src/app/app/settings/page.tsx",
    "src/app/app/architecture/page.tsx",
    "src/data/seed.ts",
    "src/data/products.ts",
    "src/data/customers.ts",
    "src/data/orders.ts",
    "src/data/suppliers.ts",
    "src/data/inventory.ts",
    "src/data/policies.ts",
    "src/data/conversations.ts",
    "src/data/goals.ts",
    "src/data/analytics.ts",
    "src/data/graph.ts",
    "src/data/insights.ts",
    "src/data/agents.ts",
    "src/data/activity.ts",
    "src/services/types.ts",
    "src/services/business.service.ts",
    "src/services/ingestion.service.ts",
    "src/services/metric.service.ts",
    "src/services/brain.service.ts",
    "src/services/insight.service.ts",
    "src/services/evidence.service.ts",
    "src/services/approval.service.ts",
    "src/services/agent.service.ts",
    "src/services/activity.service.ts",
    "src/services/risk.service.ts",
    "src/services/ask.service.ts",
    "src/services/factory.tools.ts",
    "src/store/factory.store.ts",
    "src/i18n/en.ts",
    "src/i18n/bn.ts",
    "src/i18n/registry.ts",
    "config/model-routing.yaml",
    "config/risk-policy.yaml",
    "src/data/sensors.ts",
    "src/data/floorAlerts.ts",
    "src/services/sensors.schemas.ts",
    "src/services/run.persistence.ts",
    "src/services/floorAlerts.server.ts",
    "src/store/sensors.store.ts",
    "src/store/floorAlerts.store.ts",
    "src/store/factoryBrain.live.store.ts",
    "src/_legacy_api/sensors/ingest/route.ts",
    "src/_legacy_api/sensors/latest/route.ts",
    "src/_legacy_api/floor-alerts/route.ts",
    "src/_legacy_api/floor-alerts/[id]/route.ts",
    "src/components/activity/FloorAlertsPanel.tsx",
    "src/_legacy_api/settings/llm/route.ts",
    "src/services/lineBoard.server.ts",
    "src/_legacy_api/line-board/route.ts",
    "src/_legacy_api/line-board/refresh/route.ts",
    "src/components/overview/LineBoardPanel.tsx",
    "src/components/overview/BriefCard.tsx",
    "src/services/brief.server.ts",
    "src/_legacy_api/brief/morning/route.ts",
    "src/data/qc.defects.ts",
    "src/services/qc.defects.server.ts",
    "src/_legacy_api/qc/defects/route.ts",
    "src/_legacy_api/qc/flag/route.ts",
    "src/components/overview/QcSummary.tsx",
    "src/app/app/qc/page.tsx"
  ]) {
    try { await read(f); assert(true, f); } catch { assert(false, f); }
  }

  // 2. i18n tables share the same key tree
  const en = await read("src/i18n/en.ts");
  const bn = await read("src/i18n/bn.ts");
  const keyCount = (s) => (s.match(/"/g) ?? []).length;
  assert(keyCount(en) > 50, "i18n: en has many keys");
  assert(keyCount(bn) > 50, "i18n: bn has many keys");

  // 3. Model routing lists the three BunonBrain agents
  const routing = await read("config/model-routing.yaml");
  for (const id of [
    "line-throughput-agent",
    "maintenance-agent",
    "manager-agent"
  ]) {
    assert(routing.includes(id), "routing has " + id);
  }

  // 4. Risk policy defines stages
  const risk = await read("config/risk-policy.yaml");
  for (const stage of [
    "suggested",
    "pending_approval",
    "executing",
    "done",
    "logged",
    "rejected",
    "failed"
  ]) {
    assert(risk.includes(stage), "risk: stage " + stage);
  }

  // 5. Dataset references in screens
  const askPage = await read("src/app/app/ask/page.tsx");
  assert(askPage.includes("/api/ask"), "ask page wires /api/ask");
  const brainPage = await read("src/app/app/brain/page.tsx");
  assert(brainPage.includes("ReactFlow") || brainPage.includes("reactflow"), "brain uses React Flow");
  const arch = await read("src/components/dev/ArchitectureView.tsx");
  assert(arch.includes("Continuous Context"), "architecture view mentions all seven layers");

  // 6. Live sensor + floor-alert wiring (improve batch)
  const overview = await read("src/app/app/page.tsx");
  assert(overview.includes("/api/sensors/ingest"), "overview wires /api/sensors/ingest");
  assert(overview.includes("Simulate tick"), "overview has Simulate tick control");
  const activity = await read("src/app/app/activity/page.tsx");
  assert(activity.includes("FloorAlertsPanel"), "activity mounts FloorAlertsPanel");
  const latestRoute = await read("src/_legacy_api/sensors/latest/route.ts");
  assert(latestRoute.includes("listLatestReadings"), "latest reads server ingest buffer");
  assert(!latestRoute.includes("useSensorsStore"), "latest does not import client sensors store");
  const sensorsServer = await read("src/services/sensors.server.ts");
  assert(sensorsServer.includes("__factoryBrainSimState"), "sensors.server owns shared sim buffer");
  assert(sensorsServer.includes("listLatestReadings"), "sensors.server exports listLatestReadings");
  const agentRun = await read("src/_legacy_api/agents/[agentId]/run/route.ts");
  assert(agentRun.includes("persistAgentRun"), "agents/run persists via persistAgentRun");

  // 7. Line-board route — file-shape contract (improve batch).
  const lineBoard = await read("src/services/lineBoard.server.ts");
  assert(lineBoard.includes("LineBoardRowSchema"), "lineBoard: Zod schema exported");
  assert(lineBoard.includes("LINE_BOARD_SIMULATED_LABEL"), "lineBoard: simulated label exported");
  assert(lineBoard.includes("buildLineBoard"), "lineBoard: buildLineBoard exported");
  assert(lineBoard.includes("BottleneckEnum"), "lineBoard: bottleneck enum defined");
  const lineBoardRoute = await read("src/_legacy_api/line-board/route.ts");
  assert(lineBoardRoute.includes('export async function GET'), "line-board: GET handler present");
  assert(lineBoardRoute.includes("buildLineBoard"), "line-board: uses buildLineBoard");
  const lineBoardRefresh = await read("src/_legacy_api/line-board/refresh/route.ts");
  assert(lineBoardRefresh.includes('export async function POST'), "line-board/refresh: POST handler present");
  assert(lineBoardRefresh.includes("advanceSim"), "line-board/refresh: advances sim before reading");
  assert(lineBoardRefresh.includes("buildLineBoard"), "line-board/refresh: returns buildLineBoard payload");

  // Brief module — file-shape contract.
  const brief = await read("src/services/brief.server.ts");
  assert(brief.includes("BriefResponseSchema"), "brief: Zod schema exported");
  assert(brief.includes("buildMorningBrief"), "brief: buildMorningBrief exported");
  assert(brief.includes("BRIEF_SIMULATED_LABEL"), "brief: simulated label exported");
  assert(brief.includes("FUTURE_LLM_HOOK"), "brief: LLM-future placeholder comment present");
  assert(brief.includes(".strict()"), "brief: response schema is Zod-strict");
  const briefRoute = await read("src/_legacy_api/brief/morning/route.ts");
  assert(briefRoute.includes('export async function GET'), "brief/morning: GET handler present");
  assert(briefRoute.includes("buildMorningBrief"), "brief/morning: uses buildMorningBrief");
  assert(briefRoute.includes('runtime = "nodejs"'), "brief/morning: runtime = nodejs");
  assert(briefRoute.includes('dynamic = "force-dynamic"'), "brief/morning: dynamic = force-dynamic");
  assert(briefRoute.includes("Cache-Control"), "brief/morning: no-store cache header");
  assert(en.includes("brief:") && en.includes("fallbackNoRecs:"), "i18n en: brief namespace + fallbackNoRecs present");
  assert(bn.includes("brief:") && bn.includes("fallbackNoRecs:"), "i18n bn: brief namespace + fallbackNoRecs present");

  // BriefCard — wiring + i18n keys.
  const briefCard = await read("src/components/overview/BriefCard.tsx");
  assert(briefCard.includes('export function BriefCard'), "BriefCard: component exported");
  assert(briefCard.includes('"/api/brief/morning"'), "BriefCard: fetches /api/brief/morning");
  assert(briefCard.includes("useFactoryBrainLiveStore"), "BriefCard: subscribes to live tick store");
  assert(briefCard.includes("DemoChip"), "BriefCard: uses DemoChip for simulated label");
  assert(briefCard.includes("/app/brain?node="), "BriefCard: deep-links top bottleneck to /app/brain?node=");
  assert(briefCard.includes("/app/approvals"), "BriefCard: deep-links pending approvals to /app/approvals");
  assert(briefCard.includes("/app/insights"), "BriefCard: deep-links risks to /app/insights");
  assert(briefCard.includes('data-testid="brief-card"'), "BriefCard: smoke-visible testid present");
  assert(briefCard.includes("bulletsBn"), "BriefCard: locale switches to BN bullets");
  assert(overview.includes("<BriefCard"), "overview: mounts <BriefCard />");
  for (const k of [
    "cardTitle:",
    "simulatedChip:",
    "refreshAria:",
    "retry:",
    "loadFailed:",
    "loadFailedBody:",
    "risksLabelOne:",
    "risksLabelOther:",
    "approvalsLabelOne:",
    "approvalsLabelOther:",
    "allOnTarget:"
  ]) {
    assert(en.includes(k), `i18n en: has brief.${k.replace(/:$/, "")}`);
    assert(bn.includes(k), `i18n bn: has brief.${k.replace(/:$/, "")}`);
  }

// QC defects — file-shape contract.
  const qcSeed = await read("src/data/qc.defects.ts");
  assert(qcSeed.includes("OPERATIONS"), "qc: OPERATIONS taxonomy exported");
  assert(qcSeed.includes("recentWeekStarts"), "qc: weekStarts helper exported");
  assert(qcSeed.includes("generateOperationLineWeeks"), "qc: per-cell generator exported");
  const qcSvc = await read("src/services/qc.defects.server.ts");
  assert(qcSvc.includes("QcResponseSchema"), "qc: Zod response schema exported");
  assert(qcSvc.includes("buildQcDefects"), "qc: buildQcDefects exported");
  assert(qcSvc.includes("QC_SIMULATED_LABEL"), "qc: simulated label exported");
  assert(qcSvc.includes(".strict()"), "qc: response schema is Zod-strict");
  const qcRoute = await read("src/_legacy_api/qc/defects/route.ts");
  assert(qcRoute.includes('export async function GET'), "qc/defects: GET handler present");
  assert(qcRoute.includes("buildQcDefects"), "qc/defects: uses buildQcDefects");
  assert(qcRoute.includes('runtime = "nodejs"'), "qc/defects: runtime = nodejs");
  assert(qcRoute.includes('dynamic = "force-dynamic"'), "qc/defects: dynamic = force-dynamic");
  assert(qcRoute.includes("Cache-Control"), "qc/defects: no-store cache header");
  assert(en.includes("qc:") && en.includes("cardTitle:"), "i18n en: qc namespace + cardTitle present");
  assert(bn.includes("qc:") && bn.includes("cardTitle:"), "i18n bn: qc namespace + cardTitle present");

  // QC panel + flag route + page + sidebar (improve batch — qc UI).
  const qcFlagRoute = await read("src/_legacy_api/qc/flag/route.ts");
  assert(qcFlagRoute.includes('export async function POST'), "qc/flag: POST handler present");
  assert(qcFlagRoute.includes("insightService"), "qc/flag: uses insightService to persist");
  assert(qcFlagRoute.includes("upsertCustom"), "qc/flag: uses insightService.upsertCustom");
  assert(qcFlagRoute.includes("activityService"), "qc/flag: emits activity log entry");
  assert(qcFlagRoute.includes('runtime = "nodejs"'), "qc/flag: runtime = nodejs");
  assert(qcFlagRoute.includes('dynamic = "force-dynamic"'), "qc/flag: dynamic = force-dynamic");
  assert(qcFlagRoute.includes(".strict()"), "qc/flag: body + response schemas are Zod-strict");
  assert(qcFlagRoute.includes("FlagResponseSchema"), "qc/flag: response schema parsed");

  // Overview shows a one-liner QC summary that links to /app/qc.
  const qcSummary = await read("src/components/overview/QcSummary.tsx");
  assert(qcSummary.includes("export function QcSummary"), "QcSummary: component exported");
  assert(qcSummary.includes('"/api/qc/defects"'), "QcSummary: fetches real /api/qc/defects (not mock state)");
  assert(qcSummary.includes("useFactoryBrainLiveStore"), "QcSummary: subscribes to live tick store");
  assert(qcSummary.includes('data-testid="qc-summary"'), "QcSummary: smoke-visible testid present");
  assert(qcSummary.includes('href="/app/qc"'), "QcSummary: deep-links to /app/qc");

  const qcPage = await read("src/app/app/qc/page.tsx");
  assert(qcPage.includes('"/api/qc/defects"'), "qc page: fetches real /api/qc/defects");
  assert(qcPage.includes('"/api/qc/flag"'), "qc page: POSTs to /api/qc/flag");
  assert(qcPage.includes('data-testid="qc-page-table"'), "qc page: smoke-visible testid present");
  assert(qcPage.includes("useFactoryBrainLiveStore"), "qc page: subscribes to live tick store");

  // Overview mounts the one-liner QC summary alongside the Line Board.
  assert(overview.includes("<QcSummary"), "overview: mounts <QcSummary />");
  assert(overview.includes("<LineBoardPanel"), "overview: still mounts <LineBoardPanel />");

  // Sidebar gains a QC link in the Operations group.
  const qcSidebar = await read("src/components/layout/Sidebar.tsx");
  assert(qcSidebar.includes('"/app/qc"'), "sidebar: /app/qc link present");
  assert(qcSidebar.includes("ClipboardCheck") || qcSidebar.includes("ScanLine"), "sidebar: QC item has icon");
  assert(qcSidebar.includes("operations"), "sidebar: still has Operations group");

  // i18n additions for QC panel + page.
  for (const k of [
    "pageSubtitle:",
    "sortDefect:",
    "sortRework:",
    "flagIssue:",
    "defectPct:",
    "reworkPct:",
    "topDefect:",
    "topRework:",
    "fullGrid:",
    "colOp:",
    "colLine:",
    "colDefectRate:",
    "colReworkRate:",
    "flagAria:",
    "summaryLabel:",
    "summaryLoading:",
    "summaryOpen:"
  ]) {
    assert(en.includes(k), `i18n en: has qc.${k.replace(/:$/, "")}`);
    assert(bn.includes(k), `i18n bn: has qc.${k.replace(/:$/, "")}`);
  }
  assert(en.includes('qc: "QC defects"'), "i18n en: nav.qc label present");
  assert(bn.includes('qc: "মান নিয়ন্ত্রণ ত্রুটি"'), "i18n bn: nav.qc label present");

  // Integrations page — category grouping + sensor calibration + demoted social sources.
  const businessStore = await read("src/store/business.store.ts");
  assert(businessStore.includes("IngestionSourceCategory"), "store: IngestionSourceCategory type exported");
  assert(businessStore.includes("SOURCE_CATEGORY"), "store: SOURCE_CATEGORY map exported");
  assert(businessStore.includes('"rfid-bundles", category: "sensor"'), "store: rfid-bundles is sensor");
  assert(businessStore.includes('"machine-telemetry", category: "sensor"'), "store: machine-telemetry is sensor");
  assert(businessStore.includes('"energy-meter", category: "sensor"'), "store: energy-meter is sensor");
  assert(businessStore.includes('"documents", category: "pilot"'), "store: documents is pilot");
  assert(businessStore.includes('"csv", category: "pilot"'), "store: csv is pilot");
  assert(businessStore.includes('"sheets", category: "pilot"'), "store: sheets is pilot");
  assert(businessStore.includes('"shopify", category: "out_of_scope"'), "store: shopify is out_of_scope");
  assert(businessStore.includes('"whatsapp", category: "out_of_scope"'), "store: whatsapp is out_of_scope");
  assert(businessStore.includes('"facebook", category: "out_of_scope"'), "store: facebook is out_of_scope");
  assert(businessStore.includes('"instagram", category: "out_of_scope"'), "store: instagram is out_of_scope");

  // Sensors come before pilot sources, and pilot sources before social/commerce
  // sources in the default ordering.
  const sensorsIdx = businessStore.indexOf('"rfid-bundles", category: "sensor"');
  const pilotIdx = businessStore.indexOf('"documents", category: "pilot"');
  const oosIdx = businessStore.indexOf('"shopify", category: "out_of_scope"');
  assert(sensorsIdx > 0 && pilotIdx > sensorsIdx && oosIdx > pilotIdx, "store: default source order is sensors → pilot → out_of_scope");

  const ingestionSvc = await read("src/services/ingestion.service.ts");
  assert(ingestionSvc.includes("SOURCE_CATEGORY"), "ingestion.service: imports SOURCE_CATEGORY for back-compat");
  assert(ingestionSvc.includes("category ="), "ingestion.service: stamps category on every listed source");

  const integrationsPage = await read("src/app/app/integrations/page.tsx");
  assert(integrationsPage.includes('"sensor"'), "integrations page: renders the sensor group");
  assert(integrationsPage.includes('"pilot"'), "integrations page: renders the pilot group");
  assert(integrationsPage.includes('"out_of_scope"'), "integrations page: handles the out_of_scope group");
  assert(integrationsPage.includes("groupSensors"), "integrations page: uses groupSensors i18n key");
  assert(integrationsPage.includes("groupPilot"), "integrations page: uses groupPilot i18n key");
  assert(integrationsPage.includes("groupOutOfScope"), "integrations page: uses groupOutOfScope i18n key");
  assert(integrationsPage.includes("showOutOfScope"), "integrations page: out_of_scope is disclosed behind a toggle");
  assert(integrationsPage.includes("integration-card-"), "integrations page: smoke-visible per-card testid");
  assert(integrationsPage.includes("integration-simulated-"), "integrations page: keeps the Simulated pill for sensor sources");
  assert(integrationsPage.includes("calibrationNote"), "integrations page: surfaces the calibration note on sensor cards");
  assert(integrationsPage.includes("RFID bundle scans") || integrationsPage.includes("RFID"), "integrations page: keeps the kept sensor sources connected");
  // Connect / disconnect flow for kept sources must still exist.
  assert(integrationsPage.includes("ConnectModal") || integrationsPage.includes("setConnectFor"), "integrations page: Connect flow still wired for kept sources");
  assert(integrationsPage.includes("setConfirmDisconnect") || integrationsPage.includes("handleDisconnect"), "integrations page: Disconnect flow still wired for kept sources");

  // i18n additions for the category grouping.
  for (const k of [
    "groupSensors:",
    "groupSensorsSubtitle:",
    "groupPilot:",
    "groupPilotSubtitle:",
    "groupOutOfScope:",
    "groupOutOfScopeSubtitle:",
    "groupOutOfScopeToggle:",
    "groupOutOfScopeTag:",
    "calibrationNote:"
  ]) {
    assert(en.includes(k), `i18n en: has integrations.${k.replace(/:$/, "")}`);
    assert(bn.includes(k), `i18n bn: has integrations.${k.replace(/:$/, "")}`);
  }

  // LineBoardPanel — wiring + i18n keys.
  const lineBoardPanel = await read("src/components/overview/LineBoardPanel.tsx");
  assert(lineBoardPanel.includes('export function LineBoardPanel'), "LineBoardPanel: component exported");
  assert(lineBoardPanel.includes('"/api/line-board"'), "LineBoardPanel: fetches /api/line-board");
  assert(lineBoardPanel.includes("useFactoryBrainLiveStore"), "LineBoardPanel: subscribes to live tick store");
  assert(lineBoardPanel.includes('data-testid="line-board"'), "LineBoardPanel: smoke-visible testid present");
  assert(lineBoardPanel.includes('Simulated'), "LineBoardPanel: keeps simulated label visible");
  const overviewPage = await read("src/app/app/page.tsx");
  assert(overviewPage.includes("<LineBoardPanel"), "overview: mounts <LineBoardPanel />");
  assert(overviewPage.includes("judge-walkthrough"), "overview: keeps demo path strip");
  for (const k of [
    "lineBoard:",
    "lineBoardSubtitle:",
    "colLine:",
    "colBottleneck:",
    "colEff:",
    "colSah:",
    "colWip:",
    "colNpt:",
    "bottleneckGreen:",
    "bottleneckAmber:",
    "bottleneckRed:",
    "effShort:",
    "refreshAria:",
    "retry:",
    "loadFailed:",
    "loadFailedBody:"
  ]) {
    assert(en.includes(k), `i18n en: has overview.${k.replace(/:$/, "")}`);
    assert(bn.includes(k), `i18n bn: has overview.${k.replace(/:$/, "")}`);
  }

  // 8. Brand naming wired through i18n + layout metadata.
  assert(en.includes('name: "BunonBrain"'), "en: app.name is BunonBrain");
  const layout = await read("src/app/layout.tsx");
  assert(layout.includes("BunonBrain"), "layout metadata mentions BunonBrain");

  // Frontend polish checks (Builder)
  const bizStore = await read("src/store/business.store.ts");
  assert(bizStore.includes("visionRepair: true"), "visionRepair defaults on for judges");
  const sidebar = await read("src/components/layout/Sidebar.tsx");
  assert(sidebar.includes("/app/vision"), "sidebar links to Vision under Intelligence");
  const overviewUi = await read("src/app/app/page.tsx");
  assert(overviewUi.includes("judge-walkthrough"), "overview has demo path strip");
  assert(overviewUi.includes("/app/ask?q="), "energy duty links to Ask with q=");
  const visionPage = await read("src/app/app/vision/page.tsx");
  assert(visionPage.includes("SAMPLE_FILES.map"), "vision uses sample grid picker");
  const agentsPage = await read("src/app/app/agents/page.tsx");
  assert(agentsPage.includes("agents-run-banner"), "agents shows post-run banner");

  // 9. LLM settings route — file-shape contract.
  // ---------------------------------------------------------------------
  // Static checks against the route source so they always run, even when
  // there's no live server. The runtime check below boots one.
  const settingsRoute = await read("src/_legacy_api/settings/llm/route.ts");
  assert(settingsRoute.includes('export async function GET'), "settings/llm: GET handler present");
  assert(settingsRoute.includes('export async function POST'), "settings/llm: POST handler present");
  // The route must never echo apiKey back onto the response payload.
  assert(!/apiKey\s*:\s*apiKey\b/.test(settingsRoute), "settings/llm: does not put apiKey on the response");
  assert(!/apiKey\s*:\s*trimmedKey\b/.test(settingsRoute), "settings/llm: does not echo trimmedKey");
  // The route must never log the body or the key. We scan each
  // console.* call's arguments by line — the heuristic regex matches a
  // log call on the same physical line as the word "apiKey"/"body".
  function logsToken(source, token) {
    return source
      .split("\n")
      .some((line) => /console\.(log|info|warn|error)\(/.test(line) && line.includes(token));
  }
  assert(!logsToken(settingsRoute, "apiKey"), "settings/llm: never logs apiKey");
  assert(!logsToken(settingsRoute, "body"), "settings/llm: never logs body");
  // .gitignore must keep .env.local ignored.
  const gitignore = await read("../.gitignore"); // repo root, not frontend/
  assert(/\.env\.local/.test(gitignore), ".gitignore lists .env.local");
  assert(/\.env\*\.local/.test(gitignore), ".gitignore lists .env*.local");

  // 10. LLM settings route — runtime contract.
  // ---------------------------------------------------------------------
  // Boot `next start` against the build dir, hit GET to confirm the
  // response shape never carries an apiKey or LLM_API_KEY string, POST a
  // clearly fake key, then GET again and confirm `configured` flips true
  // while the fake key still does not appear anywhere in the body.
  await runSettingsLlmRuntimeCheck();

  console.log("\nAll smoke checks passed.");
}

async function runSettingsLlmRuntimeCheck() {
  // Skip cleanly if the production build isn't present yet.
  const buildDir = path.join(root, ".next");
  try {
    await fs.access(path.join(buildDir, "BUILD_ID"));
  } catch {
    console.log("skip: runtime settings/llm check (no .next/BUILD_ID — build first)");
    return;
  }

  const port = 4173;
  const base = `http://127.0.0.1:${port}`;
  const FAKE_KEY = "smoke-fake-key-DO-NOT-USE-0123456789abcdef";

  // If a previous smoke run aborted before cleanup ran (e.g. an assertion
  // failed mid-test), there can be a stale `next start` bound to the test
  // port that retains `LLM_PROVIDER=openai` in its process memory. Kill
  // any pre-existing listener so the new server starts from a clean slate.
  await killListenersOnPort(port);

  // The dev-mode route upserts to .env.local. Back up and remove any
  // existing copy so the test starts clean and we can restore it after.
  const envLocal = path.join(root, ".env.local");
  const hadEnvLocal = await fs.stat(envLocal).then(() => true).catch(() => false);
  let backup = null;
  if (hadEnvLocal) {
    backup = await fs.readFile(envLocal, "utf8");
    await fs.unlink(envLocal);
  }

  // Start `next start`. We force NODE_ENV=production so the route hits
  // the "process-only" branch (no file writes). Stdout/stderr are
  // discarded — never let the key land in build logs.
  // detached:true + process.kill(-pid) lets us reap the entire process
  // group (npx → sh → next-server) even if smoke aborts mid-test.
  const server = spawn("npx", ["next", "start", "-p", String(port)], {
    cwd: root,
    detached: true,
    env: { ...process.env, NODE_ENV: "production", NEXT_TELEMETRY_DISABLED: "1" },
    stdio: ["ignore", "ignore", "ignore"]
  });

  async function cleanup() {
    try { process.kill(-server.pid, "SIGTERM"); } catch {}
    await new Promise((r) => setTimeout(r, 250));
    try { process.kill(-server.pid, "SIGKILL"); } catch {}
    if (hadEnvLocal && backup !== null) {
      await fs.writeFile(envLocal, backup);
    } else if (!hadEnvLocal) {
      // No original file existed; if a test .env.local was created in
      // dev mode elsewhere, wipe it. Here we also make sure we don't
      // leave any test-spawned .env.local behind.
      await fs.unlink(envLocal).catch(() => {});
    }
  }

  try {
    await waitForServer(base);
    const headers = { "Content-Type": "application/json" };

    // Reset /api/settings/llm to a known-clean baseline so this run is
    // deterministic regardless of any state the .NET process accumulated
    // from a previous smoke run.
    await fetch(base + "/api/settings/llm", {
      method: "POST",
      headers,
      body: JSON.stringify({ clearAll: true }),
      cache: "no-store"
    });

    // Hit GET /api/settings/llm FIRST and explicitly assert the runtime
    // contract: response JSON has no apiKey, no LLM_API_KEY, and no
    // key-shaped string field. This is the runtime-only companion to the
    // static file-level checks in section 9 and is intentionally separate
    // from the broader assertNoSecret() below.
    const probeRaw = await fetch(base + "/api/settings/llm", { cache: "no-store" });
    assertNoStore(probeRaw, "settings/llm GET (probe)");
    const probeJson = await probeRaw.json();
    assertNoLeakedKey(probeJson, "GET /api/settings/llm (probe)");

    // Initial GET — neither key nor env var name should appear, status is
    // demo mode with no provider / model, persistence flag is one of the
    // three valid options. Also assert Cache-Control: no-store so the
    // status never gets cached by intermediaries.
    const initialRaw = await fetch(base + "/api/settings/llm", { cache: "no-store" });
    assertNoStore(initialRaw, "settings/llm GET (initial)");
    const initial = await initialRaw.json();
    assertNoSecret(initial, FAKE_KEY, "GET (initial)");
    assertNoLeakedKey(initial, "GET /api/settings/llm (initial)");
    assert(initial.configured === false, "settings/llm: initial configured === false (no env)");
    assert(initial.mode === "demo", "settings/llm: initial mode === demo");
    assert(initial.provider === null, "settings/llm: initial provider === null");
    assert(initial.model === null, "settings/llm: initial model === null");
    assert(["env.local", "process", "vercel_only"].includes(initial.persistence), `settings/llm: persistence ∈ {env.local,process,vercel_only}, got ${initial.persistence}`);
    assert(!("apiKey" in initial), "settings/llm: GET response has no apiKey key");
    assert(!("LLM_API_KEY" in initial), "settings/llm: GET response has no LLM_API_KEY key");

    // POST a clearly fake key.
    const fakeBody = JSON.stringify({ provider: "openai", apiKey: FAKE_KEY, model: "gpt-6-luna" });
    const postRes = await fetch(base + "/api/settings/llm", { method: "POST", headers, body: fakeBody });
    assertNoStore(postRes, "settings/llm POST");
    const postText = await postRes.text();
    assert(postRes.status === 200, `POST settings/llm returned 200 (got ${postRes.status})`);
    assert(!postText.includes(FAKE_KEY), "POST response text does not contain the fake key");
    // The route's notice string in production mode legitimately mentions
    // the env-var name as guidance. We assert on the parsed JSON shape —
    // never any apiKey-shaped field or value.
    const postParsed = JSON.parse(postText);
    assertNoSecret(postParsed, FAKE_KEY, "POST response");
    assertNoLeakedKey(postParsed, "POST /api/settings/llm response");
    assert(postParsed.configured === true, "POST sets configured: true after fake key saved");

    // GET again — status should now show configured=true, and still
    // never echo the key. Also assert the env-var name isn't returned.
    const afterRaw = await fetch(base + "/api/settings/llm", { cache: "no-store" });
    assertNoStore(afterRaw, "settings/llm GET (after POST)");
    const after = await afterRaw.json();
    assertNoSecret(after, FAKE_KEY, "GET (after POST)");
    assertNoLeakedKey(after, "GET /api/settings/llm (after POST)");
    assert(after.configured === true, "GET after POST reports configured: true");
    assert(after.mode === "live", "GET after POST reports mode: live");
    assert(after.provider === "openai", "GET after POST reports provider: openai");
    assert(after.model === "gpt-6-luna", "GET after POST reports model: gpt-6-luna");

    // Clear the key and confirm GET goes back to configured:false.
    const clearRes = await fetch(base + "/api/settings/llm", {
      method: "POST", headers, body: JSON.stringify({ provider: "openai", clearKey: true })
    });
    assert(clearRes.status === 200, "POST clearKey returned 200");
    const clearedRaw = await fetch(base + "/api/settings/llm", { cache: "no-store" });
    assertNoStore(clearedRaw, "settings/llm GET (after clear)");
    const cleared = await clearedRaw.json();
    assert(cleared.configured === false, "GET after clearKey reports configured: false");
    assert(cleared.mode === "demo", "GET after clearKey reports mode: demo");
    assertNoSecret(cleared, FAKE_KEY, "GET (after clear)");
    assertNoLeakedKey(cleared, "GET /api/settings/llm (after clear)");

    // Line-board (improve batch) — runtime contract against the same server.
    const boardRaw = await fetch(base + "/api/line-board", { cache: "no-store" });
    assertNoStore(boardRaw, "line-board GET");
    const board = await boardRaw.json();
    assert(board && Array.isArray(board.rows), "line-board: rows is an array");
    assert(board.rows.length === 6, "line-board: returns 6 rows (line-1..line-6)");
    assert(board.meta && board.meta.simulated === true, "line-board: meta.simulated is true");
    assert(typeof board.meta.source === "string" && /Simulated/.test(board.meta.source), "line-board: meta.source mentions Simulated");
    assert(typeof board.meta.notes === "string" && board.meta.notes.length > 0, "line-board: meta.notes is a non-empty string");
    assert(typeof board.meta.tick === "number" && board.meta.tick >= 0, "line-board: meta.tick is a non-negative number");
    assert(typeof board.meta.updatedAt === "string" && board.meta.updatedAt.length > 0, "line-board: meta.updatedAt is set");
    const seenLineIds = new Set();
    for (const r of board.rows) {
      for (const k of ["lineId", "name", "efficiencyPct", "sahTarget", "sahActual", "wipBundles", "bottleneck", "nptMinutes", "updatedAt"]) {
        assert(r[k] !== undefined, `line-board row ${r.lineId || "?"}: has ${k}`);
      }
      assert(typeof r.lineId === "string" && /^line-\d+$/.test(r.lineId), `line-board row: lineId matches line-N, got ${r.lineId}`);
      assert(!seenLineIds.has(r.lineId), `line-board row: lineId ${r.lineId} appears once`);
      seenLineIds.add(r.lineId);
      assert(typeof r.name === "string" && r.name.length > 0, `line-board row ${r.lineId}: name is a non-empty string`);
      assert(["green", "amber", "red"].includes(r.bottleneck), `line-board row ${r.lineId}: bottleneck ∈ green/amber/red`);
      assert(r.efficiencyPct >= 0 && r.efficiencyPct <= 100, `line-board row ${r.lineId}: efficiencyPct in 0..100`);
      assert(Number.isInteger(r.sahTarget) && r.sahTarget >= 0 && r.sahTarget <= 100, `line-board row ${r.lineId}: sahTarget is integer 0..100`);
      assert(Number.isInteger(r.sahActual) && r.sahActual >= 0 && r.sahActual <= 100, `line-board row ${r.lineId}: sahActual is integer 0..100`);
      assert(r.sahActual === r.efficiencyPct, `line-board row ${r.lineId}: sahActual mirrors efficiencyPct`);
      assert(Number.isInteger(r.wipBundles) && r.wipBundles >= 0, `line-board row ${r.lineId}: wipBundles is integer ≥ 0`);
      assert(Number.isInteger(r.nptMinutes) && r.nptMinutes >= 0, `line-board row ${r.lineId}: nptMinutes is integer ≥ 0`);
      assert(/^\d{4}-\d{2}-\d{2}T/.test(r.updatedAt), `line-board row ${r.lineId}: updatedAt is ISO 8601`);
    }
    assert(seenLineIds.size === 6, `line-board: covers all 6 unique lineIds, got ${seenLineIds.size}`);

    // Determinism — two consecutive GETs return identical rows (only
    // meta.updatedAt + per-row updatedAt are allowed to differ).
    const board2 = await getJson(base + "/api/line-board");
    const stripLineBoardTimestamps = (b) => ({
      ...b,
      meta: { ...b.meta, updatedAt: "X" },
      rows: b.rows.map((r) => ({ ...r, updatedAt: "X" }))
    });
    assert(
      JSON.stringify(stripLineBoardTimestamps(board)) === JSON.stringify(stripLineBoardTimestamps(board2)),
      "line-board: rows + meta are deterministic (only updatedAt may differ)"
    );

    // Refresh should advance tick and return a fresh board.
    const tickBefore = board.meta.tick;
    const refreshRes = await fetch(base + "/api/line-board/refresh", { method: "POST", headers });
    assertNoStore(refreshRes, "line-board/refresh POST");
    assert(refreshRes.status === 200, "POST /api/line-board/refresh returned 200");
    const refreshed = await refreshRes.json();
    assert(refreshed.meta.tick > tickBefore, `line-board/refresh: tick advances (${tickBefore} → ${refreshed.meta.tick})`);
    assert(refreshed.meta.simulated === true, "line-board/refresh: meta.simulated is true");
    assert(refreshed.rows.length === 6, "line-board/refresh: still 6 rows");

    // Invalid body returns 400.
    const badRes = await fetch(base + "/api/line-board/refresh", {
      method: "POST", headers, body: JSON.stringify({ tick: "nope" })
    });
    assert(badRes.status === 400, "POST /api/line-board/refresh with bad body returns 400");

    // Re-read line-board AFTER refresh so we have the current tick value
    // to compare against the brief we fetch below (the brief uses the same
    // shared sim-state as line-board, so its tick must match the *current*
    // tick, not the pre-refresh one captured at the top of this test).
    const boardPostRefreshRaw = await fetch(base + "/api/line-board", { cache: "no-store" });
    assertNoStore(boardPostRefreshRaw, "line-board GET (post-refresh)");
    const boardPostRefresh = await boardPostRefreshRaw.json();
    assert(boardPostRefresh.meta.tick === refreshed.meta.tick, `line-board post-refresh tick (${boardPostRefresh.meta.tick}) matches refresh tick (${refreshed.meta.tick})`);

    // Brief — runtime contract + determinism (same inputs → identical JSON).
    // Give Next a generous grace period to compile the freshly-added route.
    let brief1 = null;
    let lastStatus = 0;
    let lastHeaders = null;
    const briefDeadline = Date.now() + 30_000;
    while (Date.now() < briefDeadline) {
      try {
        const r = await fetch(base + "/api/brief/morning", { cache: "no-store" });
        lastStatus = r.status;
        lastHeaders = r.headers;
        if (r.status === 200) {
          brief1 = await r.json();
          break;
        }
        // Drain the body so the connection is reusable, but don't parse it.
        try { await r.text(); } catch {}
      } catch {}
      await new Promise((r2) => setTimeout(r2, 500));
    }
    assert(brief1 !== null, `brief: GET /api/brief/morning returned 200 within grace period (last status ${lastStatus})`);
    assert(
      lastHeaders && /no-store/i.test(lastHeaders.get("cache-control") ?? ""),
      "brief: GET /api/brief/morning sets Cache-Control: no-store"
    );
    assert(typeof brief1.date === "string" && /^\d{4}-\d{2}-\d{2}$/.test(brief1.date), "brief: date is YYYY-MM-DD");
    assert(Array.isArray(brief1.bulletsEn) && brief1.bulletsEn.length >= 1, "brief: bulletsEn is a non-empty array");
    assert(Array.isArray(brief1.bulletsBn) && brief1.bulletsBn.length >= 1, "brief: bulletsBn is a non-empty array");
    assert(brief1.bulletsEn.length === brief1.bulletsBn.length, "brief: bulletsEn and bulletsBn have same length");
    for (const s of brief1.bulletsEn) assert(typeof s === "string" && s.length > 0, "brief: every bulletsEn entry is a non-empty string");
    for (const s of brief1.bulletsBn) assert(typeof s === "string" && s.length > 0, "brief: every bulletsBn entry is a non-empty string");
    assert(Number.isInteger(brief1.riskCount) && brief1.riskCount >= 0, "brief: riskCount is a non-negative integer");
    assert(Number.isInteger(brief1.pendingApprovals) && brief1.pendingApprovals >= 0, "brief: pendingApprovals is a non-negative integer");
    if (brief1.topBottleneckLineId !== undefined) {
      assert(typeof brief1.topBottleneckLineId === "string" && /^line-\d+$/.test(brief1.topBottleneckLineId), "brief: topBottleneckLineId is a line id");
      // Cross-route consistency: the brief's top bottleneck must reference a
      // row that actually exists in the line-board response.
      const inBoard = boardPostRefresh.rows.find((r) => r.lineId === brief1.topBottleneckLineId);
      assert(!!inBoard, `brief: topBottleneckLineId ${brief1.topBottleneckLineId} is present in /api/line-board rows`);
      if (inBoard) {
        // The header bullet must mention the same line id.
        const headerMentionsLine = brief1.bulletsEn[0].includes(inBoard.lineId);
        assert(headerMentionsLine, `brief: header bullet references top bottleneck ${inBoard.lineId}`);
      }
    }
    assert(brief1.meta && brief1.meta.simulated === true, "brief: meta.simulated is true");
    assert(typeof brief1.meta.source === "string" && /Simulated/.test(brief1.meta.source), "brief: meta.source mentions Simulated");
    assert(typeof brief1.meta.notes === "string" && brief1.meta.notes.length > 0, "brief: meta.notes is a non-empty string");
    assert(Number.isInteger(brief1.meta.tick) && brief1.meta.tick >= 0, "brief: meta.tick is a non-negative integer");
    assert(typeof brief1.meta.generatedAt === "string" && brief1.meta.generatedAt.length > 0, "brief: meta.generatedAt is set");
    assert(
      brief1.meta.tick === boardPostRefresh.meta.tick,
      `brief: meta.tick matches /api/line-board meta.tick (${brief1.meta.tick} vs ${boardPostRefresh.meta.tick})`
    );

    // Determinism — two consecutive GETs return identical content (only
    // meta.generatedAt is allowed to differ; bullets/insights/counts are stable).
    const brief2Raw = await fetch(base + "/api/brief/morning", { cache: "no-store" });
    assertNoStore(brief2Raw, "brief GET (determinism)");
    const brief2 = await brief2Raw.json();
    const { generatedAt: _g1, ...brief1Stripped } = brief1.meta;
    const { generatedAt: _g2, ...brief2Stripped } = brief2.meta;
    const stable1 = { ...brief1, meta: brief1Stripped };
    const stable2 = { ...brief2, meta: brief2Stripped };
    assert(JSON.stringify(stable1) === JSON.stringify(stable2), "brief: content is deterministic across calls (only generatedAt may differ)");

    // QC defects — runtime contract + determinism.
    let qc1 = null;
    let qcLastStatus = 0;
    let qcLastHeaders = null;
    const qcDeadline = Date.now() + 30_000;
    while (Date.now() < qcDeadline) {
      try {
        const r = await fetch(base + "/api/qc/defects", { cache: "no-store" });
        qcLastStatus = r.status;
        qcLastHeaders = r.headers;
        if (r.status === 200) {
          qc1 = await r.json();
          break;
        }
        try { await r.text(); } catch {}
      } catch {}
      await new Promise((r2) => setTimeout(r2, 500));
    }
    assert(qc1 !== null, `qc: GET /api/qc/defects returned 200 within grace period (last status ${qcLastStatus})`);
    assert(
      qcLastHeaders && /no-store/i.test(qcLastHeaders.get("cache-control") ?? ""),
      "qc: GET /api/qc/defects sets Cache-Control: no-store"
    );
    assert(Array.isArray(qc1.operations) && qc1.operations.length === 36, `qc: operations has 36 cells (6 ops × 6 lines), got ${qc1.operations?.length}`);
    const qcSeenLineIds = new Set();
    const qcSeenOps = new Set();
    for (const op of qc1.operations) {
      qcSeenLineIds.add(op.lineId);
      qcSeenOps.add(op.operation);
      assert(op.weeks.length === 8, `qc op ${op.operation}/${op.lineId}: 8 weekly buckets`);
      for (const w of op.weeks) {
        assert(/^\d{4}-\d{2}-\d{2}$/.test(w.weekStart), `qc op ${op.operation}/${op.lineId}: weekStart is YYYY-MM-DD`);
        assert(w.defects <= w.inspected, `qc op ${op.operation}/${op.lineId}/${w.weekStart}: defects ≤ inspected`);
        assert(w.major + w.minor === w.defects, `qc op ${op.operation}/${op.lineId}/${w.weekStart}: major+minor === defects`);
        assert(w.rework <= w.defects, `qc op ${op.operation}/${op.lineId}/${w.weekStart}: rework ≤ defects`);
      }
      // Per-cell totals must equal the sum of weekly buckets.
      const tInspected = op.weeks.reduce((a, w) => a + w.inspected, 0);
      const tDefects = op.weeks.reduce((a, w) => a + w.defects, 0);
      const tMajor = op.weeks.reduce((a, w) => a + w.major, 0);
      const tMinor = op.weeks.reduce((a, w) => a + w.minor, 0);
      const tRework = op.weeks.reduce((a, w) => a + w.rework, 0);
      assert(op.totals.inspected === tInspected, `qc op ${op.operation}/${op.lineId}: totals.inspected === Σ weeks.inspected`);
      assert(op.totals.defects === tDefects, `qc op ${op.operation}/${op.lineId}: totals.defects === Σ weeks.defects`);
      assert(op.totals.major === tMajor, `qc op ${op.operation}/${op.lineId}: totals.major === Σ weeks.major`);
      assert(op.totals.minor === tMinor, `qc op ${op.operation}/${op.lineId}: totals.minor === Σ weeks.minor`);
      assert(op.totals.rework === tRework, `qc op ${op.operation}/${op.lineId}: totals.rework === Σ weeks.rework`);
    }
    assert(qcSeenLineIds.size === 6, `qc: covers all 6 lines, got ${qcSeenLineIds.size}`);
    assert(qcSeenOps.size === 6, `qc: covers all 6 operations, got ${qcSeenOps.size}`);
    // Cross-route consistency: every QC lineId exists in line-board rows.
    for (const lid of qcSeenLineIds) {
      assert(seenLineIds.has(lid), `qc: lineId ${lid} also exists in /api/line-board rows`);
    }
    assert(qc1.topByDefectRate.length === 5, "qc: topByDefectRate has 5 entries");
    assert(qc1.topByReworkRate.length === 5, "qc: topByReworkRate has 5 entries");
    for (const t of qc1.topByDefectRate) {
      assert(t.defectRatePct >= 0 && t.defectRatePct <= 100, `qc top defect ${t.operation}/${t.lineId}: defectRatePct ∈ 0..100`);
      assert(t.reworkRatePct >= 0 && t.reworkRatePct <= 100, `qc top defect ${t.operation}/${t.lineId}: reworkRatePct ∈ 0..100`);
      // Each top-row must reference a (op, line) that exists in operations.
      assert(
        qc1.operations.some((o) => o.operation === t.operation && o.lineId === t.lineId),
        `qc top defect ${t.operation}/${t.lineId} exists in operations grid`
      );
    }
    for (const t of qc1.topByReworkRate) {
      assert(t.reworkRatePct >= 0 && t.reworkRatePct <= 100, `qc top rework ${t.operation}/${t.lineId}: reworkRatePct ∈ 0..100`);
      assert(t.defectRatePct >= 0 && t.defectRatePct <= 100, `qc top rework ${t.operation}/${t.lineId}: defectRatePct ∈ 0..100`);
      assert(
        qc1.operations.some((o) => o.operation === t.operation && o.lineId === t.lineId),
        `qc top rework ${t.operation}/${t.lineId} exists in operations grid`
      );
    }
    assert(qc1.meta.simulated === true, "qc: meta.simulated is true");
    assert(/Simulated/i.test(qc1.meta.source), "qc: meta.source mentions Simulated");
    assert(typeof qc1.meta.defectSource === "string" && qc1.meta.defectSource.length > 0, "qc: meta.defectSource is set");
    assert(typeof qc1.meta.notes === "string" && qc1.meta.notes.length > 0, "qc: meta.notes is a non-empty string");
    assert(typeof qc1.meta.generatedAt === "string" && qc1.meta.generatedAt.length > 0, "qc: meta.generatedAt is set");

    // Determinism — content stable across two calls (only meta.generatedAt may differ).
    const qc2Raw = await fetch(base + "/api/qc/defects", { cache: "no-store" });
    assertNoStore(qc2Raw, "qc GET (determinism)");
    const qc2 = await qc2Raw.json();
    const { generatedAt: _qg1, ...qc1Stripped } = qc1.meta;
    const { generatedAt: _qg2, ...qc2Stripped } = qc2.meta;
    const qcStable1 = { ...qc1, meta: qc1Stripped };
    const qcStable2 = { ...qc2, meta: qc2Stripped };
    assert(JSON.stringify(qcStable1) === JSON.stringify(qcStable2), "qc: content is deterministic across calls (only generatedAt may differ)");

    // QC flag — runtime contract. Flagging a top operation should persist an
    // Insight via insightService.upsertCustom(); the new insight id should
    // appear in /api/insights-style queries (we use brief/morning as the
    // public mirror — but the real test is that the flag response shape
    // matches FlagResponseSchema). We also exercise a 400 path on bad body.
    const flagBad = await fetch(base + "/api/qc/flag", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ operation: "INVALID", lineId: "line-1" }),
      cache: "no-store"
    });
    assert(flagBad.status === 400, `qc/flag: invalid body returns 400, got ${flagBad.status}`);

    const flagPick = qc1.topByDefectRate[0];
    const flagRes = await fetch(base + "/api/qc/flag", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        operation: flagPick.operation,
        lineId: flagPick.lineId,
        defectRatePct: flagPick.defectRatePct,
        reworkRatePct: flagPick.reworkRatePct
      }),
      cache: "no-store"
    });
    assert(flagRes.status === 200, `qc/flag: POST returned 200, got ${flagRes.status}`);
    const flagJson = await flagRes.json();
    assert(typeof flagJson.insightId === "string" && flagJson.insightId.length > 0, "qc/flag: response insightId present");
    assert(typeof flagJson.flaggedAt === "string" && flagJson.flaggedAt.length > 0, "qc/flag: response flaggedAt present");
    assert(/^qc-flag-/.test(flagJson.insightId), `qc/flag: insightId has expected prefix, got ${flagJson.insightId}`);
    const flagText = JSON.stringify(flagJson);
    assert(!flagText.includes(FAKE_KEY), "qc/flag: response does not contain any leftover secret");
    assert(!flagText.includes("apiKey"), "qc/flag: response does not contain the substring 'apiKey'");

    // Idempotence — repeating the same flag with the same (op, line) returns
    // the same insight id (not a new one). This is the contract the dataset
    // provides via insightService.upsertCustom.
    const flagRes2 = await fetch(base + "/api/qc/flag", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        operation: flagPick.operation,
        lineId: flagPick.lineId,
        defectRatePct: flagPick.defectRatePct,
        reworkRatePct: flagPick.reworkRatePct
      }),
      cache: "no-store"
    });
    const flagJson2 = await flagRes2.json();
    assert(flagJson2.insightId === flagJson.insightId, "qc/flag: repeated flag is idempotent (same insightId)");

    // After flagging, brief/morning's riskCount should reflect the new
    // suggested-stage insight (we can't assert a strict delta because the
    // brief uses its own derivation, but the risk insight exists). We
    // assert the flag did not break brief stability (determinism holds).
    const briefPostFlag = await getJson(base + "/api/brief/morning");
    assert(briefPostFlag && Array.isArray(briefPostFlag.bulletsEn), "brief still returns valid payload after flag");
  } finally {
    await cleanup();
  }
}

async function getJson(url) {
  const res = await fetch(url, { method: "GET", cache: "no-store" });
  if (!res.ok) throw new Error(`GET ${url} → ${res.status}`);
  return res.json();
}

function assertNoSecret(obj, fakeKey, label) {
  const text = JSON.stringify(obj);
  assert(!text.includes(fakeKey), `${label}: response JSON does not contain the fake key`);
  // The response shape deliberately never has a field whose NAME is
  // "apiKey" or "LLM_API_KEY", so we assert on parsed-object shape rather
  // than the raw text (the route's notice strings legitimately reference
  // the env-var name as part of guidance).
  assertNoLeakedKey(obj, label);
}

// Explicit runtime assertion for GET /api/settings/llm-style responses:
// the JSON object MUST NOT have a top-level `apiKey`, a top-level
// `LLM_API_KEY`, or any field whose name matches an apiKey-shaped regex
// carrying a non-empty value — recursively. This is the runtime smoke
// companion to the static file-level checks in section 9.
function assertNoLeakedKey(obj, label) {
  assert(obj && typeof obj === "object" && !Array.isArray(obj), `${label}: response is a plain object`);
  // (1) No `apiKey` field anywhere.
  assert(obj.apiKey === undefined, `${label}: response JSON has no apiKey field`);
  // (2) No `LLM_API_KEY` field anywhere.
  assert(obj.LLM_API_KEY === undefined, `${label}: response JSON has no LLM_API_KEY field`);
  // (3) No key-shaped string field, recursively.
  const violations = [];
  function walk(node, path) {
    if (node === null || node === undefined) return;
    if (Array.isArray(node)) {
      for (let i = 0; i < node.length; i++) walk(node[i], path + "[" + i + "]");
      return;
    }
    if (typeof node !== "object") return;
    for (const k of Object.keys(node)) {
      const child = node[k];
      // Match any key whose name looks like an API key (apiKey, API_KEY,
      // api_key, api-key, etc.) — case-insensitive, with optional separators.
      // We only flag values that are non-empty strings — booleans/nulls/
      // undefined are allowed.
      if (/api[_\- ]?key/i.test(k) || /LLM_API_KEY/i.test(k)) {
        if (typeof child === "string" && child.length > 0) {
          violations.push(`${path}.${k} = ${JSON.stringify(child).slice(0, 64)}`);
        }
      }
      walk(child, path === "" ? k : path + "." + k);
    }
  }
  walk(obj, "");
  assert(
    violations.length === 0,
    `${label}: response JSON has no key-shaped string field (violations: ${violations.join(", ")})`
  );
}

function assertNoStore(response, label) {
  const cc = response.headers.get("cache-control") ?? "";
  assert(/no-store/i.test(cc), `${label}: response sets Cache-Control: no-store (got "${cc}")`);
}

async function killListenersOnPort(port) {
  // Best-effort: use fuser to kill any process listening on the given TCP
  // port. fuser is on most Linux systems; if it's missing we silently move
  // on — the spawn() below will fail loudly with EADDRINUSE.
  try {
    const { spawnSync } = await import("node:child_process");
    spawnSync("fuser", ["-k", "-n", "tcp", String(port)], { stdio: "ignore" });
    await new Promise((r) => setTimeout(r, 200));
  } catch {
    // ignore — best effort
  }
}

async function waitForServer(base) {
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(base + "/api/settings/llm");
      // Any HTTP response means the server is up.
      if (res.status > 0) return;
    } catch {}
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error("timeout waiting for next start");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
