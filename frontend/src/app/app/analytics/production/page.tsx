"use client";

// Production analytics from IoT production counters (pieces under each
// machine's counting rule; duplicates never counted twice) and audited
// corrections; planned vs actual only where active production plans exist.

import { Activity, Factory, GitCompare, ListChecks } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { AN, AnalyticsFilterBar, AnalyticsTabs, BarList, DataTable, KpiGrid, ScopeLine, TrendChart, pct, useAnalyticsFilters, useDefinitions } from "@/components/analytics/shared";
import { useResource } from "@/lib/useResource";
import { P } from "@/lib/access";
import type { AnalyticsGroup, ProductionAnalytics } from "@/lib/analytics";

export default function ProductionAnalyticsPage() {
  return (
    <RequirePermission perms={[P.ProductionView, P.MonitoringView]} what="Production analytics">
      <Production />
    </RequirePermission>
  );
}

function Production() {
  const { f, set, query, factories } = useAnalyticsFilters(`${AN}/production`);
  const defs = useDefinitions();
  const r = useResource<ProductionAnalytics>(`/api/analytics/production${query()}`, { pollMs: 60_000 });
  const d = r.data;
  const extra = (g: AnalyticsGroup) => [g.sharePct != null ? `${g.sharePct} %` : null, g.piecesPerHour != null ? `${g.piecesPerHour}/h` : null].filter(Boolean).join(" · ") || null;
  return (
    <div className="page" data-testid="analytics-production">
      <PageHeader eyebrow="Factory analytics" title="Production analytics"
        description="Machine-counted output from production counters, under each machine's counting rule — not camera activity predictions." />
      <AnalyticsTabs />
      <AnalyticsFilterBar f={f} set={set} factories={factories} show={{ machine: true, shift: true }} />
      {r.error && !d ? <ErrorState title="Could not load production analytics" body={r.error.message} retryLabel="Try again" onRetry={() => void r.reload()} />
        : !d ? <SkeletonRows rows={8} />
        : (
          <>
            <ScopeLine scope={d.scope} warnings={d.warnings} />
            <KpiGrid kpis={d.kpis} defs={defs} />
            <div className="grid gap-4 lg:grid-cols-2 mb-4">
              <Panel title={d.hourly.length > 0 ? "Hourly output" : "Daily output"} subtitle={d.hourly.length > 0 ? "Pieces per hour (factory time)" : "Pieces per production day; dashed = planned"} icon={<Activity size={15} />}>
                <div className="p-4"><TrendChart points={d.hourly.length > 0 ? d.hourly : d.daily} testId="production-trend" /></div>
              </Panel>
              <Panel title="Line comparison" subtitle="Click a line to drill down" icon={<GitCompare size={15} />} testId="production-lines">
                <div className="p-4"><BarList rows={d.byLine} value={(g) => g.pieces} label={(g) => g.label}
                  extra={(g) => [extra(g), g.achievementPct != null ? `${g.achievementPct} % of plan` : null].filter(Boolean).join(" · ") || null}
                  onSelect={(g) => set({ line: g.key })} /></div>
              </Panel>
              <Panel title="Shift comparison" icon={<GitCompare size={15} />}><div className="p-4"><BarList rows={d.byShift} value={(g) => g.pieces} label={(g) => g.label} extra={extra} /></div></Panel>
              <Panel title="Machine-wise output" subtitle="Click a machine to drill down" icon={<Factory size={15} />}>
                <div className="p-4"><BarList rows={d.byMachine} value={(g) => g.pieces} label={(g) => g.label} extra={extra} onSelect={(g) => set({ machine: g.key })} /></div>
              </Panel>
              {d.hourly.length > 0 && (
                <Panel title="Daily output" subtitle="Dashed = planned"><div className="p-4"><TrendChart points={d.daily} /></div></Panel>
              )}
              <Panel title="Workstations"><div className="p-4"><BarList rows={d.byWorkstation} value={(g) => g.pieces} label={(g) => g.label} extra={extra} onSelect={(g) => set({ workstation: g.key })} /></div></Panel>
              <Panel title="Departments and floors">
                <div className="p-4 grid gap-4 sm:grid-cols-2">
                  <BarList rows={d.byDepartment} value={(g) => g.pieces} label={(g) => g.label} onSelect={(g) => set({ department: g.key })} empty="No department data." />
                  <BarList rows={d.byFloor} value={(g) => g.pieces} label={(g) => g.label} onSelect={(g) => set({ floor: g.key })} empty="No floor data." />
                </div>
              </Panel>
              {d.byFactory.length > 1 && <Panel title="Factories"><div className="p-4"><BarList rows={d.byFactory} value={(g) => g.pieces} label={(g) => g.label} onSelect={(g) => set({ factory: g.key })} /></div></Panel>}
            </div>
            <div className="grid gap-4 lg:grid-cols-2">
              <Panel title="Planned vs actual" subtitle="Only where an active production plan exists" testId="production-plan">
                <DataTable columns={["Line", "Planned", "Counted", "Achievement", "Plans"]} empty="No active production plan covers these lines and dates."
                  rows={d.planVsActual.map((p) => [p.lineName, p.planned.toLocaleString("en-US"), p.actual.toLocaleString("en-US"), pct(p.achievementPct), p.plans])} />
              </Panel>
              <Panel title="Counter data quality" subtitle="Resets, readings that added nothing, corrections" icon={<ListChecks size={15} />} testId="production-quality">
                <DataTable columns={["Counter events", "Resets", "Not counted", "Suspect jumps", "Corrections", "Correction pieces"]}
                  rows={[[d.quality.countEvents, d.quality.resets, d.quality.excluded, d.quality.suspectJumps, d.quality.corrections, d.quality.correctionPieces]]} />
                <p className="px-4 pb-3 text-caption text-fg-tertiary">{d.quality.duplicateHandling}</p>
              </Panel>
            </div>
          </>
        )}
    </div>
  );
}
