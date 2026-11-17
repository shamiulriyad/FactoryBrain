"use client";

// Factory overview: workforce, production, machines now, overtime, open
// alerts and when each data source last delivered data. Every KPI shows its
// definition and denominator; missing data reads "No data", never zero.

import Link from "next/link";
import { ArrowRight, Database } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { AN, ANALYTICS_PERMS, AnalyticsFilterBar, AnalyticsTabs, DataTable, KpiCard, ScopeLine, useAnalyticsFilters, useDefinitions } from "@/components/analytics/shared";
import { useResource } from "@/lib/useResource";
import { ago } from "@/lib/monitoring";
import type { FactoryOverview } from "@/lib/analytics";

export default function AnalyticsOverviewPage() {
  return (
    <RequirePermission perms={ANALYTICS_PERMS} what="Factory analytics">
      <Overview />
    </RequirePermission>
  );
}

function Overview() {
  const { f, set, query, factories } = useAnalyticsFilters(AN);
  const defs = useDefinitions();
  const r = useResource<FactoryOverview>(`/api/analytics/overview${query()}`, { pollMs: 60_000 });
  const d = r.data;
  return (
    <div className="page" data-testid="analytics-overview">
      <PageHeader eyebrow="Factory analytics" title="Factory overview"
        description="Measured facts from attendance, production counters, machine sensors and alerts — with their definitions, denominators and data coverage." />
      <AnalyticsTabs />
      <AnalyticsFilterBar f={f} set={set} factories={factories} show={{ workstation: false }} />
      {r.error && !d ? <ErrorState title="Could not load analytics" body={r.error.message} retryLabel="Try again" onRetry={() => void r.reload()} />
        : !d ? <SkeletonRows rows={8} />
        : (
          <>
            <ScopeLine scope={d.scope} warnings={d.warnings} />
            <div className="space-y-4">
              {d.groups.map((g) => (
                <Panel key={g.id} title={g.title} subtitle={g.note ?? undefined} testId={`overview-${g.id}`}
                  right={g.link ? <Link href={`${g.link}${query()}`} className="text-caption text-[var(--accent)] inline-flex items-center gap-1">Details <ArrowRight size={12} /></Link> : undefined}>
                  <div className="p-4 grid gap-3 grid-cols-2 md:grid-cols-3 xl:grid-cols-4">{g.kpis.map((k) => <KpiCard key={k.metricId} k={k} defs={defs} />)}</div>
                </Panel>
              ))}
              <Panel title="Data coverage" subtitle="How much data each source delivered in this scope and period, and when it last did" icon={<Database size={15} />} testId="overview-coverage">
                <DataTable columns={["Source", "Records", "Last record", "Status", "Note"]} empty="No data source is visible for your role."
                  rows={d.coverage.map((c) => [c.label, c.records.toLocaleString("en-US"), c.lastRecordAt ? ago(c.lastRecordAt) : "never",
                    <Badge key={c.source} tone={c.availability === "OK" ? "success" : "neutral"}>{c.availability === "OK" ? "Data" : "No data"}</Badge>, c.note])} />
              </Panel>
            </div>
          </>
        )}
    </div>
  );
}
