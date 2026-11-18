"use client";

// Camera & device overview: how many cameras are registered, configured
// (source, reference frame, zones) and healthy, and the device health
// summary with what needs attention. Every figure comes from the API.
// Nothing here is an AI result: computer vision is not connected yet.

import Link from "next/link";
import { Activity, AlertTriangle, BrainCircuit, Cctv, FlaskConical, Image as ImageIcon, Plus, RefreshCw, Shapes, Video, Webcam, Wifi, WifiOff } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { StatCard } from "@/components/ui/StatCard";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useMyFactories } from "@/components/workers/shared";
import { useQueryState } from "@/components/attendance/shared";
import { BASE, CameraDeviceTabs, DeviceEventTable, DeviceHealthBadge } from "@/components/cameras/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDateTime } from "@/lib/format";
import { P } from "@/lib/access";
import { AI_NOT_CONNECTED_NOTE, CAMERA_TYPE_LABEL, DEVICE_CATEGORY_LABEL, SIMULATED_DEVICE_NOTE, SOURCE_TYPE_LABEL, type Camera, type CameraOverview, type DeviceHealthOverview } from "@/lib/cameras";
import type { PagedResult } from "@/lib/access";

export default function CameraDeviceOverviewPage() {
  return (
    <RequirePermission perms={[P.CamerasView, P.DevicesView]} what="Cameras & devices">
      <Overview />
    </RequirePermission>
  );
}

function Overview() {
  const q = useQueryState(BASE);
  const { factories } = useMyFactories();
  const factory = q.get("factory");
  const canCameras = useCan(P.CamerasView);
  const canDevices = useCan(P.DevicesView);
  const canAddCamera = useCan(P.CamerasManage);
  const canAddDevice = useCan(P.DevicesManage);
  const suffix = factory ? `?factoryId=${factory}` : "";
  const cams = useResource<CameraOverview>(canCameras ? `/api/cameras/overview${suffix}` : null, { pollMs: 60_000 });
  const health = useResource<DeviceHealthOverview>(canDevices ? `/api/devices/health${suffix}` : null, { pollMs: 30_000 });
  const c = cams.data;
  const h = health.data;
  const link = (path: string, extra = "") => `${BASE}${path}${factory || extra ? "?" : ""}${[factory ? `factory=${factory}` : "", extra].filter(Boolean).join("&")}`;

  return (
    <div className="page" data-testid="camera-device-overview">
      <PageHeader
        title="Cameras & devices"
        description="Registered cameras, what they cover and how they are configured, and the health of every device in your scope."
        actions={
          <div className="flex flex-wrap gap-2">
            {canAddCamera && <Link href={`${BASE}/cameras/new`}><Button size="sm" variant="primary" iconLeft={<Plus size={13} />}>Register camera</Button></Link>}
            {canAddDevice && <Link href={`${BASE}/devices/new`}><Button size="sm" variant="secondary" iconLeft={<Plus size={13} />}>Register device</Button></Link>}
          </div>
        }
      />
      <CameraDeviceTabs className="mt-4" />

      <div className="mt-4 flex flex-wrap items-center gap-3">
        {(factories?.length ?? 0) > 1 && (
          <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null })}>
            <option value="">All factories</option>
            {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        )}
        {h && <span className="text-caption text-fg-tertiary">Health as of {formatDateTime(h.asOf)}</span>}
        <Button size="sm" variant="secondary" className="ml-auto" aria-label="Refresh" onClick={() => { void cams.reload(); void health.reload(); }}>
          <RefreshCw size={13} className={cn((cams.refreshing || health.refreshing) && "animate-spin")} />
        </Button>
      </div>

      {canCameras && (
        <section className="mt-6" aria-labelledby="cams-heading">
          <h2 id="cams-heading" className="text-body font-semibold text-fg-primary flex items-center gap-2"><Cctv size={15} /> Cameras</h2>
          {cams.error && !c && <ErrorState className="mt-3" title="Couldn't load cameras" body={errorMessage(cams.error)} retryLabel="Try again" onRetry={() => void cams.reload()} />}
          <div className="mt-3 grid gap-3 grid-cols-2 md:grid-cols-4 xl:grid-cols-6" data-testid="camera-stats">
            <StatCard label="Registered" value={c?.total ?? "—"} icon={<Cctv size={14} />} loading={cams.loading} href={link("/cameras")} testId="stat-cameras" />
            <StatCard label="Active" value={c?.byStatus.Active ?? "—"} tone="good" loading={cams.loading} href={link("/cameras", "status=Active")} />
            <StatCard label="Inactive / maintenance" value={c ? (c.byStatus.Inactive ?? 0) + (c.byStatus.Maintenance ?? 0) : "—"} tone={c && c.byStatus.Maintenance > 0 ? "warn" : "neutral"} loading={cams.loading} />
            <StatCard label="No video source" value={c?.withoutSource ?? "—"} tone={c && c.withoutSource > 0 ? "warn" : "neutral"} icon={<Video size={14} />} loading={cams.loading} href={link("/cameras", "source=None")} />
            <StatCard label="No reference frame" value={c?.withoutReferenceFrame ?? "—"} tone={c && c.withoutReferenceFrame > 0 ? "warn" : "neutral"} icon={<ImageIcon size={14} />} hint="Needed before drawing zones" loading={cams.loading} />
            <StatCard label="Active zones" value={c?.activeZones ?? "—"} hint={c ? `On ${c.withZones} camera${c.withZones === 1 ? "" : "s"}` : undefined} icon={<Shapes size={14} />} loading={cams.loading} />
          </div>
          {c && c.total > 0 && (
            <div className="mt-4 grid gap-4 lg:grid-cols-3">
              <Breakdown title="By type" rows={Object.entries(c.byType).map(([k, v]) => [CAMERA_TYPE_LABEL[k] ?? k, v])} />
              <Breakdown title="By video source" rows={Object.entries(c.bySource).map(([k, v]) => [SOURCE_TYPE_LABEL[k] ?? k, v])} />
              <Breakdown title="Camera health (linked devices)" rows={[...Object.entries(c.byHealth).map(([k, v]) => [k, v] as [string, number]), ["No health device", c.withoutHealthDevice]]}
                foot={c.simulatedHealth > 0 ? `${c.simulatedHealth} camera health device${c.simulatedHealth === 1 ? " is" : "s are"} SIMULATED.` : undefined} />
            </div>
          )}
          {c && c.total === 0 && <EmptyState className="mt-4" icon={<Cctv size={18} />} title="No cameras registered" body={canAddCamera ? "Register a camera (a recorded video works for the prototype) to map coverage and draw zones." : "Cameras appear here once an administrator registers them."} />}
          <LiveAiCameras factory={factory} canAdd={canAddCamera} />
          <Callout className="mt-4" tone="info" title="AI processing">{AI_NOT_CONNECTED_NOTE}</Callout>
        </section>
      )}

      {canDevices && (
        <section className="mt-8" aria-labelledby="dev-heading">
          <h2 id="dev-heading" className="text-body font-semibold text-fg-primary flex items-center gap-2"><Activity size={15} /> Device health</h2>
          {health.error && !h && <ErrorState className="mt-3" title="Couldn't load device health" body={errorMessage(health.error)} retryLabel="Try again" onRetry={() => void health.reload()} />}
          <div className="mt-3 grid gap-3 grid-cols-2 md:grid-cols-4 xl:grid-cols-6" data-testid="device-stats">
            <StatCard label="Devices" value={h?.total ?? "—"} hint={h ? `${h.real} real · ${h.simulated} simulated` : undefined} loading={health.loading} href={link("/devices")} testId="stat-devices" />
            <StatCard label="Physically connected" value={h?.physicallyConnected ?? "—"} hint="Real devices online" tone="good" icon={<Wifi size={14} />} loading={health.loading} href={link("/devices", "health=Online&simulated=false")} testId="stat-physical" />
            <StatCard label="Simulated online" value={h?.simulatedOnline ?? "—"} hint="Not physical hardware" icon={<FlaskConical size={14} />} loading={health.loading} href={link("/devices", "health=Online&simulated=true")} />
            <StatCard label="Offline" value={h?.byHealth.Offline ?? "—"} tone={h && h.byHealth.Offline > 0 ? "bad" : "neutral"} icon={<WifiOff size={14} />} loading={health.loading} href={link("/devices", "health=Offline")} />
            <StatCard label="Heartbeat overdue" value={h?.heartbeatOverdue ?? "—"} tone={h && h.heartbeatOverdue > 0 ? "warn" : "neutral"} loading={health.loading} />
            <StatCard label="Errors (24 h)" value={h?.errorsLast24Hours ?? "—"} tone={h && h.errorsLast24Hours > 0 ? "warn" : "neutral"} icon={<AlertTriangle size={14} />} loading={health.loading} href={link("/health")} />
          </div>
          {h && h.simulated > 0 && <Callout className="mt-4" tone="warning" title="Simulated devices in this view">{SIMULATED_DEVICE_NOTE}</Callout>}
          <div className="mt-4 grid gap-6 xl:grid-cols-2">
            <Panel flush title="Needs attention" icon={<AlertTriangle size={14} />} subtitle="Offline, overdue or with a recent error" right={<Link href={link("/health")} className="text-caption hover:underline">Health dashboard</Link>}>
              {health.loading ? <div className="p-4"><SkeletonRows rows={3} /></div>
                : (h?.needsAttention.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<Wifi size={18} />} title="Nothing needs attention" body="Devices that go offline, stop sending heartbeats or report errors appear here." />
                : (
                  <ul className="divide-y divide-border-subtle">
                    {h!.needsAttention.map((d) => (
                      <li key={d.id} className="px-4 py-2.5 flex flex-wrap items-center gap-2 text-caption">
                        <Link href={`${BASE}/devices/${d.id}`} className="font-mono text-body hover:underline">{d.code}</Link>
                        <span className="text-fg-secondary">{DEVICE_CATEGORY_LABEL[d.category] ?? d.category}</span>
                        <span className="ml-auto"><DeviceHealthBadge d={d} /></span>
                        {d.lastErrorMessage && <span className="w-full text-fg-tertiary">{d.lastErrorCode ? `${d.lastErrorCode}: ` : ""}{d.lastErrorMessage}</span>}
                      </li>
                    ))}
                  </ul>
                )}
            </Panel>
            <Panel flush title="Recent device events" icon={<Activity size={14} />}>
              {health.loading ? <div className="p-4"><SkeletonRows rows={3} /></div>
                : (h?.recentEvents.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<Activity size={18} />} title="No device events yet" body="Connections, missed heartbeats, errors and status changes are recorded here." />
                : <DeviceEventTable rows={h!.recentEvents} testId="recent-events" />}
            </Panel>
          </div>
        </section>
      )}
    </div>
  );
}

function Breakdown({ title, rows, foot }: { title: string; rows: [string, number][]; foot?: string }) {
  const total = rows.reduce((s, [, v]) => s + v, 0) || 1;
  return (
    <Panel title={title}>
      <ul className="space-y-2">
        {rows.map(([label, value]) => (
          <li key={label} className="text-caption">
            <div className="flex justify-between"><span className="text-fg-secondary">{label}</span><span className="tabular-nums text-fg-primary">{value}</span></div>
            <div className="mt-1 h-1.5 rounded-full bg-surface-2 overflow-hidden"><div className="h-full bg-[var(--accent)]" style={{ width: `${(value / total) * 100}%` }} /></div>
          </li>
        ))}
      </ul>
      {foot && <p className="mt-3 text-caption text-[var(--risk-medium)]">{foot}</p>}
    </Panel>
  );
}

/** Browser-webcam cameras: open one and press Start camera to analyse it live with OpenAI vision. */
function LiveAiCameras({ factory, canAdd }: { factory: string; canAdd: boolean }) {
  const list = useResource<PagedResult<Camera>>(`/api/cameras?source=LocalWebcam&sort=code&pageSize=12${factory ? `&factoryId=${factory}` : ""}`);
  const cams = list.data?.items ?? [];
  return (
    <Panel className="mt-4" title="Live AI camera" icon={<BrainCircuit size={14} />} testId="live-ai-cameras"
      subtitle="Your browser's webcam, sampled frames analysed by OpenAI vision on the server: people, activity, helmet and safety vest. Nothing to install.">
      {list.loading ? <SkeletonRows rows={2} rowHeight="h-8" /> : cams.length === 0 ? (
        <p className="text-caption text-fg-tertiary">
          No camera uses a browser webcam yet.{canAdd ? " Register a camera (type Webcam) and set its video source to “Local webcam”." : " Ask a camera administrator to set one up."}
        </p>
      ) : (
        <ul className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
          {cams.map((cam) => (
            <li key={cam.id}>
              <Link href={`${BASE}/cameras/${cam.id}#live-ai`} className="surface flex items-center gap-2 px-3 py-2 hover:bg-bg-subtle" data-testid="live-ai-camera-link">
                <Webcam size={14} aria-hidden />
                <span className="font-mono text-body">{cam.code}</span>
                <span className="text-caption text-fg-secondary truncate">{cam.name}</span>
                <span className="ml-auto text-caption text-[var(--accent)]">{cam.status === "Active" ? "Start camera →" : cam.status}</span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}
