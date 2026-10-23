import { dataset } from "./dataset";
import type { InsightPublic, Stage, RiskTier } from "./types";

export const insightService = {
  feed(opts?: { agentId?: string; risk?: RiskTier; stage?: Stage; limit?: number }): InsightPublic[] {
    let items = dataset.insights.slice();
    if (opts?.agentId) items = items.filter((i) => i.agentId === opts.agentId);
    if (opts?.risk) items = items.filter((i) => i.recommendation.riskTier === opts.risk);
    if (opts?.stage) items = items.filter((i) => i.stage === opts.stage);
    items.sort((a, b) => (b.pinned ? 1 : 0) - (a.pinned ? 1 : 0));
    if (opts?.limit) items = items.slice(0, opts.limit);
    return items;
  },
  groupedByStage(): Record<Stage, InsightPublic[]> {
    const groups: Record<Stage, InsightPublic[]> = {
      suggested: [],
      pending_approval: [],
      executing: [],
      done: [],
      logged: [],
      rejected: [],
      failed: []
    };
    for (const i of dataset.insights) {
      groups[i.stage].push(i);
    }
    return groups;
  },
  pinned(): InsightPublic[] {
    return dataset.insights.filter((i) => i.pinned);
  },
  get(id: string): InsightPublic | undefined {
    return dataset.insights.find((i) => i.id === id);
  },
  /** Push a user-saved insight into the dataset (from Ask → Save). */
  upsertCustom(input: {
    id: string;
    agentId: string;
    agentLabel: string;
    title: string;
    titleBn: string;
    finding: string;
    findingBn: string;
    recommendation: InsightPublic["recommendation"];
    confidence: number;
  }): InsightPublic {
    const existing = dataset.insights.findIndex((i) => i.id === input.id);
    const now = new Date().toISOString();
    const insight: InsightPublic = {
      ...input,
      factors: [],
      evidence: [],
      stage: "suggested",
      createdAt: now,
      updatedAt: now
    };
    if (existing >= 0) dataset.insights[existing] = insight;
    else dataset.insights.unshift(insight);
    return insight;
  }
};