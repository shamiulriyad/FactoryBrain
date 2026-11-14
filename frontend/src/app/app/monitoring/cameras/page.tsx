"use client";

// Camera monitoring grid: every camera in scope with its connection (from
// its health device: real or SIMULATED, never mixed), the kind of source it
// has, alerts and last activity. Tiles show the configured reference frame
// (a still image, labelled as such) — never a fake live picture. Status
// changes arrive live.

import Link from "next/link";
import { useState } from "react";
import { Bell, Cctv, Grid2X2, Grid3X3, LayoutGrid, Video, Webcam, Radio, ImageOff } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useFactoryOrg, useMyFactories } from "@/components/workers/shared";
import { SearchBox, useQueryState } from "@/components/attendance/shared";
import { useMediaUrl } from "@/components/cameras/shared";
import { ConnectionBadge, LiveIndicator, MonitoringTabs, SeverityBadge } from "@/components/monitoring/shared";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { P } from "@/lib/access";
import { cn } from "@/lib/cn";
import { FEED_KIND_LABEL, MON, ago, qs, type MonitoringCamera } from "@/lib/monitoring";

export default function CameraGridPage() {
  return (
    <RequirePermission perms={[P.MonitoringView, P.CamerasView]} what="Camera monitoring">
      <Grid />
    </RequirePermission>
  );
}

const COLUMNS: Record<string, string> = {
  "2": "sm:grid-cols-2",
  "3": "sm:grid-cols-2 lg:grid-cols-3",
  "4": "sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-4"
};

function Grid() {
  const q = useQueryState(`${MON}/cameras`);
  const { factories } = useMyFactories();
  const factory = q.get("factory");
  const org = useFactoryOrg(factory || null);
  const [cols, setCols] = useState("3");
  const cams = useResource<MonitoringCamera[]>(`/api/monitoring/cameras${qs({ factoryId: factory, lineId: q.get("line"), connection: q.get("connection"), search: q.get("search") })}`,
    { pollMs: 60_000 });
  const hub = useMonitoringHub(factory || null, {
    onCameraStatus: () => void cams.reload(),
    onAlert: (_, a) => { if (a.camera) void cams.reload(); },
    onResync: () => void cams.reload()
  });
  const list = cams.data ?? [];
  const online = list.filter((c) => c.connection.status === "ONLINE").length;

  return (
    <div className="page" data-testid="camera-grid-page">
      <PageHeader eyebrow="Central monitoring" title="Camera monitoring"
        description="Every camera you may see, with its connection, source and alerts. Open a camera for playback, preview and its live events."
        meta={<LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} />} />
      <MonitoringTabs className="mb-4" />

      <div className="flex flex-wrap items-center gap-3 mb-4">
        {factories && factories.length > 1 && (
          <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null, line: null })}>
            <option value="">All my factories</option>
            {factories.map((f) => <option key={f.id} value={f.id}>{f.code}</option>)}
          </FilterSelect>
        )}
        <FilterSelect label="Line" value={q.get("line")} onChange={(v) => q.set({ line: v || null })}>
          <option value="">{factory || (factories?.length ?? 0) <= 1 ? "All" : "Choose a factory"}</option>
          {(org.lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </FilterSelect>
        <FilterSelect label="Connection" value={q.get("connection")} onChange={(v) => q.set({ connection: v || null })} testId="grid-connection">
          <option value="">All</option>
          <option value="ONLINE">Online</option>
          <option value="OFFLINE">Offline</option>
          <option value="UNKNOWN">Unknown</option>
          <option value="INACTIVE">Deactivated</option>
          <option value="MAINTENANCE">Maintenance</option>
        </FilterSelect>
        <SearchBox value={q.get("search")} onChange={(v) => q.set({ search: v || null })} placeholder="Camera ID, name or place" label="Search cameras" />
        <div className="ml-auto flex items-center gap-1" role="group" aria-label="Grid size">
          {([["2", Grid2X2], ["3", Grid3X3], ["4", LayoutGrid]] as const).map(([n, Icon]) => (
            <button key={n} type="button" onClick={() => setCols(n)} aria-pressed={cols === n} title={`${n} columns`}
              className={cn("h-8 w-8 grid place-items-center rounded-[var(--radius-control)] border", cols === n ? "border-[var(--accent)] text-[var(--accent)]" : "border-border-subtle text-fg-tertiary")}>
              <Icon size={14} aria-hidden />
            </button>
          ))}
        </div>
      </div>

      {cams.error && !cams.data ? (
        <ErrorState retryLabel="Try again" title="Could not load cameras" body={cams.error.message} onRetry={() => void cams.reload()} />
      ) : !cams.data ? (
        <SkeletonRows rows={4} rowHeight="h-40" />
      ) : list.length === 0 ? (
        <EmptyState icon={<Cctv size={20} />} title="No cameras" body="No camera in your scope matches. Cameras are registered in Cameras & devices." />
      ) : (
        <>
          <p className="text-caption text-fg-tertiary mb-2" data-testid="grid-summary">{list.length} camera(s) · {online} online</p>
          <ul className={cn("grid gap-4 grid-cols-1", COLUMNS[cols])} data-testid="camera-grid">
            {list.map((c) => <li key={c.id}><CameraTile c={c} /></li>)}
          </ul>
        </>
      )}
    </div>
  );
}

function FeedIcon({ kind }: { kind: string }) {
  if (kind === "RECORDED_VIDEO") return <Video size={13} aria-hidden />;
  if (kind === "LOCAL_WEBCAM") return <Webcam size={13} aria-hidden />;
  if (kind === "LIVE_STREAM") return <Radio size={13} aria-hidden />;
  return <ImageOff size={13} aria-hidden />;
}

function CameraTile({ c }: { c: MonitoringCamera }) {
  const frame = useMediaUrl(c.hasReferenceFrame ? `/api/cameras/${c.id}/reference-frame` : null);
  const off = c.connection.status === "OFFLINE";
  return (
    <Link href={`${MON}/cameras/${c.id}`} className={cn("block rounded-[var(--radius-card,12px)] border bg-surface-1 overflow-hidden hover:border-[var(--accent)]",
      off ? "border-[var(--risk-high)]" : "border-border-subtle")} data-testid="camera-tile" data-connection={c.connection.status}>
      <div className="relative aspect-video bg-[#0b0f14] grid place-items-center">
        {frame.url ? (
          // eslint-disable-next-line @next/next/no-img-element -- an object URL from an authenticated fetch
          <img src={frame.url} alt={`Reference frame of ${c.code}`} className={cn("absolute inset-0 h-full w-full object-cover", off && "grayscale opacity-60")} />
        ) : (
          <span className="text-caption text-white/60 inline-flex items-center gap-1.5"><FeedIcon kind={c.feed.kind} /> {FEED_KIND_LABEL[c.feed.kind]}</span>
        )}
        <span className="absolute left-2 top-2 rounded bg-black/70 px-1.5 py-0.5 font-mono text-[11px] text-white">{c.code}</span>
        {frame.url && <span className="absolute right-2 bottom-2 rounded bg-black/70 px-1.5 py-0.5 text-[10px] text-white/80">Reference still · not live</span>}
        {off && <span className="absolute inset-x-0 bottom-0 bg-[var(--risk-high)]/90 text-white text-caption text-center py-1">Offline</span>}
      </div>
      <div className="p-3 space-y-1.5">
        <div className="flex items-center gap-2">
          <span className="font-medium text-fg-primary truncate">{c.name}</span>
          <span className="ml-auto"><ConnectionBadge status={c.connection.status} label={c.connection.label} simulated={c.connection.isSimulated} /></span>
        </div>
        <p className="text-caption text-fg-secondary truncate">{[c.factoryCode, c.line?.name, c.floorName].filter(Boolean).join(" · ")}</p>
        <div className="flex flex-wrap items-center gap-2 text-caption text-fg-tertiary">
          <span className="inline-flex items-center gap-1"><FeedIcon kind={c.feed.kind} />{FEED_KIND_LABEL[c.feed.kind]}</span>
          {c.coveredWorkstations.length > 0 && <span>· {c.coveredWorkstations.slice(0, 4).join(", ")}{c.coveredWorkstations.length > 4 ? "…" : ""}</span>}
        </div>
        <div className="flex items-center gap-2 text-caption">
          {c.activeAlerts > 0 ? (
            <span className="inline-flex items-center gap-1 text-[var(--risk-high)]"><Bell size={12} aria-hidden />{c.activeAlerts} alert(s)</span>
          ) : <span className="text-fg-tertiary">No active alerts</span>}
          {c.highestSeverity && <SeverityBadge severity={c.highestSeverity} />}
          <span className="ml-auto text-fg-tertiary">Last event {ago(c.lastEventAt)}</span>
        </div>
      </div>
    </Link>
  );
}
