"use client";

// Device details: identity, placement, health (last seen, last connection,
// last error), connection and event history, heartbeats, status changes.
// A simulated device is labelled everywhere and never shown as physically
// connected; real devices document the ingestion contract hardware uses.

import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { Activity, Cpu, FlaskConical, HeartPulse, Pencil, PlugZap, RefreshCw, Trash2 } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, Select, TextArea } from "@/components/ui/Field";
import { ConfirmDialog, Modal } from "@/components/ui/Modal";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { Pagination } from "@/components/access/shared";
import { Fact, Val } from "@/components/workers/shared";
import { tableCls } from "@/components/attendance/shared";
import { BASE, CameraDeviceTabs, DeviceEventTable, DeviceHealthBadge, EventSource, LinkedRef, ModeBadge } from "@/components/cameras/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { apiJson, errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDate, formatDateTime } from "@/lib/format";
import { P, type PagedResult } from "@/lib/access";
import { DEVICE_CATEGORY_LABEL, DEVICE_STATUSES, EVENT_TYPE_LABEL, SIMULATED_DEVICE_NOTE, seconds, type Device, type DeviceEvent, type DeviceHeartbeat } from "@/lib/cameras";

export default function DeviceDetailPage() {
  return (
    <RequirePermission perms={[P.DevicesView]} what="Device details">
      <Detail />
    </RequirePermission>
  );
}

function Detail() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const toast = useToast();
  const dev = useResource<Device>(`/api/devices/${id}`, { pollMs: 30_000 });
  const canManage = useCan(P.DevicesManage);
  const canLogs = useCan(P.DevicesLogsView, P.DevicesManage);
  const canSimulate = useCan(P.DevicesSimulate);
  const [dialog, setDialog] = useState<null | "status" | "delete">(null);
  const [eventsKey, setEventsKey] = useState(0);
  const d = dev.data;

  if (dev.loading) return <div className="page"><SkeletonRows rows={8} /></div>;
  if (dev.error || !d) return <div className="page"><ErrorState title="Couldn't load the device" body={errorMessage(dev.error)} retryLabel="Try again" onRetry={() => void dev.reload()} /></div>;

  async function remove() {
    try {
      await apiJson(`/api/devices/${d!.id}`, { method: "DELETE" });
      toast.push({ tone: "success", title: `Device ${d!.code} deleted` });
      router.push(`${BASE}/devices`);
    } catch (e) {
      toast.push({ tone: "error", title: "Not deleted", description: errorMessage(e) });
      setDialog(null);
    }
  }

  return (
    <div className="page" data-testid="device-detail">
      <PageHeader
        eyebrow={<Link href={`${BASE}/devices`} className="hover:underline">Device directory</Link>}
        title={<span className="flex flex-wrap items-center gap-3"><span className="font-mono">{d.code}</span><span className="text-fg-secondary font-normal">{d.name}</span><ModeBadge simulated={d.isSimulated} /></span>}
        description={`${DEVICE_CATEGORY_LABEL[d.category] ?? d.category} · ${d.factoryCode}${d.line ? ` · ${d.line.name}` : ""}`}
        actions={
          <div className="flex flex-wrap gap-2">
            {d.isSimulated && canSimulate && <Link href={`${BASE}/simulator?device=${d.id}`}><Button size="sm" variant="secondary" iconLeft={<FlaskConical size={13} />}>Open in simulator</Button></Link>}
            {canManage && <Link href={`${BASE}/devices/${d.id}/edit`}><Button size="sm" variant="secondary" iconLeft={<Pencil size={13} />}>Edit</Button></Link>}
            {canManage && <Button size="sm" variant="secondary" onClick={() => setDialog("status")} data-testid="device-status-change">Change status</Button>}
            {canManage && <Button size="sm" variant="ghost" iconLeft={<Trash2 size={13} />} onClick={() => setDialog("delete")} data-testid="device-delete">Delete</Button>}
            <Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => { void dev.reload(); setEventsKey((k) => k + 1); }}><RefreshCw size={13} className={cn(dev.refreshing && "animate-spin")} /></Button>
          </div>
        }
      />
      <CameraDeviceTabs className="mt-4" />
      {d.isSimulated && <Callout className="mt-4" tone="warning" title="Simulated device">{SIMULATED_DEVICE_NOTE}</Callout>}
      {d.status !== "Active" && <Callout className="mt-4" tone={d.status === "Disabled" ? "danger" : "warning"} title={`Device ${d.status.toLowerCase()}`}>{d.statusReason ?? "No reason recorded."}{d.status === "Disabled" ? " Its messages are refused." : ""}</Callout>}

      <div className="mt-6 grid gap-6 xl:grid-cols-3">
        <Panel className="xl:col-span-2" title="Health" icon={<HeartPulse size={14} />} testId="device-health">
          <dl className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            <Fact label="Health"><DeviceHealthBadge d={d} /></Fact>
            <Fact label="Physically connected" testId="device-physical">{d.physicallyConnected ? "Yes" : d.isSimulated ? "No (simulated device)" : "No"}</Fact>
            <Fact label="Heartbeat">{d.heartbeatOverdue ? <span className="text-[var(--risk-high)]">Overdue</span> : `every ${seconds(d.heartbeatIntervalSeconds)}`} · offline after {seconds(d.offlineAfterSeconds)}</Fact>
            <Fact label="Last seen"><Val>{d.lastSeenAt ? formatDateTime(d.lastSeenAt) : "Never"}</Val></Fact>
            <Fact label="Last heartbeat"><Val>{d.lastHeartbeatAt ? formatDateTime(d.lastHeartbeatAt) : null}</Val></Fact>
            <Fact label="Last successful connection"><Val>{d.lastConnectedAt ? formatDateTime(d.lastConnectedAt) : null}</Val></Fact>
            <Fact label="Last disconnection"><Val>{d.lastDisconnectedAt ? formatDateTime(d.lastDisconnectedAt) : null}</Val></Fact>
            <Fact label="Last error">
              {d.lastErrorAt ? <span><span className="font-mono">{d.lastErrorCode ?? "Error"}</span> {d.lastErrorMessage} <span className="text-fg-tertiary">({formatDateTime(d.lastErrorAt)})</span></span> : <span className="text-fg-tertiary">None</span>}
            </Fact>
          </dl>
        </Panel>

        <Panel title="Device" icon={<Cpu size={14} />}>
          <dl className="grid gap-3">
            <Fact label="Status">{d.status}</Fact>
            <Fact label="Workstation"><Val>{d.workstation && <LinkedRef code={d.workstation.code} name={d.workstation.name} />}</Val></Fact>
            <Fact label="Machine"><Val>{d.machine && <LinkedRef code={d.machine.code} name={d.machine.name} />}</Val></Fact>
            {d.camera && <Fact label="Camera"><LinkedRef href={`${BASE}/cameras/${d.camera.id}`} code={d.camera.code} name={d.camera.name} /></Fact>}
            {d.attendanceReader && <Fact label="Attendance reader"><LinkedRef code={d.attendanceReader.code} name={d.attendanceReader.name} /></Fact>}
            <Fact label="Hardware"><Val>{[d.manufacturer, d.model, d.serialNumber && `S/N ${d.serialNumber}`].filter(Boolean).join(" · ") || null}</Val></Fact>
            <Fact label="Firmware"><Val mono>{d.firmwareVersion}</Val></Fact>
            <Fact label="Installed"><Val>{d.installedOn ? formatDate(d.installedOn) : null}</Val></Fact>
            {Object.keys(d.metadata).length > 0 && (
              <Fact label="Metadata">
                <ul className="text-caption font-mono">{Object.entries(d.metadata).map(([k, v]) => <li key={k}><span className="text-fg-tertiary">{k}</span> {v}</li>)}</ul>
              </Fact>
            )}
          </dl>
        </Panel>
      </div>

      {!d.isSimulated && canManage && (
        <Panel className="mt-6" title="Connecting the hardware" icon={<PlugZap size={14} />} subtitle="The one ingestion contract real devices (or their gateway, or a future MQTT bridge) use.">
          <ul className="space-y-1.5 text-caption text-fg-secondary">
            <li><span className="font-mono text-fg-primary">POST /api/devices/{d.id}/heartbeat</span> — {"{ occurredAt?, messageId?, firmwareVersion?, metrics? }"}</li>
            <li><span className="font-mono text-fg-primary">POST /api/devices/{d.id}/events</span> — {"{ kind: Online | Offline | Error, code?, message?, severity?, occurredAt?, messageId? }"}</li>
            <li><span className="font-mono text-fg-primary">POST /api/devices/ingest</span> — the same, addressed by <span className="font-mono">factoryId</span> + <span className="font-mono">deviceCode</span> (MQTT topic <span className="font-mono">bunonbrain/&lt;factory&gt;/devices/{d.code}/&lt;kind&gt;</span>).</li>
            <li>Gateways sign in with an account that holds “Manage devices”. A repeated <span className="font-mono">messageId</span> is recorded once.</li>
          </ul>
        </Panel>
      )}

      {canLogs ? (
        <div className="mt-6 grid gap-6 xl:grid-cols-2">
          <EventsPanel deviceId={d.id} refreshKey={eventsKey} />
          <HeartbeatsPanel deviceId={d.id} refreshKey={eventsKey} />
        </div>
      ) : (
        <p className="mt-6 text-caption text-fg-tertiary">Event and heartbeat history needs the “View device logs” permission.</p>
      )}

      <StatusDialog open={dialog === "status"} device={d} onClose={() => setDialog(null)}
        onDone={(n) => { dev.setData(n); setDialog(null); setEventsKey((k) => k + 1); toast.push({ tone: "success", title: "Status changed" }); }} />
      <ConfirmDialog open={dialog === "delete"} title={`Delete device ${d.code}?`} tone="danger" confirmLabel="Delete device" onCancel={() => setDialog(null)} onConfirm={() => void remove()}
        description="Only a device that never sent a heartbeat or event can be deleted. A device with history is kept; disable it instead." testId="device-delete-dialog" />
    </div>
  );
}

function EventsPanel({ deviceId, refreshKey }: { deviceId: string; refreshKey: number }) {
  const [type, setType] = useState("");
  const [page, setPage] = useState(1);
  useEffect(() => setPage(1), [type]);
  const events = useResource<PagedResult<DeviceEvent>>(`/api/devices/${deviceId}/events?page=${page}&pageSize=15${type ? `&type=${type}` : ""}&k=${refreshKey}`);
  return (
    <Panel flush title="Connection & event history" icon={<Activity size={14} />}
      right={<FilterSelect label="Type" value={type} onChange={setType}><option value="">All</option>{Object.entries(EVENT_TYPE_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</FilterSelect>}>
      {events.loading ? <div className="p-4"><SkeletonRows rows={4} /></div>
        : events.error ? <ErrorState className="m-4" title="Couldn't load events" body={errorMessage(events.error)} retryLabel="Try again" onRetry={() => void events.reload()} />
        : (events.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<Activity size={18} />} title="No events" body="Connections, missed heartbeats, errors and status changes appear here." />
        : <><DeviceEventTable rows={events.data!.items} showDevice={false} testId="device-events" /><Pagination page={page} pageSize={15} total={events.data!.total} onPage={setPage} /></>}
    </Panel>
  );
}

function HeartbeatsPanel({ deviceId, refreshKey }: { deviceId: string; refreshKey: number }) {
  const [page, setPage] = useState(1);
  const beats = useResource<PagedResult<DeviceHeartbeat>>(`/api/devices/${deviceId}/heartbeats?page=${page}&pageSize=15&k=${refreshKey}`);
  const { th, td } = tableCls;
  return (
    <Panel flush title="Heartbeats" icon={<HeartPulse size={14} />} subtitle={beats.data ? `${beats.data.total} recorded` : undefined}>
      {beats.loading ? <div className="p-4"><SkeletonRows rows={4} /></div>
        : beats.error ? <ErrorState className="m-4" title="Couldn't load heartbeats" body={errorMessage(beats.error)} retryLabel="Try again" onRetry={() => void beats.reload()} />
        : (beats.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<HeartPulse size={18} />} title="No heartbeats yet" body="This device has never sent a heartbeat." />
        : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full min-w-[520px]" data-testid="device-heartbeats">
                <thead className="border-b border-border-subtle"><tr><th className={th}>Sent</th><th className={th}>Received</th><th className={th}>Source</th><th className={th}>Metrics</th></tr></thead>
                <tbody className="divide-y divide-border-subtle">
                  {beats.data!.items.map((h) => (
                    <tr key={h.id}>
                      <td className={cn(td, "whitespace-nowrap")}>{formatDateTime(h.occurredAt)}</td>
                      <td className={cn(td, "whitespace-nowrap text-fg-tertiary")}>{formatDateTime(h.receivedAt)}</td>
                      <td className={td}><EventSource source={h.source} simulated={h.isSimulated} /></td>
                      <td className={cn(td, "font-mono text-fg-secondary")}>{h.metrics ? Object.entries(h.metrics).map(([k, v]) => `${k}=${v}`).join(" ") : "—"}{h.firmwareVersion ? ` fw ${h.firmwareVersion}` : ""}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={page} pageSize={15} total={beats.data!.total} onPage={setPage} />
          </>
        )}
    </Panel>
  );
}

function StatusDialog({ open, device: d, onClose, onDone }: { open: boolean; device: Device; onClose: () => void; onDone: (d: Device) => void }) {
  const [status, setStatus] = useState("Maintenance");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { if (open) { setStatus(d.status === "Active" ? "Maintenance" : "Active"); setReason(""); setError(null); } }, [open, d.status]);
  async function save() {
    setBusy(true);
    try {
      onDone(await apiJson<Device>(`/api/devices/${d.id}/status`, { method: "PATCH", body: { status, reason: reason || null } }));
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }
  const needsReason = status !== "Active";
  return (
    <Modal open={open} onClose={onClose} locked={busy} title={`Change status of ${d.code}`} testId="device-status-dialog"
      description="Maintenance keeps recording messages; Disabled refuses them. Every change is an event in the device's history."
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button><Button variant={status === "Disabled" ? "danger" : "primary"} onClick={() => void save()} disabled={busy || status === d.status || (needsReason && !reason.trim())} data-testid="device-status-save">{busy ? "Saving…" : "Change status"}</Button></>}>
      <div className="space-y-4">
        {error && <Callout tone="danger" title="Not changed">{error}</Callout>}
        <Field label="New status">
          {(ids) => <Select id={ids.id} value={status} onChange={(e) => setStatus(e.target.value)}>{DEVICE_STATUSES.map((s) => <option key={s} value={s} disabled={s === d.status}>{s}{s === d.status ? " (current)" : ""}</option>)}</Select>}
        </Field>
        <Field label="Reason" hint={needsReason ? "Required" : "Optional"}>
          {(ids) => <TextArea id={ids.id} rows={2} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} />}
        </Field>
      </div>
    </Modal>
  );
}
