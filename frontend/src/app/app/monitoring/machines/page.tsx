"use client";

// Machine monitoring: machines and their derived states, production counts
// by line / shift / machine, device health, state and counter history,
// machine alerts and the IoT simulator. Live over SignalR. Simulated and real
// data are shown separately (a switch), never added together.

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { Activity, Bell, Cpu, Factory as FactoryIcon, History, Play, Radio, Square, Wand2, Wrench } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useFactoryOrg, useMyFactories } from "@/components/workers/shared";
import { useQueryState } from "@/components/attendance/shared";
import { AlertActionButtons, AlertActionDialog, AlertRow, ConnectionBadge, LiveIndicator, MonitoringTabs, type AlertAction } from "@/components/monitoring/shared";
import { BucketChart, CountBars, DeviceConfigDialog, MachineCard, MachineStateBadge, StateBar } from "@/components/monitoring/machines";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { useCan } from "@/lib/auth";
import { P, type PagedResult } from "@/lib/access";
import { apiJson, errorMessage } from "@/lib/api";
import { MON, ago, dhaka, dhakaInputToIso, dhakaToday, qs, type MonitoringAlert } from "@/lib/monitoring";
import {
  MACHINE_DISCLAIMER, MACHINE_STATES, SIM_SCENARIOS, hours, stateLabel,
  type CounterEvent, type IotDevice, type MachineOverview, type ProductionReport, type SimulationResult, type SimulationRun, type StateTransition
} from "@/lib/iot";

const BASE = `${MON}/machines`;
const TABS = [
  { id: "machines", label: "Machines" },
  { id: "production", label: "Production" },
  { id: "devices", label: "Device health" },
  { id: "history", label: "State history" },
  { id: "counts", label: "Counter history" },
  { id: "alerts", label: "Machine alerts" },
  { id: "simulator", label: "IoT simulator" }
] as const;

type Filters = { factory: string; department: string; floor: string; line: string; workstation: string; machine: string; device: string; shift: string; from: string; to: string; source: string };

export default function MachinesPage() {
  return (
    <RequirePermission perms={[P.MonitoringView, P.DevicesView, P.ProductionView]} what="Machine monitoring">
      <MachineRoom />
    </RequirePermission>
  );
}

function MachineRoom() {
  const q = useQueryState(BASE);
  const { factories } = useMyFactories();
  const tab = q.get("tab") || "machines";
  const canSimulate = useCan(P.DevicesSimulate);
  const canProduction = useCan(P.ProductionView, P.MonitoringView);
  const factory = q.get("factory") || (factories && factories.length === 1 ? factories[0].id : "");
  const f: Filters = {
    factory, department: q.get("department"), floor: q.get("floor"), line: q.get("line"), workstation: q.get("workstation"), machine: q.get("machine"),
    device: q.get("device"), shift: q.get("shift"), from: q.get("from"), to: q.get("to"), source: q.get("source") || "REAL"
  };
  const scope = { factoryId: f.factory, departmentId: f.department, floorId: f.floor, lineId: f.line, workstationId: f.workstation, deviceId: f.device, source: f.source };
  const overview = useResource<MachineOverview>(`/api/iot/overview${qs({ ...scope, machineId: f.machine })}`, { pollMs: 30_000 });
  const o = overview.data;
  const [reloadKey, setReloadKey] = useState(0);
  const pending = useRef<ReturnType<typeof setTimeout> | null>(null);
  const reloadSoon = useCallback(() => {
    if (pending.current) return;
    pending.current = setTimeout(() => { pending.current = null; void overview.reload(); setReloadKey((k) => k + 1); }, 1500);
  }, [overview]);
  useEffect(() => () => { if (pending.current) clearTimeout(pending.current); }, []);
  const hub = useMonitoringHub(f.factory || null, {
    onMachine: () => reloadSoon(),
    onProduction: () => reloadSoon(),
    onAlert: (_, a) => { if (a.machine) reloadSoon(); },
    onDeviceStatus: () => reloadSoon(),
    onResync: () => void overview.reload()
  });
  const simulated = f.source === "SIMULATED";

  return (
    <div className="page" data-testid="machines-page">
      <PageHeader eyebrow="Central monitoring" title="Machine monitoring"
        description="Machine states from sensor telemetry, production counts from piece counters, device health and machine alerts."
        meta={<LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} />}
        actions={
          <div className="flex flex-wrap items-end gap-2">
            {factories && factories.length > 1 && (
              <FilterSelect label="Factory" value={f.factory} onChange={(v) => q.set({ factory: v || null, department: null, floor: null, line: null, workstation: null, machine: null, device: null, shift: null })} testId="machines-factory">
                <option value="">All my factories</option>
                {factories.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
              </FilterSelect>
            )}
            <FilterSelect label="Data" value={f.source} onChange={(v) => q.set({ source: v === "REAL" ? null : v })} testId="machines-source">
              <option value="REAL">Real machines</option>
              <option value="SIMULATED">Simulated data</option>
            </FilterSelect>
          </div>
        } />
      <MonitoringTabs className="mb-4" />
      {simulated
        ? <Callout tone="demo" className="mb-4" testId="machines-simulated">SIMULATED data from the IoT simulator — not real machines or real production. It is never added to real figures.</Callout>
        : <Callout tone="info" className="mb-4">{MACHINE_DISCLAIMER}</Callout>}
      <ScopeFilters f={f} set={(v) => q.set(v)} machines={o?.machines.map((m) => ({ id: m.id, code: m.code })) ?? []} showTime={tab === "production" || tab === "history" || tab === "counts"} />

      {o && (
        <section aria-label="Machine status" className="grid gap-3 grid-cols-2 md:grid-cols-4 xl:grid-cols-8 mb-4" data-testid="machine-counts">
          <Count label="Machines" value={o.counts.total} hint={`${o.counts.unmonitored} without a sensor`} />
          <Count label="Running" value={o.counts.running} tone="RUNNING" />
          <Count label="Idle" value={o.counts.idle} tone="IDLE" />
          <Count label="Stopped" value={o.counts.stopped} tone="STOPPED" bad={o.counts.stopped > 0} />
          <Count label="Error" value={o.counts.error} tone="ERROR" bad={o.counts.error > 0} />
          <Count label="Offline" value={o.counts.offline} hint={`${o.counts.unknown} unknown · ${o.counts.maintenance} in maintenance`} bad={o.counts.offline > 0} />
          <Count label="Pieces today" value={o.canSeeProduction ? o.piecesToday : "—"} hint={o.canSeeProduction ? "From production counters" : "Needs production access"} />
          <Count label="Downtime today" value={o.canSeeProduction ? hours(o.downtimeSecondsToday) : "—"} hint="Stopped + error, all machines" />
        </section>
      )}

      <div role="tablist" aria-label="Machine sections" className="mb-4 flex flex-wrap gap-1 border-b border-border-subtle">
        {TABS.filter((t) => (t.id !== "simulator" || canSimulate) && ((t.id !== "production" && t.id !== "counts") || canProduction)).map((t) => (
          <button key={t.id} type="button" role="tab" aria-selected={tab === t.id} onClick={() => q.set({ tab: t.id === "machines" ? null : t.id })}
            className={`px-3 h-9 text-caption border-b-2 -mb-px ${tab === t.id ? "border-[var(--accent)] text-fg-primary font-medium" : "border-transparent text-fg-secondary hover:text-fg-primary"}`}
            data-testid={`machines-tab-${t.id}`}>
            {t.label}{t.id === "alerts" && o?.alerts.length ? ` (${o.alerts.length})` : ""}
          </button>
        ))}
      </div>

      {overview.error && !o ? <ErrorState retryLabel="Try again" title="Could not load machines" body={overview.error.message} onRetry={() => void overview.reload()} />
        : !o ? <SkeletonRows rows={6} />
        : tab === "production" ? <Production f={f} reloadKey={reloadKey} />
        : tab === "devices" ? <Devices o={o} f={f} reloadKey={reloadKey} onChanged={() => void overview.reload()} />
        : tab === "history" ? <StateHistory f={f} reloadKey={reloadKey} />
        : tab === "counts" ? <CounterHistory f={f} reloadKey={reloadKey} />
        : tab === "alerts" ? <MachineAlerts alerts={o.alerts} onChanged={() => void overview.reload()} />
        : tab === "simulator" && canSimulate ? <Simulator o={o} f={f} onDone={() => void overview.reload()} />
        : <Machines o={o} />}
    </div>
  );
}

function Count({ label, value, hint, tone, bad }: { label: string; value: number | string; hint?: string; tone?: string; bad?: boolean }) {
  return (
    <div className={`rounded-[var(--radius-card,12px)] border bg-surface-1 px-4 py-3 ${bad ? "border-[var(--risk-medium)]" : "border-border-subtle"}`}>
      <p className="text-caption text-fg-secondary">{label}</p>
      <p className="text-2xl font-semibold tabular-nums">{value}</p>
      {hint ? <p className="text-caption text-fg-tertiary truncate" title={hint}>{hint}</p> : tone ? <p className="text-caption text-fg-tertiary">{stateLabel(tone)}</p> : null}
    </div>
  );
}

function ScopeFilters({ f, set, machines, showTime }: { f: Filters; set: (v: Record<string, string | null>) => void; machines: { id: string; code: string }[]; showTime: boolean }) {
  const org = useFactoryOrg(f.factory || null);
  const ws = useResource<PagedResult<{ id: string; code: string; lineId: string }>>(f.factory ? `/api/workstations?factoryId=${f.factory}&pageSize=200` : null);
  const shifts = useResource<{ id: string; code: string; name: string }[]>(f.factory ? `/api/shifts?factoryId=${f.factory}&includeInactive=false` : null);
  const devices = useResource<IotDevice[]>(`/api/iot/devices${qs({ factoryId: f.factory, source: f.source })}`);
  const workstations = (ws.data?.items ?? []).filter((w) => !f.line || w.lineId === f.line);
  return (
    <div className="mb-4 flex flex-wrap items-end gap-2" data-testid="machines-filters">
      {f.factory && (
        <>
          <FilterSelect label="Department" value={f.department} onChange={(v) => set({ department: v || null })}>
            <option value="">All</option>{(org.departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </FilterSelect>
          <FilterSelect label="Floor" value={f.floor} onChange={(v) => set({ floor: v || null })}>
            <option value="">All</option>{(org.floors.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </FilterSelect>
          <FilterSelect label="Line" value={f.line} onChange={(v) => set({ line: v || null, workstation: null })} testId="machines-line">
            <option value="">All</option>{(org.lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
          </FilterSelect>
          <FilterSelect label="Workstation" value={f.workstation} onChange={(v) => set({ workstation: v || null })}>
            <option value="">All</option>{workstations.map((w) => <option key={w.id} value={w.id}>{w.code}</option>)}
          </FilterSelect>
        </>
      )}
      <FilterSelect label="Machine" value={f.machine} onChange={(v) => set({ machine: v || null })} testId="machines-machine">
        <option value="">All</option>{machines.map((m) => <option key={m.id} value={m.id}>{m.code}</option>)}
      </FilterSelect>
      <FilterSelect label="Device" value={f.device} onChange={(v) => set({ device: v || null })}>
        <option value="">All</option>{(devices.data ?? []).map((d) => <option key={d.deviceId} value={d.deviceId}>{d.code}</option>)}
      </FilterSelect>
      {showTime && (
        <>
          {shifts.data && shifts.data.length > 0 && (
            <FilterSelect label="Shift" value={f.shift} onChange={(v) => set({ shift: v || null })}>
              <option value="">All</option>{shifts.data.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </FilterSelect>
          )}
          <label className="text-caption text-fg-secondary flex flex-col gap-1">From
            <Input type="date" value={f.from} max={dhakaToday()} onChange={(e) => set({ from: e.target.value || null })} className="h-9" data-testid="machines-from" />
          </label>
          <label className="text-caption text-fg-secondary flex flex-col gap-1">To
            <Input type="date" value={f.to} max={dhakaToday()} onChange={(e) => set({ to: e.target.value || null })} className="h-9" />
          </label>
        </>
      )}
    </div>
  );
}

/** The API query for a filter set, with the date range as Dhaka days. */
function apiQuery(f: Filters, extra: Record<string, string | number | null> = {}) {
  return qs({
    factoryId: f.factory, departmentId: f.department, floorId: f.floor, lineId: f.line, workstationId: f.workstation, machineId: f.machine, deviceId: f.device,
    shiftId: f.shift, source: f.source, from: f.from ? dhakaInputToIso(`${f.from}T00:00`) : null, to: f.to ? dhakaInputToIso(`${f.to}T23:59`) : null, ...extra
  });
}

// ─── Machines ──────────────────────────────────────────────────────────────

function Machines({ o }: { o: MachineOverview }) {
  const [state, setState] = useState("");
  const rows = o.machines.filter((m) => !state || (m.monitored && m.state === state));
  return (
    <div className="grid gap-4 xl:grid-cols-3">
      <Panel className="xl:col-span-2" title="Machines" subtitle={`Derived from sensor telemetry and device health · ${dhaka(o.asOf)} (Dhaka)`} icon={<Cpu size={15} />}
        right={<FilterSelect label="State" value={state} onChange={setState}><option value="">All</option>{MACHINE_STATES.map((s) => <option key={s} value={s}>{stateLabel(s)}</option>)}</FilterSelect>}
        testId="machines-grid">
        {rows.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No machine matches. Machines are registered under Workstations; bind a machine sensor to a machine in Cameras &amp; devices.</p> : (
          <div className="p-4 grid gap-2 sm:grid-cols-2 2xl:grid-cols-3">{rows.map((m) => <MachineCard key={m.id} m={m} />)}</div>
        )}
      </Panel>
      <div className="space-y-4">
        {o.canSeeProduction && (
          <Panel title="Production today" subtitle="By line and shift" icon={<FactoryIcon size={15} />} testId="machines-production-today">
            <div className="p-4 space-y-4">
              <CountBars rows={o.productionByLine} />
              {o.productionByShift.length > 0 && <div><p className="text-caption text-fg-tertiary mb-1">By shift</p><CountBars rows={o.productionByShift} /></div>}
            </div>
          </Panel>
        )}
        <Panel title="Recent device events" icon={<Radio size={15} />} testId="machines-device-events">
          {o.recentDeviceEvents.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">None.</p> : (
            <ul className="p-4 space-y-1 text-caption">{o.recentDeviceEvents.map((e) => (
              <li key={e.id} className="truncate"><span className="tabular-nums text-fg-tertiary">{ago(e.occurredAt)}</span> · <span className="font-mono">{e.deviceCode}</span> · {e.eventType}{e.message ? ` — ${e.message}` : ""}{e.isSimulated ? " · simulated" : ""}</li>
            ))}</ul>
          )}
        </Panel>
      </div>
    </div>
  );
}

// ─── Production ────────────────────────────────────────────────────────────

function Production({ f, reloadKey }: { f: Filters; reloadKey: number }) {
  const r = useResource<ProductionReport>(`/api/iot/production${apiQuery(f)}`);
  const reload = r.reload;
  useEffect(() => { if (reloadKey) void reload(); }, [reloadKey, reload]);
  const canCorrect = useCan(P.ProductionManage);
  const [correcting, setCorrecting] = useState(false);
  const d = r.data;
  if (r.error && !d) return <ErrorState title="Could not load production" body={r.error.message} retryLabel="Try again" onRetry={() => void reload()} />;
  if (!d) return <SkeletonRows rows={6} />;
  return (
    <div className="space-y-4" data-testid="machines-production">
      <section className="grid gap-3 grid-cols-2 lg:grid-cols-5">
        <Count label="Total pieces" value={d.totalPieces} hint={`${d.countedPieces} counted · ${d.correctionPieces >= 0 ? "+" : ""}${d.correctionPieces} corrections`} />
        <Count label="Pieces per hour" value={d.piecesPerHour ?? "—"} hint="Over the selected period" />
        <Count label="Machine running" value={hours(d.runningSeconds)} hint="Machine time, not worker time" />
        <Count label="Machine idle" value={hours(d.idleSeconds)} />
        <Count label="Machine downtime" value={hours(d.downtimeSeconds)} hint="Stopped + error" bad={d.downtimeSeconds > 0} />
      </section>
      <p className="text-caption text-fg-tertiary">{dhaka(d.from)} – {dhaka(d.to)} ({d.timeZone}) · {d.note}</p>
      <div className="grid gap-4 lg:grid-cols-3">
        <Panel title="By line" testId="production-by-line"><div className="p-4"><CountBars rows={d.byLine} /></div></Panel>
        <Panel title="By shift" testId="production-by-shift"><div className="p-4"><CountBars rows={d.byShift} /></div></Panel>
        <Panel title="By workstation"><div className="p-4"><CountBars rows={d.byWorkstation} /></div></Panel>
      </div>
      <div className="grid gap-4 lg:grid-cols-2">
        <Panel title={d.hourly.length > 0 ? "Hourly" : "Daily"} subtitle={d.hourly.length > 0 ? "Pieces per hour (Dhaka time)" : "Pieces per production day"} icon={<Activity size={15} />}>
          <div className="p-4"><BucketChart buckets={d.hourly.length > 0 ? d.hourly : d.daily} testId="production-chart" /></div>
        </Panel>
        <Panel title="Machines" subtitle="Pieces, running time and downtime per machine" right={canCorrect ? <Button size="sm" onClick={() => setCorrecting(true)}>Correct a count</Button> : undefined}>
          {d.machines.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No machines.</p> : (
            <div className="overflow-x-auto">
              <table className="w-full text-caption">
                <thead><tr className="text-left text-fg-tertiary"><th className="px-4 py-2">Machine</th><th>Pieces</th><th>Per running hour</th><th className="w-[40%] pr-4">Time</th></tr></thead>
                <tbody>{d.machines.map((m) => (
                  <tr key={m.machineId} className="border-t border-border-subtle align-top">
                    <td className="px-4 py-2"><Link className="text-[var(--accent)]" href={`${MON}/machines/${m.machineId}`}>{m.code}</Link><div className="text-fg-tertiary">{m.workstation?.code}</div></td>
                    <td className="py-2 tabular-nums">{m.pieces}</td>
                    <td className="py-2 tabular-nums">{m.piecesPerRunningHour ?? "—"}</td>
                    <td className="py-2 pr-4"><StateBar m={m} /></td>
                  </tr>
                ))}</tbody>
              </table>
            </div>
          )}
        </Panel>
      </div>
      {correcting && <CorrectionDialog f={f} machines={d.machines} onClose={() => setCorrecting(false)} onDone={() => { setCorrecting(false); void reload(); }} />}
    </div>
  );
}

function CorrectionDialog({ f, machines, onClose, onDone }: { f: Filters; machines: ProductionReport["machines"]; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [machine, setMachine] = useState(f.machine || machines[0]?.machineId || "");
  const [date, setDate] = useState(dhakaToday());
  const [delta, setDelta] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const factoryId = f.factory;
  async function save() {
    setBusy(true);
    setError(null);
    try {
      await apiJson("/api/iot/production/corrections", {
        method: "POST", body: { factoryId, machineId: machine || null, productionDate: date, shiftId: f.shift || null, piecesDelta: Number(delta), reason, sourceType: f.source }
      });
      toast.push({ tone: "success", title: "Correction recorded" });
      onDone();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-black/40 p-4" role="dialog" aria-modal="true" aria-label="Correct a production count">
      <div className="w-full max-w-md rounded-[var(--radius-card,12px)] bg-surface-1 p-4 space-y-3 shadow-xl" data-testid="correction-dialog">
        <p className="font-medium">Correct a production count</p>
        <p className="text-caption text-fg-tertiary">Counts are never edited: a correction (± pieces) is added beside them, with your reason, and audited.</p>
        {!factoryId && <Callout tone="warning">Choose a factory first.</Callout>}
        <Field label="Machine">{(ids) => <Select id={ids.id} value={machine} onChange={(e) => setMachine(e.target.value)}>{machines.map((m) => <option key={m.machineId} value={m.machineId}>{m.code}</option>)}</Select>}</Field>
        <Field label="Production date">{(ids) => <Input id={ids.id} type="date" value={date} onChange={(e) => setDate(e.target.value)} />}</Field>
        <Field label="Pieces (+ or −)">{(ids) => <Input id={ids.id} inputMode="numeric" value={delta} onChange={(e) => setDelta(e.target.value)} placeholder="e.g. 12 or -3" />}</Field>
        <Field label="Reason">{(ids) => <Input id={ids.id} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="What was counted wrong" />}</Field>
        {error && <Callout tone="danger">{error}</Callout>}
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void save()} disabled={busy || !factoryId || !delta || reason.trim().length < 5}>Record</Button>
        </div>
      </div>
    </div>
  );
}

// ─── Devices ───────────────────────────────────────────────────────────────

function Devices({ o, f, reloadKey, onChanged }: { o: MachineOverview; f: Filters; reloadKey: number; onChanged: () => void }) {
  const devices = useResource<IotDevice[]>(`/api/iot/devices${qs({ factoryId: f.factory, lineId: f.line, workstationId: f.workstation, machineId: f.machine, source: f.source })}`, { pollMs: 30_000 });
  const reload = devices.reload;
  useEffect(() => { if (reloadKey) void reload(); }, [reloadKey, reload]);
  const canManage = useCan(P.DevicesManage);
  const [editing, setEditing] = useState<IotDevice | null>(null);
  const b = o.bridge;
  return (
    <div className="grid gap-4 xl:grid-cols-3">
      <Panel className="xl:col-span-2" title="IoT devices" subtitle="Machine sensors, piece counters, ESP32s and gateways · heartbeat-based health" icon={<Cpu size={15} />} testId="iot-devices">
        {devices.error && !devices.data ? <p className="p-4 text-caption text-fg-tertiary">{devices.error.message}</p> : !devices.data ? <SkeletonRows rows={4} /> : devices.data.length === 0
          ? <p className="p-4 text-caption text-fg-tertiary">No devices. Register them in Cameras &amp; devices, place them on a machine, then set their IoT connection here.</p> : (
          <div className="overflow-x-auto">
            <table className="w-full text-caption">
              <thead><tr className="text-left text-fg-tertiary"><th className="px-4 py-2">Device</th><th>Type</th><th>Protocol</th><th>Machine</th><th>Health</th><th>Last heartbeat</th><th>Msgs / h</th><th /></tr></thead>
              <tbody>{devices.data.map((d) => (
                <tr key={d.deviceId} className="border-t border-border-subtle">
                  <td className="px-4 py-2"><span className="font-mono">{d.code}</span>{d.isSimulated && <Badge tone="accent" className="ml-1">Simulated</Badge>}<div className="text-fg-tertiary truncate max-w-[14rem]">{d.name}</div></td>
                  <td>{d.deviceTypeLabel}{!d.configured && <div className="text-fg-tertiary">default</div>}</td>
                  <td>{d.protocol}{d.connectionVia !== "DIRECT" ? ` via ${d.connectionVia.toLowerCase()}` : ""}{d.hasKey ? " · key" : ""}</td>
                  <td>{d.machine?.code ?? "—"}<div className="text-fg-tertiary">{d.workstation?.code ?? ""}</div></td>
                  <td><ConnectionBadge status={d.health} label={d.healthLabel} simulated={d.isSimulated} /></td>
                  <td className="tabular-nums">{ago(d.lastHeartbeatAt)}{d.firmwareVersion ? <div className="text-fg-tertiary">fw {d.firmwareVersion}</div> : null}</td>
                  <td className="tabular-nums">{d.messagesLastHour}</td>
                  <td className="pr-4">{canManage && <Button size="sm" variant="ghost" onClick={() => setEditing(d)}>Configure</Button>}</td>
                </tr>
              ))}</tbody>
            </table>
          </div>
        )}
      </Panel>
      <Panel title="MQTT bridge" subtitle="The backend's broker connection" icon={<Radio size={15} />} testId="iot-bridge">
        <div className="p-4 space-y-2 text-caption">
          <p className="flex items-center gap-2">{!b.enabled ? <Badge>Off</Badge> : b.connected ? <Badge tone="success" dot>Connected</Badge> : <Badge tone="danger" dot>Disconnected</Badge>}
            {b.broker && <span className="font-mono">{b.broker}{b.tls ? " · TLS" : ""}</span>}</p>
          <p className="text-fg-tertiary">{b.note}</p>
          {b.enabled && (
            <dl className="grid grid-cols-2 gap-1 tabular-nums">
              <dt className="text-fg-tertiary">Received</dt><dd>{b.received}</dd>
              <dt className="text-fg-tertiary">Accepted</dt><dd>{b.accepted}</dd>
              <dt className="text-fg-tertiary">Duplicates</dt><dd>{b.duplicates}</dd>
              <dt className="text-fg-tertiary">Rejected</dt><dd>{b.rejected}</dd>
              <dt className="text-fg-tertiary">Reconnects</dt><dd>{b.reconnects}</dd>
              <dt className="text-fg-tertiary">Last message</dt><dd>{ago(b.lastMessageAt)}</dd>
            </dl>
          )}
          {b.lastError && <Callout tone="warning">{b.lastError} · {ago(b.lastErrorAt)}</Callout>}
          <p className="text-fg-tertiary mb-0.5">Subscriptions</p>
          <ul className="font-mono text-[11px] space-y-0.5">{b.subscriptions.map((s) => <li key={s}>{s}</li>)}</ul>
        </div>
      </Panel>
      <DeviceConfigDialog device={editing} open={editing !== null} onClose={() => setEditing(null)} onSaved={() => { void reload(); onChanged(); }} />
    </div>
  );
}

// ─── History ───────────────────────────────────────────────────────────────

function StateHistory({ f, reloadKey }: { f: Filters; reloadKey: number }) {
  const [page, setPage] = useState(1);
  const r = useResource<PagedResult<StateTransition>>(`/api/iot/state-history${apiQuery(f, { page, pageSize: 50 })}`);
  const reload = r.reload;
  useEffect(() => { if (reloadKey) void reload(); }, [reloadKey, reload]);
  return (
    <Panel title="Machine state history" subtitle="Every derived state change, with what triggered it and how long it lasted" icon={<History size={15} />} testId="machines-history">
      {r.error && !r.data ? <p className="p-4 text-caption text-fg-tertiary">{r.error.message}</p> : !r.data ? <SkeletonRows rows={5} /> : r.data.items.length === 0
        ? <p className="p-4 text-caption text-fg-tertiary">No state changes in this range.</p> : (
        <>
          <div className="overflow-x-auto">
            <table className="w-full text-caption">
              <thead><tr className="text-left text-fg-tertiary"><th className="px-4 py-2">When (Dhaka)</th><th>Machine</th><th>Change</th><th>Lasted</th><th>Why</th><th>Trigger</th></tr></thead>
              <tbody>{r.data.items.map((t) => (
                <tr key={t.id} className="border-t border-border-subtle align-top">
                  <td className="px-4 py-2 tabular-nums whitespace-nowrap">{dhaka(t.occurredAt)}</td>
                  <td><Link className="text-[var(--accent)]" href={`${MON}/machines/${t.machineId}`}>{t.machineCode}</Link></td>
                  <td className="whitespace-nowrap">{t.fromState ? `${stateLabel(t.fromState)} → ` : ""}<MachineStateBadge state={t.toState} simulated={t.sourceType === "SIMULATED"} /></td>
                  <td className="tabular-nums">{hours(t.durationSeconds)}</td>
                  <td className="max-w-[24rem]">{t.reason}{t.deviceCode ? <span className="text-fg-tertiary"> · {t.deviceCode}</span> : null}</td>
                  <td className="text-fg-tertiary">{t.trigger.toLowerCase().replace(/_/g, " ")}</td>
                </tr>
              ))}</tbody>
            </table>
          </div>
          <Pager page={page} size={r.data.pageSize} total={r.data.total} onPage={setPage} />
        </>
      )}
    </Panel>
  );
}

function CounterHistory({ f, reloadKey }: { f: Filters; reloadKey: number }) {
  const [page, setPage] = useState(1);
  const r = useResource<PagedResult<CounterEvent>>(`/api/iot/production/counts${apiQuery(f, { page, pageSize: 50 })}`);
  const reload = r.reload;
  useEffect(() => { if (reloadKey) void reload(); }, [reloadKey, reload]);
  return (
    <Panel title="Production counter history" subtitle="Each counter message and what it was counted as (duplicates are stored once)" icon={<History size={15} />} testId="machines-counts">
      {r.error && !r.data ? <p className="p-4 text-caption text-fg-tertiary">{r.error.message}</p> : !r.data ? <SkeletonRows rows={5} /> : r.data.items.length === 0
        ? <p className="p-4 text-caption text-fg-tertiary">No counter messages in this range.</p> : (
        <>
          <div className="overflow-x-auto">
            <table className="w-full text-caption">
              <thead><tr className="text-left text-fg-tertiary"><th className="px-4 py-2">When (Dhaka)</th><th>Counter</th><th>Machine</th><th>Message</th><th>Counted</th><th>Status</th><th>Shift</th><th>Via</th></tr></thead>
              <tbody>{r.data.items.map((e) => (
                <tr key={e.id} className="border-t border-border-subtle align-top">
                  <td className="px-4 py-2 tabular-nums whitespace-nowrap">{dhaka(e.occurredAt)}</td>
                  <td className="font-mono">{e.deviceCode}</td>
                  <td>{e.machine?.code ?? "—"}<div className="text-fg-tertiary">{e.line?.name}</div></td>
                  <td className="tabular-nums">{e.counterMode.toLowerCase()} {e.rawValue}</td>
                  <td className="tabular-nums">{e.pieces} pc{e.pulses !== e.pieces ? <span className="text-fg-tertiary"> ({e.pulses} pulses)</span> : null}</td>
                  <td><Badge tone={e.status === "COUNTED" ? "success" : e.status === "SUSPECT_JUMP" ? "danger" : "neutral"}>{e.status.toLowerCase().replace(/_/g, " ")}</Badge>
                    {e.note && <div className="text-fg-tertiary max-w-[18rem]">{e.note}</div>}</td>
                  <td>{e.shiftCode ?? "—"}<div className="text-fg-tertiary">{e.productionDate}</div></td>
                  <td className="text-fg-tertiary">{e.transport.toLowerCase()}{e.sourceType === "SIMULATED" ? " · simulated" : ""}</td>
                </tr>
              ))}</tbody>
            </table>
          </div>
          <Pager page={page} size={r.data.pageSize} total={r.data.total} onPage={setPage} />
        </>
      )}
    </Panel>
  );
}

function Pager({ page, size, total, onPage }: { page: number; size: number; total: number; onPage: (p: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / size));
  if (pages <= 1) return null;
  return (
    <div className="flex items-center justify-end gap-2 px-4 py-2 text-caption">
      <span className="text-fg-tertiary">{total} rows · page {page} of {pages}</span>
      <Button size="sm" variant="ghost" disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous</Button>
      <Button size="sm" variant="ghost" disabled={page >= pages} onClick={() => onPage(page + 1)}>Next</Button>
    </div>
  );
}

// ─── Alerts ────────────────────────────────────────────────────────────────

function MachineAlerts({ alerts, onChanged }: { alerts: MonitoringAlert[]; onChanged: () => void }) {
  const [acting, setActing] = useState<{ alert: MonitoringAlert; action: AlertAction } | null>(null);
  return (
    <Panel title="Machine alerts" subtitle="Offline machines, errors, unexpected stoppages, long idle, temperature, vibration, counters and unusual transitions · the usual alert workflow"
      icon={<Bell size={15} />} right={<Link className="text-caption text-[var(--accent)]" href={`${MON}/alerts`}>All alerts</Link>} testId="machines-alerts">
      {alerts.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No active machine alert.</p> : (
        <ul className="divide-y divide-border-subtle">{alerts.map((a) => (
          <li key={a.id} className="px-4 py-2 flex flex-wrap items-center justify-between gap-2">
            <div className="min-w-0 flex-1"><AlertRow a={a} compact /></div>
            <AlertActionButtons a={a} onAction={(action) => setActing({ alert: a, action })} />
          </li>
        ))}</ul>
      )}
      <AlertActionDialog alert={acting?.alert ?? null} action={acting?.action ?? "acknowledge"} onClose={() => setActing(null)} onDone={() => { setActing(null); onChanged(); }} />
    </Panel>
  );
}

// ─── Simulator ─────────────────────────────────────────────────────────────

function Simulator({ o, f, onDone }: { o: MachineOverview; f: Filters; onDone: () => void }) {
  const toast = useToast();
  const canManage = useCan(P.DevicesManage);
  const all = useResource<MachineOverview>(`/api/iot/overview${qs({ factoryId: f.factory, source: "REAL" })}`);
  const simMachines = o.source === "SIMULATED" ? o.machines : [];
  const setupCandidates = (all.data?.machines ?? []).filter((m) => m.devices.length === 0 && m.workstation);
  const [target, setTarget] = useState(f.machine || "");
  const [scenario, setScenario] = useState("RUNNING");
  const [count, setCount] = useState("5");
  const [value, setValue] = useState("");
  const [delay, setDelay] = useState("300");
  const [setup, setSetup] = useState("");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<SimulationResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const runs = useResource<SimulationRun[]>("/api/iot/simulator/runs", { pollMs: 5_000 });
  const needs = (SIM_SCENARIOS.find((s) => s.id === scenario) as { needs?: string } | undefined)?.needs;
  const factoryId = f.factory || simMachines[0]?.factoryId || "";

  async function call<T>(path: string, body: unknown, ok: (r: T) => void) {
    setBusy(true);
    setError(null);
    try { ok(await apiJson<T>(path, { method: "POST", body })); onDone(); }
    catch (e) { setError(errorMessage(e)); }
    finally { setBusy(false); }
  }

  return (
    <div className="grid gap-4 xl:grid-cols-2" data-testid="iot-simulator">
      <Panel title="Send a scenario" subtitle="Simulated devices only · same topics, payloads and pipeline as real devices · everything is SIMULATED" icon={<Wand2 size={15} />}>
        <div className="p-4 space-y-3">
          {o.source !== "SIMULATED" && <Callout tone="info">Switch “Data” to <b>Simulated data</b> to see simulated machines here.</Callout>}
          {!factoryId && <Callout tone="warning">Choose a factory first.</Callout>}
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Scenario">{(ids) => <Select id={ids.id} value={scenario} onChange={(e) => setScenario(e.target.value)} data-testid="iot-sim-scenario">{SIM_SCENARIOS.map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}</Select>}</Field>
            <Field label="Target" hint={f.line ? "Or the whole line chosen above" : "One machine, or every simulated machine of the factory"}>{(ids) => (
              <Select id={ids.id} value={target} onChange={(e) => setTarget(e.target.value)} data-testid="iot-sim-target">
                <option value="">{f.workstation ? "The workstation chosen above" : f.line ? "The line chosen above" : "Whole factory"}</option>
                {simMachines.map((m) => <option key={m.id} value={m.id}>{m.code}</option>)}
              </Select>
            )}</Field>
            {needs === "count" && <Field label="Pieces">{(ids) => <Input id={ids.id} inputMode="numeric" value={count} onChange={(e) => setCount(e.target.value)} />}</Field>}
            {needs === "value" && <Field label={scenario === "TEMPERATURE" ? "Temperature (°C)" : "Vibration (mm/s)"} hint="Empty: a value above the demo limit">{(ids) => <Input id={ids.id} inputMode="decimal" value={value} onChange={(e) => setValue(e.target.value)} />}</Field>}
            {needs === "delay" && <Field label="Delayed by (s)">{(ids) => <Input id={ids.id} inputMode="numeric" value={delay} onChange={(e) => setDelay(e.target.value)} />}</Field>}
          </div>
          <Button variant="primary" iconLeft={<Play size={14} />} disabled={busy || !factoryId} data-testid="iot-sim-send"
            onClick={() => void call<SimulationResult>("/api/iot/simulator/send", {
              scenario, factoryId, machineId: target || null, workstationId: !target && f.workstation ? f.workstation : null, lineId: !target && f.line ? f.line : null,
              count: needs === "count" ? Number(count) : null, value: needs === "value" && value ? Number(value) : null, delaySeconds: needs === "delay" ? Number(delay) : null
            }, (r) => { setResult(r); toast.push({ tone: "success", title: `Sent ${r.messages.length} simulated message(s)` }); })}>Send</Button>
          {error && <Callout tone="danger">{error}</Callout>}
          {result && (
            <div className="space-y-2" data-testid="iot-sim-result">
              {result.notes.map((n) => <p key={n} className="text-caption text-fg-tertiary">{n}</p>)}
              <ul className="space-y-1 text-caption">{result.messages.map((m, i) => (
                <li key={i} className="rounded border border-border-subtle p-2">
                  <p className="flex flex-wrap items-center gap-2"><Badge tone={m.outcome === "Accepted" ? "success" : m.outcome === "Duplicate" ? "info" : "danger"}>{m.outcome}</Badge>
                    <span className="font-mono">{m.topic}</span></p>
                  <p className="font-mono text-[11px] text-fg-tertiary break-all">{m.payload}</p>
                  {(m.code || m.error) && <p className="text-fg-secondary">{m.code ? `${m.code}: ` : ""}{m.error}</p>}
                </li>
              ))}</ul>
            </div>
          )}
        </div>
      </Panel>
      <div className="space-y-4">
        <Panel title="Continuous simulation" subtitle="Plausible telemetry and counts every few seconds (at most 2 hours)" icon={<Activity size={15} />}>
          <ContinuousRun f={f} factoryId={factoryId} target={target} runs={runs.data ?? []} onChanged={() => void runs.reload()} />
        </Panel>
        {canManage && (
          <Panel title="Set up simulated devices" subtitle="Adds a simulated sensor and piece counter to a machine without real devices" icon={<Wrench size={15} />}>
            <div className="p-4 space-y-3">
              {setupCandidates.length === 0 ? <p className="text-caption text-fg-tertiary">Every placed machine already has devices, or no machine is placed at a workstation yet.</p> : (
                <>
                  <Field label="Machine">{(ids) => (
                    <Select id={ids.id} value={setup} onChange={(e) => setSetup(e.target.value)} data-testid="iot-sim-setup-machine">
                      <option value="">Choose…</option>{setupCandidates.map((m) => <option key={m.id} value={m.id}>{m.code} · {m.workstation?.code}</option>)}
                    </Select>
                  )}</Field>
                  <Button disabled={busy || !setup} data-testid="iot-sim-setup" onClick={() => void call<{ notes: string[] }>("/api/iot/simulator/setup", { machineId: setup, withCounter: true },
                    (r) => { toast.push({ tone: "success", title: "Simulated devices ready", description: r.notes.join(" ") }); setSetup(""); void all.reload(); })}>Add simulated devices</Button>
                </>
              )}
            </div>
          </Panel>
        )}
      </div>
    </div>
  );
}

function ContinuousRun({ f, factoryId, target, runs, onChanged }: { f: Filters; factoryId: string; target: string; runs: SimulationRun[]; onChanged: () => void }) {
  const [minutes, setMinutes] = useState("10");
  const [interval, setInterval_] = useState("5");
  const [profile, setProfile] = useState("MIXED");
  const [error, setError] = useState<string | null>(null);
  async function start() {
    setError(null);
    try {
      await apiJson("/api/iot/simulator/runs", {
        method: "POST", body: { factoryId, machineId: target || null, workstationId: !target && f.workstation ? f.workstation : null, lineId: !target && f.line ? f.line : null,
          durationMinutes: Number(minutes), intervalSeconds: Number(interval), profile }
      });
      onChanged();
    } catch (e) { setError(errorMessage(e)); }
  }
  async function stop(id: string) {
    try { await apiJson(`/api/iot/simulator/runs/${id}/stop`, { method: "POST", body: {} }); onChanged(); }
    catch (e) { setError(errorMessage(e)); }
  }
  return (
    <div className="p-4 space-y-3">
      <div className="grid gap-3 grid-cols-3">
        <Field label="Minutes">{(ids) => <Input id={ids.id} inputMode="numeric" value={minutes} onChange={(e) => setMinutes(e.target.value)} />}</Field>
        <Field label="Every (s)">{(ids) => <Input id={ids.id} inputMode="numeric" value={interval} onChange={(e) => setInterval_(e.target.value)} />}</Field>
        <Field label="Profile">{(ids) => <Select id={ids.id} value={profile} onChange={(e) => setProfile(e.target.value)}><option value="MIXED">Mixed</option><option value="BUSY">Busy</option><option value="FAULTY">Faulty</option></Select>}</Field>
      </div>
      <Button iconLeft={<Play size={14} />} disabled={!factoryId} onClick={() => void start()} data-testid="iot-sim-run">Start</Button>
      {error && <Callout tone="danger">{error}</Callout>}
      {runs.length > 0 && (
        <ul className="space-y-1 text-caption">{runs.map((r) => (
          <li key={r.id} className="flex flex-wrap items-center justify-between gap-2 rounded border border-border-subtle p-2">
            <span><Badge tone={r.status === "RUNNING" ? "success" : r.status === "FAILED" ? "danger" : "neutral"}>{r.status.toLowerCase()}</Badge> {r.target} · {r.profile.toLowerCase()} ·
              {" "}{r.messagesSent} sent{r.messagesRejected ? `, ${r.messagesRejected} rejected` : ""} · ends {dhaka(r.endsAt)}{r.lastError ? <span className="text-fg-tertiary"> · {r.lastError}</span> : null}</span>
            {r.status === "RUNNING" && <Button size="sm" variant="ghost" iconLeft={<Square size={12} />} onClick={() => void stop(r.id)}>Stop</Button>}
          </li>
        ))}</ul>
      )}
    </div>
  );
}
