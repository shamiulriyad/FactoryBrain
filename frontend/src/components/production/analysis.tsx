"use client";

// Line analysis widgets: station workload bars, the bottleneck card, and
// the forms that record the inputs behind them (time studies and the layout
// as found on the floor). Loads, capacities and the constraint come from the
// API; these components only draw them.

import { useEffect, useMemo, useState } from "react";
import { Plus, Trash2 } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { useToast } from "@/components/ui/Toast";
import { announceDataChanged } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { cn } from "@/lib/cn";
import { BOTTLENECK_LABEL, fmt, fmtPct } from "@/lib/production";
import { recordLayout, recordObservation } from "@/services/production.api";
import type { BottleneckResult, LayoutStation, LineLayout, ProductionStyle, WorkloadMetrics } from "@/services/contracts";

/** Horizontal bars of station load (minutes per piece) against the cycle time. */
export function WorkloadBars({ stations, metrics, testId }: { stations: LayoutStation[]; metrics: WorkloadMetrics; testId?: string }) {
  const max = Math.max(metrics.cycleTimeMinutes, ...stations.map((s) => s.loadMinutes), 0.0001);
  return (
    <div data-testid={testId}>
      <p className="text-caption text-fg-tertiary mb-2">
        Station load in standard minutes per piece. Bar length is relative to the cycle time ({fmt(metrics.cycleTimeMinutes, 3)} min, the busiest station).
      </p>
      <ul className="space-y-1.5">
        {stations.map((s) => (
          <li key={s.stationNo} className="grid grid-cols-[3rem_minmax(0,1fr)_5rem] items-center gap-2 text-caption">
            <span className="text-fg-tertiary tabular-nums">WS {s.stationNo}</span>
            <div>
              <div className="h-2.5 rounded-full bg-surface-3 overflow-hidden" role="presentation">
                <div className={cn("h-full rounded-full", s.stationNo === metrics.bottleneckStationNo ? "bg-[var(--risk-high)]" : "bg-[var(--accent)]")}
                  style={{ width: `${Math.max(2, (s.loadMinutes / max) * 100)}%` }} />
              </div>
              <p className="mt-0.5 text-fg-secondary truncate" title={s.operations.join(", ")}>
                {s.operations.join(", ")}{s.machineType && <span className="text-fg-tertiary"> · {s.machineType}</span>}
              </p>
            </div>
            <span className="text-right tabular-nums text-fg-primary">{fmt(s.loadMinutes, 3)} min</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function MetricsRow({ m }: { m: WorkloadMetrics }) {
  return (
    <dl className="grid grid-cols-2 sm:grid-cols-5 gap-3 text-body">
      <div><dt className="text-caption text-fg-tertiary">Workstations</dt><dd className="tabular-nums">{m.stations}</dd></div>
      <div><dt className="text-caption text-fg-tertiary">Cycle time</dt><dd className="tabular-nums">{fmt(m.cycleTimeMinutes, 3)} min</dd></div>
      <div><dt className="text-caption text-fg-tertiary">Capacity at 100 %</dt><dd className="tabular-nums">{fmt(m.capacityPerHour, 1)} pcs/h</dd></div>
      <div><dt className="text-caption text-fg-tertiary">Balance efficiency</dt><dd className="tabular-nums">{fmtPct(m.balanceEfficiencyPct)}</dd></div>
      <div><dt className="text-caption text-fg-tertiary">Smoothness index</dt><dd className="tabular-nums">{fmt(m.smoothnessIndex, 3)}</dd></div>
    </dl>
  );
}

export function BottleneckCard({ b }: { b: BottleneckResult }) {
  return (
    <div className="space-y-3" data-testid="bottleneck-card" data-status={b.status}>
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={b.status === "observed" ? "info" : b.status === "standard_only" ? "warning" : "neutral"}>{BOTTLENECK_LABEL[b.status]}</Badge>
        {b.constraint && (
          <span className="text-body text-fg-primary" data-testid="bottleneck-constraint">
            Constraint: {b.constraint.sequence}. {b.constraint.name}
            {b.constraintStationNo != null && ` (workstation ${b.constraintStationNo})`} · about {fmt(b.constraintCapacityPerHour, 1)} pcs/h
          </span>
        )}
        <span className="text-caption text-fg-tertiary">{b.observedOperations} of {b.totalOperations} operations time-studied</span>
      </div>
      {b.evidence.length > 0 && <ul className="list-disc pl-4 text-caption text-fg-secondary space-y-0.5">{b.evidence.map((e, i) => <li key={i}>{e}</li>)}</ul>}
      {b.limitations.length > 0 && (
        <Callout tone="warning" title="Limitations"><ul className="list-disc pl-4">{b.limitations.map((e, i) => <li key={i}>{e}</li>)}</ul></Callout>
      )}
      {b.missingInputs.length > 0 && (
        <Callout tone="info" title="Missing inputs"><ul className="list-disc pl-4">{b.missingInputs.map((e, i) => <li key={i}>{e}</li>)}</ul></Callout>
      )}
      {b.operations.length > 0 && (
        <div className="overflow-x-auto">
          <table className="data-table" data-testid="bottleneck-ops">
            <thead><tr><th className="num">Seq</th><th>Operation</th><th className="num">SMV</th><th className="num">Observed cycle</th><th className="num">Operators</th><th className="num">Measured capacity</th><th className="num">Performance</th><th className="num">Standard capacity</th><th className="num">Queue</th></tr></thead>
            <tbody>
              {b.operations.map((o) => (
                <tr key={o.operationId} className={cn(b.constraint?.operationId === o.operationId && "bg-[var(--risk-high-soft)]")}>
                  <td className="num">{o.sequence}</td>
                  <td>{o.name}{o.machineType && <span className="block text-caption text-fg-tertiary">{o.machineType}</span>}</td>
                  <td className="num">{fmt(o.smv, 3)}</td>
                  <td className="num">{o.observedCycleSeconds == null ? "—" : `${fmt(o.observedCycleSeconds, 1)} s`}{o.sampleCount != null && <span className="block text-caption text-fg-tertiary">{o.sampleCount} cycles</span>}</td>
                  <td className="num">{o.operatorsAssigned ?? "—"}</td>
                  <td className="num">{o.observedCapacityPerHour == null ? "—" : `${fmt(o.observedCapacityPerHour, 1)}/h`}</td>
                  <td className="num">{fmtPct(o.performancePct)}</td>
                  <td className="num">{o.standardCapacityPerHour == null ? "—" : `${fmt(o.standardCapacityPerHour, 1)}/h`}</td>
                  <td className="num">{o.queueBefore ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function useSave() {
  const toast = useToast();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  async function run(fn: () => Promise<unknown>, title: string, after: () => void) {
    setBusy(true);
    setError(null);
    try { await fn(); toast.push({ title }); announceDataChanged(); after(); }
    catch (e) { setError(e); }
    finally { setBusy(false); }
  }
  return { busy, error, run, fe: (k: string) => (isApiError(error) ? error.fieldErrors[k]?.join(" ") ?? null : null), clear: () => setError(null) };
}

export function TimeStudyModal({ open, onClose, lineId, style, onSaved }: { open: boolean; onClose: () => void; lineId: string; style: ProductionStyle | null; onSaved: () => void }) {
  const s = useSave();
  const [f, setF] = useState({ operationId: "", cycleSeconds: "", sampleCount: "10", operatorsAssigned: "1", queueBefore: "", notes: "" });
  useEffect(() => {
    if (open) { s.clear(); setF({ operationId: style?.operations[0]?.id ?? "", cycleSeconds: "", sampleCount: "10", operatorsAssigned: "1", queueBefore: "", notes: "" }); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, style]);
  if (!style) return null;
  return (
    <Modal open={open} onClose={onClose} locked={s.busy} testId="study-form" title={`Record a time study · ${style.code} on ${lineId}`}
      description="The mean of the timed cycles for one operator, and how many operators work the operation. Queue counts in front of operations corroborate the bottleneck."
      footer={<>
        <Button variant="ghost" onClick={onClose} disabled={s.busy}>Cancel</Button>
        <Button variant="primary" type="submit" form="study-form-el" disabled={s.busy} data-testid="study-save">{s.busy ? "Saving…" : "Save"}</Button>
      </>}>
      <form id="study-form-el" className="space-y-3" onSubmit={(e) => {
        e.preventDefault();
        void s.run(() => recordObservation({
          lineId, styleId: style.id, operationId: f.operationId, cycleSeconds: Number(f.cycleSeconds), sampleCount: Number(f.sampleCount),
          operatorsAssigned: Number(f.operatorsAssigned), queueBefore: f.queueBefore === "" ? null : Number(f.queueBefore), notes: f.notes.trim() || undefined
        }), "Time study recorded", () => { onSaved(); onClose(); });
      }}>
        {s.error ? <Callout tone="danger" title="Not saved">{errorMessage(s.error)}</Callout> : null}
        <Field label="Operation" error={s.fe("operationId")}>
          {(a) => (
            <Select id={a.id} value={f.operationId} onChange={(e) => setF({ ...f, operationId: e.target.value })} required data-testid="study-op">
              {style.operations.map((o) => <option key={o.id} value={o.id}>{o.sequence}. {o.name} (SMV {fmt(o.smv, 3)})</option>)}
            </Select>
          )}
        </Field>
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <Field label="Mean cycle (s)" error={s.fe("cycleSeconds")}>{(a) => <Input id={a.id} type="number" step="0.1" min={0} value={f.cycleSeconds} onChange={(e) => setF({ ...f, cycleSeconds: e.target.value })} required data-testid="study-cycle" />}</Field>
          <Field label="Cycles timed" error={s.fe("sampleCount")}>{(a) => <Input id={a.id} type="number" min={1} value={f.sampleCount} onChange={(e) => setF({ ...f, sampleCount: e.target.value })} required />}</Field>
          <Field label="Operators on it" error={s.fe("operatorsAssigned")}>{(a) => <Input id={a.id} type="number" min={1} value={f.operatorsAssigned} onChange={(e) => setF({ ...f, operatorsAssigned: e.target.value })} required data-testid="study-operators" />}</Field>
          <Field label="Queue (pcs)" error={s.fe("queueBefore")}>{(a) => <Input id={a.id} type="number" min={0} value={f.queueBefore} onChange={(e) => setF({ ...f, queueBefore: e.target.value })} data-testid="study-queue" />}</Field>
        </div>
        <Field label="Notes (optional)" error={s.fe("notes")}>{(a) => <Input id={a.id} value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} maxLength={500} />}</Field>
      </form>
    </Modal>
  );
}

export function LayoutModal({ open, onClose, lineId, workstations, style, current, onSaved }: {
  open: boolean; onClose: () => void; lineId: string; workstations: number; style: ProductionStyle | null; current?: LineLayout | null; onSaved: () => void;
}) {
  const s = useSave();
  const [rows, setRows] = useState<string[][]>([]);
  useEffect(() => {
    if (!open) return;
    s.clear();
    setRows(current?.stations.map((st) => st.operationIds) ?? (style?.operations.map((o) => [o.id]) ?? []));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, current, style]);
  const ops = useMemo(() => style?.operations ?? [], [style]);
  if (!style) return null;
  return (
    <Modal open={open} onClose={onClose} locked={s.busy} className="sm:max-w-2xl" testId="layout-form" title={`Record the current layout · ${style.code} on ${lineId}`}
      description={`Which operations each workstation performs today, as found on the floor (at most ${workstations} workstations). An operation on several workstations is shared evenly.`}
      footer={<>
        <Button variant="ghost" onClick={onClose} disabled={s.busy}>Cancel</Button>
        <Button variant="primary" type="submit" form="layout-form-el" disabled={s.busy || rows.length === 0} data-testid="layout-save">{s.busy ? "Saving…" : "Save layout"}</Button>
      </>}>
      <form id="layout-form-el" className="space-y-2" onSubmit={(e) => {
        e.preventDefault();
        void s.run(() => recordLayout({ lineId, styleId: style.id, stations: rows.map((r, i) => ({ stationNo: i + 1, operationIds: r })) }),
          "Layout recorded", () => { onSaved(); onClose(); });
      }}>
        {s.error ? <Callout tone="danger" title="Not saved">{errorMessage(s.error)}</Callout> : null}
        {rows.map((r, i) => (
          <div key={i} className="flex flex-wrap items-center gap-2">
            <span className="w-12 text-caption text-fg-tertiary">WS {i + 1}</span>
            <div className="flex flex-wrap gap-2 flex-1">
              {ops.map((o) => (
                <label key={o.id} className="flex items-center gap-1 text-caption">
                  <input type="checkbox" checked={r.includes(o.id)}
                    onChange={(e) => setRows((all) => all.map((x, j) => (j === i ? (e.target.checked ? [...x, o.id] : x.filter((id) => id !== o.id)) : x)))} />
                  {o.sequence}
                </label>
              ))}
            </div>
            <Button size="sm" variant="ghost" aria-label={`Remove workstation ${i + 1}`} onClick={() => setRows((all) => all.filter((_, j) => j !== i))}><Trash2 size={13} /></Button>
          </div>
        ))}
        <Button size="sm" variant="secondary" iconLeft={<Plus size={13} />} disabled={rows.length >= workstations} onClick={() => setRows((all) => [...all, []])}>Add workstation</Button>
        <p className="text-caption text-fg-tertiary">Operations: {ops.map((o) => `${o.sequence}. ${o.name}`).join(" · ")}</p>
      </form>
    </Modal>
  );
}
