"use client";

// One camera, expanded: what can honestly be shown for its source (recorded
// video playback, a local webcam preview from the viewer's own browser, or
// "live stream configured but not connected"), its connection, coverage,
// zones, the AI integration status, its tracks with their predicted activity,
// its alerts and its events, live. No stream URL or credential ever reaches
// this page, and no video frame or keypoint from the computer-vision service.

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useCallback, useEffect, useRef, useState } from "react";
import { ArrowLeft, Bell, Bot, Cctv, PersonStanding, Radio, Scissors, Settings2, Shapes, Video, Webcam } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { Badge } from "@/components/ui/Badge";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { AuthVideo, useMediaUrl } from "@/components/cameras/shared";
import {
  AlertActionButtons, AlertActionDialog, AlertRow, ConnectionBadge, EventList, LiveIndicator, MonitoringTabs, mergeEvents, type AlertAction
} from "@/components/monitoring/shared";
import { ActivityTrackTable, IdentityLegend } from "@/components/monitoring/activity";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { useCan } from "@/lib/auth";
import { P } from "@/lib/access";
import {
  ACTIVITY_DISCLAIMER, FEED_KIND_LABEL, MON, dhaka, isActivityEvent, isGarmentEvent,
  type MonitoringActivity, type MonitoringAlert, type MonitoringCamera, type MonitoringCameraDetail, type MonitoringEvent
} from "@/lib/monitoring";

export default function CameraExpandedPage() {
  return (
    <RequirePermission perms={[P.MonitoringView, P.CamerasView]} what="Camera monitoring">
      <Expanded />
    </RequirePermission>
  );
}

function Expanded() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const detail = useResource<MonitoringCameraDetail>(`/api/monitoring/cameras/${id}`, { pollMs: 60_000 });
  const d = detail.data;
  const siblings = useResource<MonitoringCamera[]>(d ? `/api/monitoring/cameras?factoryId=${d.camera.factoryId}` : null);
  const canCameraSettings = useCan(P.CamerasView);
  const canSeeActivity = useCan(P.MonitoringView);
  const activity = useResource<MonitoringActivity>(canSeeActivity ? `/api/monitoring/activity?cameraId=${id}` : null, { pollMs: 30_000 });
  const garment = useResource<MonitoringActivity>(canSeeActivity ? `/api/monitoring/activity?cameraId=${id}&kind=garment` : null, { pollMs: 30_000 });
  const [events, setEvents] = useState<MonitoringEvent[]>([]);
  const [acting, setActing] = useState<{ alert: MonitoringAlert; action: AlertAction } | null>(null);

  const pending = useRef<ReturnType<typeof setTimeout> | null>(null);
  const reloadActivity = useCallback(() => {
    if (pending.current) return;
    pending.current = setTimeout(() => { pending.current = null; void activity.reload(); void garment.reload(); }, 1500);
  }, [activity, garment]);
  useEffect(() => () => { if (pending.current) clearTimeout(pending.current); }, []);
  // Latest predicted activity per temporary track, from the activity summary and then from live events.
  const [liveActivity, setLiveActivity] = useState<Map<string, { activity: string; confidence?: number | null }>>(new Map());
  useEffect(() => {
    if (!activity.data) return;
    setLiveActivity(new Map(activity.data.tracks.map((t) => [t.trackId, { activity: t.activity, confidence: t.confidence }])));
  }, [activity.data]);

  useEffect(() => { if (d) setEvents(d.recentEvents); }, [d]);
  const hub = useMonitoringHub(d?.camera.factoryId ?? null, {
    onEvent: (e) => {
      if (e.camera?.id !== id) return;
      setEvents((cur) => mergeEvents(cur, e, 50));
      if (isActivityEvent(e.eventType) && e.trackId) {
        const a = typeof e.payload?.activity === "string" ? e.payload.activity : null;
        if (a && !isGarmentEvent(e.eventType)) setLiveActivity((cur) => new Map(cur).set(e.trackId!, { activity: a, confidence: e.confidence }));
        reloadActivity();
      } else if (e.eventType === "PERSON_LOST") reloadActivity();
    },
    onCameraStatus: (m) => { if (m.id === id) void detail.reload(); },
    onAlert: (_, a) => { if (a.camera?.id === id) void detail.reload(); },
    onResync: () => void detail.reload()
  }, !!d);

  if (detail.error && !d) return <div className="page"><ErrorState retryLabel="Try again" title="Camera not available" body={detail.error.message} onRetry={() => void detail.reload()} /></div>;
  if (!d) return <div className="page"><SkeletonRows rows={6} /></div>;
  const c = d.camera;

  return (
    <div className="page" data-testid="camera-expanded">
      <PageHeader eyebrow={<Link href={`${MON}/cameras`} className="inline-flex items-center gap-1"><ArrowLeft size={12} /> Camera monitoring</Link>}
        title={<span className="inline-flex items-center gap-3"><span className="font-mono">{c.code}</span> {c.name}</span>}
        description={[c.factoryCode, c.line?.name, c.floorName].filter(Boolean).join(" · ")}
        meta={<div className="flex flex-wrap items-center gap-3"><ConnectionBadge status={c.connection.status} label={c.connection.label} simulated={c.connection.isSimulated} /><LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} /></div>}
        actions={
          <div className="flex flex-wrap items-center gap-2">
            {(siblings.data?.length ?? 0) > 1 && (
              <FilterSelect label="Camera" value={id} onChange={(v) => router.push(`${MON}/cameras/${v}`)} testId="camera-switch">
                {siblings.data!.map((s) => <option key={s.id} value={s.id}>{s.code} · {s.name}</option>)}
              </FilterSelect>
            )}
            {canCameraSettings && <Link href={`/app/cameras-devices/cameras/${c.id}`}><Button size="sm" variant="ghost" iconLeft={<Settings2 size={13} />}>Camera settings</Button></Link>}
          </div>
        } />
      <MonitoringTabs className="mb-4" />

      <div className="grid gap-4 xl:grid-cols-3">
        <div className="xl:col-span-2 space-y-4">
          <Panel title="View" subtitle={FEED_KIND_LABEL[c.feed.kind]} icon={<Cctv size={15} />} testId="camera-viewer">
            <div className="p-4"><Viewer c={c} /></div>
          </Panel>
          {canSeeActivity && (
            <Panel title="Tracks and predicted activity" subtitle="Temporary track ids on this camera · last 5 minutes" icon={<PersonStanding size={15} />} flush testId="camera-activity">
              <div className="p-4 border-b border-border-subtle space-y-3">
                <IdentityLegend />
                <p className="text-caption text-fg-tertiary">{ACTIVITY_DISCLAIMER}</p>
              </div>
              {activity.error && !activity.data ? <p className="px-4 py-6 text-caption text-fg-tertiary">Activity could not be loaded: {activity.error.message}</p>
                : !activity.data ? <div className="p-4"><SkeletonRows rows={2} /></div>
                : <ActivityTrackTable tracks={activity.data.tracks.filter((t) => t.current)} showCamera={false}
                    empty="No track on this camera has an activity prediction right now (activity recognition may be off for this camera)." />}
              <div className="px-4 pt-4 pb-1 border-t border-border-subtle">
                <p className="text-caption font-medium text-fg-primary inline-flex items-center gap-1.5"><Scissors size={12} aria-hidden /> Garment activity (trained model)</p>
              </div>
              {garment.error && !garment.data ? <p className="px-4 py-3 text-caption text-fg-tertiary">Garment activity could not be loaded: {garment.error.message}</p>
                : !garment.data ? <div className="p-4"><SkeletonRows rows={1} /></div>
                : garment.data.tracks.some((t) => t.current) ? (
                  <ActivityTrackTable garment tracks={garment.data.tracks.filter((t) => t.current)} showCamera={false} empty="" />
                ) : (
                  <p className="px-4 pb-3 text-caption text-fg-tertiary" data-testid="camera-garment-empty">
                    No garment activity predictions on this camera right now. They appear when a trained, evaluated garment model runs on it.
                  </p>
                )}
              <div className="px-4 py-2 border-t border-border-subtle text-caption flex flex-wrap gap-4">
                <Link className="text-[var(--accent)]" href={`${MON}/activity?factory=${c.factoryId}&camera=${c.id}`}>Posture history for this camera</Link>
                <Link className="text-[var(--accent)]" href={`${MON}/activity?kind=garment&factory=${c.factoryId}&camera=${c.id}`}>Garment activity history</Link>
              </div>
            </Panel>
          )}
          <Panel title="Events on this camera" subtitle="Live · Dhaka time" icon={<Bell size={15} />} flush testId="camera-events">
            <div className="max-h-[26rem] overflow-y-auto">
              <EventList rows={events} dense activityByTrack={liveActivity} empty={<p className="px-4 py-6 text-caption text-fg-tertiary">No events for this camera yet.</p>} />
            </div>
            <div className="px-4 py-2 border-t border-border-subtle text-caption">
              <Link className="text-[var(--accent)]" href={`${MON}/events?factory=${c.factoryId}&camera=${c.id}`}>Open in the event timeline</Link>
            </div>
          </Panel>
        </div>

        <div className="space-y-4">
          <Panel title="Connection" icon={<Radio size={15} />} testId="camera-connection">
            <dl className="p-4 grid grid-cols-2 gap-x-4 gap-y-3 text-caption">
              <Fact label="Status"><ConnectionBadge status={c.connection.status} simulated={c.connection.isSimulated} /></Fact>
              <Fact label="Physically connected">{c.connection.physicallyConnected ? "Yes" : "No"}</Fact>
              <Fact label="Health device">{c.connection.healthDeviceCode ?? "None"}</Fact>
              <Fact label="Last seen">{dhaka(c.connection.lastSeenAt)}</Fact>
              <Fact label="Source">{c.feed.sourceType}</Fact>
              <Fact label="Stream check">{c.feed.connectionCheck === "NOT_CHECKED" ? "Not checked" : "n/a"}</Fact>
            </dl>
            <p className="px-4 pb-4 text-caption text-fg-tertiary">{c.connection.label}</p>
            {c.connection.lastError && <div className="px-4 pb-4"><Callout tone="warning">Last error: {c.connection.lastError}</Callout></div>}
          </Panel>

          <Panel title="AI integration" icon={<Bot size={15} />} testId="camera-ai">
            <div className="p-4 space-y-2">
              <Badge tone={d.aiStatus === "CONNECTED" ? "success" : "neutral"} testId="camera-ai-status">
                {d.aiStatus === "CONNECTED" ? "Analysing" : d.aiStatus === "IDLE" ? "Not analysing now" : "Not connected"}
              </Badge>
              <p className="text-caption text-fg-secondary">{d.aiMessage}</p>
              <p className="text-caption text-fg-tertiary">No video frames, poses or keypoints are sent to this dashboard: only structured events. Developers check the model on the service&apos;s own protected preview.</p>
            </div>
          </Panel>

          <Panel title="Coverage and zones" icon={<Shapes size={15} />} testId="camera-zones">
            <div className="p-4 space-y-3 text-caption">
              <p><span className="text-fg-tertiary">Main line:</span> {c.line?.name ?? "—"}</p>
              <p><span className="text-fg-tertiary">Workstations:</span> {c.coveredWorkstations.length ? c.coveredWorkstations.join(", ") : "—"}</p>
              {d.zones.length === 0 ? <p className="text-fg-tertiary">No active zones.</p> : (
                <ul className="flex flex-wrap gap-1.5">{d.zones.map((z) => <li key={z.id}><Badge tone={z.zoneType === "Restricted" ? "danger" : "info"}>{z.name}</Badge></li>)}</ul>
              )}
            </div>
          </Panel>

          <Panel title="Active alerts" icon={<Bell size={15} />} flush testId="camera-alerts">
            {d.activeAlerts.length === 0 ? <p className="px-4 py-6 text-caption text-fg-tertiary">No active alerts.</p> : (
              <ul className="divide-y divide-border-subtle">
                {d.activeAlerts.map((a) => (
                  <li key={a.id}>
                    <AlertRow a={a} compact />
                    <div className="px-4 pb-3"><AlertActionButtons a={a} onAction={(action) => setActing({ alert: a, action })} /></div>
                  </li>
                ))}
              </ul>
            )}
          </Panel>
        </div>
      </div>

      <AlertActionDialog alert={acting?.alert ?? null} action={acting?.action ?? null} onClose={() => setActing(null)}
        onDone={() => { setActing(null); void detail.reload(); }} />
    </div>
  );
}

function Fact({ label, children }: { label: string; children: React.ReactNode }) {
  return <div><dt className="text-fg-tertiary">{label}</dt><dd className="text-fg-primary mt-0.5">{children}</dd></div>;
}

/** What the source allows: playback, a local preview, or an honest "not connected". */
function Viewer({ c }: { c: MonitoringCamera }) {
  const frame = useMediaUrl(c.hasReferenceFrame && c.feed.kind !== "RECORDED_VIDEO" ? `/api/cameras/${c.id}/reference-frame` : null);
  if (c.feed.kind === "RECORDED_VIDEO" && c.feed.playable)
    return (
      <div className="space-y-2" data-testid="viewer-recorded">
        <AuthVideo path={`/api/cameras/${c.id}/source/video`} />
        <p className="text-caption text-fg-secondary inline-flex items-center gap-1.5"><Video size={12} aria-hidden /> Recorded video{c.feed.videoFileName ? ` (${c.feed.videoFileName})` : ""}: playback of a recording, not a live feed.</p>
      </div>
    );
  if (c.feed.kind === "LOCAL_WEBCAM") return <WebcamPreview label={c.feed.webcamLabel} note={c.feed.note} />;
  return (
    <div className="space-y-3" data-testid={c.feed.kind === "LIVE_STREAM" ? "viewer-stream" : "viewer-none"}>
      <div className="relative aspect-video rounded-[var(--radius-control)] bg-[#0b0f14] grid place-items-center overflow-hidden">
        {frame.url && (
          // eslint-disable-next-line @next/next/no-img-element -- an object URL from an authenticated fetch
          <img src={frame.url} alt={`Reference frame of ${c.code}`} className="absolute inset-0 h-full w-full object-contain opacity-40" />
        )}
        <div className="relative text-center px-6">
          <p className="text-white font-medium">{c.feed.kind === "LIVE_STREAM" ? "Live stream not connected" : "No video source"}</p>
          <p className="text-white/70 text-caption mt-1 max-w-md">{c.feed.note}</p>
          {frame.url && <p className="text-white/50 text-[11px] mt-2">Background: the configured reference still, not a live picture.</p>}
        </div>
      </div>
    </div>
  );
}

/** The viewer's own browser camera, only after they ask for it, stopped when they leave. */
function WebcamPreview({ label, note }: { label?: string | null; note: string }) {
  const video = useRef<HTMLVideoElement>(null);
  const [stream, setStream] = useState<MediaStream | null>(null);
  const [error, setError] = useState<string | null>(null);
  const supported = typeof navigator !== "undefined" && !!navigator.mediaDevices?.getUserMedia;

  useEffect(() => {
    if (video.current) video.current.srcObject = stream;
    return () => stream?.getTracks().forEach((t) => t.stop());
  }, [stream]);

  async function start() {
    setError(null);
    try {
      setStream(await navigator.mediaDevices.getUserMedia({ video: true, audio: false }));
    } catch (e) {
      setError(e instanceof Error && e.name === "NotAllowedError" ? "Camera permission was refused in this browser." : "No camera is available in this browser.");
    }
  }

  return (
    <div className="space-y-3" data-testid="viewer-webcam">
      <Callout tone="info" title="Local webcam preview">{note}{label ? ` Configured device: ${label}.` : ""}</Callout>
      <div className="relative aspect-video rounded-[var(--radius-control)] bg-[#0b0f14] grid place-items-center overflow-hidden">
        {stream ? <video ref={video} autoPlay muted playsInline className="absolute inset-0 h-full w-full object-contain" /> : (
          <span className="text-white/70 text-caption inline-flex items-center gap-1.5"><Webcam size={14} aria-hidden /> Preview off</span>
        )}
        {stream && <span className="absolute left-2 top-2 rounded bg-black/70 px-1.5 py-0.5 text-[10px] text-white">This browser&apos;s camera · not a factory feed · nothing is recorded</span>}
      </div>
      {!supported ? <p className="text-caption text-fg-tertiary">This browser can&apos;t open a camera.</p>
        : stream ? <Button size="sm" variant="secondary" onClick={() => setStream(null)}>Stop preview</Button>
        : <Button size="sm" variant="primary" iconLeft={<Webcam size={13} />} onClick={() => void start()} data-testid="webcam-start">Start local preview</Button>}
      {error && <Callout tone="warning">{error}</Callout>}
    </div>
  );
}
