"use client";

// Operational insights: rule-based findings with their evidence, data
// sources, reliability and what to look into. They never claim a root cause.

import Link from "next/link";
import { Lightbulb, ShieldCheck } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { AN, ANALYTICS_PERMS, AnalyticsFilterBar, AnalyticsTabs, ScopeLine, useAnalyticsFilters } from "@/components/analytics/shared";
import { useResource } from "@/lib/useResource";
import { MON, dhaka } from "@/lib/monitoring";
import { INSIGHT_LABEL, type OperationalInsights } from "@/lib/analytics";

const TONE = { HIGH: "danger", MEDIUM: "warning", LOW: "neutral" } as const;

export default function InsightsPage() {
  return (
    <RequirePermission perms={ANALYTICS_PERMS} what="Operational insights">
      <InsightsView />
    </RequirePermission>
  );
}

function InsightsView() {
  const { f, set, query, factories } = useAnalyticsFilters(`${AN}/insights`);
  const r = useResource<OperationalInsights>(`/api/analytics/insights${query()}`, { pollMs: 120_000 });
  const d = r.data;
  return (
    <div className="page" data-testid="analytics-insights">
      <PageHeader eyebrow="Factory analytics" title="Operational insights"
        description="Rule-based pointers to where to look — each with its evidence, data sources and reliability. Human review decides what they mean." />
      <AnalyticsTabs />
      <AnalyticsFilterBar f={f} set={set} factories={factories} show={{ machine: true, eventSource: false }} />
      {r.error && !d ? <ErrorState title="Could not load insights" body={r.error.message} retryLabel="Try again" onRetry={() => void r.reload()} />
        : !d ? <SkeletonRows rows={6} />
        : (
          <>
            <Callout tone="info" className="mb-3">{d.note}</Callout>
            <ScopeLine scope={d.scope} warnings={d.warnings} />
            {d.insights.length === 0
              ? <Panel title="No findings"><p className="p-4 text-caption text-fg-tertiary">No rule found anything to look into for this scope and period (see which rules could run below).</p></Panel>
              : (
                <div className="space-y-3 mb-4" data-testid="insights-list">
                  {d.insights.map((i) => (
                    <Panel key={i.id} title={i.title} subtitle={`${INSIGHT_LABEL[i.type] ?? i.type} · detected ${dhaka(i.detectedAt)}`} icon={<Lightbulb size={15} />}
                      right={<span className="flex gap-1"><Badge tone={TONE[i.severity]}>{i.severity.toLowerCase()}</Badge><Badge tone={i.reliability === "HIGH" ? "success" : "neutral"}>reliability {i.reliability.toLowerCase()}</Badge></span>}>
                      <div className="p-4 grid gap-3 md:grid-cols-2 text-caption">
                        <div>
                          <p className="text-fg-tertiary mb-1">Evidence</p>
                          <ul className="space-y-0.5">{i.evidence.map((e) => <li key={e.label}><span className="text-fg-secondary">{e.label}:</span> <span className="font-medium tabular-nums">{e.value}</span></li>)}</ul>
                          <p className="text-fg-tertiary mt-2">Data: {i.dataSources.join(", ")}</p>
                          <p className="text-fg-tertiary">{i.reliabilityReason}</p>
                        </div>
                        <div className="space-y-2">
                          <p><span className="text-fg-tertiary">Look into: </span>{i.recommendation}</p>
                          <p className="text-fg-tertiary">{i.caveat}</p>
                          <p className="flex flex-wrap gap-2">
                            {i.lineId && <button type="button" className="text-[var(--accent)]" onClick={() => set({ line: i.lineId! })}>Filter to {i.lineName}</button>}
                            {i.machineId && <Link className="text-[var(--accent)]" href={`${MON}/machines/${i.machineId}`}>Open machine {i.machineCode}</Link>}
                          </p>
                        </div>
                      </div>
                    </Panel>
                  ))}
                </div>
              )}
            <Panel title="Rules" subtitle="Which rules could run for your role and data" icon={<ShieldCheck size={15} />} testId="insights-rules">
              <ul className="p-4 grid gap-1 sm:grid-cols-2 text-caption">
                {d.rules.map((x) => (
                  <li key={x.type} className="flex items-center gap-2"><Badge tone={x.evaluated ? "success" : "neutral"}>{x.evaluated ? "checked" : "not checked"}</Badge>
                    {INSIGHT_LABEL[x.type] ?? x.type}{x.reason ? <span className="text-fg-tertiary"> — {x.reason}</span> : null}</li>
                ))}
              </ul>
            </Panel>
          </>
        )}
    </div>
  );
}
