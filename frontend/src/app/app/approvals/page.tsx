"use client";

import { useEffect, useMemo, useState, useCallback, useRef } from "react";
import { useSearchParams } from "next/navigation";
import { motion, AnimatePresence } from "framer-motion";
import { Check, X as XIcon, ExternalLink } from "lucide-react";
import { useT } from "@/lib/useT";
import { approvalService } from "@/services/approval.service";
import { insightService } from "@/services/insight.service";
import { activityService } from "@/services/activity.service";
import { Panel } from "@/components/ui/Panel";
import { RiskBadge } from "@/components/ui/RiskBadge";
import { StageBadge } from "@/components/ui/StatusPill";
import { EmptyState } from "@/components/ui/EmptyState";
import { Button } from "@/components/ui/Button";
import { EvidenceBlock } from "@/components/evidence/EvidenceBlock";
import { useToast } from "@/components/ui/Toast";
import { cn } from "@/lib/cn";
import type { InsightPublic, RiskTier, Stage } from "@/services/types";

type FilterAgent = string | "all";
type FilterRisk = RiskTier | "all";
type FilterStatus = "pending_approval" | "all";
type SortKey = "risk" | "recency";

const RISK_RANK: Record<RiskTier, number> = { high: 0, medium: 1, low: 2 };

export default function ApprovalsPage() {
  const { t, locale } = useT();
  const toast = useToast();
  const search = useSearchParams();
  const [tick, setTick] = useState(0);
  const [agentFilter, setAgentFilter] = useState<FilterAgent>("all");
  const [riskFilter, setRiskFilter] = useState<FilterRisk>("all");
  const [statusFilter, setStatusFilter] = useState<FilterStatus>("pending_approval");
  const [sortKey, setSortKey] = useState<SortKey>("risk");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [rejectingId, setRejectingId] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const [focusedId, setFocusedId] = useState<string | null>(null);
  const cardRefs = useRef<Record<string, HTMLDivElement | null>>({});

  // Re-derive on a soft tick so stage transitions show in real time.
  useEffect(() => {
    const id = setInterval(() => setTick((n) => n + 1), 700);
    return () => clearInterval(id);
  }, []);

  // Cross-tab nudge: when /app/agents finishes a run, bump immediately
  // (rather than waiting up to 700ms for the next soft tick).
  useEffect(() => {
    const onRefresh = () => setTick((n) => n + 1);
    window.addEventListener("bunonbrain:insights-refresh", onRefresh as EventListener);
    return () => window.removeEventListener("bunonbrain:insights-refresh", onRefresh as EventListener);
  }, []);

  // Honor ?focus=apr_xx deep links by scrolling into view + highlighting.
  useEffect(() => {
    const f = search?.get("focus");
    if (!f) return;
    setFocusedId(f);
    setTimeout(() => {
      const el = cardRefs.current[f];
      if (el) el.scrollIntoView({ behavior: "smooth", block: "center" });
    }, 120);
    const t2 = setTimeout(() => setFocusedId(null), 2200);
    return () => clearTimeout(t2);
  }, [search]);

  // tick is intentionally in the deps to re-derive after server refresh; touch
  // it here so ESLint sees a real read.
  const all = useMemo(() => { void tick; return insightService.feed(); }, [tick]);
  const items = useMemo(() => {
    void tick; // re-derive on server tick
    let filtered = all.filter((i) => statusFilter === "all" ? true : i.stage === "pending_approval");
    if (agentFilter !== "all") filtered = filtered.filter((i) => i.agentId === agentFilter);
    if (riskFilter !== "all") filtered = filtered.filter((i) => i.recommendation.riskTier === riskFilter);
    filtered.sort((a, b) => {
      if (sortKey === "risk") return RISK_RANK[a.recommendation.riskTier] - RISK_RANK[b.recommendation.riskTier];
      return +new Date(b.updatedAt) - +new Date(a.updatedAt);
    });
    return filtered;
  }, [all, agentFilter, riskFilter, statusFilter, sortKey, tick]);

  const agents = useMemo(() => {
    const seen = new Set<string>();
    return all.filter((i) => {
      if (seen.has(i.agentId)) return false;
      seen.add(i.agentId);
      return true;
    }).map((i) => ({ id: i.agentId, label: i.agentLabel }));
  }, [all]);

  // tick is intentionally in the deps to refresh after a server tick.
  const decisionsToday = useMemo(() => { void tick; return activityService.countToday(); }, [tick]);

  const approve = useCallback((id: string) => {
    const insight = all.find((i) => i.id === id);
    approvalService.approve(id);
    setTick((n) => n + 1);
    toast.push({
      title: t("approvals.approvedToast") as string,
      description: insight?.recommendation.title,
      ttlMs: 5000,
      onUndo: () => {
        if (insight) {
          insight.stage = "pending_approval";
          insight.updatedAt = new Date().toISOString();
          setTick((n) => n + 1);
        }
      }
    });
  }, [all, toast, t]);

  function reject(id: string) {
    if (!reason.trim()) return;
    const insight = all.find((i) => i.id === id);
    approvalService.reject(id, reason);
    setRejectingId(null);
    setReason("");
    setTick((n) => n + 1);
    toast.push({
      title: t("approvals.rejectedToast") as string,
      description: insight?.recommendation.title,
      ttlMs: 5000
    });
  }

  // approve is captured via the latest closure via the items array; items is
  // already in deps, so we don't list approve separately.
  const onKey = useCallback((e: KeyboardEvent) => {
    if (rejectingId) return;
    const focused = document.activeElement as HTMLElement | null;
    if (!focused) return;
    const card = focused.closest("[data-approval-card]") as HTMLElement | null;
    const id = card?.dataset.approvalCard;
    if (!id) return;
    if (e.key.toLowerCase() === "a") {
      e.preventDefault();
      approve(id);
    } else if (e.key.toLowerCase() === "r") {
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
  }, [items, rejectingId, approve]);

  useEffect(() => {
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onKey]);

  function bulkApproveLow() {
    const count = approvalService.bulkApproveLow();
    setTick((n) => n + 1);
    setSelected(new Set());
    toast.push({ title: `Approved ${count} low-risk items`, ttlMs: 3000 });
  }

  return (
    <div className="px-6 md:px-8 py-6 max-w-4xl mx-auto" data-tour="approvals">
      <div className="flex items-end justify-between gap-3 flex-wrap">
        <div>
          <h1 className="text-display font-semibold tracking-tight">{t("approvals.title")}</h1>
          <p className="mt-1 text-caption text-fg-tertiary">{t("approvals.subtitle")}</p>
        </div>
        <div className="flex items-center gap-2">
          <Select label={t("approvals.filterAgent")} value={agentFilter} onChange={(v) => setAgentFilter(v as FilterAgent)}>
            <option value="all">All agents</option>
            {agents.map((a) => (
              <option key={a.id} value={a.id}>{a.label}</option>
            ))}
          </Select>
          <Select label={t("approvals.filterRisk")} value={riskFilter} onChange={(v) => setRiskFilter(v as FilterRisk)}>
            <option value="all">All risk</option>
            <option value="high">High</option>
            <option value="medium">Medium</option>
            <option value="low">Low</option>
          </Select>
          <Select label={t("approvals.filterStatus")} value={statusFilter} onChange={(v) => setStatusFilter(v as FilterStatus)}>
            <option value="pending_approval">Pending</option>
            <option value="all">All</option>
          </Select>
          <Select label="Sort" value={sortKey} onChange={(v) => setSortKey(v as SortKey)}>
            <option value="risk">{t("approvals.sortRisk")}</option>
            <option value="recency">{t("approvals.sortRecency")}</option>
          </Select>
        </div>
      </div>

      <div className="mt-4 flex items-center justify-between">
        <p className="text-caption text-fg-tertiary">
          {items.length} pending · {decisionsToday} decisions today
        </p>
        {items.some((i) => i.recommendation.riskTier === "low") && (
          <Button variant="success" size="sm" onClick={bulkApproveLow}>
            {t("approvals.bulkApproveLow")}
          </Button>
        )}
      </div>

      {items.length === 0 ? (
        <div className="mt-8">
          <EmptyState
            title={t("approvals.empty") as string}
            body={t("approvals.emptyToday") as string}
          />
        </div>
      ) : (
        <div className="mt-6 space-y-4">
          <AnimatePresence>
            {items.map((ins) => {
              const isFocused = focusedId === ins.id;
              return (
                <motion.div
                  key={ins.id}
                  ref={(el) => { cardRefs.current[ins.id] = el; }}
                  tabIndex={0}
                  data-approval-card={ins.id}
                  layout
                  initial={{ opacity: 0, y: 8 }}
                  animate={{ opacity: 1, y: 0 }}
                  exit={{ opacity: 0, scale: 0.98 }}
                  transition={{ duration: 0.22 }}
                  className={cn(
                    "outline-none focus-visible:ring-2 focus-visible:ring-[var(--focus-ring)] rounded-md",
                    isFocused && "ring-2 ring-accent"
                  )}
                >
                  <Panel
                    title={
                      <div className="flex items-center gap-2 min-w-0">
                        <span className="mono-pill text-fg-tertiary shrink-0">{ins.agentLabel}</span>
                        <span className="text-body text-fg-primary truncate">{locale === "bn" ? ins.titleBn : ins.title}</span>
                      </div>
                    }
                    right={
                      <div className="flex items-center gap-2">
                        <StageBadge stage={ins.stage} label={t(`insights.stages.${ins.stage}`) as string} />
                        <RiskBadge tier={ins.recommendation.riskTier} label={t(`risk.${ins.recommendation.riskTier}`) as string} />
                      </div>
                    }
                  >
                    <div className="space-y-3">
                      <div>
                        <p className="mono-pill text-fg-tertiary mb-1">{t("approvals.reasoning")}</p>
                        <p className="text-body text-fg-secondary leading-6">{locale === "bn" ? ins.findingBn : ins.finding}</p>
                      </div>

                      <div>
                        <p className="mono-pill text-fg-tertiary mb-1">Recommended action</p>
                        <p className="text-body text-fg-primary">{locale === "bn" ? ins.recommendation.titleBn : ins.recommendation.title}</p>
                        <p className="mt-0.5 text-caption text-fg-secondary">{locale === "bn" ? ins.recommendation.actionBn : ins.recommendation.action}</p>
                      </div>

                      <EvidenceBlock refs={ins.evidence} />

                      <div className="flex items-center justify-end gap-2 pt-2 border-t border-border-subtle">
                        <Button variant="ghost" size="sm">
                          <ExternalLink size={12} /> {t("approvals.viewDetails")}
                        </Button>
                        <Button variant="danger" size="sm" onClick={() => setRejectingId(ins.id)}>
                          <XIcon size={12} /> {t("approvals.reject")}
                        </Button>
                        <Button variant="success" size="sm" onClick={() => approve(ins.id)}>
                          <Check size={12} /> {t("approvals.approve")}
                        </Button>
                      </div>

                      {rejectingId === ins.id && (
                        <motion.div
                          initial={{ height: 0, opacity: 0 }}
                          animate={{ height: "auto", opacity: 1 }}
                          exit={{ height: 0, opacity: 0 }}
                          className="border-t border-border-subtle pt-3"
                        >
                          <label className="block">
                            <span className="text-caption text-fg-tertiary mb-1.5 block">{t("approvals.rejectedReason")}</span>
                            <textarea
                              autoFocus
                              value={reason}
                              onChange={(e) => setReason(e.target.value)}
                              placeholder="Why are you rejecting?"
                              className="min-h-16 w-full rounded-md bg-surface-2 border border-border-subtle px-3 py-2 text-body text-fg-primary placeholder:text-fg-tertiary focus:outline-none focus:border-border-strong"
                            />
                          </label>
                          <div className="mt-2 flex items-center justify-end gap-2">
                            <Button variant="ghost" size="sm" onClick={() => { setRejectingId(null); setReason(""); }}>
                              {t("common.cancel")}
                            </Button>
                            <Button variant="danger" size="sm" disabled={!reason.trim()} onClick={() => reject(ins.id)}>
                              {t("approvals.reject")}
                            </Button>
                          </div>
                        </motion.div>
                      )}
                    </div>
                  </Panel>
                </motion.div>
              );
            })}
          </AnimatePresence>
        </div>
      )}
    </div>
  );
}

function Select({ label, value, onChange, children }: { label: string; value: string; onChange: (v: string) => void; children: React.ReactNode }) {
  return (
    <label className="text-caption text-fg-tertiary flex items-center gap-1.5">
      <span className="hidden md:inline">{label}</span>
      <select
        value={value}
        onChange={(e) => onChange(e.target.value)}
        className="h-9 rounded-md bg-surface-2 border border-border-subtle px-2 text-caption text-fg-primary"
      >
        {children}
      </select>
    </label>
  );
}