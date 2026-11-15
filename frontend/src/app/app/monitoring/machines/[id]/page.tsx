"use client";

// One machine: its derived state and latest readings, today's running time
// and downtime, devices, state history, raw telemetry, production counter
// history, machine alerts, maintenance and rules. The workstation's
// assignment and verified identity are shown beside it, never merged into it.

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { ArrowLeft, Bell, Cpu, Gauge, History, ListOrdered, Settings2, UserRound, Wrench } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { AlertActionButtons, AlertActionDialog, AlertRow, ConnectionBadge, LiveIndicator, MonitoringTabs, type AlertAction } from "@/components/monitoring/shared";
import { MachineProfileDialog, MachineStateBadge, StateBar } from "@/components/monitoring/machines";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { P } from "@/lib/access";
import { apiJson, errorMessage } from "@/lib/api";
import { MON, ago, dhaka, type MonitoringAlert } from "@/lib/monitoring";
import { CONDITION_LABEL, MACHINE_DISCLAIMER, hours, num, stateLabel, type MachineDetail, type MachineDevice } from "@/lib/iot";

export default function MachinePage() {
  return (
    <RequirePermission perms={[P.MonitoringView, P.DevicesView, P.ProductionView]} what="Machine monitoring">
      <MachineView />
    </RequirePermission>
  );
}

function MachineView() {
  const { id } = useParams<{ id: string }>();
  const r = useResource<MachineDetail>(`/api/iot/machines/${id}`, { pollMs: 30_000 });
  const d = r.data;
  const pending = useRef<ReturnType<typeof setTimeout> | null>(null);
  const reloadSoon = useCallback(() => {
    if (pending.current) return;
    pending.current = setTimeout(() => { pending.current = null; void r.reload(); }, 1000);
  }, [r]);
  useEffect(() => () => { if (pending.current) clearTimeout(pending.current); }, []);
  const hub = useMonitoringHub(d?.machine.factoryId ?? null, {
    onMachine: (_, m) => { if (m.id === id) reloadSoon(); },
    onProduction: (p) => { if (p.machineId === id) reloadSoon(); },
    onAlert: (_, a) => { if (a.machine?.id === id) reloadSoon(); },
    onResync: () => void r.reload()
  }, !!d);
  const [rules, setRules] = useState(false);
  const [maintenance, setMaintenance] = useState(false);
  const [reset, setReset] = useState<MachineDevice | null>(null);
  const [acting, setActing] = useState<{ alert: MonitoringAlert; action: AlertAction } | null>(null);

  if (r.error && !d) return <div className="page"><ErrorState title="Could not load the machine" body={r.error.message} retryLabel="Try again" onRetry={() => void r.reload()} /></div>;
  if (!d) return <div className="page"><SkeletonRows rows={8} /></div>;
  const m = d.machine;

  return (
    <div className="page" data-testid="machine-page">
      <Link href={`${MON}/machines`} className="inline-flex items-center gap-1 text-caption text-[var(--accent)] mb-2"><ArrowLeft size={13} />Machine monitoring</Link>
      <PageHeader eyebrow={`${m.factoryCode} · ${m.line?.name ?? "no line"} · ${m.workstation?.code ?? "not placed"}`} title={`Machine ${m.code}`}
        description={m.machineType ?? undefined} meta={<LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} />}
        actions={d.canConfigure ? (
          <div className="flex gap-2">
            <Button iconLeft={<Settings2 size={14} />} onClick={() => setRules(true)} data-testid="machine-rules">Rules</Button>
            <Button iconLeft={<Wrench size={14} />} onClick={() => setMaintenance(true)} data-testid="machine-maintenance">{m.inMaintenance ? "End maintenance" : "Maintenance"}</Button>
          </div>
        ) : undefined} />
      <MonitoringTabs className="mb-4" />
      {m.isSimulated && <Callout tone="demo" className="mb-4">SIMULATED machine data from the IoT simulator — not a real machine.</Callout>}

      <div className="grid gap-4 xl:grid-cols-3 mb-4">
        <Panel title="Now" icon={<Gauge size={15} />} testId="machine-now">
          <div className="p-4 space-y-3">
            <div className="flex items-center gap-2">{m.monitored ? <MachineStateBadge state={m.state} simulated={m.isSimulated} testId="machine-state" /> : <Badge>No sensor</Badge>}
              {m.since && <span className="text-caption text-fg-tertiary">since {dhaka(m.since)}</span>}</div>
            {m.reason && <p className="text-caption text-fg-secondary">{m.reason}</p>}
            {m.inMaintenance && <Callout tone="info">In maintenance: {m.maintenanceReason}</Callout>}
            <dl className="grid grid-cols-2 gap-2 text-caption">
              <div><dt className="text-fg-tertiary">Power</dt><dd className="tabular-nums">{num(m.powerWatts, 0, " W")}</dd></div>
              <div><dt className="text-fg-tertiary">Current</dt><dd className="tabular-nums">{num(m.currentAmps, 2, " A")}</dd></div>
              <div><dt className="text-fg-tertiary">Temperature</dt><dd className="tabular-nums">{num(m.temperatureC, 1, " °C")}</dd></div>
              <div><dt className="text-fg-tertiary">Vibration</dt><dd className="tabular-nums">{num(m.vibrationMmS, 1, " mm/s")}</dd></div>
              <div><dt className="text-fg-tertiary">Last telemetry</dt><dd>{ago(m.lastTelemetryAt)}</dd></div>
              <div><dt className="text-fg-tertiary">Last heartbeat</dt><dd>{ago(m.lastHeartbeatAt)}</dd></div>
              <div><dt className="text-fg-tertiary">Last count</dt><dd>{ago(m.lastCountAt)}</dd></div>
              <div><dt className="text-fg-tertiary">Error</dt><dd className="font-mono">{m.errorCode ?? "—"}</dd></div>
            </dl>
            {m.activeConditions.length > 0 && <div className="flex flex-wrap gap-1">{m.activeConditions.map((c) => <Badge key={c} tone="warning">{CONDITION_LABEL[c] ?? c}</Badge>)}</div>}
          </div>
        </Panel>
        <Panel title="Today" subtitle="Machine time by state (Dhaka day)" icon={<History size={15} />} testId="machine-today">
          <div className="p-4 space-y-3">
            <StateBar m={d.today} />
            <dl className="grid grid-cols-2 gap-2 text-caption">
              <div><dt className="text-fg-tertiary">Running</dt><dd>{hours(d.today.runningSeconds)}</dd></div>
              <div><dt className="text-fg-tertiary">Downtime</dt><dd>{hours(d.today.downtimeSeconds)}</dd></div>
              {d.canSeeProduction && <div><dt className="text-fg-tertiary">Pieces</dt><dd className="tabular-nums text-xl font-semibold">{d.today.pieces}</dd></div>}
              {d.canSeeProduction && <div><dt className="text-fg-tertiary">Per running hour</dt><dd className="tabular-nums">{d.today.piecesPerRunningHour ?? "—"}</dd></div>}
            </dl>
            <p className="text-[11px] text-fg-tertiary">{MACHINE_DISCLAIMER}</p>
          </div>
        </Panel>
        <Panel title="Workstation context" subtitle="Separate records, shown side by side" icon={<UserRound size={15} />} testId="machine-context">
          <div className="p-4 space-y-2 text-caption">
            {!d.context ? <p className="text-fg-tertiary">The machine isn&apos;t placed at a workstation.</p> : (
              <>
                <p><span className="text-fg-tertiary">Scheduled assignment: </span>{d.context.assignedWorker ? `${d.context.assignedWorker.code} · ${d.context.assignedWorker.name ?? ""} (since ${dhaka(d.context.assignedSince)})` : "none visible to you"}</p>
                <p><span className="text-fg-tertiary">Verified at the workstation: </span>{d.context.verifiedWorker
                  ? `${d.context.verifiedWorker.code} · ${d.context.verifiedCamera} track ${d.context.verifiedTrack} (since ${dhaka(d.context.verifiedAt)})` : "nobody verified now"}</p>
                <p className="text-fg-tertiary">{d.context.note}</p>
              </>
            )}
          </div>
        </Panel>
      </div>

      <div className="grid gap-4 xl:grid-cols-2 mb-4">
        <Panel title="Devices" icon={<Cpu size={15} />} testId="machine-devices">
          {m.devices.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No device bound. Bind a machine sensor or counter to this machine in Cameras &amp; devices.</p> : (
            <ul className="divide-y divide-border-subtle text-caption">{m.devices.map((x) => (
              <li key={x.id} className="px-4 py-2 flex flex-wrap items-center justify-between gap-2">
                <span><span className="font-mono">{x.code}</span> · {x.deviceTypeLabel} · {x.protocol}{x.decidesState ? " · decides state" : ""}{x.isCounter ? " · counter" : ""}</span>
                <span className="flex items-center gap-2"><ConnectionBadge status={x.health} label={x.healthLabel} simulated={x.isSimulated} />
                  {x.isCounter && d.canCorrect && <Button size="sm" variant="ghost" onClick={() => setReset(x)}>Reset counter</Button>}</span>
              </li>
            ))}</ul>
          )}
        </Panel>
        <Panel title="Machine alerts" icon={<Bell size={15} />} testId="machine-alerts">
          {d.alerts.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No alerts.</p> : (
            <ul className="divide-y divide-border-subtle">{d.alerts.map((a) => (
              <li key={a.id} className="px-4 py-2 flex flex-wrap items-center justify-between gap-2">
                <div className="min-w-0 flex-1"><AlertRow a={a} compact /></div>
                <AlertActionButtons a={a} onAction={(action) => setActing({ alert: a, action })} />
              </li>
            ))}</ul>
          )}
        </Panel>
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <Panel title="State history" subtitle="Derived from valid telemetry and device health" icon={<History size={15} />} testId="machine-transitions">
          {d.recentTransitions.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No state changes yet.</p> : (
            <ul className="divide-y divide-border-subtle text-caption">{d.recentTransitions.map((t) => (
              <li key={t.id} className="px-4 py-2">
                <p className="flex flex-wrap items-center gap-2"><span className="tabular-nums text-fg-tertiary">{dhaka(t.occurredAt)}</span>
                  {t.fromState && <span>{stateLabel(t.fromState)} →</span>}<MachineStateBadge state={t.toState} simulated={t.sourceType === "SIMULATED"} />
                  <span className="text-fg-tertiary">lasted {hours(t.durationSeconds)}</span></p>
                <p className="text-fg-secondary">{t.reason}</p>
              </li>
            ))}</ul>
          )}
        </Panel>
        <Panel title="Raw telemetry" subtitle="As received (kept for the retention period)" icon={<ListOrdered size={15} />} testId="machine-telemetry">
          {d.recentTelemetry.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No telemetry yet.</p> : (
            <div className="overflow-x-auto">
              <table className="w-full text-caption">
                <thead><tr className="text-left text-fg-tertiary"><th className="px-4 py-2">When</th><th>State</th><th>W</th><th>°C</th><th>mm/s</th><th>Outcome</th><th>Via</th></tr></thead>
                <tbody>{d.recentTelemetry.map((t) => (
                  <tr key={t.id} className="border-t border-border-subtle">
                    <td className="px-4 py-1.5 tabular-nums whitespace-nowrap">{dhaka(t.occurredAt)}</td>
                    <td>{t.errorCode ? <span className="font-mono">{t.errorCode}</span> : stateLabel(t.reportedState)}</td>
                    <td className="tabular-nums">{num(t.powerWatts, 0)}</td>
                    <td className="tabular-nums">{num(t.temperatureC, 1)}</td>
                    <td className="tabular-nums">{num(t.vibrationMmS, 1)}</td>
                    <td><Badge tone={t.outcome === "APPLIED" ? "success" : "neutral"}>{t.outcome.toLowerCase().replace(/_/g, " ")}</Badge></td>
                    <td className="text-fg-tertiary">{t.transport.toLowerCase()}</td>
                  </tr>
                ))}</tbody>
              </table>
            </div>
          )}
        </Panel>
        {d.canSeeProduction && (
          <Panel className="xl:col-span-2" title="Production counter history" icon={<ListOrdered size={15} />} testId="machine-counts">
            {d.recentCounts.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No counts yet.</p> : (
              <div className="overflow-x-auto">
                <table className="w-full text-caption">
                  <thead><tr className="text-left text-fg-tertiary"><th className="px-4 py-2">When</th><th>Counter</th><th>Message</th><th>Pieces</th><th>Status</th><th>Shift</th><th>Via</th></tr></thead>
                  <tbody>{d.recentCounts.map((e) => (
                    <tr key={e.id} className="border-t border-border-subtle align-top">
                      <td className="px-4 py-1.5 tabular-nums whitespace-nowrap">{dhaka(e.occurredAt)}</td>
                      <td className="font-mono">{e.deviceCode}</td>
                      <td className="tabular-nums">{e.counterMode.toLowerCase()} {e.rawValue}</td>
                      <td className="tabular-nums">{e.pieces}</td>
                      <td>{e.status.toLowerCase().replace(/_/g, " ")}{e.note && <div className="text-fg-tertiary">{e.note}</div>}</td>
                      <td>{e.shiftCode ?? "—"}</td>
                      <td className="text-fg-tertiary">{e.transport.toLowerCase()}</td>
                    </tr>
                  ))}</tbody>
                </table>
              </div>
            )}
          </Panel>
        )}
      </div>

      <MachineProfileDialog machineId={m.id} profile={d.profile} open={rules} onClose={() => setRules(false)} onSaved={() => void r.reload()} />
      <MaintenanceDialog machineId={m.id} on={!m.inMaintenance} open={maintenance} onClose={() => setMaintenance(false)} onDone={() => { setMaintenance(false); void r.reload(); }} />
      <ResetDialog device={reset} onClose={() => setReset(null)} onDone={() => { setReset(null); void r.reload(); }} />
      <AlertActionDialog alert={acting?.alert ?? null} action={acting?.action ?? "acknowledge"} onClose={() => setActing(null)} onDone={() => { setActing(null); void r.reload(); }} />
    </div>
  );
}

function MaintenanceDialog({ machineId, on, open, onClose, onDone }: { machineId: string; on: boolean; open: boolean; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save() {
    setBusy(true);
    setError(null);
    try {
      await apiJson(`/api/iot/machines/${machineId}/maintenance`, { method: "POST", body: { inMaintenance: on, reason: reason || null } });
      toast.push({ tone: "success", title: on ? "Maintenance started" : "Maintenance ended" });
      setReason("");
      onDone();
    } catch (e) { setError(errorMessage(e)); }
    finally { setBusy(false); }
  }
  return (
    <Modal open={open} onClose={onClose} locked={busy} title={on ? "Start maintenance" : "End maintenance"}
      description={on ? "While in maintenance, telemetry is stored but can't change the state, and stoppage alerts don't apply." : "The machine goes to UNKNOWN until its next telemetry."}
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button><Button variant="primary" onClick={() => void save()} disabled={busy || (on && reason.trim().length < 3)}>Confirm</Button></>}>
      {on && <Field label="Reason">{(ids) => <Input id={ids.id} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Needle and bobbin change" />}</Field>}
      {error && <Callout tone="danger" className="mt-3">{error}</Callout>}
    </Modal>
  );
}

function ResetDialog({ device, onClose, onDone }: { device: MachineDevice | null; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save() {
    if (!device) return;
    setBusy(true);
    setError(null);
    try {
      await apiJson("/api/iot/production/counter-reset", { method: "POST", body: { deviceId: device.id, value: 0, reason } });
      toast.push({ tone: "success", title: `Reset of ${device.code} recorded` });
      setReason("");
      onDone();
    } catch (e) { setError(errorMessage(e)); }
    finally { setBusy(false); }
  }
  return (
    <Modal open={device !== null} onClose={onClose} locked={busy} title={`Reset counter ${device?.code ?? ""}`}
      description="Records that the counter was reset to 0. It counts nothing and is audited; earlier counts are kept."
      footer={<><Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button><Button variant="primary" onClick={() => void save()} disabled={busy || reason.trim().length < 5}>Record reset</Button></>}>
      <Field label="Reason">{(ids) => <Input id={ids.id} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Counter replaced" />}</Field>
      {error && <Callout tone="danger" className="mt-3">{error}</Callout>}
    </Modal>
  );
}
