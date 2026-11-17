"use client";

// AI activity insights: aggregate camera AI observations. Estimates, not
// facts: never per worker, never pay, discipline or ranking; only VERIFIED
// identity associations count as confirmed.

import { Camera, Fingerprint, Gauge, ScanLine } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Callout } from "@/components/ui/Callout";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { AN, AnalyticsFilterBar, AnalyticsTabs, BarList, DataTable, KpiGrid, ScopeLine, TrendChart, pct, useAnalyticsFilters, useDefinitions } from "@/components/analytics/shared";
import { useResource } from "@/lib/useResource";
import { P } from "@/lib/access";
import { activityName, ago } from "@/lib/monitoring";
import type { ActivityInsights } from "@/lib/analytics";

export default function ActivityInsightsPage() {
  return (
    <RequirePermission perms={[P.MonitoringView]} what="AI activity insights">
      <ActivityView />
    </RequirePermission>
  );
}

function ActivityView() {
  const { f, set, query, factories } = useAnalyticsFilters(`${AN}/activity`);
  const defs = useDefinitions();
  const r = useResource<ActivityInsights>(`/api/analytics/activity${query()}`, { pollMs: 60_000 });
  const d = r.data;
  return (
    <div className="page" data-testid="analytics-activity">
      <PageHeader eyebrow="Factory analytics" title="AI activity insights"
        description="What the camera AI observed, in aggregate. Predictions and observations — not verified work, never per worker." />
      <AnalyticsTabs />
      <AnalyticsFilterBar f={f} set={set} factories={factories} show={{ family: true }} />
      {r.error && !d ? <ErrorState title="Could not load AI insights" body={r.error.message} retryLabel="Try again" onRetry={() => void r.reload()} />
        : !d ? <SkeletonRows rows={8} />
        : (
          <>
            <Callout tone="demo" className="mb-3" testId="activity-disclaimer">{d.disclaimer}</Callout>
            <ScopeLine scope={d.scope} warnings={d.warnings} />
            <KpiGrid kpis={d.kpis} defs={defs} />
            <div className="grid gap-4 lg:grid-cols-2 mb-4">
              <Panel title="Predicted activity distribution" subtitle="AI estimate · share of predictions" icon={<ScanLine size={15} />} testId="activity-distribution">
                <div className="p-4"><BarList rows={d.distribution} value={(x) => x.predictions} label={(x) => activityName(x.activity)} extra={(x) => (x.sharePct != null ? `${x.sharePct} %` : null)} empty="No predictions in this range." /></div>
              </Panel>
              <Panel title="Predictions per day" subtitle="AI estimate · dashed: uncertain" icon={<ScanLine size={15} />}>
                <div className="p-4"><TrendChart points={d.daily.map((x) => ({ key: x.date, label: x.date.slice(5), start: x.date, value: x.predictions, planned: x.uncertain }))} /></div>
              </Panel>
              <Panel title="Confidence" subtitle="The model's own score, not accuracy" icon={<Gauge size={15} />}>
                <div className="p-4"><BarList rows={d.confidence} value={(x) => x.predictions} label={(x) => x.band} extra={(x) => (x.sharePct != null ? `${x.sharePct} %` : null)} /></div>
              </Panel>
              <Panel title="Verified vs unverified observations" subtitle="Only VERIFIED associations count as confirmed" icon={<Fingerprint size={15} />} testId="activity-identity">
                <DataTable columns={["Tracks observed", "Verified", "Unverified", "Ambiguous / review", "Verified workers"]}
                  rows={[[d.identity.tracksObserved, d.identity.verifiedTracks, d.identity.unverifiedTracks, d.identity.ambiguousOrReview, d.identity.verifiedWorkers]]} />
                <p className="px-4 pb-3 text-caption text-fg-tertiary">{d.identity.note}</p>
              </Panel>
            </div>
            <Panel className="mb-4" title="By line" subtitle="Click a line to drill down">
              <DataTable columns={["Line", "Predictions", "Uncertain", "Uncertain %", "Top predictions"]}
                rows={d.byLine.map((l) => [l.lineId ? <button key={l.lineId} type="button" className="text-[var(--accent)]" onClick={() => set({ line: l.lineId })}>{l.lineName}</button> : l.lineName,
                  l.predictions, l.uncertain, pct(l.predictions > 0 ? Math.round((l.uncertain / l.predictions) * 1000) / 10 : null),
                  l.activities.slice().sort((a, b) => b.predictions - a.predictions).slice(0, 3).map((a) => `${activityName(a.activity)} ${a.sharePct ?? 0} %`).join(", ")])} />
            </Panel>
            <Panel title="Camera coverage and observation time" icon={<Camera size={15} />} testId="activity-cameras">
              <DataTable columns={["Camera", "Hours with data", "Tracks", "Predictions", "Observation", "Last event"]} empty="No camera sent observations in this range."
                rows={d.cameras.map((c) => [c.code, c.hoursWithData, c.tracks, c.predictions, `${c.observationHours.toFixed(1)} h`, ago(c.lastEventAt)])} />
            </Panel>
          </>
        )}
    </div>
  );
}
