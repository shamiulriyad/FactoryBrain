"use client";

// Central monitoring room: equipment status, active alerts, the live event
// feed and the state of every production line in scope, updated in real time
// over SignalR. Filters live in the URL. Only data the signed-in user may see
// is ever sent by the API or the hub. No AI results: computer vision is not
// connected yet, so vision events here are SIMULATED or come from producers
// using the ingestion API. With the computer-vision service running, vision
// events are REAL (producer CV_SERVICE) and include predicted activity.

import Link from "next/link";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Bell, Cctv, Cpu, Activity, ArrowRight, Factory as FactoryIcon, Filter, Fingerprint, PersonStanding, X } from "lucide-react";
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
import { AlertRow, EventList, LiveIndicator, MonitoringTabs, SeverityBadge, mergeEvents } from "@/components/monitoring/shared";
import { ActivitySummary } from "@/components/monitoring/activity";
import type { IdentityOverview } from "@/lib/identity";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { P } from "@/lib/access";
import { cn } from "@/lib/cn";
import {
  AI_NOT_CONNECTED, ALERT_STATUS_LABEL, EVENT_TYPES, EVENT_TYPE_LABEL, LINE_STATUS_LABEL, MON, ago, dhaka, dhakaInputToIso, isActivityEvent, qs,
  type LineStatus, type MonitoringActivity, type MonitoringCamera, type MonitoringEvent, type MonitoringOverview
} from "@/lib/monitoring";

export default function MonitoringRoomPage() {
  return (
    <RequirePermission perms={[P.MonitoringView, P.DevicesView]} what="Central monitoring room">
      <Room />
    </RequirePermission>
  );
}

const LINE_STATUS_STYLE: Record<string, string> = {
  ALERT: "border-[var(--risk-high)] bg-[color-mix(in_srgb,var(--risk-high)_8%,transparent)]",
  ATTENTION: "border-[var(--risk-medium)] bg-[color-mix(in_srgb,var(--risk-medium)_8%,transparent)]",
  NORMAL: "border-border-subtle",
  NO_COVERAGE: "border-dashed border-border-subtle opacity-80"
};

function Room() {
  const q = useQueryState(MON);
  const { factories } = useMyFactories();
  const factory = q.get("factory");
  const filters = {
    factoryId: factory, departmentId: q.get("department"), floorId: q.get("floor"), lineId: q.get("line"), cameraId: q.get("camera"),
    eventType: q.get("type"), alertStatus: q.get("alerts"), from: dhakaInputToIso(q.get("from")), to: dhakaInputToIso(q.get("to"))
  };
  const path = `/api/monitoring/overview${qs(filters)}`;
  const overview = useResource<MonitoringOverview>(path, { pollMs: 60_000 });
  const org = useFactoryOrg(factory || null);
  const cameras = useResource<MonitoringCamera[]>(factory ? `/api/monitoring/cameras?factoryId=${factory}` : null);
  const [showFilters, setShowFilters] = useState(false);
  const [feed, setFeed] = useState<MonitoringEvent[]>([]);
  const [fresh, setFresh] = useState<Set<string>>(new Set());
  const o = overview.data;

  useEffect(() => { if (o) setFeed(o.recentEvents); }, [o]);

  // Counts change with every alert and status message: refetch, but at most every 2 s.
  const pending = useRef<ReturnType<typeof setTimeout> | null>(null);
  const reloadSoon = useCallback(() => {
    if (pending.current) return;
    pending.current = setTimeout(() => { pending.current = null; void overview.reload(); }, 2000);
  }, [overview]);
  useEffect(() => () => { if (pending.current) clearTimeout(pending.current); }, []);

  const matches = useCallback((e: MonitoringEvent) =>
    (!filters.departmentId) && (!filters.floorId) // department / floor aren't on pushed events' summary: refetch instead
    && (!filters.lineId || e.line?.id === filters.lineId)
    && (!filters.cameraId || e.camera?.id === filters.cameraId)
    && (!filters.eventType || e.eventType === filters.eventType)
    && (!filters.to), [filters.departmentId, filters.floorId, filters.lineId, filters.cameraId, filters.eventType, filters.to]);

  const [activityKey, setActivityKey] = useState(0);
  const hub = useMonitoringHub(factory || null, {
    onEvent: (e) => {
      if (isActivityEvent(e.eventType) || e.eventType === "PERSON_LOST" || e.eventType === "ZONE_ENTERED") setActivityKey((k) => k + 1);
      if (matches(e)) {
        setFeed((cur) => mergeEvents(cur, e, 40));
        setFresh((cur) => new Set(cur).add(e.id));
        setTimeout(() => setFresh((cur) => { const n = new Set(cur); n.delete(e.id); return n; }), 4000);
      } else if (filters.departmentId || filters.floorId) reloadSoon();
      if (e.category === "SYSTEM") reloadSoon();
    },
    onAlert: () => reloadSoon(),
    onIdentity: () => setActivityKey((k) => k + 1),
    onCameraStatus: () => reloadSoon(),
    onDeviceStatus: () => reloadSoon(),
    onResync: () => void overview.reload()
  });

  const activeFilters = Object.entries({ department: filters.departmentId, floor: filters.floorId, line: filters.lineId, camera: filters.cameraId,
    type: filters.eventType, alerts: q.get("alerts"), from: q.get("from"), to: q.get("to") }).filter(([, v]) => v).length;

  return (
    <div className="page" data-testid="monitoring-room">
      <PageHeader
        eyebrow="Central monitoring"
        title="Monitoring room"
        description="Cameras, devices, alerts and events across the factories and lines you are responsible for, as they happen."
        meta={<LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} />}
        actions={
          <div className="flex flex-wrap items-center gap-2">
            {factories && factories.length > 1 && (
              <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null, department: null, floor: null, line: null, camera: null })} testId="room-factory">
                <option value="">All my factories</option>
                {factories.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
              </FilterSelect>
            )}
            <Button size="sm" variant={showFilters || activeFilters ? "secondary" : "ghost"} iconLeft={<Filter size={13} />} onClick={() => setShowFilters((s) => !s)} data-testid="room-filters-toggle">
              Filters{activeFilters ? ` (${activeFilters})` : ""}
            </Button>
          </div>
        }
      />
      <MonitoringTabs className="mb-4" />

      {showFilters && (
        <Panel className="mb-4" testId="room-filters">
          <div className="p-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <FilterSelect label="Department" value={q.get("department")} onChange={(v) => q.set({ department: v || null })}>
              <option value="">{factory ? "All" : "Choose a factory"}</option>
              {(org.departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </FilterSelect>
            <FilterSelect label="Floor" value={q.get("floor")} onChange={(v) => q.set({ floor: v || null })}>
              <option value="">{factory ? "All" : "Choose a factory"}</option>
              {(org.floors.data ?? []).map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}
            </FilterSelect>
            <FilterSelect label="Line" value={q.get("line")} onChange={(v) => q.set({ line: v || null })}>
              <option value="">All</option>
              {(org.lines.data ?? o?.lines.map((l) => ({ id: l.lineId, name: l.lineName })) ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </FilterSelect>
            <FilterSelect label="Camera" value={q.get("camera")} onChange={(v) => q.set({ camera: v || null })}>
              <option value="">{factory ? "All" : "Choose a factory"}</option>
              {(cameras.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.code} · {c.name}</option>)}
            </FilterSelect>
            <FilterSelect label="Event type" value={q.get("type")} onChange={(v) => q.set({ type: v || null })}>
              <option value="">All</option>
              {EVENT_TYPES.map((t) => <option key={t} value={t}>{EVENT_TYPE_LABEL[t]}</option>)}
            </FilterSelect>
            <FilterSelect label="Alerts" value={q.get("alerts")} onChange={(v) => q.set({ alerts: v || null })}>
              <option value="">Active</option>
              {["OPEN", "ACKNOWLEDGED", "ESCALATED", "RESOLVED", "DISMISSED"].map((s) => <option key={s} value={s}>{ALERT_STATUS_LABEL[s]}</option>)}
            </FilterSelect>
            <label className="flex items-center gap-2 text-caption text-fg-tertiary">From
              <Input type="datetime-local" value={q.get("from")} onChange={(e) => q.set({ from: e.target.value || null })} aria-label="From (Dhaka time)" />
            </label>
            <label className="flex items-center gap-2 text-caption text-fg-tertiary">To
              <Input type="datetime-local" value={q.get("to")} onChange={(e) => q.set({ to: e.target.value || null })} aria-label="To (Dhaka time)" />
            </label>
          </div>
          <div className="px-4 pb-3 flex items-center justify-between text-caption text-fg-tertiary">
            <span>Times are Dhaka time (UTC+6). Without a range, events of the last 24 hours are shown.</span>
            {activeFilters > 0 && (
              <Button size="sm" variant="ghost" iconLeft={<X size={12} />} onClick={() => q.set({ department: null, floor: null, line: null, camera: null, type: null, alerts: null, from: null, to: null })}>
                Clear filters
              </Button>
            )}
          </div>
        </Panel>
      )}

      {overview.error && !o ? (
        <ErrorState retryLabel="Try again" title="Could not load the monitoring room" body={overview.error.message} onRetry={() => void overview.reload()} />
      ) : !o ? (
        <SkeletonRows rows={6} />
      ) : (
        <>
          {/* Status strip */}
          <section aria-label="Status" className="grid gap-3 grid-cols-2 lg:grid-cols-5 mb-4" data-testid="room-status">
            <Tile icon={<Cctv size={15} />} label="Cameras" testId="tile-cameras"
              value={<><span className="text-[var(--risk-low,#16a34a)]">{o.cameras.online}</span><span className="text-fg-tertiary"> / {o.cameras.total}</span></>}
              hint={`${o.cameras.online} online · ${o.cameras.offline} offline · ${o.cameras.unknown} unknown`}
              bad={o.cameras.offline > 0} href={`${MON}/cameras${factory ? `?factory=${factory}` : ""}`} />
            <Tile icon={<Cpu size={15} />} label="Devices" testId="tile-devices"
              value={<><span className="text-[var(--risk-low,#16a34a)]">{o.devices.online}</span><span className="text-fg-tertiary"> / {o.devices.total}</span></>}
              hint={`${o.devices.offline} offline · ${o.devices.real} real · ${o.devices.simulated} simulated`}
              bad={o.devices.offline > 0} href={`${MON}/devices${factory ? `?factory=${factory}` : ""}`} />
            <Tile icon={<Bell size={15} />} label="Active alerts" testId="tile-alerts" value={o.alerts.active}
              hint={`${o.alerts.critical + o.alerts.high} high or critical · ${o.alerts.escalated} escalated`}
              bad={o.alerts.critical + o.alerts.high > 0} href={`${MON}/alerts?status=ACTIVE${factory ? `&factory=${factory}` : ""}`} />
            <Tile icon={<Bell size={15} />} label="Unacknowledged" testId="tile-unacknowledged" value={o.alerts.unacknowledged}
              hint={o.alerts.simulated > 0 ? `${o.alerts.simulated} of the active alerts are SIMULATED` : "Waiting for someone to look"}
              bad={o.alerts.unacknowledged > 0} href={`${MON}/alerts?status=OPEN${factory ? `&factory=${factory}` : ""}`} />
            <Tile icon={<Activity size={15} />} label="Events (1 h)" testId="tile-events" value={o.events.lastHour}
              hint={`${o.events.inRange} in range · ${o.events.simulatedInRange} simulated · ${o.events.realInRange} real`}
              href={`${MON}/events${factory ? `?factory=${factory}` : ""}`} />
          </section>

          {!o.canSeeVision && (
            <Callout tone="info" className="mb-4">You see camera, device and model status. People-tracking events need the monitoring permission.</Callout>
          )}

          <div className="grid gap-4 xl:grid-cols-5 mb-4">
            <Panel className="xl:col-span-3" title="Live event feed" subtitle="Newest first · Dhaka time" icon={<Activity size={15} />}
              right={<Link href={`${MON}/events${factory ? `?factory=${factory}` : ""}`} className="text-caption text-[var(--accent)] inline-flex items-center gap-1">Timeline <ArrowRight size={12} /></Link>}
              flush testId="room-feed">
              <div className="max-h-[28rem] overflow-y-auto">
                <EventList rows={feed} highlight={fresh} testId="room-feed-list"
                  empty={<p className="px-4 py-8 text-center text-caption text-fg-tertiary">No events in this range. Start the monitoring simulator to see the room come alive, or wait for equipment status changes.</p>} />
              </div>
            </Panel>
            <Panel className="xl:col-span-2" title="Alerts" subtitle={q.get("alerts") ? ALERT_STATUS_LABEL[q.get("alerts")] : "Active, most severe first"} icon={<Bell size={15} />}
              right={<Link href={`${MON}/alerts${factory ? `?factory=${factory}` : ""}`} className="text-caption text-[var(--accent)] inline-flex items-center gap-1">All alerts <ArrowRight size={12} /></Link>}
              flush testId="room-alerts">
              {o.activeAlerts.length === 0 ? (
                <p className="px-4 py-8 text-center text-caption text-fg-tertiary">No alerts here. Quiet is good.</p>
              ) : (
                <ul className="divide-y divide-border-subtle max-h-[28rem] overflow-y-auto">
                  {o.activeAlerts.map((a) => <li key={a.id}><AlertRow a={a} compact /></li>)}
                </ul>
              )}
            </Panel>
          </div>

          {o.canSeeVision && <IdentityPanel factory={factory} reloadKey={activityKey} />}
          {o.canSeeVision && <ActivityPanel factory={factory} reloadKey={activityKey} />}

          <Panel title="Production lines" subtitle="Camera coverage, alerts and activity per line" icon={<FactoryIcon size={15} />} className="mb-4" testId="room-lines">
            {o.lines.length === 0 ? (
              <p className="px-4 py-6 text-caption text-fg-tertiary">No production lines in your scope.</p>
            ) : (
              <div className="p-4 grid gap-3 sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-4">
                {o.lines.map((l) => <LineTile key={l.lineId} l={l} onPick={() => q.set({ line: l.lineId })} />)}
              </div>
            )}
          </Panel>

          {o.factories.length > 1 && (
            <Panel title="Factories" icon={<FactoryIcon size={15} />} className="mb-4" flush testId="room-factories">
              <ul className="divide-y divide-border-subtle">
                {o.factories.map((f) => (
                  <li key={f.id} className="flex flex-wrap items-center gap-3 px-4 py-3">
                    <StatusDot status={f.status} />
                    <button type="button" className="font-medium text-fg-primary hover:underline" onClick={() => q.set({ factory: f.id })}>{f.code} · {f.name}</button>
                    <span className="text-caption text-fg-secondary">Cameras {f.camerasOnline}/{f.cameras} online{f.camerasOffline ? ` · ${f.camerasOffline} offline` : ""}</span>
                    <span className="text-caption text-fg-secondary">Devices {f.devices - f.devicesOffline}/{f.devices}</span>
                    <span className="text-caption text-fg-secondary">{f.activeAlerts} active alert(s)</span>
                    {f.highestSeverity && <SeverityBadge severity={f.highestSeverity} />}
                  </li>
                ))}
              </ul>
            </Panel>
          )}

          <p className="text-caption text-fg-tertiary" data-testid="room-ai-note">{AI_NOT_CONNECTED} Updated {dhaka(o.asOf)} (Dhaka).</p>
        </>
      )}
    </div>
  );
}

/** Worker identity at a glance: verified, unverified and ambiguous tracks, review queue, readers, recent changes. */
function IdentityPanel({ factory, reloadKey }: { factory: string; reloadKey: number }) {
  const data = useResource<IdentityOverview>(`/api/identity/overview${qs({ factoryId: factory })}`, { pollMs: 60_000 });
  const reload = data.reload;
  const last = useRef(0);
  useEffect(() => {
    if (reloadKey === 0) return;
    const wait = Math.max(0, 3000 - (Date.now() - last.current));
    const t = setTimeout(() => { last.current = Date.now(); void reload(); }, wait);
    return () => clearTimeout(t);
  }, [reloadKey, reload]);
  const d = data.data;
  const verified = d?.tracks.filter((t) => t.identity === "VERIFIED") ?? [];
  return (
    <Panel title="Worker identity" subtitle="Tracks verified by RFID card, reader location, assignment and camera zone. Never by appearance." icon={<Fingerprint size={15} />}
      right={<Link href={`${MON}/identity${factory ? `?factory=${factory}` : ""}`} className="text-caption text-[var(--accent)] inline-flex items-center gap-1">Identity <ArrowRight size={12} /></Link>}
      className="mb-4" testId="room-identity">
      <div className="p-4">
        {data.error && !d ? <p className="text-caption text-fg-tertiary">Identity could not be loaded: {data.error.message}</p>
          : !d ? <SkeletonRows rows={1} />
          : (
            <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
              <dl className="grid grid-cols-2 gap-2 text-caption sm:col-span-2 xl:col-span-1">
                <div><dt className="text-fg-tertiary">Verified now</dt><dd className="text-xl font-semibold tabular-nums">{d.counts.verifiedNow}</dd></div>
                <div><dt className="text-fg-tertiary">Unverified tracks</dt><dd className="text-xl font-semibold tabular-nums">{d.counts.unverifiedTracks}</dd></div>
                <div><dt className="text-fg-tertiary">Ambiguous</dt><dd className={cn("text-xl font-semibold tabular-nums", d.counts.ambiguousTracks > 0 && "text-[var(--risk-medium)]")}>{d.counts.ambiguousTracks}</dd></div>
                <div><dt className="text-fg-tertiary">Pending review</dt><dd className="text-xl font-semibold tabular-nums">{d.counts.pendingReview}</dd></div>
                <div className="col-span-2"><dt className="text-fg-tertiary">RFID readers online</dt><dd className="tabular-nums">{d.counts.readersOnline} / {d.counts.readers}</dd></div>
              </dl>
              <div className="text-caption">
                <p className="text-fg-tertiary mb-1">Currently verified</p>
                {verified.length === 0 ? <p className="text-fg-tertiary">None.</p> : (
                  <ul className="space-y-0.5">{verified.slice(0, 6).map((t) => (
                    <li key={`${t.cameraId}/${t.trackId}`}><span className="font-mono">{t.cameraCode} · {t.trackId}</span> → <span className="font-medium">{t.worker?.code}</span> · {t.workstation?.code}</li>
                  ))}</ul>
                )}
              </div>
              <div className="text-caption">
                <p className="text-fg-tertiary mb-1">Recent changes</p>
                {d.recentChanges.length === 0 ? <p className="text-fg-tertiary">None.</p> : (
                  <ul className="space-y-0.5">{d.recentChanges.slice(0, 6).map((c) => (
                    <li key={c.id} className="truncate"><span className="tabular-nums text-fg-tertiary">{dhaka(c.at)}</span> · {c.cameraCode} · {c.trackId} → {c.toStatus.toLowerCase().replace(/_/g, " ")}</li>
                  ))}</ul>
                )}
              </div>
            </div>
          )}
      </div>
    </Panel>
  );
}

/** Current predicted activity of tracks in scope; refreshed (at most every 3 s) when activity events arrive. */
function ActivityPanel({ factory, reloadKey }: { factory: string; reloadKey: number }) {
  const activity = useResource<MonitoringActivity>(`/api/monitoring/activity${qs({ factoryId: factory })}`, { pollMs: 60_000 });
  const reload = activity.reload;
  const last = useRef(0);
  useEffect(() => {
    if (reloadKey === 0) return;
    const wait = Math.max(0, 3000 - (Date.now() - last.current));
    const t = setTimeout(() => { last.current = Date.now(); void reload(); }, wait);
    return () => clearTimeout(t);
  }, [reloadKey, reload]);
  const a = activity.data;
  return (
    <Panel title="Predicted activity" subtitle="Tracks in view now · posture and movement predictions, not identities or productivity" icon={<PersonStanding size={15} />}
      right={<Link href={`${MON}/activity${factory ? `?factory=${factory}` : ""}`} className="text-caption text-[var(--accent)] inline-flex items-center gap-1">Activity <ArrowRight size={12} /></Link>}
      className="mb-4" testId="room-activity">
      <div className="p-4 space-y-2">
        {activity.error && !a ? <p className="text-caption text-fg-tertiary">Activity could not be loaded: {activity.error.message}</p>
          : !a ? <SkeletonRows rows={1} />
          : (
            <>
              <ActivitySummary data={a} compact />
              <p className="text-caption text-fg-tertiary">
                {a.counts.currentTracks} track(s) with a prediction in the last {Math.round(a.activeSeconds / 60)} min
                {a.counts.uncertainRate != null && <> · {Math.round(a.counts.uncertainRate * 100)}% of the last hour&apos;s activity events were uncertain</>}
              </p>
            </>
          )}
      </div>
    </Panel>
  );
}

function Tile({ icon, label, value, hint, bad, href, testId }: { icon: React.ReactNode; label: string; value: React.ReactNode; hint: string; bad?: boolean; href: string; testId: string }) {
  return (
    <Link href={href} data-testid={testId}
      className={cn("rounded-[var(--radius-card,12px)] border bg-surface-1 px-4 py-3 hover:border-[var(--accent)] transition-colors",
        bad ? "border-[var(--risk-high)]" : "border-border-subtle")}>
      <span className="flex items-center gap-1.5 text-caption text-fg-secondary">{icon}{label}</span>
      <span className="block text-2xl font-semibold tabular-nums mt-1">{value}</span>
      <span className="block text-caption text-fg-tertiary mt-0.5 truncate" title={hint}>{hint}</span>
    </Link>
  );
}

function StatusDot({ status }: { status: string }) {
  const color = status === "ALERT" ? "bg-[var(--risk-high)]" : status === "ATTENTION" ? "bg-[var(--risk-medium)]" : status === "NORMAL" ? "bg-[var(--risk-low,#16a34a)]" : "bg-border-strong";
  return <span className={cn("h-2.5 w-2.5 rounded-full shrink-0", color)} title={LINE_STATUS_LABEL[status] ?? status} aria-hidden />;
}

function LineTile({ l, onPick }: { l: LineStatus; onPick: () => void }) {
  return (
    <button type="button" onClick={onPick} className={cn("text-left rounded-[var(--radius-control)] border p-3 hover:border-[var(--accent)]", LINE_STATUS_STYLE[l.status])} data-testid="line-tile" data-status={l.status}>
      <div className="flex items-center gap-2">
        <StatusDot status={l.status} />
        <span className="font-medium text-fg-primary truncate">{l.lineName}</span>
        {l.factoryCode && <span className="ml-auto text-caption text-fg-tertiary">{l.factoryCode}</span>}
      </div>
      <p className="text-caption text-fg-secondary mt-1">{LINE_STATUS_LABEL[l.status]}</p>
      <dl className="mt-2 grid grid-cols-3 gap-2 text-caption">
        <div><dt className="text-fg-tertiary">Cameras</dt><dd className="tabular-nums">{l.camerasOnline}/{l.cameras}{l.camerasOffline > 0 && <span className="text-[var(--risk-high)]"> · {l.camerasOffline} off</span>}</dd></div>
        <div><dt className="text-fg-tertiary">Alerts</dt><dd className="tabular-nums flex items-center gap-1">{l.activeAlerts}{l.highestSeverity && <SeverityBadge severity={l.highestSeverity} />}</dd></div>
        <div><dt className="text-fg-tertiary">Events 1 h</dt><dd className="tabular-nums">{l.eventsLastHour}</dd></div>
      </dl>
      <p className="text-caption text-fg-tertiary mt-1">Last event {ago(l.lastEventAt)}</p>
    </button>
  );
}
