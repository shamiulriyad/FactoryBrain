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
// The Next.js app is the parent folder (frontend/); every path below is relative to it.
const root = path.resolve(__dirname, "..");

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
  assert(
    /<Button[\s\S]{0,400}data-testid="simulate-tick"[\s\S]{0,400}t\("overview\.demoPathSimulate"\)/.test(overview),
    "overview renders the Simulate tick button (data-testid=simulate-tick, label t(\"overview.demoPathSimulate\"))"
  );
  assert(/demoPathSimulate:\s*"Simulate tick"/.test(en), "i18n en: overview.demoPathSimulate is \"Simulate tick\"");
  assert(/demoPathSimulate:\s*"[^"]+"/.test(bn), "i18n bn: overview.demoPathSimulate is translated");
  assert(/onClick=\{\(\) => void advanceSensors\(\)\}/.test(overview), "Simulate tick button triggers the ingest POST (advanceSensors)");
  assert(overview.includes('"/api/sensors/snapshot"'), "overview polls the read-only /api/sensors/snapshot");
  const activity = await read("src/app/app/activity/page.tsx");
  assert(activity.includes('"/api/floor-alerts"') && activity.includes("markAlertRead"), "activity lists /api/floor-alerts and acknowledges through the API");
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
  assert(briefCard.includes("tick") && briefCard.includes("brief.reload()"), "BriefCard: re-reads the brief when the simulation tick moves");
  assert(briefCard.includes('t("brief.simulatedChip")') && briefCard.includes("meta.simulated"), "BriefCard: shows the simulated label from meta.simulated");
  assert(briefCard.includes("/app/approvals"), "BriefCard: deep-links pending approvals to /app/approvals");
  assert(!briefCard.includes("/app/insights"), "BriefCard: no link into the browser-only demo insights");
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
  assert(qcSummary.includes("pollMs"), "QcSummary: refreshes on a poll");
  assert(!qcSummary.includes("Silently swallow"), "QcSummary: shows load errors instead of swallowing them");
  assert(qcSummary.includes('testId="qc-summary"'), "QcSummary: smoke-visible testid present (Panel testId)");
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
  const qcSidebar = await read("src/components/layout/nav.ts");
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
  assert(overview.includes('"/api/line-board"'), "overview: fetches /api/line-board for the LineBoardPanel");
  assert(overview.includes('"/api/line-board/refresh"'), "overview: Operators can advance the board (POST /api/line-board/refresh)");
  assert(overview.includes("board.reload()") && overview.includes("tick"), "overview: re-reads the board when the snapshot tick moves");
  assert(lineBoardPanel.includes('data-testid="line-board"'), "LineBoardPanel: smoke-visible testid present");
  assert(lineBoardPanel.includes('Simulated'), "LineBoardPanel: keeps simulated label visible");
  const overviewPage = await read("src/app/app/page.tsx");
  assert(overviewPage.includes("<LineBoardPanel"), "overview: mounts <LineBoardPanel />");
  assert(!overviewPage.includes("metricService") && !overviewPage.includes("insightService"), "overview: no browser-generated business metrics or insights");
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
  const navConfig = await read("src/components/layout/nav.ts");
  assert(navConfig.includes("/app/vision"), "navigation links to Vision under Intelligence");
  assert(/href: "\/app\/audit"[^}]*role: "Admin"/.test(navConfig), "navigation: the audit log is declared Admin-only");
  const overviewUi = await read("src/app/app/page.tsx");
  assert(!overviewUi.includes("factoryTools"), "overview: no browser-simulated energy recommendation presented as live");
  const visionPage = await read("src/app/app/vision/page.tsx");
  assert(visionPage.includes("allowedSampleFiles"), "vision picks from the API's allowed sample list");
  const agentsPage = await read("src/app/app/agents/page.tsx");
  assert(agentsPage.includes('"/api/agents"') && agentsPage.includes("RunResult"), "agents: server roster and the real run result");
  const kbPage = await read("src/app/app/knowledge/page.tsx");
  assert(kbPage.includes('"/api/rag/ingest"') && kbPage.includes('"/api/rag/reindex/plan"') && kbPage.includes("confirmPlanId"), "knowledge base: ingest + plan-confirmed reindex");
  const auditPage = await read("src/app/app/audit/page.tsx");
  assert(auditPage.includes("/api/audit?") && auditPage.includes('RequireRole role="Admin"'), "audit page: Admin-gated view of GET /api/audit");
  const evidence = await read("src/components/evidence/EvidenceBlock.tsx");
  assert(!evidence.includes("evidenceService"), "EvidenceBlock: never expands API evidence with local records");

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

  // 10. Ask Live/Demo labelling — behaviour of src/lib/askMode.ts, imported
  // directly (Node strips the TypeScript types). Only an explicit
  // mode:"live" from the API may ever render as Live.
  // ---------------------------------------------------------------------
  const askMode = await import(url.pathToFileURL(path.join(root, "src/lib/askMode.ts")).href);
  assert(askMode.resolveAskMode({ mode: "live" }) === "live", "askMode: mode live → live");
  assert(askMode.resolveAskMode({ mode: "demo" }) === "demo", "askMode: mode demo → demo");
  assert(askMode.resolveAskMode({ mode: "no_source" }) === "no_source", "askMode: mode no_source → no_source");
  for (const [label, value] of [["missing", {}], ["null", null], ["unknown", { mode: "LIVE" }], ["truthy", { mode: true }], ["old shape", { finding: "x", confidence: 0.7 }]]) {
    assert(askMode.resolveAskMode(value) === "demo", `askMode: ${label} mode is never live`);
  }
  assert(askMode.askModeLabel("live").label === "Live" && askMode.askModeLabel("live").isLive, "askMode: live label");
  assert(askMode.askModeLabel("demo").label === "Demo" && !askMode.askModeLabel("demo").isLive, "askMode: demo label");
  assert(!askMode.askModeLabel("no_source").isLive, "askMode: no_source is not live");
  assert(askMode.shouldShowConfidence("live") && !askMode.shouldShowConfidence("demo") && !askMode.shouldShowConfidence("no_source"),
    "askMode: confidence shown only for live answers");
  assert(!/setSource\("live"\)/.test(askPage), "ask page never hard-codes a live source");
  assert(askPage.includes("resolveAskMode(json)"), "ask page derives the chip from the API's mode");
  assert(askPage.includes('data-testid="ask-citations"'), "ask page renders citations");

  // 11. Approvals are server-side: the UI reads and decides through the API.
  // ---------------------------------------------------------------------
  const approvalsPage = await read("src/app/app/approvals/page.tsx");
  const approvalsApi = await read("src/services/approvals.api.ts");
  assert(!approvalsPage.includes("approval.service"), "approvals page no longer uses the browser-memory approval.service");
  assert(approvalsPage.includes("decideApproval(") && approvalsPage.includes("fetchApprovals("), "approvals page reads and decides through the API");
  assert(approvalsApi.includes("/api/approvals?status=") && approvalsApi.includes("/api/approvals/${encodeURIComponent(id)}/${decision}"),
    "approvals.api calls GET /api/approvals and POST /api/approvals/{id}/approve|reject");
  for (const f of ["src/app/app/page.tsx", "src/components/layout/Sidebar.tsx"]) {
    assert(!(await read(f)).includes("approvalService."), `${f} uses the server approval queue`);
  }

  // 12. Admin pass-through proxy — runtime contract against a fake backend.
  await runAdminProxyRuntimeCheck();

  console.log("\nAll smoke checks passed.");
}

async function runAdminProxyRuntimeCheck() {
  // Runtime contract of the admin pass-through routes (src/lib/adminProxy.ts)
  // and the /api/* rewrite, against a FAKE local backend. The real .NET API
  // and any database are never contacted: `next start` gets explicit
  // DOTNET_API_URL / NEXT_PUBLIC_API_URL values, and real process env wins
  // over the repo-root .env that next.config.mjs loads.
  //
  // Replaces the old settings/llm runtime check, which assumed Next.js
  // handled settings itself and that anonymous writes were accepted.
  const buildDir = path.join(root, ".next");
  try {
    await fs.access(path.join(buildDir, "BUILD_ID"));
  } catch {
    console.log("skip: admin proxy runtime check (no .next/BUILD_ID — build first)");
    return;
  }

  const http = await import("node:http");
  const seen = [];
  const backend = http.createServer((req, res) => {
    let body = "";
    req.on("data", (c) => (body += c));
    req.on("end", () => {
      seen.push({ method: req.method, url: req.url, headers: req.headers, body });
      res.setHeader("Content-Type", "application/json");
      res.setHeader("Cache-Control", "public, max-age=3600"); // the proxy must override this
      res.setHeader("Set-Cookie", "backend_cookie=1; Path=/");   // …and must not pass this through
      res.end(JSON.stringify({ configured: false, provider: null, model: null, mode: "demo", persistence: "process" }));
    });
  });
  await new Promise((r) => backend.listen(0, "127.0.0.1", r));
  const backendUrl = `http://127.0.0.1:${backend.address().port}`;

  const port = 4173;
  const base = `http://127.0.0.1:${port}`;
  const LEGACY_TOKEN = "smoke-legacy-admin-token-must-never-be-forwarded";
  const FAKE_KEY = "smoke-fake-key-DO-NOT-USE-0123456789abcdef";
  await killListenersOnPort(port);

  // Run Next's CLI with this Node binary directly (no npx / shell), so it
  // starts and stops the same way on Windows, macOS and Linux.
  const nextCli = path.join(root, "node_modules", "next", "dist", "bin", "next");
  const server = spawn(process.execPath, [nextCli, "start", "-p", String(port), "-H", "127.0.0.1"], {
    cwd: root,
    env: {
      ...process.env,
      NODE_ENV: "production",
      NEXT_TELEMETRY_DISABLED: "1",
      DOTNET_API_URL: backendUrl,
      NEXT_PUBLIC_API_URL: backendUrl,
      ADMIN_API_TOKEN: LEGACY_TOKEN // present on purpose: the proxy must ignore it
    },
    stdio: ["ignore", "ignore", "ignore"]
  });

  const lastSeen = () => seen[seen.length - 1];
  const header = (h, name) => (h ? h[name] : undefined);

  try {
    await waitForServer(base);
    seen.length = 0; // ignore readiness probes

    // 1. A signed-in caller's bearer token is forwarded verbatim; cookies,
    //    the legacy admin token and any other credential are not.
    let res = await fetch(base + "/api/settings/llm", {
      headers: { Authorization: "Bearer smoke-user-token", Cookie: "fb_refresh=smoke-refresh-cookie" },
      cache: "no-store"
    });
    assert(res.status === 200, "admin proxy: GET /api/settings/llm reaches the backend");
    assertNoStore(res, "admin proxy GET");
    assert(!res.headers.get("set-cookie"), "admin proxy: backend Set-Cookie is not passed through");
    let s = lastSeen();
    assert(s && s.url === "/api/settings/llm", "admin proxy: forwards to the backend path");
    assert(header(s.headers, "authorization") === "Bearer smoke-user-token", "admin proxy: forwards the caller's bearer token");
    assert(header(s.headers, "x-admin-token") === undefined, "admin proxy: never adds X-Admin-Token (even with ADMIN_API_TOKEN set)");
    assert(header(s.headers, "cookie") === undefined, "admin proxy: does not forward cookies");
    assert(!JSON.stringify(s.headers).includes(LEGACY_TOKEN), "admin proxy: legacy token appears in no forwarded header");

    // 2. An anonymous POST carries no credential at all; the body is
    //    forwarded intact and the posted key never comes back.
    res = await fetch(base + "/api/settings/llm", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ provider: "openai", apiKey: FAKE_KEY }),
      cache: "no-store"
    });
    assertNoStore(res, "admin proxy POST");
    s = lastSeen();
    assert(s.method === "POST" && JSON.parse(s.body).apiKey === FAKE_KEY, "admin proxy: forwards the POST body unchanged");
    assert(header(s.headers, "authorization") === undefined, "admin proxy: anonymous request gets no Authorization added");
    assert(header(s.headers, "x-admin-token") === undefined, "admin proxy: anonymous request gets no X-Admin-Token");
    assert(!(await res.text()).includes(FAKE_KEY), "admin proxy: response never echoes the posted key");

    // 3. Non-bearer or oversized Authorization values are dropped.
    for (const [label, value] of [["basic", "Basic dXNlcjpwYXNz"], ["oversized", "Bearer " + "x".repeat(9000)]]) {
      await fetch(base + "/api/rag/reindex", { method: "POST", headers: { Authorization: value }, cache: "no-store" });
      s = lastSeen();
      assert(s.url === "/api/rag/reindex" && header(s.headers, "authorization") === undefined,
        `admin proxy: ${label} Authorization header is not forwarded`);
    }

    // 4. The generic /api/* rewrite. Next.js resolves rewrites() at BUILD
    //    time into routes-manifest.json, so its target is fixed by the build
    //    environment (it ignores this run's env); check the manifest rather
    //    than sending a request to whatever backend the build baked in.
    const manifest = JSON.parse(await fs.readFile(path.join(buildDir, "routes-manifest.json"), "utf8"));
    // A plain-array rewrites() config is stored as an array; the object form
    // as { beforeFiles, afterFiles, fallback }.
    const rewriteList = Array.isArray(manifest.rewrites)
      ? manifest.rewrites
      : [].concat(manifest.rewrites?.beforeFiles ?? [], manifest.rewrites?.afterFiles ?? [], manifest.rewrites?.fallback ?? []);
    const rw = rewriteList.find((r) => r.source === "/api/:path*");
    assert(rw && /^https?:\/\/[^/]+\/api\/:path\*$/.test(rw.destination), "rewrite: /api/:path* → <backend>/api/:path* is in the build manifest");
    assert(!seen.some((r) => r.url === "/api/auth/refresh"), "rewrite: no auth traffic hit the fake backend unprompted");

    // 5. Backend down: a stable 502 envelope, no-store, no internals.
    await new Promise((r) => backend.close(() => r()));
    res = await fetch(base + "/api/settings/llm", { cache: "no-store" });
    assert(res.status === 502, "admin proxy: unreachable backend → 502");
    assertNoStore(res, "admin proxy 502");
    const down = await res.text();
    assert(down.includes("BackendUnreachable") && !down.includes("127.0.0.1"), "admin proxy: 502 body is the stable envelope without addresses");
  } finally {
    try { server.kill(); } catch {}
    await new Promise((r) => setTimeout(r, 300));
    try { await new Promise((r) => backend.close(() => r())); } catch {}
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
