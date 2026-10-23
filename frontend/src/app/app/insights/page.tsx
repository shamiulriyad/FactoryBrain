"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "next/navigation";
import { motion } from "framer-motion";
import { Filter as FilterIcon } from "lucide-react";
import { useT } from "@/lib/useT";
import { insightService } from "@/services/insight.service";
import { agentService } from "@/services/agent.service";
import { useBusinessStore } from "@/store/business.store";
import { Panel } from "@/components/ui/Panel";
import { InsightCard } from "@/components/insights/InsightCard";
import { EmptyState } from "@/components/ui/EmptyState";
import { Skeleton } from "@/components/ui/Skeleton";
import { AgentAvatar, AGENT_PALETTE, type AgentGlyph } from "@/components/agents/AgentAvatar";
import { cn } from "@/lib/cn";
import type { Stage, RiskTier } from "@/services/types";

const STAGE_ORDER: Stage[] = ["suggested", "pending_approval", "executing", "done", "logged"];
const RISK_OPTIONS: RiskTier[] = ["low", "medium", "high"];

export default function InsightsPage() {
  const { t, locale } = useT();
  const search = useSearchParams();
  const [agentFilter, setAgentFilter] = useState<string | null>(null);
  const [riskFilter, setRiskFilter] = useState<RiskTier | null>(null);
  const [focusedId, setFocusedId] = useState<string | null>(null);
  // Soft tick keeps the page in sync with insights that were persisted by
  // agent runs (the runs mutate the shared `dataset.insights` array on the
  // server side; the client re-derives via this re-render trigger).
  const [tick, setTick] = useState(0);
  useEffect(() => {
    const id = setInterval(() => setTick((n) => n + 1), 1500);
    return () => clearInterval(id);
  }, []);
  // Manual refresh hook for components that want to nudge us after they
  // mutate state (e.g. the agent drawer closing).
  useEffect(() => {
    const onRefresh = () => setTick((n) => n + 1);
    window.addEventListener("bunonbrain:insights-refresh", onRefresh as EventListener);
    return () => window.removeEventListener("bunonbrain:insights-refresh", onRefresh as EventListener);
  }, []);
  // tick is intentionally in the deps to re-group after server refresh; the
  // service itself doesn't reference it.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const groups = useMemo(() => insightService.groupedByStage(), [tick]);
  const agents = useMemo(() => agentService.list(), []);
  const selectedAgentId = useBusinessStore((s) => s.selectedAgentId);
  const setSelectedAgent = useBusinessStore((s) => s.setSelectedAgent);

  const focusParam = search?.get("focus");
  const filterParam = search?.get("filter");

  // Pre-filter from search query.
  useEffect(() => {
    if (filterParam === "throughput") {
      setRiskFilter(null);
      setAgentFilter("line-throughput-agent");
    } else if (filterParam === "machines") {
      setAgentFilter("maintenance-agent");
    } else if (filterParam === "brief") {
      setAgentFilter("manager-agent");
    }
  }, [filterParam]);

  // Mirror the Ask-page agent selection into the filter chip.
  useEffect(() => {
    if (selectedAgentId) setAgentFilter(selectedAgentId);
  }, [selectedAgentId]);

  // Focus handling: scroll to the insight, mark for 2s with a ring.
  useEffect(() => {
    if (!focusParam) return;
    setFocusedId(focusParam);
    setTimeout(() => {
      const el = document.querySelector(`[data-insight-id="${focusParam}"]`) as HTMLElement | null;
      if (el) el.scrollIntoView({ behavior: "smooth", block: "center" });
    }, 150);
    const t = setTimeout(() => setFocusedId(null), 2400);
    return () => clearTimeout(t);
  }, [focusParam]);

  function applyFilters(items: typeof groups.suggested) {
    return items.filter(
      (i) => (!agentFilter || i.agentId === agentFilter) && (!riskFilter || i.recommendation.riskTier === riskFilter)
    );
  }

  return (
    <div className="px-6 md:px-8 py-6 max-w-6xl mx-auto" data-tour="insights">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-display font-semibold tracking-tight">{t("insights.title")}</h1>
        </div>
        <div className="flex items-center gap-2">
          {agentFilter && (() => {
            const a = agentService.get(agentFilter);
            if (!a) return null;
            const palette = AGENT_PALETTE[a.glyph as AgentGlyph];
            return (
              <button
                type="button"
                onClick={() => { setAgentFilter(null); setSelectedAgent(null); }}
                className="inline-flex items-center gap-2 h-9 pl-1.5 pr-3 rounded-full border text-caption transition-colors"
                style={{
                  background: palette.soft,
                  borderColor: palette.ring,
                  color: palette.strong
                }}
                aria-label="Clear agent filter"
              >
                <AgentAvatar agentId={a.id} size={22} framed={false} />
                <span className="font-medium">{locale === "bn" ? a.nameBn : a.name}</span>
                <span className="text-[10px]">✕</span>
              </button>
            );
          })()}
          <select
            value={agentFilter ?? ""}
            onChange={(e) => setAgentFilter(e.target.value || null)}
            className="h-9 rounded-md bg-surface-2 border border-border-subtle px-3 text-caption text-fg-secondary"
          >
            <option value="">All agents</option>
            {agents.map((a) => (
              <option key={a.id} value={a.id}>{a.name}</option>
            ))}
          </select>
          <select
            value={riskFilter ?? ""}
            onChange={(e) => setRiskFilter((e.target.value || null) as RiskTier | null)}
            className="h-9 rounded-md bg-surface-2 border border-border-subtle px-3 text-caption text-fg-secondary"
          >
            <option value="">All risk</option>
            {RISK_OPTIONS.map((r) => (
              <option key={r} value={r}>{r}</option>
            ))}
          </select>
        </div>
      </div>

      <div className="mt-6 grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
        {STAGE_ORDER.map((stage, idx) => {
          const items = applyFilters(groups[stage]);
          return (
            <motion.section
              key={stage}
              initial={{ opacity: 0, y: 8 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.3, delay: idx * 0.04 }}
            >
              <Panel
                className={cn(stage === "pending_approval" && "border-stage-pending/40")}
                title={
                  <span className="flex items-center gap-2">
                    <span className="mono-pill text-fg-tertiary">{items.length}</span>
                    <span>{t(`insights.stages.${stage}`)}</span>
                  </span>
                }
              >
                {items.length === 0 ? (
                  <div className="space-y-2.5" aria-busy>
                    <Skeleton className="h-4 w-[60%]" />
                    <Skeleton className="h-3 w-[80%]" />
                    <Skeleton className="h-3 w-[68%]" />
                  </div>
                ) : (
                  <div className="space-y-3">
                    {items.map((i) => (
                      <InsightCard
                        key={i.id}
                        insight={i}
                        focused={focusedId === i.id}
                      />
                    ))}
                  </div>
                )}
              </Panel>
            </motion.section>
          );
        })}
      </div>
    </div>
  );
}
