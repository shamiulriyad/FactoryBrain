"use client";

// Predicted activity of temporary camera tracks, in two separate families:
// generic posture (standing, sitting, walking, bending, reaching: rules over
// the pose) and garment activity (sewing, cutting, packing…: a trained
// model, shown with its version and evaluation status). Current tracks,
// distributions by camera and production line, track details and recent
// transitions, filters in the URL, live refresh over SignalR. Predictions,
// never identities and never productivity.

import { useCallback, useEffect, useRef } from "react";
import { Box, PersonStanding, Cctv, Factory as FactoryIcon, History, Scissors, X } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useFactoryOrg, useMyFactories } from "@/components/workers/shared";
import { useQueryState } from "@/components/attendance/shared";
import { EventList, LiveIndicator, MonitoringTabs } from "@/components/monitoring/shared";
import { ActivityDistribution, ActivityLegend, ActivityModels, ActivitySummary, ActivityTrackTable, IdentityLegend } from "@/components/monitoring/activity";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { P } from "@/lib/access";
import {
  ACTIVITIES, ACTIVITY_DISCLAIMER, GARMENT_ACTIVITIES, GARMENT_DISCLAIMER, MON, activityName, dhaka, dhakaInputToIso, isActivityEvent, isGarmentEvent, qs,
  type ActivityKind, type MonitoringActivity, type MonitoringCamera
} from "@/lib/monitoring";

const BASE = `${MON}/activity`;

export default function ActivityPage() {
  return (
    <RequirePermission perms={[P.MonitoringView]} what="Activity monitoring">
      <Activity />
    </RequirePermission>
  );
}

function Activity() {
  const q = useQueryState(BASE);
  const { factories } = useMyFactories();
  const factory = q.get("factory");
  const kind: ActivityKind = q.get("kind") === "garment" ? "garment" : "generic";
  const garment = kind === "garment";
  const org = useFactoryOrg(factory || null);
  const cameras = useResource<MonitoringCamera[]>(factory ? `/api/monitoring/cameras?factoryId=${factory}` : null);
  const path = `/api/monitoring/activity${qs({
    kind: garment ? "garment" : null, factoryId: factory, lineId: q.get("line"), cameraId: q.get("camera"), activity: q.get("activity"), sourceType: q.get("source"),
    uncertainOnly: q.get("uncertain") === "1" ? true : null, from: dhakaInputToIso(q.get("from")), to: dhakaInputToIso(q.get("to"))
  })}`;
  const data = useResource<MonitoringActivity>(path, { pollMs: 30_000 });
  const d = data.data;

  const pending = useRef<ReturnType<typeof setTimeout> | null>(null);
  const reloadSoon = useCallback(() => {
    if (pending.current) return;
    pending.current = setTimeout(() => { pending.current = null; void data.reload(); }, 2000);
  }, [data]);
  useEffect(() => () => { if (pending.current) clearTimeout(pending.current); }, []);
  const hub = useMonitoringHub(factory || null, {
    onEvent: (e) => { if ((isActivityEvent(e.eventType) && isGarmentEvent(e.eventType) === garment) || e.eventType === "PERSON_LOST") reloadSoon(); },
    onResync: () => void data.reload()
  });

  const filters = ["line", "camera", "activity", "source", "uncertain", "from", "to"].filter((k) => q.get(k)).length;   // "kind" is a view, not a filter
  const lines = org.lines.data ?? d?.byLine.map((l) => ({ id: l.key, name: l.name })) ?? [];
  const labels: string[] = d?.activities ?? [...(garment ? GARMENT_ACTIVITIES : ACTIVITIES)];

  return (
    <div className="page" data-testid="activity-page">
      <PageHeader eyebrow="Central monitoring" title="Activity"
        description="Predicted posture and movement of tracked people: standing, sitting, walking, bending, reaching, or unknown."
        meta={<LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} />}
        actions={factories && factories.length > 1 ? (
          <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null, line: null, camera: null })} testId="activity-factory">
            <option value="">All my factories</option>
            {factories.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        ) : undefined} />
      <MonitoringTabs className="mb-4" />

      <div role="tablist" aria-label="Kind of activity" className="mb-4 inline-flex rounded-[var(--radius-control)] border border-border-subtle p-0.5" data-testid="activity-kind">
        {([["generic", "Generic posture", <PersonStanding key="g" size={13} aria-hidden />], ["garment", "Garment activity", <Scissors key="s" size={13} aria-hidden />]] as const).map(([k, text, icon]) => (
          <button key={k} type="button" role="tab" aria-selected={kind === k} data-testid={`activity-kind-${k}`}
            onClick={() => q.set({ kind: k === "garment" ? "garment" : null, activity: null })}
            className={`inline-flex items-center gap-1.5 px-3 h-8 rounded-[calc(var(--radius-control)-2px)] text-caption ${kind === k ? "bg-[var(--accent-soft)] text-fg-primary font-medium" : "text-fg-secondary hover:text-fg-primary"}`}>
            {icon}{text}
          </button>
        ))}
      </div>

      <Callout tone="info" className="mb-4" testId="activity-disclaimer">{garment ? GARMENT_DISCLAIMER : ACTIVITY_DISCLAIMER}</Callout>

      <Panel className="mb-4" testId="activity-filters">
        <div className="p-4 flex flex-wrap items-center gap-3">
          <FilterSelect label="Line" value={q.get("line")} onChange={(v) => q.set({ line: v || null })}>
            <option value="">All</option>
            {lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
          </FilterSelect>
          <FilterSelect label="Camera" value={q.get("camera")} onChange={(v) => q.set({ camera: v || null })} testId="activity-camera">
            <option value="">{factory ? "All" : "All (choose a factory to list)"}</option>
            {(cameras.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.code} · {c.name}</option>)}
          </FilterSelect>
          <FilterSelect label="Activity" value={q.get("activity")} onChange={(v) => q.set({ activity: v || null })} testId="activity-filter">
            <option value="">All</option>
            {labels.map((a) => <option key={a} value={a}>{activityName(a)}</option>)}
          </FilterSelect>
          <FilterSelect label="Source" value={q.get("source")} onChange={(v) => q.set({ source: v || null })}>
            <option value="">Real and simulated</option>
            <option value="REAL">Real only</option>
            <option value="SIMULATED">Simulated only</option>
          </FilterSelect>
          <label className="inline-flex items-center gap-2 text-caption text-fg-secondary">
            <input type="checkbox" checked={q.get("uncertain") === "1"} onChange={(e) => q.set({ uncertain: e.target.checked ? "1" : null })} data-testid="activity-uncertain" />
            Uncertain only
          </label>
          <label className="flex items-center gap-2 text-caption text-fg-tertiary">From
            <Input type="datetime-local" value={q.get("from")} onChange={(e) => q.set({ from: e.target.value || null })} aria-label="From (Dhaka time)" />
          </label>
          <label className="flex items-center gap-2 text-caption text-fg-tertiary">To
            <Input type="datetime-local" value={q.get("to")} onChange={(e) => q.set({ to: e.target.value || null })} aria-label="To (Dhaka time)" />
          </label>
          {filters > 0 && (
            <Button size="sm" variant="ghost" iconLeft={<X size={12} />}
              onClick={() => q.set({ line: null, camera: null, activity: null, source: null, uncertain: null, from: null, to: null })}>Clear filters</Button>
          )}
        </div>
        <p className="px-4 pb-3 text-caption text-fg-tertiary">Dhaka time (UTC+6). Without a range: the last hour. A track is current when it had a prediction in the last 5 minutes and wasn&apos;t lost.</p>
      </Panel>

      {data.error && !d ? (
        <ErrorState retryLabel="Try again" title="Could not load activity" body={data.error.message} onRetry={() => void data.reload()} />
      ) : !d ? (
        <SkeletonRows rows={6} />
      ) : (
        <>
          <Panel title={garment ? "Current garment activity" : "Current posture"} subtitle={`As of ${dhaka(d.to)} (Dhaka)`}
            icon={garment ? <Scissors size={15} /> : <PersonStanding size={15} />} className="mb-4" testId="activity-current">
            <div className="p-4 space-y-3">
              <ActivitySummary data={d} />
              <div className="flex flex-wrap items-center gap-2 text-caption text-fg-secondary" data-testid="activity-models-row">
                <span className="inline-flex items-center gap-1"><Box size={12} aria-hidden />{garment ? "Models" : "Classifier"}:</span>
                <ActivityModels models={d.models ?? []} kind={d.kind} />
              </div>
              {d.truncated && <Callout tone="warning">Only the latest activity events of this range were used. Narrow the range or the filters.</Callout>}
            </div>
          </Panel>

          <div className="grid gap-4 xl:grid-cols-2 mb-4">
            <Panel title="By camera" subtitle="Observed time per predicted activity" icon={<Cctv size={15} />} right={<ActivityLegend activities={labels} />} flush testId="activity-by-camera">
              <ActivityDistribution rows={d.byCamera} order={labels} testId="activity-by-camera-list" linkFor={(r) => `${MON}/cameras/${r.key}`}
                empty="No activity predictions in this range. Start activity recognition on a camera, or the simulator." />
            </Panel>
            <Panel title="By production line" subtitle="Observed time per predicted activity" icon={<FactoryIcon size={15} />} flush testId="activity-by-line">
              <ActivityDistribution rows={d.byLine} order={labels} testId="activity-by-line-list" empty="No predictions on cameras assigned to a line in this range." />
            </Panel>
          </div>

          <Panel title="Tracks" subtitle="Temporary camera tracks and their latest predicted activity" icon={<PersonStanding size={15} />} flush className="mb-4" testId="activity-tracks-panel">
            <div className="p-4 border-b border-border-subtle"><IdentityLegend /></div>
            <ActivityTrackTable tracks={d.tracks} garment={garment}
              empty={garment ? "No tracks with garment activity predictions match. Garment recognition needs a trained, evaluated model on the camera." : "No tracks with activity predictions match."} />
          </Panel>

          <Panel title="Recent transitions" subtitle="Accepted activity changes and uncertain periods · newest first" icon={<History size={15} />} flush testId="activity-transitions">
            <div className="max-h-[28rem] overflow-y-auto">
              <EventList rows={d.transitions} dense empty={<p className="px-4 py-6 text-caption text-fg-tertiary">No activity changes in this range.</p>} />
            </div>
          </Panel>
          <p className="mt-3 text-caption text-fg-tertiary" data-testid="activity-note">{d.note}</p>
        </>
      )}
    </div>
  );
}
