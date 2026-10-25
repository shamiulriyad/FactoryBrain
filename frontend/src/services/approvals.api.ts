// Server-side approval queue (GET /api/approvals, POST /api/approvals/{id}/approve|reject).
//
// The API owns the queue: decisions are atomic, role-checked (Operator or
// Admin; High risk needs Admin) and written to an append-only audit log.
// This module only fetches and submits; it never changes an approval's
// state locally. (The older src/services/approval.service.ts mutated a
// browser-memory dataset and is no longer used by the app.)

import { create } from "zustand";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import type { InsightPublic, RiskTier } from "./types";

export type ApprovalStatus = "pending_approval" | "approved" | "rejected";
export type ApprovalFilter = "pending" | "approved" | "rejected" | "all";

export type ServerApproval = {
  id: string;
  insightId: string;
  title: string;
  titleBn: string;
  reason: string | null;
  riskTier: RiskTier;
  status: ApprovalStatus;
  createdAt: string;
  decidedAt: string | null;
  decidedByEmail: string | null;
  decisionReason: string | null;
  insight: InsightPublic | null;
};

type ApprovalList = { pendingCount: number; items: ServerApproval[] };

type PendingState = {
  /** Pending items (newest first) for the Overview card. */
  pending: ServerApproval[];
  pendingCount: number;
  loaded: boolean;
  error: string | null;
};

/** Shared pending queue for the sidebar badge and the Overview card. */
export const usePendingApprovals = create<PendingState>(() => ({
  pending: [],
  pendingCount: 0,
  loaded: false,
  error: null,
}));

export async function fetchApprovals(filter: ApprovalFilter = "pending", limit = 100): Promise<ApprovalList> {
  return apiJson<ApprovalList>(`/api/approvals?status=${filter}&limit=${limit}`);
}

/** Refresh the shared pending queue. Never throws; failures land in `error`. */
export async function refreshPendingApprovals(): Promise<void> {
  try {
    const list = await fetchApprovals("pending", 20);
    usePendingApprovals.setState({ pending: list.items, pendingCount: list.pendingCount, loaded: true, error: null });
  } catch (e) {
    usePendingApprovals.setState({ loaded: true, error: (e as Error).message });
  }
}

export type DecisionResult =
  | { ok: true; approval: ServerApproval }
  | { ok: false; status: number; message: string };

/**
 * Approve or reject on the server. Rejection needs a reason (the API returns
 * 400 without one). The shared pending queue is refreshed afterwards either
 * way, so every view reflects the server's state.
 */
export async function decideApproval(id: string, decision: "approve" | "reject", reason?: string): Promise<DecisionResult> {
  try {
    const approval = await apiJson<ServerApproval>(`/api/approvals/${encodeURIComponent(id)}/${decision}`, {
      method: "POST",
      body: { reason: reason?.trim() || null },
    });
    return { ok: true, approval };
  } catch (e) {
    // 409: already decided by someone else; 403: role / high-risk rule; 400: reason missing.
    return { ok: false, status: isApiError(e) ? e.status : 0, message: errorMessage(e) };
  } finally {
    void refreshPendingApprovals();
  }
}

/** The insight to render for an approval, falling back to the approval's own fields. */
export function approvalInsight(a: ServerApproval): InsightPublic {
  if (a.insight) return a.insight;
  return {
    id: a.insightId,
    agentId: "unknown",
    agentLabel: "Agent",
    title: a.title,
    titleBn: a.titleBn,
    finding: a.reason ?? "",
    findingBn: a.reason ?? "",
    factors: [],
    recommendation: { title: a.title, titleBn: a.titleBn, action: "", actionBn: "", riskTier: a.riskTier, targetStage: "pending_approval" },
    evidence: [],
    stage: a.status,
    createdAt: a.createdAt,
    updatedAt: a.decidedAt ?? a.createdAt,
    confidence: 0,
  };
}
