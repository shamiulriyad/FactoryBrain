"use client";

// Machine monitoring building blocks: state badge, machine card, state
// duration bar, simple production bars, and the dialogs to configure a
// machine's rules and a device's IoT connection (including its device key).

import Link from "next/link";
import { useEffect, useState } from "react";
import { Cpu, KeyRound, Thermometer, Waves, Zap } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { useToast } from "@/components/ui/Toast";
import { apiJson, errorMessage } from "@/lib/api";
import { MON, ago } from "@/lib/monitoring";
import {
  CONDITION_LABEL, DEVICE_TYPES, STATE_COLOR, STATE_TONE, hours, num, stateLabel,
  type BucketCount, type DeviceKeyIssued, type GroupCount, type IotDevice, type Machine, type MachineMetrics, type MachineProfile
} from "@/lib/iot";

export function MachineStateBadge({ state, simulated, testId }: { state: string; simulated?: boolean; testId?: string }) {
  return (
    <span className="inline-flex items-center gap-1" data-testid={testId}>
      <Badge tone={STATE_TONE[state] ?? "neutral"} dot>{stateLabel(state)}</Badge>
      {simulated && <Badge tone="accent">Simulated</Badge>}
    </span>
  );
}

/** One machine at a glance: state, readings, conditions, devices. */
export function MachineCard({ m }: { m: Machine }) {
  return (
    <Link href={`${MON}/machines/${m.id}`} className="block rounded-[var(--radius-card,12px)] border border-border-subtle bg-surface-1 p-3 hover:border-[var(--accent)]"
      data-testid={`machine-card-${m.code}`}>
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="font-medium truncate">{m.code}</p>
          <p className="text-caption text-fg-tertiary truncate">{m.workstation?.code ?? "Not placed"}{m.line ? ` · ${m.line.name}` : ""}</p>
        </div>
        {m.monitored ? <MachineStateBadge state={m.state} simulated={m.isSimulated} /> : <Badge tone="neutral">No sensor</Badge>}
      </div>
      {m.monitored && (
        <>
          <p className="mt-1 text-caption text-fg-secondary truncate" title={m.reason ?? undefined}>{m.since ? `since ${ago(m.since)}` : ""}{m.reason ? ` · ${m.reason}` : ""}</p>
          <div className="mt-2 flex flex-wrap gap-x-3 gap-y-1 text-caption tabular-nums text-fg-secondary">
            {m.powerWatts != null && <span className="inline-flex items-center gap-1"><Zap size={12} />{num(m.powerWatts, 0, " W")}</span>}
            {m.temperatureC != null && <span className="inline-flex items-center gap-1"><Thermometer size={12} />{num(m.temperatureC, 1, " °C")}</span>}
            {m.vibrationMmS != null && <span className="inline-flex items-center gap-1"><Waves size={12} />{num(m.vibrationMmS, 1, " mm/s")}</span>}
            <span className="inline-flex items-center gap-1"><Cpu size={12} />{m.devices.length} device{m.devices.length === 1 ? "" : "s"}</span>
          </div>
          {m.activeConditions.length > 0 && (
            <div className="mt-2 flex flex-wrap gap-1">{m.activeConditions.map((c) => <Badge key={c} tone="warning">{CONDITION_LABEL[c] ?? c}</Badge>)}</div>
          )}
        </>
      )}
    </Link>
  );
}

/** Time in each state over a period, as one stacked bar. */
export function StateBar({ m, className }: { m: MachineMetrics; className?: string }) {
  const parts = [
    ["RUNNING", m.runningSeconds], ["IDLE", m.idleSeconds], ["STOPPED", m.stoppedSeconds], ["ERROR", m.errorSeconds],
    ["MAINTENANCE", m.maintenanceSeconds], ["OFFLINE", m.offlineSeconds], ["UNKNOWN", m.unknownSeconds]
  ] as const;
  const total = parts.reduce((s, [, v]) => s + v, 0) || 1;
  return (
    <div className={className}>
      <div className="flex h-2.5 w-full overflow-hidden rounded-full bg-surface-2" role="img"
        aria-label={parts.filter(([, v]) => v > 0).map(([s, v]) => `${stateLabel(s)} ${hours(v)}`).join(", ")}>
        {parts.filter(([, v]) => v > 0).map(([s, v]) => <span key={s} style={{ width: `${(v / total) * 100}%`, background: STATE_COLOR[s] }} title={`${stateLabel(s)}: ${hours(v)}`} />)}
      </div>
      <div className="mt-1 flex flex-wrap gap-x-3 text-[11px] text-fg-tertiary">
        {parts.filter(([, v]) => v > 0).map(([s, v]) => (
          <span key={s} className="inline-flex items-center gap-1"><span className="size-2 rounded-full" style={{ background: STATE_COLOR[s] }} />{stateLabel(s)} {hours(v)}</span>
        ))}
      </div>
    </div>
  );
}

/** Horizontal bars for pieces per line / machine / shift. */
export function CountBars({ rows, empty = "No counts in this period.", testId }: { rows: GroupCount[]; empty?: string; testId?: string }) {
  const max = Math.max(1, ...rows.map((r) => Math.abs(r.pieces)));
  if (rows.length === 0) return <p className="text-caption text-fg-tertiary">{empty}</p>;
  return (
    <ul className="space-y-1.5" data-testid={testId}>
      {rows.map((r) => (
        <li key={r.key} className="grid grid-cols-[minmax(0,9rem)_1fr_auto] items-center gap-2 text-caption">
          <span className="truncate" title={r.label}>{r.label}</span>
          <span className="h-2 rounded-full bg-surface-2 overflow-hidden"><span className="block h-full rounded-full bg-[var(--accent)]" style={{ width: `${(Math.abs(r.pieces) / max) * 100}%` }} /></span>
          <span className="tabular-nums font-medium">{r.pieces}{r.piecesPerHour != null ? <span className="text-fg-tertiary font-normal"> · {r.piecesPerHour}/h</span> : null}</span>
        </li>
      ))}
    </ul>
  );
}

/** Pieces per hour (or day) as columns. */
export function BucketChart({ buckets, testId }: { buckets: BucketCount[]; testId?: string }) {
  const max = Math.max(1, ...buckets.map((b) => b.pieces));
  if (buckets.length === 0) return <p className="text-caption text-fg-tertiary">No data for this range.</p>;
  return (
    <div className="flex items-end gap-1 h-32 overflow-x-auto" data-testid={testId} role="img" aria-label={buckets.map((b) => `${b.label}: ${b.pieces}`).join(", ")}>
      {buckets.map((b) => (
        <div key={b.start} className="flex flex-col items-center justify-end h-full min-w-[22px] flex-1" title={`${b.label}: ${b.pieces} pieces`}>
          <span className="text-[10px] tabular-nums text-fg-tertiary">{b.pieces || ""}</span>
          <span className="w-full rounded-t bg-[var(--accent)]" style={{ height: `${(b.pieces / max) * 80}%`, minHeight: b.pieces ? 2 : 0 }} />
          <span className="text-[10px] text-fg-tertiary mt-0.5 whitespace-nowrap">{b.label}</span>
        </div>
      ))}
    </div>
  );
}

// ─── Machine rules ─────────────────────────────────────────────────────────

const numOrNull = (v: string) => (v.trim() === "" ? null : Number(v));

export function MachineProfileDialog({ machineId, profile, open, onClose, onSaved }: {
  machineId: string; profile: MachineProfile; open: boolean; onClose: () => void; onSaved: () => void;
}) {
  const toast = useToast();
  const [f, setF] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (!open) return;
    const s = (v: number | null | undefined) => (v == null ? "" : String(v));
    setF({
      machineKind: profile.machineKind, countingMode: profile.countingMode, pulsesPerPiece: String(profile.pulsesPerPiece),
      runningPowerWatts: s(profile.runningPowerWatts), idlePowerWatts: s(profile.idlePowerWatts), staleAfterSeconds: s(profile.staleAfterSeconds),
      stopAlertAfterSeconds: s(profile.stopAlertAfterSeconds), longIdleAlertSeconds: s(profile.longIdleAlertSeconds), maxTemperatureC: s(profile.maxTemperatureC),
      maxVibrationMmS: s(profile.maxVibrationMmS), counterSilenceAlertSeconds: s(profile.counterSilenceAlertSeconds),
      flapTransitionsPer5Min: s(profile.flapTransitionsPer5Min), alertsEnabled: profile.alertsEnabled ? "1" : "0"
    });
    setError(null);
  }, [open, profile]);
  const set = (k: string) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await apiJson(`/api/iot/machines/${machineId}/profile`, {
        method: "PUT",
        body: {
          machineKind: f.machineKind, countingMode: f.countingMode, pulsesPerPiece: Number(f.pulsesPerPiece || 1),
          runningPowerWatts: numOrNull(f.runningPowerWatts), idlePowerWatts: numOrNull(f.idlePowerWatts), staleAfterSeconds: numOrNull(f.staleAfterSeconds),
          stopAlertAfterSeconds: numOrNull(f.stopAlertAfterSeconds), longIdleAlertSeconds: numOrNull(f.longIdleAlertSeconds), maxTemperatureC: numOrNull(f.maxTemperatureC),
          maxVibrationMmS: numOrNull(f.maxVibrationMmS), counterSilenceAlertSeconds: numOrNull(f.counterSilenceAlertSeconds),
          flapTransitionsPer5Min: numOrNull(f.flapTransitionsPer5Min), alertsEnabled: f.alertsEnabled === "1", expectedVersion: profile.isDefault ? 0 : profile.version
        }
      });
      toast.push({ tone: "success", title: "Machine rules saved" });
      onSaved();
      onClose();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const numField = (k: string, label: string, hint?: string) => (
    <Field label={label} hint={hint}>{(ids) => <Input id={ids.id} inputMode="decimal" value={f[k] ?? ""} onChange={set(k)} placeholder="off" />}</Field>
  );

  return (
    <Modal open={open} onClose={onClose} locked={busy} title="Machine rules" testId="machine-profile-dialog"
      description="How counts become pieces, how power becomes a state, and when to raise machine alerts. Empty = rule off."
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button><Button variant="primary" onClick={() => void save()} disabled={busy}>Save</Button></>}>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Machine kind">{(ids) => (
          <Select id={ids.id} value={f.machineKind ?? "OTHER"} onChange={set("machineKind")}>
            <option value="SEWING">Sewing</option><option value="CUTTING">Cutting</option><option value="PRESSING">Ironing / pressing</option><option value="OTHER">Other</option>
          </Select>
        )}</Field>
        <Field label="Counting rule" hint="Not every pulse is a finished garment">{(ids) => (
          <Select id={ids.id} value={f.countingMode ?? "PIECE"} onChange={set("countingMode")}>
            <option value="PIECE">One count = one piece</option>
            <option value="PULSES_PER_PIECE">Several pulses = one piece</option>
            <option value="CYCLE_NOT_PIECE">Machine cycles (not pieces)</option>
          </Select>
        )}</Field>
        {f.countingMode === "PULSES_PER_PIECE" && numField("pulsesPerPiece", "Pulses per piece")}
        {numField("runningPowerWatts", "Running at or above (W)", "For power-only sensors")}
        {numField("idlePowerWatts", "Idle at or above (W)", "Below this: stopped")}
        {numField("staleAfterSeconds", "Unknown after no telemetry (s)")}
        {numField("stopAlertAfterSeconds", "Unexpected stop after (s)", "Only during a scheduled shift")}
        {numField("longIdleAlertSeconds", "Long idle after (s)")}
        {numField("maxTemperatureC", "Temperature limit (°C)")}
        {numField("maxVibrationMmS", "Vibration limit (mm/s)")}
        {numField("counterSilenceAlertSeconds", "Counter silent while running (s)")}
        {numField("flapTransitionsPer5Min", "Unusual: state changes per 5 min")}
        <Field label="Machine alerts">{(ids) => (
          <Select id={ids.id} value={f.alertsEnabled ?? "1"} onChange={set("alertsEnabled")}><option value="1">On</option><option value="0">Off</option></Select>
        )}</Field>
      </div>
      {error && <Callout tone="danger" className="mt-3">{error}</Callout>}
    </Modal>
  );
}

// ─── Device IoT configuration ──────────────────────────────────────────────

export function DeviceConfigDialog({ device, open, onClose, onSaved }: { device: IotDevice | null; open: boolean; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const [type, setType] = useState("");
  const [protocol, setProtocol] = useState("");
  const [via, setVia] = useState("DIRECT");
  const [client, setClient] = useState("");
  const [interval, setInterval_] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [issued, setIssued] = useState<DeviceKeyIssued | null>(null);
  useEffect(() => {
    if (!open || !device) return;
    setType(device.deviceType);
    setProtocol(device.protocol);
    setVia(device.connectionVia);
    setClient(device.mqttClientId ?? "");
    setInterval_(device.telemetryIntervalSeconds == null ? "" : String(device.telemetryIntervalSeconds));
    setError(null);
    setIssued(null);
  }, [open, device]);
  if (!device) return null;
  const types = DEVICE_TYPES.filter((t) => t.category === device.category || ((t.id === "GENERIC_SENSOR" || t.id === "MQTT_DEVICE") && device.category === "MachineSensor"));

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await apiJson(`/api/iot/devices/${device!.deviceId}/config`, {
        method: "PUT",
        body: { deviceType: type, protocol, connectionVia: via, mqttClientId: protocol === "MQTT" && client.trim() ? client.trim() : null,
          telemetryIntervalSeconds: interval.trim() ? Number(interval) : null, expectedVersion: device!.version }
      });
      toast.push({ tone: "success", title: `${device!.code} configured` });
      onSaved();
      onClose();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  async function key(revoke: boolean) {
    setBusy(true);
    setError(null);
    try {
      if (revoke) {
        await apiJson(`/api/iot/devices/${device!.deviceId}/key`, { method: "DELETE" });
        toast.push({ tone: "success", title: "Device key revoked" });
      } else setIssued(await apiJson<DeviceKeyIssued>(`/api/iot/devices/${device!.deviceId}/key`, { method: "POST", body: {} }));
      onSaved();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} locked={busy} title={`IoT connection · ${device.code}`} testId="device-config-dialog"
      description="Placement (line, workstation, machine) is set on the device itself in Cameras & devices."
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>Close</Button><Button variant="primary" onClick={() => void save()} disabled={busy}>Save</Button></>}>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Device type">{(ids) => (
          <Select id={ids.id} value={type} onChange={(e) => setType(e.target.value)}>{types.map((t) => <option key={t.id} value={t.id}>{t.label}</option>)}</Select>
        )}</Field>
        <Field label="Protocol">{(ids) => (
          <Select id={ids.id} value={protocol} onChange={(e) => setProtocol(e.target.value)} disabled={device.isSimulated}>
            {device.isSimulated ? <option value="SIMULATOR">Simulator</option> : <><option value="MQTT">MQTT (preferred)</option><option value="HTTP">HTTP with device key</option></>}
          </Select>
        )}</Field>
        <Field label="Connects through">{(ids) => (
          <Select id={ids.id} value={via} onChange={(e) => setVia(e.target.value)}><option value="DIRECT">Directly</option><option value="ESP32">An ESP32</option><option value="GATEWAY">An industrial gateway</option></Select>
        )}</Field>
        {protocol === "MQTT" && (
          <Field label="MQTT client id / broker user" hint="For the broker's ACL; never a password">{(ids) => <Input id={ids.id} value={client} onChange={(e) => setClient(e.target.value)} />}</Field>
        )}
        <Field label="Telemetry every (s)">{(ids) => <Input id={ids.id} inputMode="numeric" value={interval} onChange={(e) => setInterval_(e.target.value)} />}</Field>
      </div>
      {device.topics.length > 0 && (
        <div className="mt-3 text-caption">
          <p className="text-fg-tertiary mb-1">MQTT topics for this device</p>
          <ul className="font-mono text-[12px] space-y-0.5">{device.topics.map((t) => <li key={t}>{t}</li>)}</ul>
        </div>
      )}
      {!device.isSimulated && device.protocol === "HTTP" && (
        <div className="mt-3 rounded border border-border-subtle p-3 text-caption space-y-2">
          <p className="inline-flex items-center gap-1 font-medium"><KeyRound size={13} />Device key {device.hasKey ? <Badge tone="success">{device.keyPrefix}</Badge> : <Badge>none</Badge>}</p>
          <p className="text-fg-tertiary">The device posts to /api/iot/device/telemetry (or status, heartbeat, production) with headers X-Device-Id: {device.deviceId} and X-Device-Key.</p>
          <div className="flex gap-2">
            <Button size="sm" onClick={() => void key(false)} disabled={busy}>{device.hasKey ? "Rotate key" : "Issue key"}</Button>
            {device.hasKey && <Button size="sm" variant="danger" onClick={() => void key(true)} disabled={busy}>Revoke</Button>}
          </div>
          {issued && (
            <Callout tone="warning" testId="device-key">
              <p className="font-medium">Copy this key now: it is not shown again.</p>
              <p className="font-mono break-all select-all mt-1">{issued.key}</p>
            </Callout>
          )}
        </div>
      )}
      {error && <Callout tone="danger" className="mt-3">{error}</Callout>}
    </Modal>
  );
}
