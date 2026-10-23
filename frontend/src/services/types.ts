// Shared service contracts. Same shape under mock and api adapters.

import type { BusinessProfile, IngestionSourceCategory } from "@/store/business.store";

export type SourceStatus = "connected" | "available" | "pending";

export type IngestionSourcePublic = {
  id: string;
  label: string;
  labelBn: string;
  objectTypes: string[];
  status: SourceStatus;
  lastSync: string | null;
  records: number;
  /** sensor | pilot | out_of_scope — drives the grouping on /app/integrations.
   *  Inferred from the built-in SOURCE_CATEGORY map for sources stored
   *  before the category field existed. */
  category: IngestionSourceCategory;
  /** Optional user-supplied value (URL, phone, handle). */
  value?: string;
  /** Optional filename for CSV uploads. */
  filename?: string;
};

export type BrainEntityPublic = {
  id: string;
  kind:
    | "product"
    | "customer"
    | "supplier"
    | "policy"
    | "workflow"
    | "goal"
    | "risk"
    | "line"
    | "machine"
    | "order"
    | "buyer"
    | "process"
    | "target"
    | "compliance";
  label: string;
  labelBn: string;
  /** 0..1 importance weight — drives node size on the canvas. */
  weight?: number;
  meta?: Record<string, string | number>;
  /** Optional operational status, layered on top of kind colour. */
  status?: "healthy" | "at_risk" | "down";
};

/** Typed edge relationships for the factory graph. */
export type EdgeKind =
  | "assigned_to"
  | "contains"
  | "follows"
  | "has"
  | "threatens"
  | "sourced_from"
  | "shipped_to";

export type BrainEdgePublic = {
  id: string;
  source: string;
  target: string;
  weight: number;
  strong?: boolean;
  label?: string;
  kind?: EdgeKind;
};

export type BrainGraph = {
  nodes: BrainEntityPublic[];
  edges: BrainEdgePublic[];
};

export type Health = {
  revenue30: number;
  revenuePrev30: number;
  revenueTrend: number[];
  activeCustomers: number;
  activeCustomersPrev: number;
  inventoryAtRisk: number;
  inventoryAtRiskPrev: number;
  byRegion30: Array<{ region: string; revenue: number; orders: number }>;
  dhakaDip: { region: string; pct: number };
  stockoutRiskCount: number;
  churnRiskCount: number;
  churnRisePct: number;
};

export type Stage =
  | "suggested"
  | "pending_approval"
  | "executing"
  | "done"
  | "logged"
  | "rejected"
  | "failed";

export type RiskTier = "low" | "medium" | "high";

export type EvidenceRefPublic = {
  // `manuals` is a new domain used for citations from `src/data/manuals.ts`
  // — surfaces as `EvidenceBlock` rows that link to a specific manual id
  // (e.g. doc-3 = "Buttonhole calibration").
  domain:
    | "orders"
    | "customers"
    | "products"
    | "inventory"
    | "conversations"
    | "policies"
    | "suppliers"
    | "manuals";
  count: number;
  filter?: Record<string, string | number | boolean>;
  previewIds?: string[];
};

export type InsightPublic = {
  id: string;
  agentId: string;
  agentLabel: string;
  title: string;
  titleBn: string;
  finding: string;
  findingBn: string;
  factors: Array<{ label: string; labelBn: string; magnitude: string; magnitudeBn: string }>;
  recommendation: {
    title: string;
    titleBn: string;
    action: string;
    actionBn: string;
    riskTier: RiskTier;
    targetStage: Stage;
  };
  evidence: EvidenceRefPublic[];
  stage: Stage;
  createdAt: string;
  updatedAt: string;
  confidence: number;
  pinned?: boolean;
};

export type AgentPublic = {
  id: string;
  name: string;
  nameBn: string;
  purpose: string;
  purposeBn: string;
  risk: RiskTier | "per_action";
  execution: "auto" | "approval_required" | "auto_suggest_with_threshold" | "per_policy";
  model: string;
  contextSlices: string[];
  status: "ready" | "draft";
  tasksToday: number;
  recentCount: number;
  /** Visual identity for the avatar (per-agent colour + glyph). */
  glyph?: "line-throughput" | "maintenance" | "orchestrator";
};

export type ActivityItemPublic = {
  id: string;
  actor: string;
  actorLabel: string;
  verb: string;
  verbBn: string;
  target?: string;
  targetBn?: string;
  outcome?: "approved" | "rejected" | "executed" | "completed" | "failed" | "flagged";
  isoDate: string;
};

export type StreamChunk = {
  type: "block" | "factor" | "evidence" | "recommendation" | "done";
  index: number;
  payload: any;
};

export type AskContextPack = {
  query: string;
  scope?: string[];
  health: Health;
  relevantInsights: InsightPublic[];
  relevantEntities: BrainEntityPublic[];
};

export type AskAnswer = {
  taskId: string;
  analyzed: Array<{ domain: EvidenceRefPublic["domain"]; count: number; filter?: Record<string, any> }>;
  finding: string;
  findingBn: string;
  factors: Array<{ label: string; labelBn: string; magnitude: string; magnitudeBn: string }>;
  evidence: EvidenceRefPublic[];
  recommendation: {
    title: string;
    titleBn: string;
    action: string;
    actionBn: string;
    riskTier: RiskTier;
  };
  createdAt: string;
  confidence: number;
};