"use client";

import { useEffect, useMemo, useState, useCallback, useRef } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Check, ChevronRight, RefreshCw, Search, ShieldAlert, X as XIcon } from "lucide-react";
import { useT } from "@/lib/useT";
import {
  approvalInsight,
  decideApproval,
  fetchApprovals,
  type ApprovalFilter,
  type ServerApproval,
} from "@/services/approvals.api";
import { canOperate, isAdmin, useAuthStore } from "@/lib/auth";
import { apiJson, errorMessage } from "@/lib/api";
import { useOnDataChanged } from "@/lib/useResource";
import { formatDateTime, formatRelative } from "@/lib/format";
import { Panel } from "@/components/ui/Panel";
import { PageHeader } from "@/components/ui/PageHeader";
import { Badge, RISK_TONE } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { Button } from "@/components/ui/Button";
import { Drawer } from "@/components/ui/Drawer";
import { ConfirmDialog } from "@/components/ui/Modal";
import { SkeletonRows } from "@/components/ui/Status";
import { EvidenceBlock } from "@/components/evidence/EvidenceBlock";
import { useToast } from "@/components/ui/Toast";
import { cn } from "@/lib/cn";
import type { RiskTier } from "@/services/types";
import type { AuditEntry } from "@/services/contracts";

type FilterAgent = string | "all";
type FilterRisk = RiskTier | "all";
type SortKey = "risk" | "recency";

const RISK_RANK: Record<RiskTier, number> = { high: 0, medium: 1, low: 2 };
const MAX_REASON = 1000;
const PAGE_SIZE = 20;
/** The API returns at most 200 items per request (ApprovalService.MaxListLimit). */
const FETCH_LIMIT = 200;

const STATUS_LABEL: Record<ServerApproval["status"], string> = {
  pending_approval: "Pending",
  approved: "Approved",
  rejected: "Rejected",
};
const STATUS_TONE = { pending_approval: "warning", approved: "success", rejected: "danger" } as const;

/**
 * Approval queue, served and decided by the API. Decisions are final and
 * audited on the server; this page never changes an item's state locally,
 * it re-reads the queue after every decision.
 */
export default function ApprovalsPage() {
  const { t, locale } = useT();
  const toast = useToast();
  const search = useSearchParams();
  const user = useAuthStore((s) => s.user);
  const mayDecide = canOperate(user);
  const admin = isAdmin(user);

  const [rows, setRows] = useState<ServerApproval[]>([]);
  const [pendingCount, setPendingCount] = useState(0);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [agentFilter, setAgentFilter] = useState<FilterAgent>("all");
  const [riskFilter, setRiskFilter] = useState<FilterRisk>("all");
  // A deep link may point at an already-decided item: show every status then.
  const [statusFilter, setStatusFilter] = useState<ApprovalFilter>(() => (search?.get("focus") ? "all" : "pending"));
  const [sortKey, setSortKey] = useState<SortKey>("risk");
  const [query, setQuery] = useState("");
  const [visible, setVisible] = useState(PAGE_SIZE);
  const [rejectingId, setRejectingId] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const [focusedId, setFocusedId] = useState<string | null>(null);
  const [detailId, setDetailId] = useState<string | null>(null);
  const [confirmHigh, setConfirmHigh] = useState<ServerApproval | null>(null);
  const [bulkOpen, setBulkOpen] = useState(false);
  const [bulkProgress, setBulkProgress] = useState<{ done: number; total: number } | null>(null);
  const cardRefs = useRef<Record<string, HTMLDivElement | null>>({});

  const load = useCallback(async () => {
    setRefreshing(true);
    try {
      const list = await fetchApprovals(statusFilter, FETCH_LIMIT);
      setRows(list.items);
      setPendingCount(list.pendingCount);
      setLoadError(null);
    } catch (e) {
      setLoadError(errorMessage(e));
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [statusFilter]);

  useEffect(() => {
    setLoading(true);
    setVisible(PAGE_SIZE);
    void load();
  }, [load]);

  // Agent runs and vision analyses create approvals server-side; re-read
  // when another page signals one, and on a slow poll.
  useOnDataChanged(() => void load());
  useEffect(() => {
    const id = setInterval(() => {
      if (document.visibilityState !== "hidden") void load();
    }, 15000);
    return () => clearInterval(id);
  }, [load]);

  // Honor ?focus=<approval id or insight id> deep links.
  useEffect(() => {
    const f = search?.get("focus");
    if (!f || rows.length === 0) return;
    const match = rows.find((r) => r.id === f || r.insightId === f);
    if (!match) return;
    setFocusedId(match.id);
    setTimeout(() => cardRefs.current[match.id]?.scrollIntoView({ behavior: "smooth", block: "center" }), 120);
    const t2 = setTimeout(() => setFocusedId(null), 2200);
    return () => clearTimeout(t2);
  }, [search, rows]);

  const items = useMemo(() => {
    let filtered = rows.slice();
    if (agentFilter !== "all") filtered = filtered.filter((a) => approvalInsight(a).agentId === agentFilter);
    if (riskFilter !== "all") filtered = filtered.filter((a) => a.riskTier === riskFilter);
    const q = query.trim().toLowerCase();
    if (q) {
      filtered = filtered.filter((a) => {
        const ins = approvalInsight(a);
        return [a.title, a.titleBn, ins.finding, ins.recommendation.title, ins.agentLabel, a.id]
          .some((s) => s?.toLowerCase().includes(q));
      });
    }
    filtered.sort((a, b) => {
      if (sortKey === "risk" && RISK_RANK[a.riskTier] !== RISK_RANK[b.riskTier]) return RISK_RANK[a.riskTier] - RISK_RANK[b.riskTier];
      return +new Date(b.createdAt) - +new Date(a.createdAt);
    });
    return filtered;
  }, [rows, agentFilter, riskFilter, sortKey, query]);

  const agents = useMemo(() => {
    const seen = new Map<string, string>();
    for (const a of rows) {
      const ins = approvalInsight(a);
      if (!seen.has(ins.agentId)) seen.set(ins.agentId, ins.agentLabel);
    }
    return Array.from(seen, ([id, label]) => ({ id, label }));
  }, [rows]);

  /** Why this user can't decide this item, or null when they can. */
  const blockedReason = useCallback((a: ServerApproval): string | null => {
    if (a.status !== "pending_approval") return null;
    if (!mayDecide) return t("auth.forbidden") as string;
    if (a.riskTier === "high" && !admin) return "High-risk approvals need an Admin.";
    return null;
  }, [mayDecide, admin, t]);

  const decide = useCallback(async (a: ServerApproval, decision: "approve" | "reject", note?: string) => {
    if (busyId) return; // one decision at a time; prevents double submits
    setBusyId(a.id);
    const result = await decideApproval(a.id, decision, note);
    setBusyId(null);
    const title = approvalInsight(a).recommendation.title || a.title;
    if (result.ok) {
      toast.push({
        title: (decision === "approve" ? t("approvals.approvedToast") : t("approvals.rejectedToast")) as string,
        description: title,
      });
      if (decision === "reject") {
        setRejectingId(null);
        setReason("");
      }
    } else {
      toast.push({
        tone: "error",
        title: result.status === 409 ? "Already decided" : result.status === 403 ? "Not allowed" : "Decision not saved",
        description: result.status === 409 ? `${result.message} The list has been refreshed.` : result.message,
      });
    }
    setConfirmHigh(null);
    await load();
  }, [busyId, load, toast, t]);

  const requestApprove = useCallback((a: ServerApproval) => {
    // High-risk decisions are final and Admin-only: ask once more.
    if (a.riskTier === "high") setConfirmHigh(a);
    else void decide(a, "approve");
  }, [decide]);

  const onKey = useCallback((e: KeyboardEvent) => {
    if (rejectingId || confirmHigh || detailId || bulkOpen) return;
    const target = e.target as HTMLElement | null;
    if (target && (target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.tagName === "SELECT")) return;
    const card = target?.closest("[data-approval-card]") as HTMLElement | null;
    const id = card?.dataset.approvalCard;
    if (!id) return;
    const current = items.find((it) => it.id === id);
    if (!current) return;
    const actionable = current.status === "pending_approval" && !blockedReason(current);
    if (e.key.toLowerCase() === "a" && actionable) {
      e.preventDefault();
      requestApprove(current);
    } else if (e.key.toLowerCase() === "r" && actionable) {
      e.preventDefault();
      setRejectingId(id);
    } else if (e.key === "j" || e.key === "k") {
      e.preventDefault();
      const idx = items.findIndex((it) => it.id === id);
      const next = items[e.key === "j" ? idx + 1 : idx - 1];
      if (next) {
        cardRefs.current[next.id]?.focus();
        cardRefs.current[next.id]?.scrollIntoView({ block: "nearest", behavior: "smooth" });
      }
    }
  }, [items, rejectingId, confirmHigh, detailId, bulkOpen, blockedReason, requestApprove]);

  useEffect(() => {
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onKey]);

  const lowRiskPending = items.filter((a) => a.status === "pending_approval" && a.riskTier === "low" && !blockedReason(a));

  async function bulkApproveLow() {
    const targets = lowRiskPending.slice();
    setBulkProgress({ done: 0, total: targets.length });
    let approved = 0;
    const failures: string[] = [];
    for (const a of targets) {
      const result = await decideApproval(a.id, "approve");
      if (result.ok) approved++;
      else failures.push(result.message);
      setBulkProgress({ done: approved + failures.length, total: targets.length });
    }
    setBulkProgress(null);
    setBulkOpen(false);
    toast.push({
      tone: failures.length ? "error" : "success",
      title: `Approved ${approved} of ${targets.length} low-risk items`,
      description: failures.length ? `${failures.length} not saved: ${failures[0]}` : undefined,
    });
    await load();
  }

  const shown = items.slice(0, visible);
  const detail = detailId ? rows.find((r) => r.id === detailId) ?? null : null;

  return (
    <div className="page page-narrow" data-tour="approvals">
      <PageHeader
        eyebrow="Operations"
        title={t("approvals.title")}
        description="Agent and vision recommendations that need a person to approve or reject. Decisions are final and recorded in the audit log."
        actions={
          <>
            {lowRiskPending.length > 0 && (
              <Button variant="success" size="sm" onClick={() => setBulkOpen(true)} data-testid="approvals-bulk">
                <Check size={13} /> {t("approvals.bulkApproveLow")} ({lowRiskPending.length})
              </Button>
            )}
            <Button variant="secondary" size="sm" onClick={() => void load()} disabled={refreshing} aria-label="Refresh approvals">
              <RefreshCw size={13} className={cn(refreshing && "animate-spin")} /> Refresh
            </Button>
          </>
        }
      />

      <RolePolicyNote mayDecide={mayDecide} admin={admin} />

      <div className="mt-5 surface p-3 flex flex-wrap items-center gap-x-4 gap-y-2">
        <div className="relative flex-1 min-w-[180px]">
          <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
          <input
            type="search"
            value={query}
            onChange={(e) => { setQuery(e.target.value); setVisible(PAGE_SIZE); }}
            placeholder="Search title, finding or id"
            aria-label="Search approvals"
            className="control !h-8 !pl-8 !text-caption"
          />
        </div>
        <FilterSelect label={t("approvals.filterStatus")} value={statusFilter} onChange={(v) => setStatusFilter(v as ApprovalFilter)}>
          <option value="pending">Pending</option>
          <option value="approved">Approved</option>
          <option value="rejected">Rejected</option>
          <option value="all">All</option>
        </FilterSelect>
        <FilterSelect label={t("approvals.filterRisk")} value={riskFilter} onChange={(v) => setRiskFilter(v as FilterRisk)}>
          <option value="all">All</option>
          <option value="high">High</option>
          <option value="medium">Medium</option>
          <option value="low">Low</option>
        </FilterSelect>
        <FilterSelect label={t("approvals.filterAgent")} value={agentFilter} onChange={(v) => setAgentFilter(v as FilterAgent)}>
          <option value="all">All agents</option>
          {agents.map((a) => (
            <option key={a.id} value={a.id}>{a.label}</option>
          ))}
        </FilterSelect>
        <FilterSelect label="Sort" value={sortKey} onChange={(v) => setSortKey(v as SortKey)}>
          <option value="risk">{t("approvals.sortRisk")}</option>
          <option value="recency">{t("approvals.sortRecency")}</option>
        </FilterSelect>
      </div>

      <p className="mt-3 text-caption text-fg-tertiary" data-testid="approvals-counts">
        {items.length} shown · {pendingCount} pending on the server
        {rows.length >= FETCH_LIMIT && ` · showing the newest ${FETCH_LIMIT}`}
      </p>

      {loadError && (
        <Callout tone="danger" className="mt-3" title="Could not load approvals" action={<Button size="sm" onClick={() => void load()}>Retry</Button>}>
          {loadError}
        </Callout>
      )}

      {loading ? (
        <div className="mt-4"><SkeletonRows rows={4} rowHeight="h-36" /></div>
      ) : items.length === 0 && !loadError ? (
        <div className="mt-4 surface">
          <EmptyState
            icon={<ShieldAlert size={22} />}
            title={statusFilter === "pending" ? (t("approvals.empty") as string) : "No approvals match these filters."}
            body={statusFilter === "pending" ? (t("approvals.emptyToday") as string) : "Change the status, risk or agent filter, or clear the search."}
          />
        </div>
      ) : (
        <div className="mt-4 space-y-3">
          {shown.map((a) => {
            const ins = approvalInsight(a);
            const blocked = blockedReason(a);
            const pending = a.status === "pending_approval";
            return (
              <div
                key={a.id}
                ref={(el) => { cardRefs.current[a.id] = el; }}
                tabIndex={0}
                data-approval-card={a.id}
                aria-label={`${a.title} — ${STATUS_LABEL[a.status]}, ${a.riskTier} risk`}
                className={cn(
                  "surface outline-none focus-visible:ring-2 focus-visible:ring-[var(--focus-ring)] transition-shadow",
                  focusedId === a.id && "ring-2 ring-accent",
                  pending && a.riskTier === "high" && "border-l-[3px] border-l-[var(--risk-high)]"
                )}
              >
                <div className="p-4 md:p-5">
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div className="min-w-0">
                      <div className="flex flex-wrap items-center gap-2">
                        <Badge tone={STATUS_TONE[a.status]} dot>{STATUS_LABEL[a.status]}</Badge>
                        <Badge tone={RISK_TONE[a.riskTier]}>{t(`risk.${a.riskTier}`)}</Badge>
                        <span className="text-caption text-fg-tertiary">{ins.agentLabel}</span>
                        <span className="text-caption text-fg-tertiary" title={formatDateTime(a.createdAt, locale)}>· {formatRelative(a.createdAt, locale)}</span>
                      </div>
                      <h2 className="mt-2 text-[15px] leading-[22px] font-semibold text-fg-primary">{locale === "bn" ? a.titleBn || a.title : a.title}</h2>
                    </div>
                    <Button variant="ghost" size="sm" onClick={() => setDetailId(a.id)} aria-label={`Details for ${a.title}`}>
                      Details <ChevronRight size={13} />
                    </Button>
                  </div>

                  <div className="mt-3 grid gap-3 md:grid-cols-2">
                    <div>
                      <p className="eyebrow mb-1">{t("approvals.reasoning")}</p>
                      <p className="text-body text-fg-secondary line-clamp-3">{(locale === "bn" ? ins.findingBn : ins.finding) || "—"}</p>
                    </div>
                    <div>
                      <p className="eyebrow mb-1">Recommended action</p>
                      <p className="text-body text-fg-primary">{locale === "bn" ? ins.recommendation.titleBn : ins.recommendation.title}</p>
                      <p className="mt-0.5 text-caption text-fg-secondary">{locale === "bn" ? ins.recommendation.actionBn : ins.recommendation.action}</p>
                    </div>
                  </div>

                  {!pending && (
                    <p className="mt-3 text-caption text-fg-secondary" data-testid="approval-decision">
                      {a.status === "approved" ? "Approved" : "Rejected"} by {a.decidedByEmail ?? "unknown"}
                      {a.decidedAt ? ` · ${formatDateTime(a.decidedAt, locale)}` : ""}
                      {a.decisionReason ? ` · “${a.decisionReason}”` : ""}
                    </p>
                  )}

                  {pending && (
                    <div className="mt-4 pt-3 border-t border-border-subtle flex flex-wrap items-center justify-end gap-2">
                      {blocked && (
                        <span className="mr-auto inline-flex items-center gap-1.5 text-caption text-fg-tertiary" data-testid="approval-blocked">
                          {blocked}
                        </span>
                      )}
                      <Button
                        variant="danger"
                        size="sm"
                        data-testid="approval-reject"
                        disabled={!!blocked || busyId === a.id}
                        onClick={() => { setRejectingId(a.id); setReason(""); }}
                      >
                        <XIcon size={13} /> {t("approvals.reject")}
                      </Button>
                      <Button
                        variant="success"
                        size="sm"
                        data-testid="approval-approve"
                        disabled={!!blocked || busyId === a.id}
                        onClick={() => requestApprove(a)}
                      >
                        <Check size={13} /> {busyId === a.id ? "Saving…" : t("approvals.approve")}
                      </Button>
                    </div>
                  )}

                  {rejectingId === a.id && (
                    <div className="mt-3 rounded-[var(--radius-control)] border border-[var(--risk-high-border)] bg-[var(--risk-high-soft)] p-3">
                      <label className="block">
                        <span className="field-label">{t("approvals.rejectedReason")} (required)</span>
                        <textarea
                          autoFocus
                          data-testid="approval-reason"
                          value={reason}
                          maxLength={MAX_REASON}
                          onChange={(e) => setReason(e.target.value)}
                          placeholder="Why are you rejecting? This is saved in the audit log."
                          className="control min-h-20"
                        />
                      </label>
                      <div className="mt-2 flex items-center justify-between gap-2">
                        <span className="text-caption text-fg-tertiary">{reason.length}/{MAX_REASON}</span>
                        <div className="flex items-center gap-2">
                          <Button variant="ghost" size="sm" onClick={() => { setRejectingId(null); setReason(""); }} disabled={busyId === a.id}>
                            {t("common.cancel")}
                          </Button>
                          <Button
                            variant="danger"
                            size="sm"
                            data-testid="approval-confirm-reject"
                            disabled={!reason.trim() || busyId === a.id}
                            onClick={() => void decide(a, "reject", reason)}
                          >
                            {busyId === a.id ? "Saving…" : t("approvals.reject")}
                          </Button>
                        </div>
                      </div>
                    </div>
                  )}
                </div>
              </div>
            );
          })}
          {items.length > visible && (
            <div className="flex justify-center pt-2">
              <Button variant="secondary" onClick={() => setVisible((v) => v + PAGE_SIZE)}>
                Show {Math.min(PAGE_SIZE, items.length - visible)} more ({items.length - visible} hidden)
              </Button>
            </div>
          )}
        </div>
      )}

      <Drawer
        open={!!detail}
        onClose={() => setDetailId(null)}
        title={detail ? "Approval details" : null}
        width="w-full sm:w-[34rem]"
      >
        {detail && <ApprovalDetail approval={detail} admin={admin} />}
      </Drawer>

      <ConfirmDialog
        open={!!confirmHigh}
        title="Approve a high-risk action?"
        description="This decision is final and is recorded in the audit log under your account."
        confirmLabel="Approve"
        tone="success"
        busy={!!confirmHigh && busyId === confirmHigh.id}
        onConfirm={() => confirmHigh && void decide(confirmHigh, "approve")}
        onCancel={() => setConfirmHigh(null)}
        testId="approval-high-confirm"
      >
        {confirmHigh && (
          <div className="space-y-2">
            <p className="text-body font-medium text-fg-primary">{confirmHigh.title}</p>
            <p className="text-caption text-fg-secondary">{approvalInsight(confirmHigh).recommendation.action}</p>
          </div>
        )}
      </ConfirmDialog>

      <ConfirmDialog
        open={bulkOpen}
        title={`Approve ${lowRiskPending.length} low-risk items?`}
        description="Each item is approved separately on the server and audited under your account. Items someone else decides first are skipped."
        confirmLabel={bulkProgress ? `Approving ${bulkProgress.done}/${bulkProgress.total}…` : "Approve all"}
        tone="success"
        busy={!!bulkProgress}
        onConfirm={() => void bulkApproveLow()}
        onCancel={() => setBulkOpen(false)}
      >
        <ul className="max-h-48 overflow-y-auto space-y-1 text-caption text-fg-secondary">
          {lowRiskPending.map((a) => <li key={a.id}>• {a.title}</li>)}
        </ul>
      </ConfirmDialog>
    </div>
  );
}

function RolePolicyNote({ mayDecide, admin }: { mayDecide: boolean; admin: boolean }) {
  if (admin) {
    return (
      <Callout tone="info" className="mt-5" testId="approvals-policy">
        As an Admin you can decide every approval, including high-risk ones.
      </Callout>
    );
  }
  if (mayDecide) {
    return (
      <Callout tone="info" className="mt-5" testId="approvals-policy">
        Operators can approve or reject low- and medium-risk items. <strong>High-risk items need an Admin</strong> — the server refuses them for other roles.
      </Callout>
    );
  }
  return (
    <Callout tone="warning" className="mt-5" testId="approvals-policy">
      You have read-only access. Operators and Admins decide approvals.
    </Callout>
  );
}

function ApprovalDetail({ approval: a, admin }: { approval: ServerApproval; admin: boolean }) {
  const { t, locale } = useT();
  const ins = approvalInsight(a);
  const [audit, setAudit] = useState<AuditEntry[] | null>(null);
  const [auditError, setAuditError] = useState<string | null>(null);

  useEffect(() => {
    if (!admin) return;
    let cancelled = false;
    setAudit(null);
    setAuditError(null);
    apiJson<AuditEntry[]>(`/api/audit?entityType=approval&entityId=${encodeURIComponent(a.id)}&limit=50`)
      .then((rows) => !cancelled && setAudit(rows))
      .catch((e) => !cancelled && setAuditError(errorMessage(e)));
    return () => { cancelled = true; };
  }, [a.id, admin]);

  return (
    <div className="p-4 md:p-5 space-y-5" data-testid="approval-detail">
      <div>
        <div className="flex flex-wrap items-center gap-2">
          <Badge tone={STATUS_TONE[a.status]} dot>{STATUS_LABEL[a.status]}</Badge>
          <Badge tone={RISK_TONE[a.riskTier]}>{t(`risk.${a.riskTier}`)} risk</Badge>
        </div>
        <h3 className="mt-2 text-title font-semibold text-fg-primary">{locale === "bn" ? a.titleBn || a.title : a.title}</h3>
        {a.reason && <p className="mt-1 text-caption text-fg-tertiary">Queued because: {a.reason}</p>}
      </div>

      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-caption">
        <dt className="text-fg-tertiary">Approval id</dt><dd className="font-mono text-fg-secondary break-all">{a.id}</dd>
        <dt className="text-fg-tertiary">Insight id</dt><dd className="font-mono text-fg-secondary break-all">{a.insightId}</dd>
        <dt className="text-fg-tertiary">Agent</dt><dd className="text-fg-secondary">{ins.agentLabel}</dd>
        <dt className="text-fg-tertiary">Created</dt><dd className="text-fg-secondary">{formatDateTime(a.createdAt, locale)}</dd>
        {a.status !== "pending_approval" && (
          <>
            <dt className="text-fg-tertiary">Decided</dt>
            <dd className="text-fg-secondary">{formatDateTime(a.decidedAt, locale)} by {a.decidedByEmail ?? "unknown"}</dd>
            {a.decisionReason && (<><dt className="text-fg-tertiary">Reason</dt><dd className="text-fg-secondary">{a.decisionReason}</dd></>)}
          </>
        )}
        {ins.confidence > 0 && (<><dt className="text-fg-tertiary">Agent confidence</dt><dd className="text-fg-secondary">{Math.round(ins.confidence * 100)}%</dd></>)}
      </dl>

      <section>
        <p className="eyebrow mb-1">{t("approvals.reasoning")}</p>
        <p className="text-body text-fg-secondary leading-6">{(locale === "bn" ? ins.findingBn : ins.finding) || "—"}</p>
      </section>

      {ins.factors.length > 0 && (
        <section>
          <p className="eyebrow mb-1.5">Contributing factors</p>
          <ul className="space-y-1">
            {ins.factors.map((f, i) => (
              <li key={i} className="flex justify-between gap-3 text-caption">
                <span className="text-fg-secondary">{locale === "bn" ? f.labelBn : f.label}</span>
                <span className="text-fg-primary font-medium">{locale === "bn" ? f.magnitudeBn : f.magnitude}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section>
        <p className="eyebrow mb-1">Recommended action</p>
        <p className="text-body text-fg-primary">{locale === "bn" ? ins.recommendation.titleBn : ins.recommendation.title}</p>
        <p className="mt-0.5 text-caption text-fg-secondary">{locale === "bn" ? ins.recommendation.actionBn : ins.recommendation.action}</p>
      </section>

      {ins.evidence.length > 0 && <EvidenceBlock refs={ins.evidence} />}

      <section>
        <div className="flex items-center justify-between">
          <p className="eyebrow">Audit trail</p>
          {admin && <Link href={`/app/audit?entityType=approval&entityId=${encodeURIComponent(a.id)}`} className="text-caption text-accent hover:underline">Open in audit log</Link>}
        </div>
        {!admin ? (
          <p className="mt-1.5 text-caption text-fg-tertiary">Only Admins can read the audit log.</p>
        ) : auditError ? (
          <p className="mt-1.5 text-caption text-[var(--risk-high)]">{auditError}</p>
        ) : audit === null ? (
          <p className="mt-1.5 text-caption text-fg-tertiary">Loading…</p>
        ) : audit.length === 0 ? (
          <p className="mt-1.5 text-caption text-fg-tertiary" data-testid="approval-audit-empty">No decisions recorded yet.</p>
        ) : (
          <ol className="mt-2 space-y-2" data-testid="approval-audit">
            {audit.map((e) => (
              <li key={e.id} className="surface-2 p-2.5 text-caption">
                <p className="text-fg-primary"><span className="font-mono">{e.action}</span> · {e.fromState ?? "—"} → {e.toState ?? "—"}</p>
                <p className="mt-0.5 text-fg-tertiary">{e.actorEmail} ({e.actorRole}) · {formatDateTime(e.occurredAt, locale)}</p>
                {e.reason && <p className="mt-0.5 text-fg-secondary">“{e.reason}”</p>}
              </li>
            ))}
          </ol>
        )}
      </section>
    </div>
  );
}
