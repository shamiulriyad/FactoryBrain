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

// ── Production planning & line efficiency (/api/production/**) ─────────────
// Dates without a time are plant-local production dates (yyyy-MM-dd); instants
// are UTC ISO strings. Quantities are pieces, times minutes, rates pieces per
// hour, SMV minutes per piece. Every metric is calculated by the API; the UI
// only displays it.

export const PRODUCTION_STAGES = ["cutting", "sewing_input", "sewing_output", "finishing", "packing"] as const;
export type ProductionStage = (typeof PRODUCTION_STAGES)[number];
export const PLAN_STATUSES = ["draft", "pending_approval", "active", "completed", "cancelled"] as const;
export type PlanStatus = (typeof PLAN_STATUSES)[number];
export const ORDER_STATUSES = ["open", "closed", "cancelled"] as const;
export type OrderStatus = (typeof ORDER_STATUSES)[number];
export const PROPOSAL_STATUSES = ["proposed", "pending_approval", "applied", "rejected", "superseded"] as const;
export type ProposalStatus = (typeof PROPOSAL_STATUSES)[number];
export const OUTPUT_SOURCES = ["manual", "import", "integration"] as const;
export type OutputSource = (typeof OUTPUT_SOURCES)[number];
export const WEEKDAYS = ["sat", "sun", "mon", "tue", "wed", "thu", "fri"] as const;
export type RiskLevel = "complete" | "low" | "medium" | "high" | "insufficient_data" | "not_applicable";

export type Paged<T> = { items: T[]; page: number; pageSize: number; total: number; totalPages: number };

export type ProductionLine = { id: string; name: string; workstations: number; isActive: boolean; version: number; updatedAt: string };

export type ProductionShift = {
  code: string;
  name: string;
  start: string;
  end: string;
  breakStart?: string | null;
  breakMinutes: number;
  weekdays: string[];
  overnight: boolean;
  workingMinutes: number;
  isActive: boolean;
  version: number;
};

export type StyleOperation = { id: string; sequence: number; name: string; smv: number; machineType?: string | null; predecessors: number[] };

export type ProductionStyle = {
  id: string;
  code: string;
  name: string;
  productType?: string | null;
  approvedSmv?: number | null;
  effectiveSmv?: number | null;
  smvBasis: "approved" | "operation_bulletin" | "missing";
  operationSmvTotal: number;
  isActive: boolean;
  version: number;
  operations: StyleOperation[];
};

export type DataSourceStatus = { source: OutputSource; status: "available" | "receiving" | "not_connected"; description: string; recordsLast24h: number };

export type ProductionConfig = {
  timeZone: string;
  utcOffsetNow: string;
  requireSeparateApprover: boolean;
  maxBackfillDays: number;
  maxIntervalHours: number;
  rateLookbackDays: number;
  riskSlackPct: number;
  observationMaxAgeDays: number;
  configurationError?: string | null;
  activeLines: number;
  activeShifts: number;
  activeStyles: number;
  openOrders: number;
  dataSources: DataSourceStatus[];
  definitions: Record<string, string>;
};

export type ProductionOrderListItem = {
  id: string;
  orderNumber: string;
  styleId: string;
  styleCode: string;
  styleName: string;
  buyer?: string | null;
  quantity: number;
  dueDate: string;
  status: OrderStatus;
  producedGood: number;
  progressPct: number;
  activePlans: number;
  riskLevel: RiskLevel;
  projectedCompletion?: string | null;
  version: number;
};

export type ProductionOrder = {
  id: string;
  orderNumber: string;
  styleId: string;
  buyer?: string | null;
  quantity: number;
  dueDate: string;
  dueAt: string;
  status: OrderStatus;
  notes?: string | null;
  createdAt: string;
  createdByEmail: string;
  version: number;
};

export type StageProgress = { stage: ProductionStage; good: number; reject: number; rework: number; records: number };
export type WipBuffer = {
  key: string;
  label: string;
  from: ProductionStage;
  to: ProductionStage;
  pieces?: number | null;
  state: "ok" | "missing_to" | "inconsistent" | "partial";
};
export type LineProgress = { lineId: string; good: number; rework: number; reject: number; lastIntervalEnd?: string | null };
export type Recommendation = { code: string; action: string; reason: string; evidence: string; caveat: string };

export type RiskAssessment = {
  orderId: string;
  orderNumber: string;
  evaluatedAt: string;
  dueAt: string;
  lineIds: string[];
  level: Exclude<RiskLevel, "not_applicable">;
  remainingQuantity: number;
  remainingScheduledMinutes: number;
  requiredRatePerLineHour?: number | null;
  observedRatePerLineHour?: number | null;
  observedRateLow?: number | null;
  observedRateHigh?: number | null;
  plannedRatePerHour?: number | null;
  projectedCompletion?: string | null;
  projectedCompletionEarliest?: string | null;
  projectedCompletionLatest?: string | null;
  scheduleEnd?: string | null;
  projectedShortfall?: number | null;
  observedLineHours: number;
  method: string;
  observations: string[];
  assumptions: string[];
  limitations: string[];
  missingInputs: string[];
  recommendations: Recommendation[];
};

export type OrderDetail = {
  order: ProductionOrder;
  style: ProductionStyle;
  plans: PlanListItem[];
  stages: StageProgress[];
  wip: WipBuffer[];
  byLine: LineProgress[];
  risk: RiskAssessment;
};

export type PlanValues = {
  lineId: string;
  startDate: string;
  endDate: string;
  plannedQuantity: number;
  operators: number;
  targetEfficiencyPct: number;
  shiftCodes: string[];
  overtimeMinutesPerDay: number;
  notes?: string | null;
};

export type PlanListItem = {
  id: string;
  reference: string;
  orderId: string;
  orderNumber: string;
  lineId: string;
  lineName: string;
  startDate: string;
  endDate: string;
  plannedQuantity: number;
  operators: number;
  targetEfficiencyPct: number;
  status: PlanStatus;
  revision: number;
  hasPendingChange: boolean;
  updatedAt: string;
};

export type ApprovalRef = {
  id: string;
  status: string;
  riskTier: RiskTier;
  createdAt: string;
  decidedAt?: string | null;
  decidedByEmail?: string | null;
  decisionReason?: string | null;
};

export type FieldDiff = { field: string; from?: string | null; to?: string | null };

export type PlanChange = {
  id: string;
  kind: "created" | "edited" | "revision";
  status: "pending" | "applied" | "rejected" | "superseded";
  previous?: PlanValues | null;
  proposed: PlanValues;
  diff: FieldDiff[];
  reason?: string | null;
  approval?: ApprovalRef | null;
  requestedAt: string;
  requestedByEmail: string;
  decidedAt?: string | null;
  decidedByEmail?: string | null;
};

export type PlanTargets = {
  smv?: number | null;
  smvBasis: ProductionStyle["smvBasis"];
  hourlyTarget?: number | null;
  scheduledMinutes: number;
  workingDays: number;
  capacityAtTarget?: number | null;
  plannedMethod: "target_rate" | "even_spread";
  daily: { date: string; scheduledMinutes: number; target: number }[];
  warnings: string[];
  formula: string;
};

export type PlanProgress = {
  evaluatedAt: string;
  comparedThrough?: string | null;
  plannedToDate: number;
  actualToDate: number;
  variance: number;
  achievementPct?: number | null;
  actualTotal: number;
  remainingQuantity: number;
};

export type PlanDetail = {
  summary: PlanListItem;
  values: PlanValues;
  targets: PlanTargets;
  progress: PlanProgress;
  activationApproval?: ApprovalRef | null;
  changes: PlanChange[];
  submittedByEmail?: string | null;
  submittedAt?: string | null;
  activatedByEmail?: string | null;
  activatedAt?: string | null;
  closedReason?: string | null;
  createdByEmail: string;
  createdAt: string;
  version: number;
};

export type PlanActionResponse = { outcome: string; message: string; plan: PlanDetail };

export type PlanWrite = Partial<PlanValues> & { orderId?: string; reason?: string; expectedVersion?: number };

export type OutputEvent = {
  id: string;
  lineId: string;
  orderId: string;
  orderNumber: string;
  stage: ProductionStage;
  intervalStart: string;
  intervalEnd: string;
  productionDate: string;
  shiftCode?: string | null;
  quantity: number;
  reworkQuantity: number;
  rejectQuantity: number;
  operatorsPresent?: number | null;
  downtimeMinutes?: number | null;
  downtimeReason?: string | null;
  source: OutputSource;
  sourceRef?: string | null;
  notes?: string | null;
  isSuperseded: boolean;
  supersededById?: string | null;
  correctionOfId?: string | null;
  correctionReason?: string | null;
  recordedAt: string;
  recordedByEmail: string;
  late: boolean;
};

export type OutputWrite = {
  lineId: string;
  orderId: string;
  stage: ProductionStage;
  intervalStart: string;
  intervalEnd: string;
  quantity: number;
  reworkQuantity?: number;
  rejectQuantity?: number;
  operatorsPresent?: number | null;
  downtimeMinutes?: number | null;
  downtimeReason?: string;
  notes?: string;
};

export type OutputImportRow = {
  sourceRef?: string;
  lineId?: string;
  orderNumber?: string;
  stage?: string;
  intervalStart?: string;
  intervalEnd?: string;
  quantity?: number;
  reworkQuantity?: number;
  rejectQuantity?: number;
  operatorsPresent?: number;
  downtimeMinutes?: number;
  downtimeReason?: string;
  notes?: string;
};

export type ImportRowOutcome = {
  row: number;
  sourceRef?: string | null;
  outcome: "created" | "duplicate" | "invalid" | "not_imported";
  eventId?: string | null;
  errors?: string[] | null;
};
export type OutputImportResult = { received: number; created: number; duplicates: number; invalid: number; rows: ImportRowOutcome[] };

export type SummaryTotals = {
  actual: number;
  planned: number;
  variance: number;
  achievementPct?: number | null;
  efficiencyPct?: number | null;
  efficiencyExcludingDowntimePct?: number | null;
  earnedMinutes: number;
  availableMinutes: number;
  downtimeMinutes: number;
  rework: number;
  reject: number;
  records: number;
  scheduledPlanLineMinutes: number;
  calendarLineMinutes: number;
  capacityUtilizationPct?: number | null;
  unplannedActual: number;
};

export type SummaryRow = {
  key: string;
  label: string;
  actual: number;
  planned: number;
  variance: number;
  achievementPct?: number | null;
  efficiencyPct?: number | null;
  downtimeMinutes: number;
  rework: number;
  reject: number;
  records: number;
};

export type BottleneckStatus = "observed" | "standard_only" | "insufficient_data";

export type OrderRiskRow = {
  orderId: string;
  orderNumber: string;
  dueDate: string;
  level: RiskAssessment["level"];
  remainingQuantity: number;
  requiredRatePerLineHour?: number | null;
  observedRatePerLineHour?: number | null;
  projectedCompletion?: string | null;
};

export type LineBottleneck = {
  lineId: string;
  styleId: string;
  styleCode: string;
  status: BottleneckStatus;
  operation?: string | null;
  capacityPerHour?: number | null;
  evidence?: string | null;
};

export type ProductionSummary = {
  fromDate: string;
  toDate: string;
  timeZone: string;
  fromUtc: string;
  toUtc: string;
  evaluatedAt: string;
  comparedThrough: string;
  totals: SummaryTotals;
  byLine: SummaryRow[];
  byOrder: SummaryRow[];
  byShift: SummaryRow[];
  daily: { date: string; actual: number; planned: number; achievementPct?: number | null; efficiencyPct?: number | null; downtimeMinutes: number }[];
  wip: { lineId: string; buffers: WipBuffer[] }[];
  orderRisks: OrderRiskRow[];
  bottlenecks: LineBottleneck[];
  quality: { outputRecords: number; missingOperators: number; missingSmv: number; unplannedRecords: number; lateRecords: number; correctedRecords: number; notes: string[] };
  sources: Record<string, number>;
  definitions: Record<string, string>;
};

export type OperationCapacity = {
  operationId: string;
  sequence: number;
  name: string;
  smv: number;
  machineType?: string | null;
  observedCycleSeconds?: number | null;
  operatorsAssigned?: number | null;
  observedCapacityPerHour?: number | null;
  performancePct?: number | null;
  standardCapacityPerHour?: number | null;
  queueBefore?: number | null;
  observedAtUtc?: string | null;
  sampleCount?: number | null;
};

export type BottleneckResult = {
  status: BottleneckStatus;
  constraint?: OperationCapacity | null;
  constraintStationNo?: number | null;
  constraintCapacityPerHour?: number | null;
  operations: OperationCapacity[];
  observedOperations: number;
  totalOperations: number;
  evidence: string[];
  limitations: string[];
  missingInputs: string[];
};

export type StationLoad = { stationNo: number; operationIds: string[]; sequences: number[]; machineType?: string | null; loadMinutes: number; utilizationPct: number };

export type WorkloadMetrics = {
  stations: number;
  totalSmv: number;
  cycleTimeMinutes: number;
  capacityPerHour: number;
  balanceEfficiencyPct: number;
  balanceDelayPct: number;
  smoothnessIndex: number;
  bottleneckStationNo: number;
  stationLoads: StationLoad[];
};

export type LayoutStation = {
  stationNo: number;
  operationIds: string[];
  operations: string[];
  machineType?: string | null;
  loadMinutes: number;
  utilizationPct: number;
};

export type LineLayout = {
  id: string;
  lineId: string;
  styleId: string;
  origin: "manual" | "proposal";
  proposalId?: string | null;
  createdAt: string;
  createdByEmail: string;
  stations: LayoutStation[];
  metrics: WorkloadMetrics;
  issues: { code: string; message: string }[];
};

export type TimelinePoint = {
  start: string;
  end: string;
  label: string;
  shiftCode?: string | null;
  actual: number;
  planned: number;
  efficiencyPct?: number | null;
  downtimeMinutes: number;
  records: number;
};

export type LineDetail = {
  line: ProductionLine;
  date: string;
  shift?: string | null;
  timeZone: string;
  fromUtc: string;
  toUtc: string;
  comparedThrough: string;
  totals: SummaryTotals;
  timeline: TimelinePoint[];
  plans: PlanListItem[];
  events: OutputEvent[];
  wip: { lineId: string; buffers: WipBuffer[] }[];
  styles: { styleId: string; styleCode: string; styleName: string; bottleneck: BottleneckResult; layout?: LineLayout | null }[];
  definitions: Record<string, string>;
};

export type Observation = {
  id: string;
  lineId: string;
  styleId: string;
  operationId: string;
  operationSequence: number;
  operationName: string;
  observedAt: string;
  cycleSeconds: number;
  sampleCount: number;
  operatorsAssigned: number;
  queueBefore?: number | null;
  notes?: string | null;
  source: OutputSource;
  recordedAt: string;
  recordedByEmail: string;
};

export type BalanceResult = {
  feasible: boolean;
  status: "ok" | "missing_inputs" | "invalid_inputs" | "infeasible";
  problems: string[];
  assumptions: string[];
  proposed?: WorkloadMetrics | null;
  proposedStations: { stationNo: number; operationIds: string[] }[];
  lowerBoundCycleTimeMinutes?: number | null;
  targetCycleTimeMinutes?: number | null;
  targetMet?: boolean | null;
  stationsNeededForTarget?: number | null;
  method: string;
};

export type BalanceComparison = {
  current?: WorkloadMetrics | null;
  proposed: WorkloadMetrics;
  cycleTimeChangePct?: number | null;
  capacityChangePct?: number | null;
  balanceEfficiencyChangePts?: number | null;
  currentBottleneck?: string | null;
  proposedBottleneck: string;
};

export type BalanceProposalListItem = {
  id: string;
  reference: string;
  lineId: string;
  styleId: string;
  styleCode: string;
  workstations: number;
  status: ProposalStatus;
  cycleTimeMinutes?: number | null;
  capacityPerHour?: number | null;
  balanceEfficiencyPct?: number | null;
  createdAt: string;
  createdByEmail: string;
};

export type BalanceProposal = {
  id: string;
  reference: string;
  lineId: string;
  lineName: string;
  styleId: string;
  styleCode: string;
  styleName: string;
  workstations: number;
  targetOutputPerHour?: number | null;
  status: ProposalStatus;
  statusReason?: string | null;
  result: BalanceResult;
  proposedStations: LayoutStation[];
  current?: LineLayout | null;
  inputsChanged: boolean;
  baselineChanged: boolean;
  comparison?: BalanceComparison | null;
  approval?: ApprovalRef | null;
  createdAt: string;
  createdByEmail: string;
  submittedByEmail?: string | null;
  submittedAt?: string | null;
  appliedByEmail?: string | null;
  appliedAt?: string | null;
  version: number;
};

export type BalanceAnalysis = { result: BalanceResult; proposal?: BalanceProposal | null; current?: LineLayout | null; missingInputs: string[] };
export type ProposalActionResponse = { outcome: string; message: string; proposal: BalanceProposal };
