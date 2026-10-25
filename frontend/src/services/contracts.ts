// Wire contracts for the .NET API, mirroring the C# DTOs in
// FactoryBrain.Backend/FactoryBrain.Application/Dtos/**. The API serialises
// with camelCase names, camelCase enum strings and omits null properties
// (except where the DTO says JsonIgnoreCondition.Never), so optional fields
// below may be absent rather than null.

import type { RiskTier, Stage } from "./types";

export type Role = "Admin" | "Operator" | "Viewer";

// ── Agents ──────────────────────────────────────────────────────────────────

/** GET /api/agents — AgentDefinition rows. */
export type AgentDefinition = {
  id: string;
  name: string;
  nameBn: string;
  purpose: string;
  purposeBn: string;
  risk: RiskTier;
  execution: string;
  model: string;
  contextSlices: string[];
  status: string;
  tasksToday: number;
  recentCount: number;
  glyph?: string | null;
};

export type InsightLite = {
  id: string;
  title: string;
  titleBn: string;
  finding: string;
  findingBn: string;
  recommendationTitle: string;
  recommendationAction: string;
  riskTier: RiskTier;
  stage: Stage;
  confidence: number;
};

/** POST /api/agents/{agentId}/run */
export type AgentRunResponse = {
  source: string;
  agentId: string;
  insight: InsightLite;
  approvalPending: boolean;
  approvalReason?: string | null;
  floorAlertId?: string | null;
  activityEventId?: string | null;
  energyInsight?: { insightId: string; floorAlertId: string; score: number } | null;
  warning?: string | null;
};

// ── Audit ───────────────────────────────────────────────────────────────────

/** GET /api/audit (Admin) — newest first, at most 200 per request. */
export type AuditEntry = {
  id: number;
  occurredAt: string;
  actorUserId?: string | null;
  actorEmail: string;
  actorRole: string;
  action: string;
  entityType: string;
  entityId: string;
  fromState?: string | null;
  toState?: string | null;
  reason?: string | null;
  traceId?: string | null;
};

// ── Floor alerts ────────────────────────────────────────────────────────────

export type FloorAlert = {
  id: string;
  channel: string;
  approvalId?: string | null;
  insightId?: string | null;
  bodyEn: string;
  bodyBn: string;
  severity: "info" | "warn" | "critical";
  createdAt: string;
  read: boolean;
};

export type FloorAlertList = { simulated: boolean; source: string; count: number; alerts: FloorAlert[] };

// ── Brief / line board ──────────────────────────────────────────────────────

export type MorningBrief = {
  date: string;
  bulletsEn: string[];
  bulletsBn: string[];
  topBottleneckLineId?: string | null;
  riskCount: number;
  pendingApprovals: number;
  meta: { simulated: boolean; source: string; tick: number; generatedAt: string; notes: string };
};

export type LineBoardRow = {
  lineId: string;
  name: string;
  efficiencyPct: number;
  sahTarget: number;
  sahActual: number;
  wipBundles: number;
  bottleneck: "green" | "amber" | "red";
  nptMinutes: number;
  updatedAt: string;
};

export type LineBoard = {
  rows: LineBoardRow[];
  meta: { simulated: boolean; source: string; tick: number; updatedAt: string; notes: string };
};

// ── Sensors ─────────────────────────────────────────────────────────────────

export type SensorReading = {
  id: string;
  source: string;
  entityId: string;
  metric: string;
  value: number;
  unit: string;
  ts: string;
  simTick: number;
};

export type LineSummary = { id: string; efficiency: number; uptime: number; energyKwh: number };
export type MachineSummary = { id: string; vibration: number; temperature: number; dutyCycle: number; status: string };

/** GET /api/sensors/snapshot and POST /api/sensors/ingest */
export type SensorSnapshot = {
  simulated: boolean;
  source: string;
  tick: number;
  readings: SensorReading[];
  lines: LineSummary[];
  machines: MachineSummary[];
};

/** GET /api/sensors/latest */
export type LatestReadings = { simulated: boolean; source: string; count: number; readings: SensorReading[] };

/** GET /api/sensors/ingest */
export type SensorStatus = {
  simulated: boolean;
  source: string;
  tick: number;
  readingsCount: number;
  linesCount: number;
  machinesCount: number;
};

// ── RAG ─────────────────────────────────────────────────────────────────────

export const RAG_SOURCES = ["manual", "sop", "compliance", "faq"] as const;
export type RagSource = (typeof RAG_SOURCES)[number];
/** RagIngestRequestValidator.MaxContentBytes */
export const RAG_MAX_CONTENT_BYTES = 200 * 1024;

export type RagDocument = {
  id: string;
  title: string;
  titleBn?: string | null;
  source: string;
  tags: string[];
  chunks: number;
  embeddingProvider: string;
  embeddingModel: string;
  dims: number;
  isDemo: boolean;
  createdAt: string;
};

/** GET /api/rag/status */
export type RagStatus = {
  configuredProvider: string;
  activeProvider: string;
  model: string;
  dims: number;
  storedDims: number;
  degraded: boolean;
  lastProbeAt?: string | null;
  lastProbeOk?: boolean | null;
  pendingCount: number;
};

export type RagIngestRequest = {
  title: string;
  titleBn?: string;
  source: RagSource;
  tags?: string[];
  content: string;
  url?: string;
};

/** 201 from POST /api/rag/ingest */
export type RagIngestResponse = {
  documentId: string;
  chunks: number;
  provider: string;
  model: string;
  dims: number;
  embedded: boolean;
  warning?: string | null;
};

// ── Settings ────────────────────────────────────────────────────────────────

export type Persistence = "database" | "startup_config" | "runtime_override" | "none";
export type KeyStatus = "set" | "not_set" | "unreadable";

/** GET/POST /api/settings/llm */
export type LlmStatus = {
  configured: boolean;
  provider: string | null;
  model: string | null;
  mode: "demo" | "live";
  persistence: Persistence;
  keySet: boolean;
  keyStatus: KeyStatus;
  version: number;
  updatedAt?: string | null;
  canStoreKeys: boolean;
};

export type LlmSettingsRequest = {
  provider?: string;
  apiKey?: string;
  model?: string;
  clearKey?: boolean;
  clearAll?: boolean;
  expectedVersion?: number;
};

export const LLM_PROVIDERS = ["openai", "anthropic", "gemini"] as const;

export type EmbeddingOption = {
  provider: string;
  model: string;
  defaultDimensions: number;
  minDimensions: number;
  maxDimensions: number;
};

/** GET /api/settings/embedding */
export type EmbeddingStatus = {
  provider: string;
  model: string;
  dimensions: number;
  persistence: Persistence;
  keySet: boolean;
  keyStatus: KeyStatus;
  columnDimensions: number | null;
  dimensionsMatchColumn: boolean;
  documents: number;
  pendingDocuments: number;
  degraded: boolean;
  version: number;
  updatedAt?: string | null;
  canStoreKeys: boolean;
  supported: EmbeddingOption[];
};

export type EmbeddingTarget = { provider: string; model: string; dimensions: number };

/** POST /api/settings/embedding/plan, GET /api/rag/reindex/plan */
export type ReindexPlan = {
  planId: string;
  current: EmbeddingTarget;
  currentPersistence: Persistence;
  columnDimensions: number | null;
  target: EmbeddingTarget;
  requiresReindex: boolean;
  requiresColumnResize: boolean;
  reembedsAllDocuments: boolean;
  documents: number;
  chunks: number;
  bytes: number;
  estimatedTokensUpperBound: number;
  cost: { estimateUsd: number | null; isUpperBound: boolean; currency: string; note: string };
  settingsVersion: number;
  warnings: string[];
};

export type ReindexOutcome = {
  planId: string;
  documents: number;
  chunks: number;
  resized: boolean;
  columnDimensionsBefore?: number | null;
  columnDimensionsAfter: number;
  embedding: EmbeddingTarget;
  settingsVersion: number;
};

/** 200 from POST /api/settings/embedding */
export type EmbeddingUpdateResult = { settings: EmbeddingStatus; reindex?: ReindexOutcome | null };

// ── Users ───────────────────────────────────────────────────────────────────

export type UserRow = { id: string; email: string; role: Role; createdAt: string };

// ── Vision ──────────────────────────────────────────────────────────────────

export type VisionAllowed = { simulated: boolean; source: string; allowedSampleFiles: string[]; method: string };

export type VisionResult = {
  id: string;
  sampleFile: string;
  anomalyLabelEn: string;
  anomalyLabelBn: string;
  repairStepsEn: string[];
  repairStepsBn: string[];
  confidence: number;
  machineId?: string | null;
  createdAt: string;
};

export type VisionAnalyzeResponse = {
  simulated: boolean;
  source: string;
  result: VisionResult;
  insight?: InsightLite | null;
  approvalPending?: boolean | null;
  approvalReason?: string | null;
  floorAlertId?: string | null;
  activityEventId?: string | null;
};

// ── QC ──────────────────────────────────────────────────────────────────────

export type QcFlagRequest = {
  operation: string;
  lineId: string;
  defectRatePct?: number;
  reworkRatePct?: number;
  note?: string;
};

export type QcFlagResponse = { insightId: string; flaggedAt: string };

// ── Ask ─────────────────────────────────────────────────────────────────────

export const RAG_DEPARTMENTS = ["sewing", "cutting", "finishing", "qc", "general"] as const;
export const RAG_CATEGORIES = ["manuals", "compliance", "qc", "policy", "sop"] as const;

export type AskRequest = {
  query: string;
  filter?: { department?: (typeof RAG_DEPARTMENTS)[number]; category?: (typeof RAG_CATEGORIES)[number] };
};

export type AskRagHit = {
  id: string;
  sourceId: string;
  title: string;
  department: string;
  category: string;
  tags: string[];
  hybridScore: number;
  denseScore: number;
  bm25Score: number;
  snippet: string;
};

export type AskCitation = { id: string; title: string; snippet: string; confidence: number; source: string; url: string };
