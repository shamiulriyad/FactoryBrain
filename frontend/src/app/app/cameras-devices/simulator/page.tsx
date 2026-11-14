"use client";

// Device simulator: make a simulated device come online, send heartbeats,
// report a (sample) error or go offline. Messages go through the same
// ingestion pipeline real hardware uses, only for simulated devices, and
// are stored and shown as SIMULATED — never as physical connectivity.

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { AlertTriangle, FlaskConical, HeartPulse, Plus, Power, PowerOff, RefreshCw } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Pagination } from "@/components/access/shared";
import { useMyFactories } from "@/components/workers/shared";
import { SimulatedBadge, useQueryState } from "@/components/attendance/shared";
import { BASE, CameraDeviceTabs, DeviceEventTable, DeviceHealthBadge } from "@/components/cameras/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { apiJson, errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDateTime } from "@/lib/format";
import { P, type PagedResult } from "@/lib/access";
import { DEVICE_CATEGORY_LABEL, SIMULATED_DEVICE_NOTE, type Device, type DeviceEvent, type DeviceMessageResult, type SimulatorBatchResult } from "@/lib/cameras";

type Action = "online" | "heartbeat" | "error" | "offline";
type Last = { action: Action; ok: boolean; text: string; events: DeviceEvent[] };

export default function DeviceSimulatorPage() {
  return (
    <RequirePermission perms={[P.DevicesSimulate]} what="Device simulator">
      <Simulator />
    </RequirePermission>
  );
}

function Simulator() {
  const q = useQueryState(`${BASE}/simulator`);
  const { factories } = useMyFactories();
  const factoryId = q.get("factory") || (factories?.length === 1 ? factories[0].id : "");
  const canRegister = useCan(P.DevicesManage);
  const devices = useResource<Device[]>(`/api/simulator/devices${factoryId ? `?factoryId=${factoryId}` : ""}`);
  const [deviceId, setDeviceId] = useState(q.get("device"));
  const [page, setPage] = useState(1);
  const eventsPath = useMemo(() => {
    const p = new URLSearchParams({ page: String(page), pageSize: "20" });
    if (factoryId) p.set("factoryId", factoryId);
    if (deviceId) p.set("deviceId", deviceId);
    return `/api/simulator/devices/events?${p}`;
  }, [factoryId, deviceId, page]);
  const events = useResource<PagedResult<DeviceEvent>>(eventsPath);
  const [count, setCount] = useState("5");
  const [interval, setIntervalSec] = useState("");
  const [errorCode, setErrorCode] = useState("");
  const [errorText, setErrorText] = useState("");
  const [severity, setSeverity] = useState("Error");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [last, setLast] = useState<Last | null>(null);
  const device = devices.data?.find((d) => d.id === deviceId) ?? null;
  useEffect(() => {
    if (!deviceId && devices.data?.length) setDeviceId(devices.data[0].id);
  }, [devices.data, deviceId]);

  async function run(action: Action) {
    if (!device) return;
    setBusy(true);
    try {
      let recorded: DeviceEvent[] = [];
      let text = "";
      if (action === "heartbeat") {
        const r = await apiJson<SimulatorBatchResult>("/api/simulator/devices/heartbeat", {
          method: "POST", body: { deviceId: device.id, count: Number(count) || 1, intervalSeconds: interval ? Number(interval) : null }
        });
        recorded = r.events;
        text = `${r.accepted} simulated heartbeat${r.accepted === 1 ? "" : "s"} recorded. Health: ${r.device.healthLabel}.`;
      } else {
        const body = action === "error"
          ? { deviceId: device.id, code: errorCode || null, message: errorText || null, severity }
          : { deviceId: device.id, reason: reason || null };
        const r = await apiJson<DeviceMessageResult>(`/api/simulator/devices/${action}`, { method: "POST", body });
        recorded = r.events;
        text = `${r.outcome}. Health: ${r.device.healthLabel}.${r.events.length === 0 ? " No state change." : ""}`;
      }
      setLast({ action, ok: true, text, events: recorded });
    } catch (e) {
      setLast({ action, ok: false, text: errorMessage(e), events: [] });
    } finally {
      setBusy(false);
      void devices.reload();
      void events.reload();
    }
  }

  return (
    <div className="page" data-testid="device-simulator">
      <PageHeader title={<span className="inline-flex items-center gap-3">Device simulator <SimulatedBadge /></span>}
        description="Demonstrate device health without hardware. Only simulated devices can be driven; real devices accept messages only from hardware." />
      <CameraDeviceTabs className="mt-4" />
      <Callout className="mt-4" tone="warning" title="Everything here is simulated">{SIMULATED_DEVICE_NOTE}</Callout>

      <div className="mt-4 flex flex-wrap items-center gap-3">
        {(factories?.length ?? 0) > 1 && (
          <FilterSelect label="Factory" value={q.get("factory")} onChange={(v) => { q.set({ factory: v || null, device: null }); setDeviceId(""); }}>
            <option value="">All factories</option>
            {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        )}
        <Button size="sm" variant="secondary" className="ml-auto" aria-label="Refresh" onClick={() => { void devices.reload(); void events.reload(); }}>
          <RefreshCw size={13} className={cn(devices.refreshing && "animate-spin")} />
        </Button>
      </div>

      <div className="mt-4 grid gap-6 xl:grid-cols-[360px_minmax(0,1fr)]">
        <Panel flush title="Simulated devices" icon={<FlaskConical size={14} />}
          right={canRegister ? <Link href={`${BASE}/devices/new`} className="text-caption inline-flex items-center gap-1 hover:underline"><Plus size={12} />Register</Link> : undefined}>
          {devices.loading ? <div className="p-4"><SkeletonRows rows={4} rowHeight="h-10" /></div>
            : devices.error ? <ErrorState className="m-4" title="Couldn't load devices" body={errorMessage(devices.error)} retryLabel="Try again" onRetry={() => void devices.reload()} />
            : (devices.data?.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<FlaskConical size={18} />} title="No simulated devices" body="Register a device in Simulated mode (or a camera with a simulated health device) to use the simulator." />
            : (
              <ul role="listbox" aria-label="Simulated devices" className="divide-y divide-border-subtle" data-testid="sim-devices">
                {devices.data!.map((d) => (
                  <li key={d.id} role="option" aria-selected={d.id === deviceId}>
                    <button type="button" onClick={() => { setDeviceId(d.id); setPage(1); setLast(null); }}
                      className={cn("w-full text-left px-4 py-2.5 text-caption", d.id === deviceId ? "bg-[var(--accent-soft)]" : "hover:bg-surface-2")} data-testid={`sim-device-${d.code}`}>
                      <div className="flex items-center gap-2"><span className="font-mono text-body text-fg-primary">{d.code}</span><span className="ml-auto"><DeviceHealthBadge d={{ ...d, isSimulated: false }} /></span></div>
                      <div className="text-fg-tertiary">{DEVICE_CATEGORY_LABEL[d.category] ?? d.category} · {d.name}</div>
                    </button>
                  </li>
                ))}
              </ul>
            )}
        </Panel>

        <div className="space-y-6">
          {!device ? (
            <EmptyState icon={<FlaskConical size={18} />} title="Choose a simulated device" body="Pick a device on the left to send simulated messages." />
          ) : (
            <Panel title={<span className="inline-flex items-center gap-2"><span className="font-mono">{device.code}</span> {device.name}</span>}
              subtitle={`Last seen ${device.lastSeenAt ? formatDateTime(device.lastSeenAt) : "never"} · offline after ${device.offlineAfterSeconds} s without a message`}
              right={<Link href={`${BASE}/devices/${device.id}`} className="text-caption hover:underline">Device details</Link>} testId="sim-panel">
              <div className="flex flex-wrap items-center gap-3"><DeviceHealthBadge d={device} /><span className="text-caption text-fg-tertiary">Physically connected: No (simulated)</span></div>
              {device.status === "Disabled" && <Callout className="mt-3" tone="danger" title="Device disabled">A disabled device refuses every message, simulated or real.</Callout>}
              <div className="mt-4 grid gap-4 lg:grid-cols-2">
                <fieldset className="rounded-[var(--radius-control)] border border-border-subtle p-3 space-y-3">
                  <legend className="px-1 text-caption text-fg-tertiary">Connection</legend>
                  <Field label="Reason" hint="Optional; recorded on the event">
                    {(ids) => <Input id={ids.id} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Demo: power restored" maxLength={500} />}
                  </Field>
                  <div className="flex flex-wrap gap-2">
                    <Button size="sm" variant="primary" iconLeft={<Power size={13} />} disabled={busy} onClick={() => void run("online")} data-testid="sim-online">Come online</Button>
                    <Button size="sm" variant="secondary" iconLeft={<PowerOff size={13} />} disabled={busy} onClick={() => void run("offline")} data-testid="sim-offline">Go offline</Button>
                  </div>
                </fieldset>
                <fieldset className="rounded-[var(--radius-control)] border border-border-subtle p-3 space-y-3">
                  <legend className="px-1 text-caption text-fg-tertiary">Heartbeats</legend>
                  <div className="grid grid-cols-2 gap-3">
                    <Field label="How many">{(ids) => <Input id={ids.id} type="number" min={1} max={20} value={count} onChange={(e) => setCount(e.target.value)} data-testid="sim-count" />}</Field>
                    <Field label="Apart (s)" hint={`Default ${Math.min(device.heartbeatIntervalSeconds, 3600)}`}>{(ids) => <Input id={ids.id} type="number" min={1} max={3600} value={interval} onChange={(e) => setIntervalSec(e.target.value)} />}</Field>
                  </div>
                  <Button size="sm" variant="primary" iconLeft={<HeartPulse size={13} />} disabled={busy} onClick={() => void run("heartbeat")} data-testid="sim-heartbeat">Send heartbeats</Button>
                  <p className="field-hint">Spaced back from now, so the last one is current.</p>
                </fieldset>
                <fieldset className="rounded-[var(--radius-control)] border border-border-subtle p-3 space-y-3 lg:col-span-2">
                  <legend className="px-1 text-caption text-fg-tertiary">Error</legend>
                  <div className="grid gap-3 sm:grid-cols-3">
                    <Field label="Code" hint="Empty: a sample error">{(ids) => <Input id={ids.id} value={errorCode} onChange={(e) => setErrorCode(e.target.value)} placeholder="RFID_READ_TIMEOUT" className="font-mono" maxLength={64} />}</Field>
                    <Field label="Message">{(ids) => <Input id={ids.id} value={errorText} onChange={(e) => setErrorText(e.target.value)} maxLength={500} />}</Field>
                    <Field label="Severity">{(ids) => <Select id={ids.id} value={severity} onChange={(e) => setSeverity(e.target.value)}><option>Error</option><option>Warning</option><option>Info</option></Select>}</Field>
                  </div>
                  <Button size="sm" variant="danger" iconLeft={<AlertTriangle size={13} />} disabled={busy} onClick={() => void run("error")} data-testid="sim-error">Report error</Button>
                </fieldset>
              </div>
              {last && (
                <Callout className="mt-4" tone={last.ok ? "success" : "danger"} title={last.ok ? "Simulated message recorded" : "Refused"} testId="sim-result">
                  {last.text}
                  {last.events.length > 0 && <span className="block mt-1">Events: {last.events.map((e) => e.eventType).join(", ")}</span>}
                </Callout>
              )}
            </Panel>
          )}

          <Panel flush title="Simulated event history" subtitle={events.data ? `${events.data.total} event${events.data.total === 1 ? "" : "s"}${device ? ` for ${device.code}` : ""}` : undefined}>
            {events.loading ? <div className="p-4"><SkeletonRows rows={4} /></div>
              : events.error ? <ErrorState className="m-4" title="Couldn't load events" body={errorMessage(events.error)} retryLabel="Try again" onRetry={() => void events.reload()} />
              : (events.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<FlaskConical size={18} />} title="No simulated events yet" body="Connection changes and errors you simulate appear here." />
              : <><DeviceEventTable rows={events.data!.items} showDevice={!device} testId="sim-events" /><Pagination page={page} pageSize={20} total={events.data!.total} onPage={setPage} /></>}
          </Panel>
        </div>
      </div>
    </div>
  );
}
