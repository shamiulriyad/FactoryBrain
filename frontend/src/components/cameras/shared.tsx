"use client";

// Building blocks for cameras and devices: the section tabs, status / health
// badges (SIMULATED is always spelled out), authenticated media, the polygon
// zone editor and the event tables. The API checks every rule again.

import Link from "next/link";
import { usePathname } from "next/navigation";
import { forwardRef, useEffect, useRef, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from "react";
import { CircleOff, Radio, Wifi, WifiOff, Wrench } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { SimulatedBadge, tableCls } from "@/components/attendance/shared";
import { useCan } from "@/lib/auth";
import { cn } from "@/lib/cn";
import { formatDateTime } from "@/lib/format";
import { P } from "@/lib/access";
import {
  CAMERA_STATUS_TONE,
  EVENT_SOURCE_LABEL,
  EVENT_TYPE_LABEL,
  HEALTH_TONE,
  SOURCE_TYPE_LABEL,
  ZONE_TYPE_COLOR,
  fetchMedia,
  type CameraHealth,
  type Device,
  type DeviceEvent,
  type ZonePoint
} from "@/lib/cameras";

export const BASE = "/app/cameras-devices";

// ─── Section tabs ─────────────────────────────────────────────────────────

const SECTIONS: { href: string; label: string; perms: string[]; exact?: boolean }[] = [
  { href: BASE, label: "Overview", perms: [P.CamerasView, P.DevicesView], exact: true },
  { href: `${BASE}/cameras`, label: "Cameras", perms: [P.CamerasView] },
  { href: `${BASE}/devices`, label: "Devices", perms: [P.DevicesView] },
  { href: `${BASE}/health`, label: "Device health", perms: [P.DevicesView] },
  { href: `${BASE}/simulator`, label: "Device simulator", perms: [P.DevicesSimulate] }
];

function SectionLink({ s, active }: { s: (typeof SECTIONS)[number]; active: boolean }) {
  const allowed = useCan(...s.perms);
  if (!allowed) return null;
  return (
    <Link href={s.href} aria-current={active ? "page" : undefined}
      className={cn("px-3 h-9 inline-flex items-center whitespace-nowrap text-caption border-b-2 -mb-px",
        active ? "border-[var(--accent)] text-fg-primary font-medium" : "border-transparent text-fg-secondary hover:text-fg-primary")}>
      {s.label}
    </Link>
  );
}

export function CameraDeviceTabs({ className }: { className?: string }) {
  const pathname = usePathname() ?? "";
  return (
    <nav aria-label="Cameras and devices sections" className={cn("flex items-center gap-1 border-b border-border-subtle overflow-x-auto", className)} data-testid="camera-device-tabs">
      {SECTIONS.map((s) => (
        <SectionLink key={s.href} s={s} active={s.exact ? pathname === s.href : pathname === s.href || pathname.startsWith(s.href + "/")} />
      ))}
    </nav>
  );
}

// ─── Badges ───────────────────────────────────────────────────────────────

export function CameraStatusBadge({ status }: { status: string }) {
  return <Badge tone={CAMERA_STATUS_TONE[status] ?? "neutral"} dot testId="camera-status">{status}</Badge>;
}

const HEALTH_ICON: Record<string, ReactNode> = {
  Online: <Wifi size={11} aria-hidden />,
  Offline: <WifiOff size={11} aria-hidden />,
  Unknown: <Radio size={11} aria-hidden />,
  Maintenance: <Wrench size={11} aria-hidden />,
  Disabled: <CircleOff size={11} aria-hidden />
};

/** Health status with its label; a simulated device always carries the SIMULATED badge. */
export function HealthBadge({ health, label, simulated, testId }: { health: string; label?: string; simulated?: boolean; testId?: string }) {
  return (
    <span className="inline-flex flex-wrap items-center gap-1" data-testid={testId ?? "health"}>
      <Badge tone={simulated && health === "Online" ? "info" : HEALTH_TONE[health] ?? "neutral"} title={label}>
        <span className="inline-flex items-center gap-1">{HEALTH_ICON[health]}{label ?? health}</span>
      </Badge>
      {simulated && <SimulatedBadge />}
    </span>
  );
}

export function DeviceHealthBadge({ d }: { d: Pick<Device, "healthStatus" | "healthLabel" | "isSimulated"> }) {
  return <HealthBadge health={d.healthStatus} label={d.healthLabel} simulated={d.isSimulated} />;
}

export function CameraHealthBadge({ h }: { h?: CameraHealth | null }) {
  if (!h) return <span className="text-caption text-fg-tertiary" title="No health device is linked to this camera">No health device</span>;
  return <HealthBadge health={h.health} label={h.label} simulated={h.isSimulated} />;
}

export function ModeBadge({ simulated }: { simulated: boolean }) {
  return simulated ? <SimulatedBadge /> : <Badge tone="accent">Real device</Badge>;
}

export function SourceTypeBadge({ type }: { type?: string | null }) {
  const t = type ?? "None";
  return <Badge tone={t === "None" ? "neutral" : "info"}>{SOURCE_TYPE_LABEL[t] ?? t}</Badge>;
}

// ─── Authenticated media ──────────────────────────────────────────────────

/** Load an API media file (with the session token) as an object URL. */
export function useMediaUrl(path: string | null, refreshKey?: unknown) {
  const [url, setUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  useEffect(() => {
    if (!path) {
      setUrl(null);
      return;
    }
    const ctrl = new AbortController();
    let created: string | null = null;
    setLoading(true);
    setError(null);
    fetchMedia(path, ctrl.signal)
      .then((u) => {
        created = u;
        setUrl(u);
      })
      .catch((e: Error) => {
        if (e.name !== "AbortError") setError(e.message || "Could not load the file.");
      })
      .finally(() => setLoading(false));
    return () => {
      ctrl.abort();
      if (created) URL.revokeObjectURL(created);
    };
  }, [path, refreshKey]);
  return { url, error, loading };
}

/** A recorded video, fetched with the session (never a public URL). */
export const AuthVideo = forwardRef<HTMLVideoElement, { path: string; refreshKey?: unknown; className?: string }>(function AuthVideo({ path, refreshKey, className }, ref) {
  const media = useMediaUrl(path, refreshKey);
  if (media.loading && !media.url) return <div className={cn("grid place-items-center bg-surface-2 text-caption text-fg-tertiary aspect-video", className)}>Loading video…</div>;
  if (media.error) return <div className={cn("grid place-items-center bg-surface-2 text-caption text-[var(--risk-high)] aspect-video p-4 text-center", className)}>{media.error}</div>;
  if (!media.url) return null;
  return <video ref={ref} src={media.url} controls preload="metadata" className={cn("w-full bg-black rounded-[var(--radius-control)]", className)} data-testid="camera-video" />;
});

// ─── Zone editor ──────────────────────────────────────────────────────────

export type ZoneShape = { id: string; name: string; zoneType: string; points: ZonePoint[]; color?: string | null; isActive?: boolean };

/** Same rule as the API (ZoneGeometry): edges may not cross. Used only for immediate feedback. */
export function selfIntersects(pts: ZonePoint[]): boolean {
  const n = pts.length;
  const cross = (a: ZonePoint, b: ZonePoint, c: ZonePoint) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
  const between = (a: ZonePoint, b: ZonePoint, p: ZonePoint) =>
    Math.min(a.x, b.x) <= p.x && p.x <= Math.max(a.x, b.x) && Math.min(a.y, b.y) <= p.y && p.y <= Math.max(a.y, b.y);
  const hits = (p1: ZonePoint, p2: ZonePoint, q1: ZonePoint, q2: ZonePoint) => {
    const d1 = cross(q1, q2, p1), d2 = cross(q1, q2, p2), d3 = cross(p1, p2, q1), d4 = cross(p1, p2, q2);
    if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
    return (d1 === 0 && between(q1, q2, p1)) || (d2 === 0 && between(q1, q2, p2)) || (d3 === 0 && between(p1, p2, q1)) || (d4 === 0 && between(p1, p2, q2));
  };
  for (let i = 0; i < n; i++)
    for (let j = i + 1; j < n; j++) {
      if ((j + 1) % n === i || (i + 1) % n === j) continue;
      if (hits(pts[i], pts[(i + 1) % n], pts[j], pts[(j + 1) % n])) return true;
    }
  return false;
}

export function polygonArea(pts: ZonePoint[]): number {
  let s = 0;
  for (let i = 0; i < pts.length; i++) {
    const a = pts[i], b = pts[(i + 1) % pts.length];
    s += a.x * b.y - b.x * a.y;
  }
  return Math.abs(s / 2);
}

const clamp01 = (v: number) => Math.min(1, Math.max(0, Math.round(v * 1e6) / 1e6));

/**
 * The reference frame with zones drawn over it. With <c>draft</c> and
 * <c>onDraftChange</c> it is an editor: click the frame to add a point, drag
 * a point (or focus it and use the arrow keys) to move it, Delete / Backspace
 * removes the focused point. Coordinates are fractions of the frame (0–1).
 */
export function ZoneCanvas({ imageUrl, width, height, zones, selectedId, onSelect, draft, draftColor, onDraftChange, className }: {
  imageUrl: string;
  width: number;
  height: number;
  zones: ZoneShape[];
  selectedId?: string | null;
  onSelect?: (id: string) => void;
  draft?: ZonePoint[] | null;
  draftColor?: string;
  onDraftChange?: (pts: ZonePoint[]) => void;
  className?: string;
}) {
  const box = useRef<HTMLDivElement>(null);
  const [drag, setDrag] = useState<number | null>(null);
  const editing = !!draft && !!onDraftChange;

  function toPoint(clientX: number, clientY: number): ZonePoint | null {
    const r = box.current?.getBoundingClientRect();
    if (!r || r.width === 0 || r.height === 0) return null;
    return { x: clamp01((clientX - r.left) / r.width), y: clamp01((clientY - r.top) / r.height) };
  }

  function onFrameClick(e: React.MouseEvent) {
    if (!editing || drag !== null || (e.target as HTMLElement).dataset.vertex) return;
    const p = toPoint(e.clientX, e.clientY);
    if (p && draft!.length < 64) onDraftChange!([...draft!, p]);
  }

  function onMove(e: PointerEvent) {
    if (drag === null || !editing) return;
    const p = toPoint(e.clientX, e.clientY);
    if (p) onDraftChange!(draft!.map((q, i) => (i === drag ? p : q)));
  }

  function onVertexKey(i: number, e: KeyboardEvent) {
    if (!editing) return;
    const step = e.shiftKey ? 0.02 : 0.005;
    const move = (dx: number, dy: number) => {
      e.preventDefault();
      onDraftChange!(draft!.map((q, k) => (k === i ? { x: clamp01(q.x + dx), y: clamp01(q.y + dy) } : q)));
    };
    if (e.key === "ArrowLeft") move(-step, 0);
    else if (e.key === "ArrowRight") move(step, 0);
    else if (e.key === "ArrowUp") move(0, -step);
    else if (e.key === "ArrowDown") move(0, step);
    else if (e.key === "Delete" || e.key === "Backspace") {
      e.preventDefault();
      onDraftChange!(draft!.filter((_, k) => k !== i));
    }
  }

  const poly = (pts: ZonePoint[]) => pts.map((p) => `${p.x},${p.y}`).join(" ");
  const invalid = !!draft && draft.length >= 3 && selfIntersects(draft);

  return (
    <div className={cn("relative w-full select-none overflow-hidden rounded-[var(--radius-control)] border border-border-subtle bg-black", className)}
      style={{ aspectRatio: `${width} / ${height}` }}>
      <div ref={box} className={cn("absolute inset-0", editing && "cursor-crosshair")} onClick={onFrameClick} onPointerMove={onMove}
        onPointerUp={() => setDrag(null)} onPointerLeave={() => setDrag(null)} data-testid="zone-canvas">
        {/* eslint-disable-next-line @next/next/no-img-element -- an object URL from an authenticated fetch */}
        <img src={imageUrl} alt="Camera reference frame" draggable={false} className="absolute inset-0 h-full w-full object-fill pointer-events-none" />
        <svg viewBox="0 0 1 1" preserveAspectRatio="none" className="absolute inset-0 h-full w-full" aria-hidden>
          {zones.map((z) => {
            const color = z.color || ZONE_TYPE_COLOR[z.zoneType] || "#2F80ED";
            const selected = z.id === selectedId;
            return (
              <polygon key={z.id} points={poly(z.points)} fill={color} fillOpacity={selected ? 0.35 : 0.18} stroke={color}
                strokeWidth={selected ? 3 : 1.5} vectorEffect="non-scaling-stroke" strokeDasharray={z.isActive === false ? "6 4" : undefined}
                style={{ pointerEvents: onSelect && !editing ? "auto" : "none", cursor: onSelect && !editing ? "pointer" : undefined }}
                onClick={(e) => { e.stopPropagation(); onSelect?.(z.id); }} />
            );
          })}
          {draft && draft.length > 0 && (
            draft.length >= 3
              ? <polygon points={poly(draft)} fill={invalid ? "#EB5757" : draftColor ?? "#2F80ED"} fillOpacity={0.3} stroke={invalid ? "#EB5757" : draftColor ?? "#2F80ED"} strokeWidth={2} vectorEffect="non-scaling-stroke" />
              : <polyline points={poly(draft)} fill="none" stroke={draftColor ?? "#2F80ED"} strokeWidth={2} vectorEffect="non-scaling-stroke" />
          )}
        </svg>
        {zones.filter((z) => z.points.length > 0).map((z) => {
          const cx = z.points.reduce((s, p) => s + p.x, 0) / z.points.length;
          const cy = z.points.reduce((s, p) => s + p.y, 0) / z.points.length;
          return (
            <span key={`l-${z.id}`} className="absolute -translate-x-1/2 -translate-y-1/2 px-1.5 rounded-sm bg-black/60 text-white text-[10px] whitespace-nowrap pointer-events-none"
              style={{ left: `${cx * 100}%`, top: `${cy * 100}%` }}>
              {z.name}
            </span>
          );
        })}
        {editing && draft!.map((p, i) => (
          <button key={i} type="button" data-vertex="1" aria-label={`Point ${i + 1}: x ${p.x.toFixed(3)}, y ${p.y.toFixed(3)}. Arrow keys move it, Delete removes it.`}
            className="absolute h-3.5 w-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-white bg-[var(--accent)] shadow focus:outline-none focus-visible:ring-2 focus-visible:ring-white cursor-grab touch-none"
            style={{ left: `${p.x * 100}%`, top: `${p.y * 100}%` }}
            onPointerDown={(e) => { e.stopPropagation(); (e.target as HTMLElement).setPointerCapture(e.pointerId); setDrag(i); }}
            onPointerUp={(e) => { e.stopPropagation(); setDrag(null); }}
            onClick={(e) => e.stopPropagation()}
            onKeyDown={(e) => onVertexKey(i, e)} data-testid={`zone-vertex-${i}`} />
        ))}
      </div>
    </div>
  );
}

// ─── Tables ───────────────────────────────────────────────────────────────

export function EventTypeBadge({ type, severity }: { type: string; severity: string }) {
  const tone = type === "ConnectionOnline" ? "success" : severity === "Error" ? "danger" : severity === "Warning" ? "warning" : "neutral";
  return <Badge tone={tone}>{EVENT_TYPE_LABEL[type] ?? type}</Badge>;
}

export function EventSource({ source, simulated }: { source: string; simulated: boolean }) {
  return (
    <span className="inline-flex items-center gap-1">
      <span>{EVENT_SOURCE_LABEL[source] ?? source}</span>
      {simulated && <SimulatedBadge />}
    </span>
  );
}

export function DeviceEventTable({ rows, showDevice = true, testId }: { rows: DeviceEvent[]; showDevice?: boolean; testId?: string }) {
  const { th, td } = tableCls;
  return (
    <div className="overflow-x-auto" data-testid={testId}>
      <table className="w-full min-w-[640px]">
        <thead className="border-b border-border-subtle">
          <tr>
            <th className={th}>When</th>
            {showDevice && <th className={th}>Device</th>}
            <th className={th}>Event</th>
            <th className={th}>Details</th>
            <th className={th}>Source</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border-subtle">
          {rows.map((e) => (
            <tr key={e.id}>
              <td className={cn(td, "whitespace-nowrap")}>{formatDateTime(e.occurredAt)}</td>
              {showDevice && <td className={td}><Link href={`${BASE}/devices/${e.deviceId}`} className="font-mono hover:underline">{e.deviceCode}</Link></td>}
              <td className={td}><EventTypeBadge type={e.eventType} severity={e.severity} /></td>
              <td className={cn(td, "text-fg-secondary")}>
                {e.fromState && e.toState && <span className="mr-2 font-mono text-fg-tertiary">{e.fromState} → {e.toState}</span>}
                {e.code && <span className="mr-2 font-mono">{e.code}</span>}
                {e.message}
              </td>
              <td className={td}><EventSource source={e.source} simulated={e.isSimulated} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function LinkedRef({ href, code, name }: { href?: string; code: string; name?: string | null }) {
  const label = <><span className="font-mono">{code}</span>{name ? <span className="text-fg-tertiary"> · {name}</span> : null}</>;
  return href ? <Link href={href} className="hover:underline">{label}</Link> : <span>{label}</span>;
}
