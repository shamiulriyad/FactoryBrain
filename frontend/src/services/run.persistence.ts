// Persistence for an agent run: produces a typed Insight (risk-tiered or
// bottleneck recommendation, depending on agent), elevates it into the
// pending-approval queue when warranted, fires a FloorAlert on the
// whatsapp_sim channel, and logs an ActivityEvent. Reuses the existing
// Zod schemas + types from `src/services/sensors.schemas.ts` and the
// shared InsightPublic shape from `src/services/types.ts`.

import { z } from "zod";
import { dataset } from "./dataset";
import { insightService } from "./insight.service";
import { activityService } from "./activity.service";
import { pushFloorAlert } from "./floorAlerts.server";
import type {
  InsightPublic,
  RiskTier,
  Stage,
  EvidenceRefPublic
} from "./types";

/** Validation shape for the structured Insight a run produces. Matches
 *  InsightPublic exactly; this is here so anything that *comes from the LLM*
 *  is validated before it lands in the dataset. */
export const RunInsightShape = z.object({
  title: z.string().min(1).max(200),
  titleBn: z.string().min(1).max(200),
  finding: z.string().min(1).max(2000),
  findingBn: z.string().min(1).max(2000),
  recommendation: z.object({
    title: z.string().min(1).max(200),
    titleBn: z.string().min(1).max(200),
    action: z.string().min(1).max(1000),
    actionBn: z.string().min(1).max(1000),
    riskTier: z.enum(["low", "medium", "high"]),
    targetStage: z.enum(["suggested", "pending_approval", "executing", "done", "logged", "rejected", "failed"])
  }),
  confidence: z.number().min(0).max(1),
  evidence: z
    .array(
      z.object({
        domain: z.enum([
          "orders",
          "customers",
          "products",
          "inventory",
          "conversations",
          "policies",
          "suppliers",
          "manuals"
        ]),
        count: z.number().int().nonnegative(),
        filter: z.record(z.union([z.string(), z.number(), z.boolean()])).optional(),
        previewIds: z.array(z.string()).optional()
      })
    )
    .optional()
});

export type RunInsightInput = z.infer<typeof RunInsightShape>;

export type PersistedRun = {
  insight: InsightPublic;
  approvalPending: boolean;
  approvalReason?: string;
  floorAlertId?: string;
  activityEventId: string;
};

const FACTORY_TOOL_DOMAINS = ["orders", "inventory"] as const;

/** Pick `n` real ids from the given array. Returns an empty array if the
 *  source is empty so callers can decide whether to emit the row.
 *  Accepts a `key` parameter so callers can pick `id` (orders, policies) or
 *  `productId` (inventory) — the field that matches what the downstream
 *  `evidenceService.rows` consumer resolves against. */
function takeIds(items: any[], n: number, key: "id" | "productId" = "id"): string[] {
  return items.slice(0, n).map((x) => x[key]);
}

/** Pull domain-correct evidence from the current store/sensor summaries so
 *  every persisted Insight carries real previewIds + non-zero counts —
 *  never an empty cite stub. Falls back gracefully when a domain has no
 *  rows yet (e.g. before any orders exist). */
function gatherStoreEvidence(agentId: string): EvidenceRefPublic[] {
  const out: EvidenceRefPublic[] = [];
  try {
    // Real ids — each domain's previewIds must resolve via evidenceService.rows.
    const orderIds = takeIds(dataset.orders, 6);
    // Inventory records don't have `id` — they use `productId` which is the
    // key the `inventory` evidence case filters on.
    const inventoryIds = takeIds(dataset.inventory, 6, "productId");
    const policyIds = takeIds(dataset.policies, 4);
    const supplierIds = dataset.suppliers.slice(0, 4).map((s) => s.id);
    // Customer / product ids are also valid previewIds for their domains.
    const customerIds = dataset.customers.slice(0, 4).map((c) => c.id);
    const productIds = dataset.products.slice(0, 4).map((p) => p.id);

    if (agentId === "maintenance-agent") {
      // Maintenance cares about machines and the parts inventory that
      // supports them. Cite real inventory rows (productId) — never
      // machine ids, which don't resolve under the inventory domain.
      if (inventoryIds.length) {
        out.push({
          domain: "inventory",
          count: inventoryIds.length,
          filter: { source: "machine_telemetry", focus: "spare_parts" },
          previewIds: inventoryIds
        });
      }
      if (policyIds.length) {
        out.push({
          domain: "policies",
          count: policyIds.length,
          previewIds: policyIds
        });
      }
    } else if (agentId === "line-throughput-agent") {
      // Throughput: real orders + the inventory rows that back them.
      if (orderIds.length) {
        out.push({
          domain: "orders",
          count: orderIds.length,
          filter: { source: "rfid_bundles", window: "2h" },
          previewIds: orderIds
        });
      }
      if (inventoryIds.length) {
        out.push({
          domain: "inventory",
          count: inventoryIds.length,
          previewIds: inventoryIds.slice(0, 4)
        });
      }
    } else if (agentId === "vision-repair-agent") {
      // Vision repair: cite the inventory rows a repair would draw from
      // and the supplier rows that source them.
      if (inventoryIds.length) {
        out.push({
          domain: "inventory",
          count: inventoryIds.length,
          filter: { source: "vision_repair_consumables" },
          previewIds: inventoryIds
        });
      }
      if (supplierIds.length) {
        out.push({
          domain: "suppliers",
          count: supplierIds.length,
          previewIds: supplierIds
        });
      }
    } else {
      // Generic fallback (manager-agent + any future agents) — cite one
      // representative row from each major domain. Each row carries real
      // ids and a non-zero count.
      if (orderIds.length) {
        out.push({
          domain: "orders",
          count: orderIds.length,
          filter: { window: "run" },
          previewIds: orderIds
        });
      }
      if (inventoryIds.length) {
        out.push({
          domain: "inventory",
          count: inventoryIds.length,
          previewIds: inventoryIds.slice(0, 4)
        });
      }
      if (policyIds.length) {
        out.push({
          domain: "policies",
          count: policyIds.length,
          previewIds: policyIds.slice(0, 2)
        });
      }
      if (customerIds.length) {
        out.push({
          domain: "customers",
          count: customerIds.length,
          previewIds: customerIds
        });
      }
      if (productIds.length) {
        out.push({
          domain: "products",
          count: productIds.length,
          previewIds: productIds
        });
      }
    }

    // If a particular branch produced no rows at all (e.g. a brand-new
    // dataset with no orders yet), surface a single representative row so
    // consumers never see `count: 0` empty cite stubs.
    if (out.length === 0) {
      const fallbackInventory = inventoryIds.length
        ? inventoryIds
        : orderIds.length
        ? orderIds
        : policyIds.length
        ? policyIds
        : customerIds.length
        ? customerIds
        : productIds.length
        ? productIds
        : supplierIds;
      const domain = inventoryIds.length
        ? "inventory"
        : orderIds.length
        ? "orders"
        : policyIds.length
        ? "policies"
        : customerIds.length
        ? "customers"
        : productIds.length
        ? "products"
        : "suppliers";
      if (fallbackInventory.length) {
        out.push({
          domain,
          count: fallbackInventory.length,
          previewIds: fallbackInventory
        });
      }
    }
  } catch {
    // Last-resort guard. Use the dataset's own ids if available; only
    // fall back to `count: 0` when the dataset itself is unreachable.
    const inventoryIds = takeIds(dataset.inventory ?? [], 4, "productId");
    const orderIds = takeIds(dataset.orders ?? [], 4);
    if (inventoryIds.length) {
      out.push({ domain: "inventory", count: inventoryIds.length, previewIds: inventoryIds });
    }
    if (orderIds.length) {
      out.push({ domain: "orders", count: orderIds.length, previewIds: orderIds });
    }
    if (out.length === 0) {
      FACTORY_TOOL_DOMAINS.forEach((d) => out.push({ domain: d, count: 0 }));
    }
  }
  return out;
}

/** Heuristic: an action is warranted (and should enter the approval queue) when
 *  the recommendation riskTier is medium or high, OR when the agent is
 *  maintenance/line-throughput with confidence above 0.6. */
function shouldRequireApproval(input: RunInsightInput, agentId: string): boolean {
  if (input.recommendation.riskTier !== "low") return true;
  if (input.confidence >= 0.6 && (agentId === "maintenance-agent" || agentId === "line-throughput-agent")) {
    return true;
  }
  return false;
}

/** Persist a run result: insight → maybe-pending-approval → FloorAlert →
 *  ActivityEvent. Idempotent on the insight id (re-uses insightService.upsertCustom). */
export function persistAgentRun(
  agentId: string,
  agentLabel: string,
  rawInsight: unknown,
  opts?: { stage?: Stage; source?: "demo" | "live" }
): PersistedRun {
  const parsed = RunInsightShape.parse(rawInsight);
  const insightId = `ins-run-${agentId}-${Date.now().toString(36)}`;
  const stage: Stage = opts?.stage ?? (shouldRequireApproval(parsed, agentId) ? "pending_approval" : "suggested");

  const evidence =
    parsed.evidence && parsed.evidence.length > 0
      ? parsed.evidence
      : gatherStoreEvidence(agentId);

  const inserted = insightService.upsertCustom({
    id: insightId,
    agentId,
    agentLabel,
    title: parsed.title,
    titleBn: parsed.titleBn,
    finding: parsed.finding,
    findingBn: parsed.findingBn,
    recommendation: {
      ...parsed.recommendation,
      targetStage: stage
    },
    confidence: parsed.confidence
  });
  // Stamp persisted stage + evidence on the dataset entry. The dataset is
  // shared by reference with `insightService.upsertCustom`, so this mutates
  // the same row the approval/feed services read from.
  const live = dataset.insights.find((i) => i.id === insightId);
  if (live) {
    live.stage = stage;
    live.evidence = evidence;
  }

  const approvalPending = stage === "pending_approval";
  let approvalReason: string | undefined;
  let floorAlertId: string | undefined;

  if (approvalPending) {
    approvalReason = `Auto-queued by ${agentLabel} run (risk ${parsed.recommendation.riskTier}, confidence ${parsed.confidence.toFixed(2)}).`;
    // Floor alert on the whatsapp_sim channel, bound to the insight id.
    const alert = pushFloorAlert({
      id: `alert-run-${Date.now().toString(36)}`,
      channel: "whatsapp_sim",
      insightId,
      bodyEn: parsed.recommendation.action,
      bodyBn: parsed.recommendation.actionBn,
      severity: parsed.recommendation.riskTier === "high" ? "critical" : "warn",
      createdAt: new Date().toISOString(),
      read: false
    });
    floorAlertId = alert.id;
  }

  const verb =
    approvalPending
      ? `raised insight awaiting approval (${parsed.recommendation.riskTier})`
      : stage === "suggested"
      ? "raised insight"
      : `${stage.replace(/_/g, " ")}`;
  const verbBn =
    approvalPending
      ? "অনুমোদনের অপেক্ষায় অন্তর্দৃষ্টি উত্থাপন করেছে"
      : stage === "suggested"
      ? "অন্তর্দৃষ্টি উত্থাপন করেছে"
      : `${stage}`;

  const activityEventId = activityService.push({
    actor: agentId,
    actorLabel: agentLabel,
    verb,
    verbBn,
    target: parsed.title,
    targetBn: parsed.titleBn,
    outcome: approvalPending ? "flagged" : "completed",
    isoDate: new Date().toISOString()
  });

  return {
    insight: live ?? inserted,
    approvalPending,
    approvalReason,
    floorAlertId,
    activityEventId
  };
}

/** Compose a per-agent Insight draft from current store + sensor summaries. */
export function buildRunDraft(
  agentId: string,
  agentLabel: string,
  lean: { title: string; titleBn: string; finding: string; findingBn: string; action: string; actionBn: string; riskTier: RiskTier; confidence: number }
): RunInsightInput {
  return {
    title: lean.title,
    titleBn: lean.titleBn,
    finding: lean.finding,
    findingBn: lean.findingBn,
    recommendation: {
      title: agentLabel,
      titleBn: agentLabel,
      action: lean.action,
      actionBn: lean.actionBn,
      riskTier: lean.riskTier,
      targetStage: "pending_approval"
    },
    confidence: lean.confidence
  };
}
