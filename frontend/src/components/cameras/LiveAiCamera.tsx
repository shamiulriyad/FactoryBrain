"use client";

// Live AI camera: the viewer's own webcam (getUserMedia), sampled frames
// analysed by the OpenAI vision API on the BunonBrain server. The page keeps
// three things visibly apart:
//   • Measured facts  — camera status, frames sent, latency, usage, cost;
//   • AI estimate     — the latest single-frame answer (people, activity, PPE,
//                       approximate boxes), which can be wrong;
//   • Confirmed rule  — what several consecutive frames agreed on (the basis of
//                       events and alerts) — still AI-derived, to be checked
//                       in person.

import Link from "next/link";
import { useEffect, useMemo, useRef, useState } from "react";
import { Activity, AlertTriangle, BrainCircuit, Gauge, HardHat, Play, Radio, Shirt, Square, Users, Webcam } from "lucide-react";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Select } from "@/components/ui/Field";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { P } from "@/lib/access";
import { useBrowserCamera } from "@/lib/useBrowserCamera";
import { useCameraAiSession, type FrameOptions } from "@/lib/useCameraAiSession";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { EVENT_TYPE_LABEL, activityName } from "@/lib/monitoring";
import {
  FAILURE_LABEL,
  INCIDENT_LABEL,
  PPE_ITEM_LABEL,
  PPE_LABEL,
  PPE_TONE,
  PRESENCE_LABEL,
  SESSIONS,
  formatUsd,
  type CameraAiAnalysis,
  type CameraAiOverview,
  type CameraAiSession,
  type PpeStatus
} from "@/lib/cameraAi";

const INTERVALS = [1000, 2000, 5000, 10000];
const JPEG_QUALITY = 0.7;
const MAX_WIDTH = 640;

type Props = {
  cameraId: string;
  cameraCode: string;
  cameraActive: boolean;
  /** The camera's current source is “Local webcam” (browser webcam). */
  browserSource: boolean;
};

const time = (iso: string | null | undefined) => (iso ? new Date(iso).toLocaleTimeString() : "—");

export function LiveAiCamera({ cameraId, cameraCode, cameraActive, browserSource }: Props) {
  const canRun = useCan(P.CamerasView, P.CamerasManage);
  const cam = useBrowserCamera();
  const videoRef = useRef<HTMLVideoElement>(null);
  const overview = useResource<CameraAiOverview>(SESSIONS);
  const ai = useCameraAiSession(cameraId, videoRef);
  const limits = overview.data?.limits;
  const [intervalMs, setIntervalMs] = useState<number | null>(null);
  const interval = Math.max(intervalMs ?? limits?.defaultIntervalMs ?? 1000, limits?.minIntervalMs ?? 1000);
  const opts = useRef<FrameOptions>({ intervalMs: interval, maxWidth: MAX_WIDTH, quality: JPEG_QUALITY, maxBytes: 600 * 1024 });
  opts.current = { intervalMs: interval, maxWidth: MAX_WIDTH, quality: JPEG_QUALITY, maxBytes: limits?.maxFrameBytes ?? 600 * 1024 };

  const running = ["starting", "running", "waiting"].includes(ai.state.phase);

  // Show the stream; stop the AI when the camera itself goes away (unplugged, permission revoked).
  useEffect(() => {
    if (videoRef.current) videoRef.current.srcObject = cam.stream;
    if (!cam.stream && running) void ai.stop();
    // eslint-disable-next-line react-hooks/exhaustive-deps -- react to the stream only
  }, [cam.stream]);

  // Today's usage refreshes while analysing. Depend on the stable reload function only:
  // the resource object changes every render, which would restart the timer before it fires.
  const reloadOverview = overview.reload;
  useEffect(() => {
    if (!running) return;
    const t = setInterval(() => void reloadOverview(), 30_000);
    return () => clearInterval(t);
  }, [running, reloadOverview]);

  // Events of this camera from the live monitoring feed (stored events only; also shown when another tab triggers them).
  const hub = useMonitoringHub(null, {
    onEvent: (e) => {
      if (e.camera?.id === cameraId)
        ai.addEvents([{ id: e.id, eventType: e.eventType, summary: e.summary, occurredAt: e.occurredAt, stored: true }]);
    }
  }, running);

  async function startAll() {
    const ok = await cam.start();
    if (!ok) return;
    if (overview.data?.configured === false) return;   // preview only; the panel says why
    await ai.start(() => opts.current);
    void overview.reload();
  }

  async function stopAll() {
    await ai.stop();
    cam.stop();
    void overview.reload();
  }

  const a = ai.state.analysis;
  const s = ai.state.session;
  const stale = a ? Date.now() - new Date(a.at).getTime() > Math.max(3 * interval, 6000) : false;
  const aspect = cam.settings?.width && cam.settings?.height ? cam.settings.width / cam.settings.height : 16 / 9;

  const aiBadge: { tone: BadgeTone; label: string } = useMemo(() => {
    switch (ai.state.phase) {
      case "starting": return { tone: "info", label: "Starting AI" };
      case "running": return ai.state.frameError ? { tone: "warning", label: "AI error on last frame" } : { tone: "success", label: "AI analysing" };
      case "waiting": return { tone: "warning", label: "Reconnecting" };
      case "stopping": return { tone: "neutral", label: "Stopping" };
      case "ended": return { tone: "warning", label: "Session ended" };
      case "error": return { tone: "danger", label: "AI not started" };
      default: return { tone: "neutral", label: "AI idle" };
    }
  }, [ai.state.phase, ai.state.frameError]);

  if (!browserSource)
    return (
      <Callout tone="info" title="Live AI camera" testId="ai-camera-not-webcam">
        AI analysis runs on a browser webcam. Set this camera&apos;s video source to “Local webcam” to use it.
      </Callout>
    );

  return (
    <div className="space-y-4" data-testid="ai-camera">
      {/* ── Status and controls ── */}
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={cam.status === "live" ? "success" : cam.status === "starting" ? "info" : "neutral"} dot testId="ai-camera-status">
          {cam.status === "live" ? "Camera live" : cam.status === "starting" ? "Opening camera" : "Camera off"}
        </Badge>
        <Badge tone={aiBadge.tone} dot testId="ai-status">{aiBadge.label}</Badge>
        {s?.status === "OFFLINE" && <Badge tone="danger">Reported offline</Badge>}
        {overview.data && <Badge tone="neutral" title="OpenAI model used on the server">{overview.data.provider} · {overview.data.model}</Badge>}
        {running && hub.state === "live" && <Badge tone="info" dot><Radio size={11} aria-hidden /> Live events</Badge>}
      </div>

      {overview.data && !overview.data.configured && (
        <Callout tone="warning" title="AI analysis is not configured" testId="ai-not-configured">
          {overview.data.reason} The webcam preview still works; nothing is analysed.
        </Callout>
      )}
      {!cameraActive && <Callout tone="warning" title="Camera inactive">{cameraCode} is not active. Activate it before analysing it.</Callout>}
      {!canRun && <Callout tone="info">Starting the camera needs the “View cameras” permission.</Callout>}

      <div className="flex flex-wrap items-end gap-3">
        {cam.devices.length > 1 && (
          <label className="text-caption text-fg-secondary space-y-1">
            <span className="block">Camera</span>
            <Select value={cam.deviceId ?? ""} onChange={(e) => { cam.setDeviceId(e.target.value || null); if (cam.stream) void cam.start(e.target.value || null); }}
              aria-label="Webcam" data-testid="ai-device">
              {cam.deviceId === null && <option value="">Default camera</option>}
              {cam.devices.map((d) => <option key={d.deviceId} value={d.deviceId}>{d.label}</option>)}
            </Select>
          </label>
        )}
        <label className="text-caption text-fg-secondary space-y-1">
          <span className="block">AI sampling</span>
          <Select value={interval} onChange={(e) => setIntervalMs(Number(e.target.value))} aria-label="AI sampling interval" data-testid="ai-interval">
            {INTERVALS.filter((i) => i >= (limits?.minIntervalMs ?? 1000)).map((i) => (
              <option key={i} value={i}>{i === 1000 ? "1 frame / second" : `1 frame every ${i / 1000} s`}</option>
            ))}
          </Select>
        </label>
        {cam.stream || running ? (
          <Button variant="danger" iconLeft={<Square size={13} />} onClick={() => void stopAll()} data-testid="ai-stop">Stop camera</Button>
        ) : (
          <Button variant="primary" iconLeft={<Play size={13} />} onClick={() => void startAll()} disabled={!canRun || !cameraActive || cam.status === "starting"}
            data-testid="ai-start">Start camera</Button>
        )}
      </div>

      {cam.error && <Callout tone={cam.error.code === "denied" ? "warning" : "danger"} title="Camera" testId="ai-camera-error">{cam.error.message}</Callout>}
      {ai.state.problem && (
        <Callout tone={ai.state.phase === "waiting" ? "warning" : "danger"} title={ai.state.phase === "waiting" ? "Connection problem" : "AI session"} testId="ai-problem">
          {ai.state.problem.message}
        </Callout>
      )}

      {/* ── Preview with approximate boxes, results beside it on wide screens ── */}
      <div className="grid gap-4 xl:grid-cols-5">
      <div className="xl:col-span-3 min-w-0">
      <div className="relative w-full overflow-hidden rounded-[var(--radius-control)] bg-[#0b0f14]" style={{ aspectRatio: String(aspect) }}>
        <video ref={videoRef} autoPlay muted playsInline className={cam.stream ? "absolute inset-0 h-full w-full" : "hidden"} data-testid="ai-video" />
        {!cam.stream && (
          <div className="absolute inset-0 grid place-items-center text-white/70 text-caption">
            <span className="inline-flex items-center gap-1.5"><Webcam size={14} aria-hidden /> Camera off</span>
          </div>
        )}
        {cam.stream && a?.people.map((p, i) => p.box && (
          <div key={i} className="absolute border-2 border-dashed rounded-sm pointer-events-none"
            style={{
              left: `${p.box.x * 100}%`, top: `${p.box.y * 100}%`, width: `${p.box.width * 100}%`, height: `${p.box.height * 100}%`,
              borderColor: p.helmet === "missing" || p.safetyVest === "missing" ? "#f59e0b" : "#38bdf8", opacity: stale ? 0.35 : 1
            }} data-testid="ai-box">
            <span className="absolute -top-5 left-0 whitespace-nowrap rounded bg-black/75 px-1 text-[10px] text-white">
              ≈ Person · {activityName(p.activity)} · helmet {PPE_LABEL[p.helmet].toLowerCase()} · vest {PPE_LABEL[p.safetyVest].toLowerCase()}
            </span>
          </div>
        ))}
        {cam.stream && (
          <span className="absolute left-2 bottom-2 rounded bg-black/70 px-1.5 py-0.5 text-[10px] text-white">
            AI estimate · boxes approximate{a ? ` · frame of ${time(a.at)}${stale ? " (stale)" : ""}` : ""} · frames are analysed, never stored
          </span>
        )}
      </div>
      </div>
      <div className="xl:col-span-2 grid gap-4 sm:grid-cols-2 xl:grid-cols-1 content-start">
        <EstimatePanel a={a} frameError={ai.state.frameError} />
        <ConfirmedPanel s={s} confirmPresent={limits?.confirmPresent} confirmPpe={limits?.confirmPpe} />
      </div>
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
      <MeasuredPanel ai={ai.state} overview={overview.data} interval={interval} />

      {/* ── Events ── */}
      <section className="surface p-4" data-testid="ai-events">
        <h3 className="text-body font-medium flex items-center gap-1.5"><Activity size={14} aria-hidden /> Latest monitoring events from this camera</h3>
        <p className="text-caption text-fg-tertiary mt-0.5">Raised only after several frames agree; stored in the monitoring timeline and pushed live.</p>
        {ai.state.events.length === 0 ? (
          <p className="text-caption text-fg-tertiary mt-3">No events yet.</p>
        ) : (
          <ul className="mt-3 divide-y divide-border-subtle text-caption">
            {ai.state.events.map((e) => (
              <li key={e.id ?? `${e.eventType}-${e.occurredAt}`} className="py-1.5 flex flex-wrap items-center gap-2">
                <Badge tone={e.eventType === "PPE_VIOLATION" || e.eventType === "DETECTION_UNAVAILABLE" ? "warning" : e.eventType === "CAMERA_STATUS_CHANGED" ? "info" : "neutral"}>
                  {EVENT_TYPE_LABEL[e.eventType] ?? e.eventType}
                </Badge>
                <span className="text-fg-secondary">{e.summary ?? (e.stored ? "" : e.error)}</span>
                {e.alertId && <Link className="text-[var(--accent)] hover:underline" href={`/app/monitoring/alerts/${e.alertId}`}>Alert</Link>}
                <span className="ml-auto text-fg-tertiary">{time(e.occurredAt)}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
      </div>

      <p className="field-hint" data-testid="ai-disclaimer">
        {overview.data?.disclaimer ?? "AI estimates from still webcam frames; they can be wrong and never identify anyone."} Only sampled frames (at most one per
        interval, JPEG, ≤{MAX_WIDTH}px wide) leave this browser; the OpenAI key stays on the server.
      </p>
    </div>
  );
}

function Row({ icon, label, children }: { icon: React.ReactNode; label: string; children: React.ReactNode }) {
  return (
    <div className="flex items-start justify-between gap-3 py-1">
      <dt className="text-fg-tertiary inline-flex items-center gap-1.5">{icon}{label}</dt>
      <dd className="text-right text-fg-primary">{children}</dd>
    </div>
  );
}

function Ppe({ v }: { v: PpeStatus }) {
  return <Badge tone={PPE_TONE[v]}>{PPE_LABEL[v]}</Badge>;
}

function EstimatePanel({ a, frameError }: { a: CameraAiAnalysis | null; frameError: { code: string; message: string } | null }) {
  return (
    <section className="surface p-4" data-testid="ai-estimate">
      <h3 className="text-body font-medium flex items-center gap-1.5"><BrainCircuit size={14} aria-hidden /> AI estimate <span className="text-caption text-fg-tertiary font-normal">latest frame</span></h3>
      <p className="text-caption text-fg-tertiary">One still frame, as the model saw it. Not a confirmed fact.</p>
      {frameError && (
        <Callout className="mt-2" tone="warning" title={FAILURE_LABEL[frameError.code] ?? "Analysis failed"}>{frameError.message}</Callout>
      )}
      {!a ? <p className="text-caption text-fg-tertiary mt-3">No frame analysed yet.</p> : (
        <dl className="mt-2 text-caption">
          <Row icon={<Users size={12} aria-hidden />} label="People">
            {PRESENCE_LABEL[a.personPresence]}{a.personCount != null && a.personPresence === "present" ? ` · about ${a.personCount}` : ""}
          </Row>
          <Row icon={<Activity size={12} aria-hidden />} label="Activity">{activityName(a.sceneActivity)}</Row>
          <Row icon={<HardHat size={12} aria-hidden />} label="Helmet"><Ppe v={a.helmet} /></Row>
          <Row icon={<Shirt size={12} aria-hidden />} label="Safety vest"><Ppe v={a.safetyVest} /></Row>
          <Row icon={<AlertTriangle size={12} aria-hidden />} label="Safety incident">
            {a.incident.status === "none" ? "None seen" : a.incident.status === "unclear" ? "Can't tell" :
              <span className="text-[var(--risk-high)]">Possible: {INCIDENT_LABEL[a.incident.type ?? "OTHER"] ?? a.incident.type}{a.incident.description ? ` — ${a.incident.description}` : ""}</span>}
          </Row>
          <Row icon={<Gauge size={12} aria-hidden />} label="Image quality">{a.imageQuality}</Row>
          {a.notes && <p className="mt-1 text-fg-secondary">“{a.notes}”</p>}
          {a.adjustments.length > 0 && <p className="mt-1 text-fg-tertiary">Checked by the server: {a.adjustments.join(" ")}</p>}
          <p className="mt-1 text-fg-tertiary">Frame of {time(a.at)} · OpenAI answered in {a.latencyMs} ms</p>
        </dl>
      )}
    </section>
  );
}

function ConfirmedPanel({ s, confirmPresent, confirmPpe }: { s: CameraAiSession | null; confirmPresent?: number; confirmPpe?: number }) {
  const c = s?.confirmed;
  return (
    <section className="surface p-4" data-testid="ai-confirmed">
      <h3 className="text-body font-medium">Confirmed over several frames</h3>
      <p className="text-caption text-fg-tertiary">
        What {confirmPresent ?? 2}–{confirmPpe ?? 3} consecutive frames agreed on; this raises events and alerts. Still AI-derived: check in person before acting.
      </p>
      {!c ? <p className="text-caption text-fg-tertiary mt-3">Not started.</p> : (
        <dl className="mt-2 text-caption">
          <Row icon={<Users size={12} aria-hidden />} label="Person present">{c.personPresent ? "Yes" : "No"}</Row>
          <Row icon={<Activity size={12} aria-hidden />} label="Activity">{c.activity ? activityName(c.activity) : "—"}</Row>
          <Row icon={<HardHat size={12} aria-hidden />} label="PPE missing">
            {c.ppeViolations.length === 0 ? "None confirmed" : <span className="text-[var(--risk-high)]">{c.ppeViolations.map((p) => PPE_ITEM_LABEL[p] ?? p).join(", ")}</span>}
          </Row>
          <Row icon={<BrainCircuit size={12} aria-hidden />} label="AI detection">{c.detectionUnavailable ? <Badge tone="danger">Unavailable</Badge> : "Available"}</Row>
          <Row icon={<Webcam size={12} aria-hidden />} label="Camera feed">{c.offline ? <Badge tone="danger">Offline</Badge> : s?.status === "ENDED" ? "Stopped" : "Receiving frames"}</Row>
        </dl>
      )}
    </section>
  );
}

function MeasuredPanel({ ai, overview, interval }: { ai: ReturnType<typeof useCameraAiSession>["state"]; overview: CameraAiOverview | null; interval: number }) {
  const st = ai.session?.stats;
  const u = overview?.usageToday;
  return (
    <section className="surface p-4" data-testid="ai-measured">
      <h3 className="text-body font-medium flex items-center gap-1.5"><Gauge size={14} aria-hidden /> Measured</h3>
      <p className="text-caption text-fg-tertiary">Facts about this session, not AI output.</p>
      <dl className="mt-2 text-caption">
        <Row icon={null} label="Sampling">1 frame / {interval / 1000} s{ai.effectiveFps ? ` · actual ${ai.effectiveFps.toFixed(2)}/s` : ""}</Row>
        <Row icon={null} label="Round trip (browser)">{ai.roundTripMs != null ? `${ai.roundTripMs} ms` : "—"}</Row>
        <Row icon={null} label="OpenAI latency">{st?.lastLatencyMs != null ? `${st.lastLatencyMs} ms (avg ${st.avgLatencyMs} ms)` : "—"}</Row>
        <Row icon={null} label="Frames">{st ? `${ai.sent} sent · ${st.analysed} analysed · ${st.failed} failed · ${ai.skipped} skipped` : "—"}</Row>
        <Row icon={null} label="Frame size">{ai.frameBytes != null ? `${Math.round(ai.frameBytes / 1024)} KB JPEG` : "—"}</Row>
        <Row icon={null} label="Session tokens">{st ? `${st.inputTokens.toLocaleString()} in · ${st.outputTokens.toLocaleString()} out` : "—"}</Row>
        <Row icon={null} label="Session cost (est.)">{st?.estimatedCostUsd != null ? formatUsd(st.estimatedCostUsd) : u && !u.pricesConfigured ? "prices not set" : "—"}</Row>
        <Row icon={null} label="Today (all cameras)">{u ? `${u.calls.toLocaleString()} / ${u.dailyCallLimit.toLocaleString()} calls${u.estimatedCostUsd != null ? ` · ${formatUsd(u.estimatedCostUsd)}` : ""}` : "—"}</Row>
      </dl>
    </section>
  );
}
