"use client";

// Insights stored on the server: written by agent runs, QC inspections and
// the production rules (GET /api/insights), grouped by stage. Decisions on
// the ones that need approval happen on the Approvals page.

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { useSearchParams } from "next/navigation";
import { Lightbulb, RefreshCw } from "lucide-react";
import { useT } from "@/lib/useT";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { PageHeader } from "@/components/ui/PageHeader";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { InsightCard } from "@/components/insights/InsightCard";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import type { AgentDefinition } from "@/services/contracts";
import type { InsightPublic, Stage, RiskTier } from "@/services/types";

const STAGE_ORDER: Stage[] = ["suggested", "pending_approval", "approved", "executing", "done", "logged", "rejected", "failed"];
const RISK_OPTIONS: RiskTier[] = ["low", "medium", "high"];
const FILTER_AGENT: Record<string, string> = { throughput: "line-throughput-agent", machines: "maintenance-agent", brief: "manager-agent" };

type InsightList = { total: number; items: InsightPublic[] };

export default function InsightsPage() {
  const { t } = useT();
  const search = useSearchParams();
  const focusParam = search?.get("focus");
  const filterParam = search?.get("filter");
  const [agentFilter, setAgentFilter] = useState<string>(() => (filterParam && FILTER_AGENT[filterParam]) || "");
  const [riskFilter, setRiskFilter] = useState<string>("");
  const [focusedId, setFocusedId] = useState<string | null>(null);

  const path = useMemo(() => {
    const p = new URLSearchParams({ limit: "200" });
    if (agentFilter) p.set("agentId", agentFilter);
    if (riskFilter) p.set("risk", riskFilter);
    return `/api/insights?${p}`;
  }, [agentFilter, riskFilter]);
  const list = useResource<InsightList>(path, { pollMs: 60_000 });
  const agents = useResource<AgentDefinition[]>("/api/agents");

  const groups = useMemo(() => {
    const g = new Map<Stage, InsightPublic[]>();
    for (const i of list.data?.items ?? []) g.set(i.stage, [...(g.get(i.stage) ?? []), i]);
    return g;
  }, [list.data]);
  const stages = STAGE_ORDER.filter((s) => (groups.get(s)?.length ?? 0) > 0 || ["suggested", "pending_approval", "done"].includes(s));

  // Deep link from a floor alert: scroll to the insight and ring it briefly.
  useEffect(() => {
    if (!focusParam || !list.data) return;
    setFocusedId(focusParam);
    const scroll = setTimeout(() => {
      document.querySelector(`[data-insight-id="${focusParam}"]`)?.scrollIntoView({ behavior: "smooth", block: "center" });
    }, 150);
    const clear = setTimeout(() => setFocusedId(null), 2400);
    return () => { clearTimeout(scroll); clearTimeout(clear); };
  }, [focusParam, list.data]);

  const total = list.data?.items.length ?? 0;

  return (
    <div className="page" data-tour="insights" data-testid="insights-page">
      <PageHeader
        title={t("insights.title")}
        description="Findings saved by agent runs, QC inspections and the production rules. The ones that need a decision wait on the Approvals page."
        actions={
          <div className="flex flex-wrap items-end gap-2">
            <FilterSelect label="Agent" value={agentFilter} onChange={setAgentFilter}>
              <option value="">All agents</option>
              {(agents.data ?? []).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
            </FilterSelect>
            <FilterSelect label="Risk" value={riskFilter} onChange={setRiskFilter}>
              <option value="">All risk</option>
              {RISK_OPTIONS.map((r) => <option key={r} value={r}>{r}</option>)}
            </FilterSelect>
            <Button variant="secondary" size="sm" aria-label="Refresh" onClick={() => void list.reload()}>
              <RefreshCw size={13} className={cn(list.refreshing && "animate-spin")} />
            </Button>
          </div>
        }
      />

      {list.loading ? (
        <div className="mt-6"><SkeletonRows rows={6} /></div>
      ) : list.error && !list.data ? (
        <ErrorState className="mt-6" title="Couldn't load insights" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
      ) : total === 0 ? (
        <EmptyState
          className="mt-6"
          icon={<Lightbulb size={18} />}
          title={agentFilter || riskFilter ? "No insights match these filters" : "No insights yet"}
          body={agentFilter || riskFilter
            ? "Clear the filters to see every insight."
            : "Insights appear when an agent runs (see Agents), a QC inspection flags a defect or a production rule finds something."}
        />
      ) : (
        <>
          <p className="mt-4 text-caption text-fg-tertiary">
            {total} insight{total === 1 ? "" : "s"}
            {(groups.get("pending_approval")?.length ?? 0) > 0 && (
              <> · <Link className="text-[var(--accent)]" href="/app/approvals">{groups.get("pending_approval")!.length} waiting for approval</Link></>
            )}
          </p>
          <div className="mt-4 grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
            {stages.map((stage) => {
              const items = groups.get(stage) ?? [];
              return (
                <Panel
                  key={stage}
                  className={cn(stage === "pending_approval" && "border-stage-pending/40")}
                  title={
                    <span className="flex items-center gap-2">
                      <span className="mono-pill text-fg-tertiary">{items.length}</span>
                      <span>{t(`insights.stages.${stage}`)}</span>
                    </span>
                  }
                >
                  {items.length === 0 ? (
                    <p className="text-caption text-fg-tertiary">Nothing at this stage.</p>
                  ) : (
                    <div className="space-y-3">
                      {items.map((i) => <InsightCard key={i.id} insight={i} focused={focusedId === i.id} />)}
                    </div>
                  )}
                </Panel>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
}
