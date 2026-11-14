"use client";

// Monitoring simulator: start / stop a simulation (factory, cameras, event
// frequency, event types, duration) and trigger single demo events. Every
// event goes through the same ingestion pipeline real producers will use and
// is labelled SIMULATED; nothing here is a camera AI result or names a
// worker. Simulated devices are driven through the device simulator.

import { useEffect, useMemo, useState } from "react";
import { FlaskConical, Play, Square, Zap } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { ConfirmDialog } from "@/components/ui/Modal";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useMyFactories } from "@/components/workers/shared";
import { tableCls, useQueryState } from "@/components/attendance/shared";
import { EventList, LiveIndicator, MonitoringTabs, SourceBadge, mergeEvents } from "@/components/monitoring/shared";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { useCan } from "@/lib/auth";
import { apiJson, errorMessage } from "@/lib/api";
import { P, type PagedResult } from "@/lib/access";
import { cn } from "@/lib/cn";
import type { Device } from "@/lib/cameras";
import {
  EVENT_TYPE_LABEL, MON, SIMULATED_NOTE, dhaka, dhakaTime, qs,
  type MonitoringCamera, type MonitoringCameraDetail, type MonitoringEvent, type SimulationRun, type SimulatorStatus, type SimulatorTriggerResult
} from "@/lib/monitoring";

export default function MonitoringSimulatorPage() {
  return (
    <RequirePermission perms={[P.MonitoringSimulate]} what="Monitoring simulator">
      <Simulator />
    </RequirePermission>
  );
}

const DEFAULT_TYPES = ["PERSON_DETECTED", "TRACK_UPDATED", "PERSON_LOST", "ZONE_ENTERED", "ZONE_EXITED", "ACTIVITY_CLASSIFIED", "UNCERTAIN_DETECTION",
  "CAMERA_STATUS_CHANGED", "DEVICE_STATUS_CHANGED"];
const STATUS_CHOICES: Record<string, string[]> = {
  CAMERA_STATUS_CHANGED: ["OFFLINE", "ONLINE"],
  DEVICE_STATUS_CHANGED: ["", "OFFLINE", "ONLINE"],
  MODEL_STATUS_CHANGED: ["DEGRADED", "ERROR", "READY"]
};

function Simulator() {
  const q = useQueryState(`${MON}/simulator`);
  const toast = useToast();
  const { factories } = useMyFactories();
  // Without a choice in the URL, open the first factory that has an active camera
  // (the alphabetically first one may have nothing to simulate).
  const allCameras = useResource<MonitoringCamera[]>(!q.get("factory") && (factories?.length ?? 0) > 1 ? "/api/monitoring/cameras" : null);
  const withCamera = factories?.find((f) => allCameras.data?.some((c) => c.factoryId === f.id && c.adminStatus === "Active"));
  const factory = q.get("factory") || withCamera?.id || ((factories?.length ?? 0) > 1 && !allCameras.data && !allCameras.error ? "" : factories?.[0]?.id) || "";
  const canDevices = useCan(P.DevicesView);
  const status = useResource<SimulatorStatus>(factory ? `/api/monitoring/simulator?factoryId=${factory}` : null, { pollMs: 5_000 });
  const cameras = useResource<MonitoringCamera[]>(factory ? `/api/monitoring/cameras?factoryId=${factory}` : null);
  const devices = useResource<PagedResult<Device>>(factory && canDevices ? `/api/devices${qs({ factoryId: factory, simulated: true, pageSize: 100 })}` : null);
  const [feed, setFeed] = useState<MonitoringEvent[]>([]);
  const hub = useMonitoringHub(factory || null, {
    onEvent: (e) => { if (e.sourceType === "SIMULATED") setFeed((cur) => mergeEvents(cur, e, 60)); }
  }, !!factory);

  // ── Start form ──
  const activeCams = useMemo(() => (cameras.data ?? []).filter((c) => c.adminStatus === "Active"), [cameras.data]);
  const [picked, setPicked] = useState<string[]>([]);
  const [rate, setRate] = useState(12);
  const [duration, setDuration] = useState(15);
  const [types, setTypes] = useState<string[]>(DEFAULT_TYPES);
  const [busy, setBusy] = useState(false);
  const [confirmStop, setConfirmStop] = useState(false);
  useEffect(() => setPicked(activeCams.slice(0, 4).map((c) => c.id)), [activeCams]);
  const run = status.data?.running ?? null;
  // Everything but device / model status happens on a camera.
  const needsCamera = types.some((t) => t !== "DEVICE_STATUS_CHANGED" && t !== "MODEL_STATUS_CHANGED");

  async function start() {
    setBusy(true);
    try {
      await apiJson<SimulationRun>("/api/monitoring/simulator/start", { method: "POST", body: { factoryId: factory, cameraIds: picked, eventsPerMinute: rate, durationMinutes: duration, eventTypes: types } });
      toast.push({ tone: "success", title: "Simulation started", description: "Simulated events will appear in the monitoring room, labelled SIMULATED." });
      void status.reload();
    } catch (e) {
      toast.push({ tone: "error", title: "Could not start", description: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  }

  async function stop() {
    if (!run) return;
    setBusy(true);
    try {
      await apiJson(`/api/monitoring/simulator/${run.id}/stop`, { method: "POST", body: { reason: "Stopped from the simulator page" } });
      toast.push({ tone: "success", title: "Simulation stopped" });
      setConfirmStop(false);
      void status.reload();
    } catch (e) {
      toast.push({ tone: "error", title: "Could not stop", description: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  }

  const { th, td } = tableCls;
  const generatable = status.data?.eventTypes ?? DEFAULT_TYPES;

  return (
    <div className="page" data-testid="monitoring-simulator">
      <PageHeader eyebrow="Central monitoring" title={<span className="inline-flex items-center gap-3">Monitoring simulator <SourceBadge source="SIMULATED" /></span>}
        description="Generate demonstration events for the monitoring room without cameras or AI. They take the same path real events will take."
        meta={<LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} />}
        actions={factories && factories.length > 1 ? (
          <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null })} testId="sim-factory">
            {factories.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        ) : undefined} />
      <MonitoringTabs className="mb-4" />
      <Callout tone="warning" title="Everything here is simulated" className="mb-4">{SIMULATED_NOTE} Alerts it raises are labelled SIMULATED too.</Callout>

      {!factory ? (factories && factories.length === 0 ? <Callout tone="info">You aren&apos;t assigned to a factory.</Callout> : <SkeletonRows rows={5} />) : status.error && !status.data ? (
        <ErrorState retryLabel="Try again" title="Could not load the simulator" body={status.error.message} onRetry={() => void status.reload()} />
      ) : !status.data ? <SkeletonRows rows={5} /> : (
        <div className="grid gap-4 xl:grid-cols-5">
          <div className="xl:col-span-3 space-y-4">
            <Panel title={run ? "Running simulation" : "Start a simulation"} icon={run ? <FlaskConical size={15} /> : <Play size={15} />} testId="sim-run">
              {run ? <RunningRun run={run} onStop={() => setConfirmStop(true)} busy={busy} /> : (
                <div className="p-4 space-y-4">
                  <div>
                    <p className="field-label">Cameras ({picked.length} of {activeCams.length})</p>
                    {activeCams.length === 0 ? (
                      <Callout tone="warning" testId="sim-no-cameras">
                        This factory has no active cameras, so there is nothing to simulate here. Choose another factory above, register a camera in Cameras &amp; devices, or tick only &quot;Device status&quot; below.
                      </Callout>
                    ) : (
                      <div className="flex flex-wrap gap-2" data-testid="sim-cameras">
                        {activeCams.map((c) => (
                          <label key={c.id} className={cn("inline-flex items-center gap-2 h-8 px-2.5 rounded-[var(--radius-control)] border text-caption cursor-pointer",
                            picked.includes(c.id) ? "border-[var(--accent)] bg-[var(--accent-soft)]" : "border-border-subtle")}>
                            <input type="checkbox" checked={picked.includes(c.id)} onChange={() => setPicked((p) => p.includes(c.id) ? p.filter((x) => x !== c.id) : [...p, c.id].slice(0, status.data!.maxCameras))} />
                            <span className="font-mono">{c.code}</span>{c.activeZones > 0 && <span className="text-fg-tertiary">{c.activeZones} zone(s)</span>}
                          </label>
                        ))}
                      </div>
                    )}
                  </div>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Field label={`Events per minute (1–${status.data.maxEventsPerMinute})`}>
                      {(ids) => <Input id={ids.id} type="number" min={1} max={status.data!.maxEventsPerMinute} value={rate} onChange={(e) => setRate(Number(e.target.value))} data-testid="sim-rate" />}
                    </Field>
                    <Field label={`Stops by itself after (minutes, 1–${status.data.maxDurationMinutes})`}>
                      {(ids) => <Input id={ids.id} type="number" min={1} max={status.data!.maxDurationMinutes} value={duration} onChange={(e) => setDuration(Number(e.target.value))} data-testid="sim-duration" />}
                    </Field>
                  </div>
                  <div>
                    <p className="field-label">Event types</p>
                    <div className="flex flex-wrap gap-2">
                      {generatable.map((t) => (
                        <label key={t} className={cn("inline-flex items-center gap-2 h-8 px-2.5 rounded-[var(--radius-control)] border text-caption cursor-pointer",
                          types.includes(t) ? "border-[var(--accent)] bg-[var(--accent-soft)]" : "border-border-subtle")}>
                          <input type="checkbox" checked={types.includes(t)} onChange={() => setTypes((x) => x.includes(t) ? x.filter((y) => y !== t) : [...x, t])} />
                          {EVENT_TYPE_LABEL[t] ?? t}
                        </label>
                      ))}
                    </div>
                    <p className="text-caption text-fg-tertiary mt-1">Zone events need zones on the cameras; device status uses simulated devices only.</p>
                  </div>
                  <Button variant="primary" iconLeft={<Play size={14} />} onClick={() => void start()} disabled={busy || types.length === 0 || rate < 1 || duration < 1 || (needsCamera && picked.length === 0)} data-testid="sim-start">
                    {busy ? "Starting…" : "Start simulation"}
                  </Button>
                </div>
              )}
            </Panel>

            <TriggerPanel factory={factory} cameras={activeCams} devices={devices.data?.items ?? []} />

            <Panel title="Recent runs" flush testId="sim-recent">
              {status.data.recent.length === 0 ? <p className="px-4 py-6 text-caption text-fg-tertiary">No simulations yet.</p> : (
                <div className="overflow-x-auto">
                  <table className="w-full min-w-[40rem]">
                    <thead><tr><th className={th}>Started (Dhaka)</th><th className={th}>Status</th><th className={th}>Cameras</th><th className={th}>Rate</th><th className={th}>Events</th><th className={th}>By</th></tr></thead>
                    <tbody>
                      {status.data.recent.map((r) => (
                        <tr key={r.id}>
                          <td className={cn(td, "tabular-nums")}>{dhaka(r.startedAt)}</td>
                          <td className={cn(td, "text-caption")}>{r.status}{r.stopReason ? ` · ${r.stopReason}` : ""}</td>
                          <td className={cn(td, "text-caption")}>{r.cameras.map((c) => c.code).join(", ") || "—"}</td>
                          <td className={cn(td, "tabular-nums")}>{r.eventsPerMinute}/min</td>
                          <td className={cn(td, "tabular-nums")}>{r.eventCount}</td>
                          <td className={cn(td, "text-caption")}>{r.startedByEmail}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </Panel>
          </div>

          <Panel className="xl:col-span-2" title="Simulated events" subtitle="Live · Dhaka time" icon={<FlaskConical size={15} />} flush testId="sim-feed">
            <div className="max-h-[44rem] overflow-y-auto">
              <EventList rows={feed} dense empty={<p className="px-4 py-8 text-center text-caption text-fg-tertiary">Start a simulation or trigger an event; it appears here as it is processed.</p>} />
            </div>
          </Panel>
        </div>
      )}

      <ConfirmDialog open={confirmStop} title="Stop the simulation?" description="Events generated so far stay in the timeline, labelled SIMULATED."
        confirmLabel="Stop simulation" tone="danger" busy={busy} onConfirm={() => void stop()} onCancel={() => setConfirmStop(false)} testId="sim-stop-dialog" />
    </div>
  );
}

function RunningRun({ run, onStop, busy }: { run: SimulationRun; onStop: () => void; busy: boolean }) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), 1000); return () => clearInterval(t); }, []);
  const start = new Date(run.startedAt).getTime();
  const end = new Date(run.endsAt).getTime();
  const pct = Math.min(100, Math.max(0, ((now - start) / (end - start)) * 100));
  return (
    <div className="p-4 space-y-3" data-testid="sim-running">
      <div className="flex flex-wrap items-center gap-3">
        <SourceBadge source="SIMULATED" />
        <span className="text-body font-medium">{run.eventCount} event(s) so far</span>
        <span className="text-caption text-fg-secondary">{run.eventsPerMinute}/min · {run.cameras.map((c) => c.code).join(", ") || "no cameras"}</span>
      </div>
      <div className="h-2 rounded-full bg-surface-2 overflow-hidden" role="progressbar" aria-valuenow={Math.round(pct)} aria-valuemin={0} aria-valuemax={100} aria-label="Simulation time used">
        <div className="h-full bg-[var(--risk-medium)]" style={{ width: `${pct}%` }} />
      </div>
      <p className="text-caption text-fg-tertiary">Started {dhakaTime(run.startedAt)} by {run.startedByEmail} · stops by itself at {dhakaTime(run.endsAt)} (Dhaka)</p>
      <p className="text-caption text-fg-tertiary">Types: {run.eventTypes.map((t) => EVENT_TYPE_LABEL[t] ?? t).join(", ")}</p>
      <Button variant="danger" iconLeft={<Square size={13} />} onClick={onStop} disabled={busy} data-testid="sim-stop">Stop simulation</Button>
    </div>
  );
}

function TriggerPanel({ factory, cameras, devices }: { factory: string; cameras: MonitoringCamera[]; devices: Device[] }) {
  const toast = useToast();
  const [type, setType] = useState("PERSON_DETECTED");
  const [camera, setCamera] = useState("");
  const [zone, setZone] = useState("");
  const [device, setDevice] = useState("");
  const [track, setTrack] = useState("");
  const [confidence, setConfidence] = useState("");
  const [activity, setActivity] = useState("SEWING");
  const [toStatus, setToStatus] = useState("");
  const [busy, setBusy] = useState(false);
  const [last, setLast] = useState<SimulatorTriggerResult | null>(null);
  const cam = camera || cameras[0]?.id || "";
  const detail = useResource<MonitoringCameraDetail>(cam ? `/api/monitoring/cameras/${cam}` : null);
  const needsCamera = !["DEVICE_STATUS_CHANGED", "MODEL_STATUS_CHANGED"].includes(type);
  const statusChoices = STATUS_CHOICES[type];
  useEffect(() => { setToStatus(STATUS_CHOICES[type]?.[0] ?? ""); setZone(""); }, [type]);

  async function trigger() {
    setBusy(true);
    try {
      const r = await apiJson<SimulatorTriggerResult>("/api/monitoring/simulator/events", {
        method: "POST",
        body: {
          factoryId: factory, eventType: type, cameraId: needsCamera ? cam || null : null, zoneId: zone || null, deviceId: device || null,
          trackId: track || null, confidence: confidence ? Number(confidence) : null, activity: type === "ACTIVITY_CLASSIFIED" ? activity : null, toStatus: toStatus || null
        }
      });
      setLast(r);
      toast.push({ tone: "success", title: r.alert ? `Event sent · alert ${r.alert.code}` : "Simulated event sent" });
    } catch (e) {
      toast.push({ tone: "error", title: "Could not send", description: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title="Trigger one demo event" icon={<Zap size={15} />} testId="sim-trigger">
      <div className="p-4 grid gap-3 sm:grid-cols-2">
        <Field label="Event type">
          {(ids) => (
            <Select id={ids.id} value={type} onChange={(e) => setType(e.target.value)} data-testid="trigger-type">
              {Object.keys(EVENT_TYPE_LABEL).map((t) => <option key={t} value={t}>{EVENT_TYPE_LABEL[t]}</option>)}
            </Select>
          )}
        </Field>
        {needsCamera && (
          <Field label="Camera">
            {(ids) => (
              <Select id={ids.id} value={cam} onChange={(e) => { setCamera(e.target.value); setZone(""); }} data-testid="trigger-camera">
                {cameras.length === 0 && <option value="">No active cameras</option>}
                {cameras.map((c) => <option key={c.id} value={c.id}>{c.code} · {c.name}</option>)}
              </Select>
            )}
          </Field>
        )}
        {needsCamera && (type === "ZONE_ENTERED" || type === "ZONE_EXITED") && (
          <Field label="Zone" hint={(detail.data?.zones.length ?? 0) === 0 ? "This camera has no zones" : undefined}>
            {(ids) => (
              <Select id={ids.id} value={zone} onChange={(e) => setZone(e.target.value)} data-testid="trigger-zone">
                <option value="">Any zone of the camera</option>
                {(detail.data?.zones ?? []).map((z) => <option key={z.id} value={z.id}>{z.name} ({z.zoneType})</option>)}
              </Select>
            )}
          </Field>
        )}
        {type === "DEVICE_STATUS_CHANGED" && (
          <Field label="Simulated device" hint={devices.length === 0 ? "No simulated devices: register one in Cameras & devices" : undefined}>
            {(ids) => (
              <Select id={ids.id} value={device} onChange={(e) => setDevice(e.target.value)}>
                <option value="">First simulated device</option>
                {devices.map((d) => <option key={d.id} value={d.id}>{d.code} · {d.name}</option>)}
              </Select>
            )}
          </Field>
        )}
        {statusChoices && (
          <Field label="New status">
            {(ids) => (
              <Select id={ids.id} value={toStatus} onChange={(e) => setToStatus(e.target.value)}>
                {statusChoices.map((s) => <option key={s} value={s}>{s || "Toggle"}</option>)}
              </Select>
            )}
          </Field>
        )}
        {needsCamera && type !== "UNCERTAIN_DETECTION" && type !== "CAMERA_STATUS_CHANGED" && (
          <Field label="Track id (optional)" hint="Temporary camera number, e.g. sim-t12. Empty: continue the latest or start a new one.">
            {(ids) => <Input id={ids.id} value={track} onChange={(e) => setTrack(e.target.value)} maxLength={64} />}
          </Field>
        )}
        {["PERSON_DETECTED", "TRACK_UPDATED", "ACTIVITY_CLASSIFIED", "UNCERTAIN_DETECTION"].includes(type) && (
          <Field label="Confidence (optional, 0–1)">
            {(ids) => <Input id={ids.id} type="number" step="0.01" min={0} max={1} value={confidence} onChange={(e) => setConfidence(e.target.value)} />}
          </Field>
        )}
        {type === "ACTIVITY_CLASSIFIED" && (
          <Field label="Sample activity">
            {(ids) => (
              <Select id={ids.id} value={activity} onChange={(e) => setActivity(e.target.value)}>
                {["SEWING", "MATERIAL_HANDLING", "WALKING", "UNKNOWN"].map((a) => <option key={a} value={a}>{a}</option>)}
              </Select>
            )}
          </Field>
        )}
        <div className="sm:col-span-2 flex flex-wrap items-center gap-3">
          <Button variant="primary" iconLeft={<Zap size={13} />} onClick={() => void trigger()} disabled={busy || (needsCamera && !cam)} data-testid="trigger-send">
            {busy ? "Sending…" : "Send simulated event"}
          </Button>
          {last && (
            <span className="text-caption text-fg-secondary" data-testid="trigger-result">
              {last.outcome === "DeviceSimulated" ? last.note : last.event?.summary}{last.alert ? ` · raised ${last.alert.code}` : ""}
            </span>
          )}
        </div>
      </div>
    </Panel>
  );
}
