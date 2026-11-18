"use client";

// Camera details: metadata, coverage, video source (credentials are
// write-only: the page only learns whether they are stored), the recorded
// video or a local webcam preview, the reference frame (uploaded, grabbed
// from the video or captured from the webcam), health from the linked
// device, zones, and the AI integration status (not connected in this phase).

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { Activity, BrainCircuit, Camera as CameraIcon, Cctv, History, Image as ImageIcon, KeyRound, Pencil, Shapes, Trash2, Upload, Video } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { ConfirmDialog, Modal } from "@/components/ui/Modal";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { Fact, Val } from "@/components/workers/shared";
import { CoveragePicker } from "@/components/cameras/CameraForm";
import { LiveAiCamera } from "@/components/cameras/LiveAiCamera";
import { AuthVideo, BASE, CameraDeviceTabs, CameraHealthBadge, CameraStatusBadge, SourceTypeBadge, useMediaUrl } from "@/components/cameras/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { formatDate, formatDateTime } from "@/lib/format";
import { P } from "@/lib/access";
import {
  AI_NOT_CONNECTED_NOTE,
  CAMERA_STATUSES,
  CAMERA_TYPE_LABEL,
  SOURCE_TYPES,
  SOURCE_TYPE_LABEL,
  ZONE_TYPE_LABEL,
  bytes,
  uploadCameraVideo,
  uploadReferenceFrame,
  type Camera,
  type CameraSource,
  type CameraZone
} from "@/lib/cameras";

export default function CameraDetailPage() {
  return (
    <RequirePermission perms={[P.CamerasView]} what="Camera details">
      <Detail />
    </RequirePermission>
  );
}

function Detail() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const toast = useToast();
  const cam = useResource<Camera>(`/api/cameras/${id}`);
  const zones = useResource<CameraZone[]>(`/api/cameras/${id}/zones`);
  const [dialog, setDialog] = useState<null | "status" | "coverage" | "source" | "delete">(null);
  const [frameKey, setFrameKey] = useState(0);
  const [videoKey, setVideoKey] = useState(0);
  const c = cam.data;

  if (cam.loading) return <div className="page"><SkeletonRows rows={8} /></div>;
  if (cam.error || !c) return <div className="page"><ErrorState title="Couldn't load the camera" body={errorMessage(cam.error)} retryLabel="Try again" onRetry={() => void cam.reload()} /></div>;
  const manage = c.can.manage;

  function saved(next: Camera, message: string) {
    cam.setData(next);
    setDialog(null);
    toast.push({ tone: "success", title: message });
  }

  async function remove() {
    try {
      await apiJson(`/api/cameras/${c!.id}`, { method: "DELETE" });
      toast.push({ tone: "success", title: `Camera ${c!.code} deleted` });
      router.push(`${BASE}/cameras`);
    } catch (e) {
      toast.push({ tone: "error", title: "Not deleted", description: errorMessage(e) });
      setDialog(null);
    }
  }

  return (
    <div className="page" data-testid="camera-detail">
      <PageHeader
        eyebrow={<Link href={`${BASE}/cameras`} className="hover:underline">Camera directory</Link>}
        title={<span className="flex flex-wrap items-center gap-3"><span className="font-mono">{c.code}</span><span className="text-fg-secondary font-normal">{c.name}</span><CameraStatusBadge status={c.status} /></span>}
        description={`${CAMERA_TYPE_LABEL[c.cameraType] ?? c.cameraType} · ${c.factoryCode} · ${c.floor.name}${c.line ? ` · ${c.line.name}` : ""}`}
        actions={manage ? (
          <div className="flex flex-wrap gap-2">
            <Link href={`${BASE}/cameras/${c.id}/zones`}><Button size="sm" variant="primary" iconLeft={<Shapes size={13} />} data-testid="camera-zones-link">Configure zones</Button></Link>
            <Link href={`${BASE}/cameras/${c.id}/edit`}><Button size="sm" variant="secondary" iconLeft={<Pencil size={13} />}>Edit</Button></Link>
            <Button size="sm" variant="secondary" onClick={() => setDialog("status")} data-testid="camera-status-change">Change status</Button>
            <Button size="sm" variant="ghost" iconLeft={<Trash2 size={13} />} disabled={c.can.delete === false} title={c.can.deleteBlockedReason ?? undefined}
              onClick={() => setDialog("delete")} data-testid="camera-delete">Delete</Button>
          </div>
        ) : <Link href={`${BASE}/cameras/${c.id}/zones`}><Button size="sm" variant="secondary" iconLeft={<Shapes size={13} />}>View zones</Button></Link>}
      />
      <CameraDeviceTabs className="mt-4" />
      {c.status !== "Active" && <Callout className="mt-4" tone="warning" title={`Camera ${c.status.toLowerCase()}`}>{c.statusReason ?? "No reason recorded."}</Callout>}

      {c.source?.sourceType === "LocalWebcam" && (
        <div id="live-ai" className="mt-6 scroll-mt-20">
          <Panel title="Live AI camera" icon={<BrainCircuit size={14} />} testId="camera-live-ai"
            subtitle="This browser's webcam; sampled frames analysed by OpenAI vision on the server. Results are AI estimates.">
            <LiveAiCamera cameraId={c.id} cameraCode={c.code} cameraActive={c.status === "Active"} browserSource />
          </Panel>
        </div>
      )}

      <div className="mt-6 grid gap-6 xl:grid-cols-3">
        <div className="xl:col-span-2 space-y-6">
          <Panel title="Camera" icon={<Cctv size={14} />}>
            <dl className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
              <Fact label="Camera ID"><Val mono>{c.code}</Val></Fact>
              <Fact label="Type">{CAMERA_TYPE_LABEL[c.cameraType] ?? c.cameraType}</Fact>
              <Fact label="Factory">{c.factoryCode} · {c.factoryName}</Fact>
              <Fact label="Production floor">{c.floor.code} · {c.floor.name}</Fact>
              <Fact label="Department"><Val>{c.department?.name}</Val></Fact>
              <Fact label="Main line"><Val>{c.line?.name}</Val></Fact>
              <Fact label="Location"><Val>{c.location}</Val></Fact>
              <Fact label="Installed"><Val>{c.installedOn ? formatDate(c.installedOn) : null}</Val></Fact>
              <Fact label="Status changed">{formatDateTime(c.statusChangedAt)}</Fact>
              {c.description && <div className="sm:col-span-2 lg:col-span-3"><Fact label="Notes">{c.description}</Fact></div>}
            </dl>
          </Panel>

          <Panel title="Coverage" icon={<Shapes size={14} />} subtitle="The production areas this camera observes (used by the future vision service)."
            right={manage ? <Button size="sm" variant="secondary" onClick={() => setDialog("coverage")} data-testid="coverage-edit">Edit coverage</Button> : undefined}>
            {c.coverage.length === 0 ? <p className="text-caption text-fg-tertiary">No lines or workstations mapped yet.</p> : (
              <div className="flex flex-wrap gap-2" data-testid="coverage-list">
                {c.coverage.map((v) => v.workstation
                  ? <Badge key={v.id} tone={v.workstation.isActive ? "info" : "neutral"} title={v.line.name}><span className="font-mono">{v.workstation.code}</span> · {v.line.name}</Badge>
                  : <Badge key={v.id} tone="accent">Whole line · {v.line.name}</Badge>)}
              </div>
            )}
          </Panel>

          <SourcePanel camera={c} manage={manage} videoKey={videoKey} onChange={() => setDialog("source")} />

          <FramePanel camera={c} manage={manage} frameKey={frameKey} videoKey={videoKey}
            onSaved={(next) => { cam.setData(next); setFrameKey((k) => k + 1); toast.push({ tone: "success", title: "Reference frame saved" }); }} />
        </div>

        <div className="space-y-6">
          <Panel title="Device health" icon={<Activity size={14} />}>
            {c.health ? (
              <dl className="grid gap-3">
                <Fact label="Health"><CameraHealthBadge h={c.health} /></Fact>
                <Fact label="Health device"><Link href={`${BASE}/devices/${c.health.deviceId}`} className="font-mono hover:underline">{c.health.deviceCode}</Link></Fact>
                <Fact label="Physically connected">{c.health.physicallyConnected ? "Yes" : "No"}</Fact>
                <Fact label="Last seen"><Val>{c.health.lastSeenAt ? formatDateTime(c.health.lastSeenAt) : null}</Val></Fact>
                {c.health.lastErrorMessage && <Fact label="Last error">{c.health.lastErrorMessage} <span className="text-fg-tertiary">({formatDateTime(c.health.lastErrorAt)})</span></Fact>}
                <Fact label="Last status update">{formatDateTime(c.lastStatusUpdateAt)}</Fact>
              </dl>
            ) : (
              <p className="text-caption text-fg-tertiary">No health device is linked. Register a device of category Camera for this camera to track its heartbeats.</p>
            )}
          </Panel>

          <Panel title="Zones" icon={<Shapes size={14} />} subtitle={`${c.activeZoneCount} active`}
            right={<Link href={`${BASE}/cameras/${c.id}/zones`} className="text-caption hover:underline">{manage ? "Configure" : "View"}</Link>}>
            {zones.loading ? <SkeletonRows rows={2} rowHeight="h-8" /> : (zones.data?.length ?? 0) === 0 ? (
              <p className="text-caption text-fg-tertiary">{c.referenceFrame ? "No zones drawn yet." : "Add a reference frame, then draw zones over it."}</p>
            ) : (
              <ul className="space-y-1.5 text-caption" data-testid="camera-zone-summary">
                {zones.data!.map((z) => (
                  <li key={z.id} className="flex items-center gap-2">
                    <span className="h-2.5 w-2.5 rounded-sm" style={{ background: z.color ?? "var(--accent)" }} aria-hidden />
                    <span className="text-fg-primary">{z.name}</span>
                    <span className="text-fg-tertiary">{ZONE_TYPE_LABEL[z.zoneType] ?? z.zoneType}{z.workstation ? ` · ${z.workstation.code}` : ""}</span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          <Panel title="AI integration" icon={<BrainCircuit size={14} />} testId="camera-ai-status">
            {c.source?.sourceType === "LocalWebcam" ? <Badge tone="success">Live AI available (OpenAI vision)</Badge> : <Badge tone="neutral">Not connected</Badge>}
            <p className="mt-2 text-caption text-fg-secondary">{AI_NOT_CONNECTED_NOTE}</p>
            <ul className="mt-3 space-y-1 text-caption">
              <Ready ok={c.ai.videoSourceReady} label="Video source configured" />
              <Ready ok={c.ai.referenceFrameReady} label="Reference frame available" />
              <Ready ok={c.ai.activeZones > 0} label={`${c.ai.activeZones} active zone${c.ai.activeZones === 1 ? "" : "s"}`} />
              <Ready ok={c.coverage.length > 0} label="Coverage mapped" />
            </ul>
          </Panel>
        </div>
      </div>

      <StatusDialog open={dialog === "status"} camera={c} onClose={() => setDialog(null)} onDone={(n) => saved(n, "Status changed")} />
      {dialog === "coverage" && <CoverageDialog camera={c} onClose={() => setDialog(null)} onDone={(n) => saved(n, "Coverage saved")} />}
      {dialog === "source" && <SourceDialog camera={c} onClose={() => setDialog(null)} onDone={(n) => { saved(n, "Video source saved"); setVideoKey((k) => k + 1); }} />}
      <ConfirmDialog open={dialog === "delete"} title={`Delete camera ${c.code}?`} tone="danger" confirmLabel="Delete camera" onCancel={() => setDialog(null)} onConfirm={() => void remove()} testId="camera-delete-dialog"
        description="This removes the camera, its coverage, its source versions and its unused health device. Cameras with zones or device history can't be deleted; deactivate them instead." />
    </div>
  );
}

function Ready({ ok, label }: { ok: boolean; label: string }) {
  return <li className={ok ? "text-[var(--risk-low)]" : "text-fg-tertiary"}>{ok ? "✓" : "○"} {label}</li>;
}

// ─── Video source ─────────────────────────────────────────────────────────

function SourcePanel({ camera: c, manage, videoKey, onChange }: { camera: Camera; manage: boolean; videoKey: number; onChange: () => void }) {
  const s = c.source;
  const [history, setHistory] = useState(false);
  const versions = useResource<CameraSource[]>(history ? `/api/cameras/${c.id}/sources` : null);
  return (
    <Panel title="Video source" icon={<Video size={14} />} subtitle="The server never connects to camera URLs in this phase; nothing here proves a stream works."
      right={manage ? <Button size="sm" variant="secondary" onClick={onChange} data-testid="source-edit">Change source</Button> : undefined} testId="camera-source">
      {!s || s.sourceType === "None" ? (
        <p className="text-caption text-fg-tertiary">No video source configured.</p>
      ) : (
        <dl className="grid gap-4 sm:grid-cols-2">
          <Fact label="Source"><SourceTypeBadge type={s.sourceType} /> <span className="text-caption text-fg-tertiary">v{s.version}</span></Fact>
          <Fact label="Connection check"><Badge tone="neutral">Not checked</Badge></Fact>
          {s.streamUrl && <Fact label="Stream URL"><span className="font-mono text-caption break-all" data-testid="source-url">{s.streamUrl}</span></Fact>}
          {(s.sourceType === "Rtsp" || s.sourceType === "HttpStream") && (
            <Fact label="Credentials">
              <span className="inline-flex items-center gap-1.5 text-caption" data-testid="source-credentials"><KeyRound size={12} aria-hidden />{s.hasCredentials ? "Stored encrypted (never shown)" : "None"}</span>
            </Fact>
          )}
          {s.webcamLabel && <Fact label="Webcam">{s.webcamLabel}</Fact>}
          {s.videoFileName && <Fact label="Video file">{s.videoFileName} <span className="text-fg-tertiary">({bytes(s.videoSizeBytes)})</span></Fact>}
          {s.notes && <Fact label="Notes">{s.notes}</Fact>}
          <Fact label="Configured">{formatDateTime(s.createdAt)} · {s.createdByEmail}</Fact>
        </dl>
      )}
      {s?.sourceType === "RecordedVideo" && s.hasVideo && <div className="mt-4"><AuthVideo path={`/api/cameras/${c.id}/source/video`} refreshKey={videoKey} /></div>}
      {s?.sourceType === "LocalWebcam" && <p className="mt-4 text-caption text-fg-secondary">The live preview and AI analysis are in “Live AI camera” at the top of this page.</p>}
      <button type="button" className="mt-4 inline-flex items-center gap-1.5 text-caption text-fg-secondary hover:underline" onClick={() => setHistory((h) => !h)}>
        <History size={12} /> {history ? "Hide" : "Show"} source history
      </button>
      {history && (versions.loading ? <SkeletonRows rows={2} rowHeight="h-8" /> : (
        <ul className="mt-2 divide-y divide-border-subtle text-caption" data-testid="source-history">
          {(versions.data ?? []).map((v) => (
            <li key={v.id} className="py-1.5 flex flex-wrap gap-2">
              <span className="font-mono">v{v.version}</span><span>{SOURCE_TYPE_LABEL[v.sourceType] ?? v.sourceType}</span>
              {v.streamUrl && <span className="font-mono text-fg-tertiary break-all">{v.streamUrl}</span>}
              {v.videoFileName && <span className="text-fg-tertiary">{v.videoFileName}</span>}
              <span className="ml-auto text-fg-tertiary">{formatDateTime(v.createdAt)}{v.retiredAt ? ` – ${formatDateTime(v.retiredAt)}` : " (current)"}</span>
            </li>
          ))}
          {(versions.data?.length ?? 0) === 0 && <li className="py-1.5 text-fg-tertiary">No versions yet.</li>}
        </ul>
      ))}
    </Panel>
  );
}

/** The viewer's own webcam, opened by the browser. Nothing is recorded or sent to the server. */
function WebcamPreview({ onCapture }: { onCapture?: (blob: Blob) => void }) {
  const video = useRef<HTMLVideoElement>(null);
  const [stream, setStream] = useState<MediaStream | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => () => stream?.getTracks().forEach((t) => t.stop()), [stream]);
  async function start() {
    setError(null);
    try {
      const s = await navigator.mediaDevices.getUserMedia({ video: true, audio: false });
      setStream(s);
      if (video.current) video.current.srcObject = s;
    } catch (e) {
      setError((e as Error).message || "The browser couldn't open a camera.");
    }
  }
  function stop() {
    stream?.getTracks().forEach((t) => t.stop());
    setStream(null);
  }
  function capture() {
    const v = video.current;
    if (!v || !onCapture || v.videoWidth === 0) return;
    const canvas = document.createElement("canvas");
    canvas.width = v.videoWidth;
    canvas.height = v.videoHeight;
    canvas.getContext("2d")!.drawImage(v, 0, 0);
    canvas.toBlob((b) => b && onCapture(b), "image/png");
  }
  return (
    <div className="space-y-2">
      <video ref={video} autoPlay playsInline muted className={stream ? "w-full rounded-[var(--radius-control)] bg-black" : "hidden"} />
      {error && <p className="text-caption text-[var(--risk-high)]">{error}</p>}
      <div className="flex flex-wrap gap-2">
        {stream ? <Button size="sm" variant="secondary" onClick={stop}>Stop webcam</Button> : <Button size="sm" variant="secondary" iconLeft={<CameraIcon size={13} />} onClick={() => void start()}>Open local webcam</Button>}
        {stream && onCapture && <Button size="sm" variant="primary" onClick={capture}>Use this frame</Button>}
      </div>
      <p className="field-hint">The webcam opens in this browser only. Nothing is streamed to or stored on the server.</p>
    </div>
  );
}

function SourceDialog({ camera: c, onClose, onDone }: { camera: Camera; onClose: () => void; onDone: (c: Camera) => void }) {
  const current = c.source;
  const [type, setType] = useState(current?.sourceType && current.sourceType !== "None" ? current.sourceType : c.cameraType === "RecordedVideo" ? "RecordedVideo" : "Rtsp");
  const [url, setUrl] = useState(current?.streamUrl ?? "");
  const [user, setUser] = useState("");
  const [password, setPassword] = useState("");
  const [keep, setKeep] = useState(!!current?.hasCredentials);
  const [label, setLabel] = useState(current?.webcamLabel ?? "");
  const [notes, setNotes] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const urlType = type === "Rtsp" || type === "HttpStream";
  const canKeep = !!current?.hasCredentials && urlType;

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const next = type === "RecordedVideo" && file
        ? await uploadCameraVideo(c.id, file, notes || undefined)
        : await apiJson<Camera>(`/api/cameras/${c.id}/source`, {
            method: "PUT",
            body: { sourceType: type, streamUrl: urlType ? url.trim() : null, userName: urlType ? user.trim() || null : null, password: urlType ? password || null : null,
              keepCredentials: canKeep && keep && !user && !password, webcamLabel: type === "LocalWebcam" ? label || null : null, notes: notes || null }
          });
      setPassword("");
      onDone(next);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open onClose={onClose} locked={busy} title="Change video source" testId="source-dialog"
      description="Saving adds a new source version; earlier versions are kept. Credentials are encrypted on the server and never shown again."
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button><Button variant="primary" onClick={() => void save()} disabled={busy || (type === "RecordedVideo" && !file && !current?.hasVideo)} data-testid="source-save">{busy ? "Saving…" : "Save source"}</Button></>}>
      <div className="space-y-4">
        {error && <Callout tone="danger" title="Not saved">{error}</Callout>}
        <Field label="Source type">
          {(ids) => (
            <Select id={ids.id} value={type} onChange={(e) => setType(e.target.value)} data-testid="source-type">
              {SOURCE_TYPES.map((t) => <option key={t} value={t}>{SOURCE_TYPE_LABEL[t]}</option>)}
            </Select>
          )}
        </Field>
        {urlType && (
          <>
            <Field label="Stream URL" hint={type === "Rtsp" ? "rtsp:// or rtsps://. A user name and password in the URL are split off and encrypted." : "http:// or https:// (MJPEG / HLS). For future IP camera integration."}>
              {(ids) => <Input id={ids.id} aria-describedby={ids.describedBy} value={url} onChange={(e) => setUrl(e.target.value)} placeholder={type === "Rtsp" ? "rtsp://10.0.4.21:554/stream1" : "https://10.0.4.21/mjpeg"} className="font-mono" autoComplete="off" spellCheck={false} data-testid="source-url-input" />}
            </Field>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field label="User name" hint="Optional">
                {(ids) => <Input id={ids.id} value={user} onChange={(e) => setUser(e.target.value)} autoComplete="off" />}
              </Field>
              <Field label="Password" hint="Optional; write-only">
                {(ids) => <Input id={ids.id} type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" data-testid="source-password" />}
              </Field>
            </div>
            {canKeep && (
              <label className="flex items-center gap-2 text-caption">
                <input type="checkbox" checked={keep} onChange={(e) => setKeep(e.target.checked)} disabled={!!user || !!password} />
                Keep the stored credentials (re-encrypted for the new version)
              </label>
            )}
          </>
        )}
        {type === "LocalWebcam" && (
          <Field label="Webcam label" hint="Optional: which camera on the viewing computer to use, e.g. “Logitech C920”. The browser opens it; the server never receives the video.">
            {(ids) => <Input id={ids.id} value={label} onChange={(e) => setLabel(e.target.value)} maxLength={120} />}
          </Field>
        )}
        {type === "RecordedVideo" && (
          <Field label="Video file" hint={current?.hasVideo ? `Leave empty to keep ${current.videoFileName}. MP4 (H.264) or WebM.` : "MP4 (H.264) or WebM, up to the server's limit (50 MB by default)."}>
            {(ids) => <input id={ids.id} type="file" accept="video/mp4,video/webm" onChange={(e) => setFile(e.target.files?.[0] ?? null)} className="block text-caption" data-testid="source-video-file" />}
          </Field>
        )}
        <Field label="Notes" hint="Optional">
          {(ids) => <TextArea id={ids.id} rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={500} />}
        </Field>
      </div>
    </Modal>
  );
}

// ─── Reference frame ──────────────────────────────────────────────────────

function FramePanel({ camera: c, manage, frameKey, videoKey, onSaved }: { camera: Camera; manage: boolean; frameKey: number; videoKey: number; onSaved: (c: Camera) => void }) {
  const toast = useToast();
  const frame = useMediaUrl(c.referenceFrame ? `/api/cameras/${c.id}/reference-frame` : null, frameKey);
  const fileRef = useRef<HTMLInputElement>(null);
  const videoRef = useRef<HTMLVideoElement>(null);
  const [mode, setMode] = useState<null | "video" | "webcam">(null);
  const [busy, setBusy] = useState(false);
  const hasVideo = c.source?.sourceType === "RecordedVideo" && c.source.hasVideo;

  async function save(blob: Blob, source: "Upload" | "VideoFrame", at?: number) {
    setBusy(true);
    try {
      onSaved(await uploadReferenceFrame(c.id, blob, source, at));
      setMode(null);
    } catch (e) {
      toast.push({ tone: "error", title: "Frame not saved", description: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  }

  function grab() {
    const v = videoRef.current;
    if (!v || v.videoWidth === 0) return;
    const canvas = document.createElement("canvas");
    canvas.width = v.videoWidth;
    canvas.height = v.videoHeight;
    canvas.getContext("2d")!.drawImage(v, 0, 0);
    canvas.toBlob((b) => b && void save(b, "VideoFrame", v.currentTime), "image/png");
  }

  return (
    <Panel title="Reference frame" icon={<ImageIcon size={14} />} subtitle="The still image zones are drawn on. Zone coordinates are fractions of its width and height."
      right={manage ? (
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="secondary" iconLeft={<Upload size={13} />} disabled={busy} onClick={() => fileRef.current?.click()} data-testid="frame-upload">Upload image</Button>
          {hasVideo && <Button size="sm" variant="secondary" disabled={busy} onClick={() => setMode(mode === "video" ? null : "video")} data-testid="frame-from-video">From video</Button>}
          <Button size="sm" variant="ghost" disabled={busy} onClick={() => setMode(mode === "webcam" ? null : "webcam")}>From webcam</Button>
        </div>
      ) : undefined} testId="camera-frame">
      <input ref={fileRef} type="file" accept="image/jpeg,image/png,image/webp" className="sr-only" aria-label="Upload a reference frame"
        onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ""; if (f) void save(f, "Upload"); }} />
      {mode === "video" && (
        <div className="mb-4 space-y-2">
          <AuthVideo ref={videoRef} path={`/api/cameras/${c.id}/source/video`} refreshKey={videoKey} />
          <div className="flex items-center gap-2"><Button size="sm" variant="primary" disabled={busy} onClick={grab} data-testid="frame-grab">Use the current frame</Button><span className="field-hint">Pause the video where the view is clear.</span></div>
        </div>
      )}
      {mode === "webcam" && <div className="mb-4"><WebcamPreview onCapture={(b) => void save(b, "VideoFrame")} /></div>}
      {!c.referenceFrame ? (
        <EmptyState icon={<ImageIcon size={18} />} title="No reference frame" body={manage ? "Upload a still of the camera's view, or grab one from the recorded video." : "An administrator adds one before drawing zones."} />
      ) : frame.error ? <p className="text-caption text-[var(--risk-high)]">{frame.error}</p> : frame.url ? (
        <figure>
          {/* eslint-disable-next-line @next/next/no-img-element -- an object URL from an authenticated fetch */}
          <img src={frame.url} alt={`Reference frame of ${c.code}`} className="w-full rounded-[var(--radius-control)] border border-border-subtle" data-testid="frame-image" />
          <figcaption className="mt-1.5 text-caption text-fg-tertiary">
            {c.referenceFrame.width}×{c.referenceFrame.height} · {c.referenceFrame.source === "VideoFrame" ? `frame${c.referenceFrame.frameSeconds != null ? ` at ${c.referenceFrame.frameSeconds.toFixed(1)} s` : ""}` : "uploaded"} · {formatDateTime(c.referenceFrame.updatedAt)}
          </figcaption>
        </figure>
      ) : <SkeletonRows rows={1} rowHeight="h-48" />}
    </Panel>
  );
}

// ─── Dialogs ──────────────────────────────────────────────────────────────

function StatusDialog({ open, camera: c, onClose, onDone }: { open: boolean; camera: Camera; onClose: () => void; onDone: (c: Camera) => void }) {
  const [status, setStatus] = useState(c.status === "Active" ? "Inactive" : "Active");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { if (open) { setStatus(c.status === "Active" ? "Inactive" : "Active"); setReason(""); setError(null); } }, [open, c.status]);
  async function save() {
    setBusy(true);
    try {
      onDone(await apiJson<Camera>(`/api/cameras/${c.id}/status`, { method: "PATCH", body: { status, reason: reason || null } }));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }
  const needsReason = status !== "Active";
  return (
    <Modal open={open} onClose={onClose} locked={busy} title={`Change status of ${c.code}`} testId="camera-status-dialog"
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button><Button variant={status === "Active" ? "primary" : "danger"} onClick={() => void save()} disabled={busy || status === c.status || (needsReason && !reason.trim())} data-testid="camera-status-save">{busy ? "Saving…" : "Change status"}</Button></>}>
      <div className="space-y-4">
        {error && <Callout tone="danger" title="Not changed">{error}</Callout>}
        <Field label="New status">
          {(ids) => <Select id={ids.id} value={status} onChange={(e) => setStatus(e.target.value)}>{CAMERA_STATUSES.map((s) => <option key={s} value={s} disabled={s === c.status}>{s}{s === c.status ? " (current)" : ""}</option>)}</Select>}
        </Field>
        <Field label="Reason" hint={needsReason ? "Required" : "Optional"}>
          {(ids) => <TextArea id={ids.id} rows={2} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} data-testid="camera-status-reason" />}
        </Field>
      </div>
    </Modal>
  );
}

function CoverageDialog({ camera: c, onClose, onDone }: { camera: Camera; onClose: () => void; onDone: (c: Camera) => void }) {
  const [sel, setSel] = useState(() => ({
    lineIds: c.coverage.filter((v) => v.kind === "Line").map((v) => v.line.id),
    workstationIds: c.coverage.filter((v) => v.workstation).map((v) => v.workstation!.id)
  }));
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save() {
    setBusy(true);
    try {
      onDone(await apiJson<Camera>(`/api/cameras/${c.id}/coverage`, { method: "PUT", body: sel }));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }
  return (
    <Modal open onClose={onClose} locked={busy} title={`Coverage of ${c.code}`} className="max-w-2xl" testId="coverage-dialog"
      description="Removed items are kept in the coverage history. Workstations used by active zones can't be removed."
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button><Button variant="primary" onClick={() => void save()} disabled={busy} data-testid="coverage-save">{busy ? "Saving…" : "Save coverage"}</Button></>}>
      {error && <Callout className="mb-4" tone="danger" title="Not saved">{error}</Callout>}
      <CoveragePicker factoryId={c.factoryId} floorId={c.floor.id} lineIds={sel.lineIds} workstationIds={sel.workstationIds} onChange={setSel} />
    </Modal>
  );
}
