"use client";

// Register / edit a device. No hardware is needed to register one. The mode
// (real or simulated) is fixed at registration so real and simulated
// health never mix. Placement links are checked by the API.

import { useEffect, useMemo, useState } from "react";
import { useRouter } from "next/navigation";
import { Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Panel } from "@/components/ui/Panel";
import { useToast } from "@/components/ui/Toast";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { cn } from "@/lib/cn";
import type { PagedResult } from "@/lib/access";
import type { Machine, Workstation } from "@/lib/assignments";
import { DEVICE_CATEGORIES, DEVICE_CATEGORY_LABEL, type Camera, type Device } from "@/lib/cameras";
import { BASE } from "@/components/cameras/shared";

type Reader = { id: string; code: string; name: string; isSimulated: boolean };

export function DeviceForm({ device }: { device?: Device }) {
  const router = useRouter();
  const toast = useToast();
  const { factories } = useMyFactories();
  const editing = !!device;
  const [f, setF] = useState(() => ({
    factoryId: device?.factoryId ?? "", code: device?.code ?? "", name: device?.name ?? "", category: device?.category ?? "Esp32Gateway",
    isSimulated: device?.isSimulated ?? true, lineId: device?.line?.id ?? "", workstationId: device?.workstation?.id ?? "", machineId: device?.machine?.id ?? "",
    cameraId: device?.camera?.id ?? "", readerId: device?.attendanceReader?.id ?? "", manufacturer: device?.manufacturer ?? "", model: device?.model ?? "",
    serialNumber: device?.serialNumber ?? "", firmwareVersion: device?.firmwareVersion ?? "", installedOn: device?.installedOn ?? "",
    interval: String(device?.heartbeatIntervalSeconds ?? 60), offlineAfter: String(device?.offlineAfterSeconds ?? 180)
  }));
  const [meta, setMeta] = useState<[string, string][]>(() => Object.entries(device?.metadata ?? {}));
  useEffect(() => {
    if (!editing && !f.factoryId && factories?.length === 1) setF((s) => ({ ...s, factoryId: factories[0].id }));
  }, [factories, editing, f.factoryId]);
  const { lines } = useFactoryOrg(f.factoryId || null);
  const ws = useResource<PagedResult<Workstation>>(f.factoryId ? `/api/workstations?factoryId=${f.factoryId}&pageSize=200` : null);
  const machines = useResource<Machine[]>(f.factoryId ? `/api/machines?factoryId=${f.factoryId}` : null);
  const cameras = useResource<PagedResult<Camera>>(f.factoryId && f.category === "Camera" ? `/api/cameras?factoryId=${f.factoryId}&pageSize=200` : null);
  const readers = useResource<Reader[]>(f.factoryId && f.category === "RfidReader" ? `/api/attendance/readers?factoryId=${f.factoryId}` : null);
  const wsOptions = useMemo(() => (ws.data?.items ?? []).filter((w) => !f.lineId || w.line.id === f.lineId), [ws.data, f.lineId]);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [problem, setProblem] = useState<string | null>(null);
  const set = (k: keyof typeof f, v: string | boolean) => setF((s) => ({ ...s, [k]: v }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const local: Record<string, string> = {};
    if (!f.factoryId) local.factoryId = "Choose the factory.";
    if (!editing && !f.code.trim()) local.code = "Enter a device ID.";
    if (!f.name.trim()) local.name = "Enter a name.";
    const interval = Number(f.interval), offline = Number(f.offlineAfter);
    if (!Number.isInteger(interval) || interval < 5 || interval > 86400) local.heartbeatIntervalSeconds = "5 seconds to 24 hours.";
    if (!Number.isInteger(offline) || offline <= interval || offline > 604800) local.offlineAfterSeconds = "Longer than the heartbeat interval, at most 7 days.";
    setErrors(local);
    if (Object.keys(local).length > 0) return;
    setBusy(true);
    setProblem(null);
    const metadata = Object.fromEntries(meta.filter(([k]) => k.trim()).map(([k, v]) => [k.trim(), v]));
    const body = {
      name: f.name.trim(), category: f.category, lineId: f.lineId || null, workstationId: f.workstationId || null, machineId: f.machineId || null,
      cameraId: f.category === "Camera" ? f.cameraId || null : null, attendanceReaderId: f.category === "RfidReader" ? f.readerId || null : null,
      manufacturer: f.manufacturer || null, model: f.model || null, serialNumber: f.serialNumber || null, firmwareVersion: f.firmwareVersion || null,
      metadata, installedOn: f.installedOn || null, heartbeatIntervalSeconds: interval, offlineAfterSeconds: offline
    };
    try {
      const saved = editing
        ? await apiJson<Device>(`/api/devices/${device!.id}`, { method: "PUT", body })
        : await apiJson<Device>("/api/devices", { method: "POST", body: { ...body, factoryId: f.factoryId, code: f.code.trim(), isSimulated: f.isSimulated } });
      toast.push({ tone: "success", title: editing ? "Device saved" : `Device ${saved.code} registered` });
      router.push(`${BASE}/devices/${saved.id}`);
    } catch (err) {
      if (isApiError(err)) setErrors(Object.fromEntries(Object.entries(err.fieldErrors).map(([k, v]) => [k, v[0]])));
      setProblem(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="mt-6 space-y-6" noValidate data-testid="device-form">
      {problem && <Callout tone="danger" title="Not saved">{problem}</Callout>}
      <Panel title="Device">
        <div className="grid gap-4 md:grid-cols-2">
          {!editing && (
            <Field label="Factory" error={errors.factoryId}>
              {(ids) => (
                <Select id={ids.id} value={f.factoryId} onChange={(e) => setF((s) => ({ ...s, factoryId: e.target.value, lineId: "", workstationId: "", machineId: "", cameraId: "", readerId: "" }))} data-testid="device-factory">
                  <option value="">Choose…</option>
                  {(factories ?? []).map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
                </Select>
              )}
            </Field>
          )}
          <Field label="Device ID" hint={editing ? "The device ID can't change." : "Unique in the factory, e.g. RFID-GATE-1 or ESP32-L3-01."} error={errors.code}>
            {(ids) => <Input id={ids.id} value={f.code} disabled={editing} onChange={(e) => set("code", e.target.value.toUpperCase())} className="font-mono" maxLength={32} data-testid="device-code" />}
          </Field>
          <Field label="Name" error={errors.name}>
            {(ids) => <Input id={ids.id} value={f.name} onChange={(e) => set("name", e.target.value)} maxLength={120} data-testid="device-name" />}
          </Field>
          <Field label="Category" error={errors.category}>
            {(ids) => (
              <Select id={ids.id} value={f.category} onChange={(e) => set("category", e.target.value)} data-testid="device-category">
                {DEVICE_CATEGORIES.map((c) => <option key={c} value={c}>{DEVICE_CATEGORY_LABEL[c]}</option>)}
              </Select>
            )}
          </Field>
        </div>
        <div className="mt-4">
          <p className="field-label">Mode{editing ? " (fixed at registration)" : ""}</p>
          <div className="grid gap-2 sm:grid-cols-2" role="radiogroup" aria-label="Device mode">
            {[[true, "Simulated device", "Software only. Heartbeats and errors come from the device simulator and are always marked SIMULATED."],
              [false, "Real device", "Physical hardware or its gateway sends heartbeats to the API. Shows 'Never connected' until it does."]].map(([v, label, hint]) => (
              <label key={String(v)} className={cn("p-3 rounded-[var(--radius-control)] border text-caption", f.isSimulated === v ? "border-[var(--accent)] bg-[var(--accent-soft)]" : "border-border-subtle", editing ? "opacity-70" : "cursor-pointer")}>
                <span className="flex items-center gap-2 text-body text-fg-primary"><input type="radio" name="mode" disabled={editing} checked={f.isSimulated === v} onChange={() => set("isSimulated", v as boolean)} data-testid={`device-mode-${v ? "simulated" : "real"}`} />{label as string}</span>
                <span className="mt-1 block text-fg-tertiary">{hint as string}</span>
              </label>
            ))}
          </div>
        </div>
      </Panel>

      <Panel title="Placement" subtitle="Optional. Everything must be in the device's factory; a machine or workstation fills in its line.">
        <div className="grid gap-4 md:grid-cols-2">
          <Field label="Production line" error={errors.lineId}>
            {(ids) => (
              <Select id={ids.id} value={f.lineId} onChange={(e) => setF((s) => ({ ...s, lineId: e.target.value, workstationId: "" }))} disabled={!f.factoryId}>
                <option value="">None</option>
                {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Workstation" error={errors.workstationId}>
            {(ids) => (
              <Select id={ids.id} value={f.workstationId} onChange={(e) => set("workstationId", e.target.value)} disabled={!f.factoryId}>
                <option value="">None</option>
                {wsOptions.map((w) => <option key={w.id} value={w.id}>{w.code} · {w.line.name}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Machine" error={errors.machineId}>
            {(ids) => (
              <Select id={ids.id} value={f.machineId} onChange={(e) => set("machineId", e.target.value)} disabled={!f.factoryId}>
                <option value="">None</option>
                {(machines.data ?? []).map((m) => <option key={m.id} value={m.id}>{m.code}{m.machineType ? ` · ${m.machineType}` : ""}</option>)}
              </Select>
            )}
          </Field>
          {f.category === "Camera" && (
            <Field label="Camera" hint="The camera this device reports health for" error={errors.cameraId}>
              {(ids) => (
                <Select id={ids.id} value={f.cameraId} onChange={(e) => set("cameraId", e.target.value)} data-testid="device-camera">
                  <option value="">None</option>
                  {(cameras.data?.items ?? []).filter((c) => !c.health || c.health.deviceId === device?.id).map((c) => <option key={c.id} value={c.id}>{c.code} · {c.name}</option>)}
                </Select>
              )}
            </Field>
          )}
          {f.category === "RfidReader" && (
            <Field label="Attendance reader" hint="Link to the reader the attendance module uses. Real and simulated must match." error={errors.attendanceReaderId}>
              {(ids) => (
                <Select id={ids.id} value={f.readerId} onChange={(e) => set("readerId", e.target.value)}>
                  <option value="">None</option>
                  {(readers.data ?? []).filter((r) => r.isSimulated === f.isSimulated).map((r) => <option key={r.id} value={r.id}>{r.code} · {r.name}</option>)}
                </Select>
              )}
            </Field>
          )}
        </div>
      </Panel>

      <Panel title="Hardware & heartbeat">
        <div className="grid gap-4 md:grid-cols-3">
          <Field label="Manufacturer" hint="Optional">{(ids) => <Input id={ids.id} value={f.manufacturer} onChange={(e) => set("manufacturer", e.target.value)} maxLength={80} />}</Field>
          <Field label="Model" hint="Optional">{(ids) => <Input id={ids.id} value={f.model} onChange={(e) => set("model", e.target.value)} maxLength={80} />}</Field>
          <Field label="Serial number" hint="Optional">{(ids) => <Input id={ids.id} value={f.serialNumber} onChange={(e) => set("serialNumber", e.target.value)} maxLength={80} />}</Field>
          <Field label="Firmware version" hint="Optional; updated by heartbeats">{(ids) => <Input id={ids.id} value={f.firmwareVersion} onChange={(e) => set("firmwareVersion", e.target.value)} maxLength={80} />}</Field>
          <Field label="Installed on" hint="Optional">{(ids) => <Input id={ids.id} type="date" value={f.installedOn} onChange={(e) => set("installedOn", e.target.value)} />}</Field>
          <div />
          <Field label="Heartbeat every (seconds)" error={errors.heartbeatIntervalSeconds}>{(ids) => <Input id={ids.id} type="number" min={5} max={86400} value={f.interval} onChange={(e) => set("interval", e.target.value)} data-testid="device-interval" />}</Field>
          <Field label="Offline after (seconds)" hint="No message for this long ⇒ offline" error={errors.offlineAfterSeconds}>{(ids) => <Input id={ids.id} type="number" min={10} max={604800} value={f.offlineAfter} onChange={(e) => set("offlineAfter", e.target.value)} data-testid="device-offline-after" />}</Field>
        </div>
      </Panel>

      <Panel title="Metadata" subtitle="Free key / value pairs (MAC address, IP, MQTT client id …). Never put passwords or tokens here; the API refuses them.">
        {errors.metadata && <p className="field-error mb-2">{errors.metadata}</p>}
        <div className="space-y-2">
          {meta.map(([k, v], i) => (
            <div key={i} className="flex gap-2">
              <Input aria-label="Key" value={k} onChange={(e) => setMeta(meta.map((m, j) => (j === i ? [e.target.value, m[1]] : m)))} placeholder="mac" className="!w-48 font-mono" maxLength={40} />
              <Input aria-label="Value" value={v} onChange={(e) => setMeta(meta.map((m, j) => (j === i ? [m[0], e.target.value] : m)))} placeholder="AA:BB:CC:DD:EE:FF" maxLength={200} />
              <Button type="button" variant="ghost" size="sm" aria-label="Remove" onClick={() => setMeta(meta.filter((_, j) => j !== i))}><Trash2 size={13} /></Button>
            </div>
          ))}
          <Button type="button" variant="secondary" size="sm" iconLeft={<Plus size={13} />} disabled={meta.length >= 30} onClick={() => setMeta([...meta, ["", ""]])}>Add entry</Button>
        </div>
      </Panel>

      <div className="flex justify-end gap-2">
        <Button type="button" variant="ghost" onClick={() => router.back()} disabled={busy}>Cancel</Button>
        <Button type="submit" variant="primary" disabled={busy} data-testid="device-save">{busy ? "Saving…" : editing ? "Save changes" : "Register device"}</Button>
      </div>
    </form>
  );
}
