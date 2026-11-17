"use client";

// Capacity planning: an estimate for each allocated line from the line's real
// configuration (shifts, SMV, efficiency and operator assumptions, assigned
// workers, machine states, other allocations). Estimates are labelled as such,
// show every assumption, and say what is missing instead of inventing a number.

import Link from "next/link";
import { useState } from "react";
import { AlertTriangle, Gauge, Settings2 } from "lucide-react";
import { Callout } from "@/components/ui/Callout";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { EmptyState } from "@/components/ui/EmptyState";
import { Field, Select } from "@/components/ui/Field";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { DataTable } from "@/components/analytics/shared";
import { PL, type PlanningScope } from "@/components/planning/shared";
import { useResource } from "@/lib/useResource";
import { addDays, num, pct, planningApi, type CapacityEstimate, type CapacityOverview, type CapacityResult } from "@/lib/planning";
import type { ProductionStyle } from "@/services/contracts";

export function CapacityView({ scope }: { scope: PlanningScope }) {
  const { q, ctx, factoryId } = scope;
  const from = q.get("from") || ctx.today;
  const to = q.get("to") || addDays(ctx.today, 13);
  const r = useResource<CapacityOverview>(planningApi.capacityOverview({ factoryId, from, to }));
  const [whatIf, setWhatIf] = useState(false);
  const d = r.data;
  return (
    <>
      <div className="mb-4 flex flex-wrap items-end gap-2">
        <label className="text-caption text-fg-secondary flex flex-col gap-1">From
          <Input type="date" className="h-9" value={from} onChange={(e) => q.set({ from: e.target.value || null })} data-testid="capacity-from" /></label>
        <label className="text-caption text-fg-secondary flex flex-col gap-1">To
          <Input type="date" className="h-9" value={to} onChange={(e) => q.set({ to: e.target.value || null })} data-testid="capacity-to" /></label>
        <Button className="ml-auto" variant="secondary" iconLeft={<Gauge size={14} />} onClick={() => setWhatIf(true)} data-testid="capacity-whatif">What-if estimate</Button>
      </div>
      <Callout tone="info" className="mb-4" testId="capacity-disclaimer">{d?.disclaimer ?? "Capacity is an estimate from configured inputs, not measured output."} Standard minutes = quantity × SMV; capacity = productive minutes × operators × efficiency.</Callout>
      {r.error && !d ? <ErrorState title="Could not load capacity" body={r.error.message} retryLabel="Try again" onRetry={() => void r.reload()} />
        : !d ? <SkeletonRows rows={6} />
        : (
          <>
            {d.warnings.length > 0 && <Callout tone="warning" className="mb-4"><ul className="list-disc pl-4">{d.warnings.map((w) => <li key={w}>{w}</li>)}</ul></Callout>}
            {d.lines.length === 0 ? <div className="surface"><EmptyState icon={<Gauge size={20} />} title="No allocations in this period" body="Allocate orders to lines to see their estimated capacity." /></div> : (
              <div className="space-y-4">
                <Panel title="Lines" subtitle={`${d.from} – ${d.to}`} flush testId="capacity-lines">
                  <DataTable testId="capacity-table" columns={["Line", "Allocations", "Available hours", "Demand (std min)", "Capacity (std min)", "Utilisation", "Estimate"]}
                    rows={d.lines.map((l) => [
                      l.lineName, l.allocations, num(l.availableHours, 1), l.demandMinutes == null ? "—" : num(l.demandMinutes), l.capacityMinutes == null ? "Not configured" : num(l.capacityMinutes),
                      l.utilizationPct == null ? "—" : <span key="u" className={l.utilizationPct > 100 ? "text-[var(--risk-high)] font-medium" : ""}>{pct(l.utilizationPct)}</span>,
                      l.status === "COMPLETE" ? <Badge key="s" tone="success">Complete</Badge> : <Badge key="s" tone="warning" title={l.missing.map((m) => m.message).join(" ")}>Incomplete</Badge>
                    ])} />
                </Panel>
                {d.lines.map((l) => (
                  <Panel key={l.lineId} title={l.lineName} subtitle="Per allocation" icon={l.utilizationPct != null && l.utilizationPct > 100 ? <AlertTriangle size={15} /> : <Gauge size={15} />}>
                    <div className="p-4 space-y-3">
                      {l.notes.map((n) => <p key={n} className="text-caption text-fg-tertiary">{n}</p>)}
                      {l.missing.length > 0 && <MissingList missing={l.missing} factoryId={factoryId} />}
                      {l.estimates.map((e) => <EstimateCard key={e.label} e={e} />)}
                    </div>
                  </Panel>
                ))}
              </div>
            )}
          </>
        )}
      <WhatIf open={whatIf} onClose={() => setWhatIf(false)} scope={scope} from={from} to={to} />
    </>
  );
}

function MissingList({ missing, factoryId }: { missing: CapacityResult["missing"]; factoryId: string }) {
  return (
    <Callout tone="warning" title="Estimate incomplete: no capacity number is shown" testId="capacity-missing">
      <ul className="list-disc pl-4 space-y-1">{missing.map((m) => <li key={m.code}>{m.message} <span className="text-fg-tertiary">{m.howToFix}</span></li>)}</ul>
      <p className="mt-1"><Link href={`${PL}/setup?factory=${factoryId}`} className="text-accent hover:underline inline-flex items-center gap-1"><Settings2 size={12} /> Configure capacity assumptions</Link></p>
    </Callout>
  );
}

/** One estimate with its figures, conflicts, machine checks and every assumption it used. */
export function EstimateCard({ e }: { e: CapacityEstimate }) {
  const r = e.result;
  return (
    <div className="rounded border border-border-subtle p-3 text-caption space-y-2" data-testid="capacity-estimate">
      <p className="flex flex-wrap items-center gap-2"><span className="font-medium">{e.label}</span><Badge tone="accent">Estimate</Badge>
        {r.status === "INCOMPLETE" && <Badge tone="warning">Incomplete</Badge>}{r.canMeet === true && <Badge tone="success">Can meet the plan</Badge>}{r.canMeet === false && <Badge tone="danger">Cannot meet the plan</Badge>}</p>
      {r.status === "INCOMPLETE" ? <ul className="list-disc pl-4">{r.missing.map((m) => <li key={m.code}>{m.message} <span className="text-fg-tertiary">{m.howToFix}</span></li>)}</ul> : (
        <div className="grid grid-cols-2 md:grid-cols-5 gap-3">
          <Stat label="Available time" value={`${num(r.availableMinutes / 60, 1)} h`} sub={`${r.workingDays} working day(s)`} />
          <Stat label="Estimated capacity" value={`${num(r.capacityPieces)} pcs`} />
          <Stat label="Planned" value={`${num(r.plannedQuantity)} pcs`} sub={r.committedElsewhere > 0 ? `+ ${num(r.committedElsewhere)} other allocations` : undefined} />
          <Stat label="Utilisation" value={pct(r.utilizationPct)} />
          <Stat label="Potential shortage" value={r.shortagePieces ? `${num(r.shortagePieces)} pcs` : "None"} />
        </div>
      )}
      {r.conflicts.length > 0 && (
        <div><p className="font-medium">Conflicting allocations on this line</p>
          <ul className="list-disc pl-4">{r.conflicts.map((c) => <li key={c.allocationId}>{c.orderNumber}: {num(c.quantity)} pcs, {c.start} – {c.end} ({c.overlapDays} overlapping day(s) ≈ {num(c.overlappingQuantity)} pcs)</li>)}</ul></div>
      )}
      {r.machines.length > 0 && <p>Machines the style needs: {r.machines.map((m) => `${m.machineType} — ${m.state === "OK" ? `${m.available} available` : m.state.toLowerCase().replace("_", " ")}`).join("; ")}</p>}
      {r.warnings.map((w) => <p key={w} className="text-[var(--risk-medium)]">{w}</p>)}
      <details>
        <summary className="cursor-pointer font-medium">Assumptions used</summary>
        <ul className="mt-1 list-disc pl-4 text-fg-secondary">{r.assumptions.map((a) => <li key={a.label}><b>{a.label}:</b> {a.value} <span className="text-fg-tertiary">({a.source})</span></li>)}<li className="text-fg-tertiary">{r.formula}</li></ul>
      </details>
    </div>
  );
}

function Stat({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return <div><p className="text-fg-tertiary">{label}</p><p className="text-body font-semibold tabular-nums">{value}</p>{sub && <p className="text-fg-tertiary">{sub}</p>}</div>;
}

function WhatIf({ open, onClose, scope, from, to }: { open: boolean; onClose: () => void; scope: PlanningScope; from: string; to: string }) {
  const styles = useResource<ProductionStyle[]>(open ? planningApi.styles : null);
  const [f, setF] = useState({ lineId: "", styleId: "", quantity: "" });
  const [run, setRun] = useState<string | null>(null);
  const est = useResource<CapacityEstimate>(run);
  return (
    <Modal open={open} onClose={onClose} title="What-if capacity estimate" className="max-w-2xl"
      description="Try a style and quantity on a line over the dates above. Nothing is saved."
      footer={<><Button variant="ghost" onClick={onClose}>Close</Button>
        <Button variant="primary" disabled={!f.lineId || !f.styleId} data-testid="whatif-run"
          onClick={() => setRun(planningApi.capacity({ lineId: f.lineId, styleId: f.styleId, from, to, quantity: f.quantity ? Number(f.quantity) : 0 }))}>Estimate</Button></>}>
      <div className="grid sm:grid-cols-3 gap-3">
        <Field label="Line">{(a) => <Select id={a.id} value={f.lineId} onChange={(e) => setF({ ...f, lineId: e.target.value })} data-testid="whatif-line"><option value="">Choose</option>{scope.ctx.lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}</Select>}</Field>
        <Field label="Style">{(a) => <Select id={a.id} value={f.styleId} onChange={(e) => setF({ ...f, styleId: e.target.value })} data-testid="whatif-style"><option value="">Choose</option>{(styles.data ?? []).filter((s) => s.isActive).map((s) => <option key={s.id} value={s.id}>{s.code}</option>)}</Select>}</Field>
        <Field label="Quantity (pieces)">{(a) => <Input id={a.id} type="number" min={0} value={f.quantity} onChange={(e) => setF({ ...f, quantity: e.target.value })} data-testid="whatif-quantity" />}</Field>
      </div>
      <div className="mt-4">{run && (est.error && !est.data ? <Callout tone="danger">{est.error.message}</Callout> : est.data ? <EstimateCard e={est.data} /> : <SkeletonRows rows={3} />)}</div>
    </Modal>
  );
}
