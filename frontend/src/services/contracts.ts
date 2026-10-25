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

// ── QC inspections ──────────────────────────────────────────────────────────
// /api/qc/inspections, /api/qc/summary, /api/qc/categories, /api/qc/vision/status.
// Coordinates are fractions (0–1) of the image width / height, origin top-left.

export const QC_STAGES = ["fabric", "cutting", "inline", "end_of_line", "final"] as const;
export type QcStage = (typeof QC_STAGES)[number];
export const QC_STATUSES = ["pending", "processing", "completed", "failed", "reviewed"] as const;
export type QcStatus = (typeof QC_STATUSES)[number];
export const QC_SEVERITIES = ["minor", "major", "critical"] as const;
export type QcSeverity = (typeof QC_SEVERITIES)[number];
export const QC_OUTCOMES = ["pass", "rework", "reject"] as const;
export type QcOutcome = (typeof QC_OUTCOMES)[number];
export type QcDecision = "unreviewed" | "accepted" | "rejected" | "corrected";

export type QcInspectionListItem = {
  id: string;
  reference: string;
  status: QcStatus;
  productType: string;
  stage: QcStage;
  lineId?: string | null;
  productionOrder?: string | null;
  createdAt: string;
  createdByEmail: string;
  predictionCount: number;
  verifiedDefectCount: number;
  maxConfidence?: number | null;
  outcome?: QcOutcome | null;
  provider?: string | null;
  model?: string | null;
  failureCode?: string | null;
};

export type QcInspectionPage = { items: QcInspectionListItem[]; page: number; pageSize: number; total: number; totalPages: number };

export type QcBox = { x: number; y: number; width: number; height: number };

export type QcFinding = {
  id: string;
  source: "model" | "inspector";
  categoryCode?: string | null;
  categoryName?: string | null;
  rawLabel?: string | null;
  confidence?: number | null;
  box?: QcBox | null;
  polygon?: number[][] | null;
  severity: QcSeverity;
  description?: string | null;
  decision: QcDecision;
  correctedCategoryCode?: string | null;
  correctedSeverity?: QcSeverity | null;
  reviewerNote?: string | null;
  effectiveCategoryCode?: string | null;
  effectiveCategoryName?: string | null;
  effectiveSeverity: QcSeverity;
  verified: boolean;
  createdAt: string;
  reviewedAt?: string | null;
};

export type QcInspectionDetail = {
  id: string;
  reference: string;
  status: QcStatus;
  productType: string;
  stage: QcStage;
  lineId?: string | null;
  productionOrder?: string | null;
  notes?: string | null;
  requestedCategoryCodes: string[];
  image: { url: string; contentType: string; width: number; height: number; sizeBytes: number; originalFileName?: string | null; sha256: string };
  analysis: {
    provider?: string | null;
    model?: string | null;
    modelVersion?: string | null;
    localizationSupported?: boolean | null;
    confidenceThreshold?: number | null;
    inferenceMs?: number | null;
    attempts: number;
    processingStartedAt?: string | null;
    analyzedAt?: string | null;
    failureCode?: string | null;
    failureMessage?: string | null;
    retryable: boolean;
  };
  findings: QcFinding[];
  review?: { outcome: QcOutcome; notes?: string | null; reviewedAt: string; reviewedByEmail?: string | null } | null;
  followUp?: { approvalId: string; status: string; riskTier: "low" | "medium" | "high" } | null;
  createdAt: string;
  updatedAt: string;
  createdByEmail: string;
  version: number;
};

export type QcReviewRequest = {
  expectedVersion: number;
  outcome: QcOutcome;
  notes?: string;
  decisions: { findingId: string; decision: Exclude<QcDecision, "unreviewed">; correctedCategoryCode?: string; correctedSeverity?: QcSeverity; note?: string }[];
  additions: { categoryCode: string; severity?: QcSeverity; box?: QcBox; note?: string }[];
  requestFollowUp?: boolean;
  followUpNote?: string;
};

export type QcVisionStatus = {
  configured: boolean;
  mode: "llm" | "http" | "none";
  provider?: string | null;
  model?: string | null;
  localizationSupported?: boolean | null;
  reason?: string | null;
  confidenceThreshold: number;
  maxImageBytes: number;
  acceptedContentTypes: string[];
  confidenceNote: string;
};

export type QcCategory = {
  code: string;
  name: string;
  nameBn?: string | null;
  description?: string | null;
  defaultSeverity: QcSeverity;
  isActive: boolean;
  sortOrder: number;
  version: number;
  updatedAt: string;
};

export type QcSummary = {
  from: string;
  to: string;
  totals: {
    inspections: number;
    inProgress: number;
    failed: number;
    awaitingReview: number;
    reviewed: number;
    passed: number;
    rework: number;
    rejected: number;
    verifiedDefects: number;
    defectiveRatePct?: number | null;
    dhu?: number | null;
    rejectOrReworkRatePct?: number | null;
  };
  model: { predictionsReviewed: number; accepted: number; corrected: number; rejected: number; acceptedPct?: number | null; inspectorAdded: number; awaitingReview: number };
  byCategory: { code?: string | null; name: string; count: number; sharePct: number }[];
  bySeverity: { minor: number; major: number; critical: number };
  byLine: { lineId: string; reviewed: number; defective: number; defectiveRatePct?: number | null; verifiedDefects: number }[];
  daily: { date: string; inspections: number; reviewed: number; defective: number; verifiedDefects: number; defectiveRatePct?: number | null }[];
  recurring: { lineId: string; categoryCode?: string | null; categoryName: string; defects: number; inspections: number }[];
  recurringThreshold: number;
  definitions: Record<string, string>;
  generatedAt: string;
};

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

/** GET /api/ask/status — the Python RAG service that answers Ask and agent runs. */
export type RagServiceStatus = {
  service: "python-rag";
  reachable: boolean;
  ready: boolean;
  qdrant: string;
  collection: string;
  llmModel: string;
  embeddingProvider: string;
  embeddingModel: string;
  error?: string | null;
};
