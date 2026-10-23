import { dataset } from "./dataset";
import { activityService } from "./activity.service";
import type { InsightPublic, Stage } from "./types";

let pendingDecisions: Array<{
  id: string;
  insightId: string;
  decision: "approved" | "rejected";
  decidedAt: string;
  reason?: string;
}> = [];

function record(insightId: string, outcome: "approved" | "rejected", reason?: string) {
  pendingDecisions.push({
    id: `pd-${Date.now()}`,
    insightId,
    decision: outcome,
    decidedAt: new Date().toISOString(),
    reason
  });
}

function log(insight: InsightPublic, outcome: "approved" | "rejected", reason?: string) {
  activityService.push({
    actor: "user",
    actorLabel: "You",
    verb: outcome === "approved" ? "approved" : "rejected",
    verbBn: outcome === "approved" ? "অনুমোদন করেছেন" : "প্রত্যাখ্যান করেছেন",
    target: insight.recommendation.title,
    targetBn: insight.recommendation.titleBn,
    outcome: outcome === "approved" ? "approved" : "rejected",
    isoDate: new Date().toISOString()
  });
  if (reason) {
    activityService.push({
      actor: "user",
      actorLabel: "You",
      verb: "noted rejection reason",
      verbBn: "প্রত্যাখ্যানের কারণ নোট করেছেন",
      target: reason,
      targetBn: reason,
      isoDate: new Date().toISOString()
    });
  }
}

export const approvalService = {
  queue(): InsightPublic[] {
    return dataset.insights.filter((i) => i.stage === "pending_approval" || i.stage === "suggested");
  },
  pending(): InsightPublic[] {
    return dataset.insights.filter((i) => i.stage === "pending_approval");
  },
  approve(id: string): { ok: boolean; insightId: string } {
    const insight = dataset.insights.find((i) => i.id === id);
    if (!insight) return { ok: false, insightId: id };
    const prevStage: Stage = insight.stage;
    insight.stage = "executing";
    insight.updatedAt = new Date().toISOString();
    record(id, "approved");
    log(insight, "approved");
    // Simulate execution lifecycle
    setTimeout(() => {
      insight.stage = "done";
      insight.updatedAt = new Date().toISOString();
      activityService.push({
        actor: insight.agentId,
        actorLabel: insight.agentLabel,
        verb: "completed execution",
        verbBn: "সম্পাদন সম্পন্ন করেছে",
        target: insight.recommendation.title,
        targetBn: insight.recommendation.titleBn,
        outcome: "executed",
        isoDate: new Date().toISOString()
      });
      // Brain update: an approved maintenance insight lowers the machine-downtime risk node.
      lowerMachineRiskIfPresent(insight);
      setTimeout(() => {
        insight.stage = "logged";
        insight.updatedAt = new Date().toISOString();
      }, 1200);
    }, 1500);
    void prevStage;
    return { ok: true, insightId: id };
  },
  reject(id: string, reason: string): { ok: boolean; insightId: string } {
    const insight = dataset.insights.find((i) => i.id === id);
    if (!insight) return { ok: false, insightId: id };
    insight.stage = "rejected";
    insight.updatedAt = new Date().toISOString();
    record(id, "rejected", reason);
    log(insight, "rejected", reason);
    return { ok: true, insightId: id };
  },
  history() {
    return pendingDecisions;
  },
  bulkApproveLow() {
    const items = this.pending().filter((i) => i.recommendation.riskTier === "low");
    for (const i of items) this.approve(i.id);
    return items.length;
  }
};

function lowerMachineRiskIfPresent(insight: InsightPublic) {
  if (insight.agentId !== "maintenance-agent") return;
  const node = dataset.graph.nodes.find((n) => n.kind === "risk" && /(machine|downtime|bearing)/i.test(n.label));
  if (node && node.meta) {
    node.meta = { ...node.meta, severity: "medium" };
  }
}