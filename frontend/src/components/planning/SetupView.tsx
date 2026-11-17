"use client";

// Planning setup: the two things planning must not guess. Which counter
// devices measure finished garments leaving a line (a line has many machine
// counters that count different operations), and each line's capacity
// assumptions (efficiency and operators). Until they are set, output stays
// "no data" and capacity stays "incomplete".

import { useEffect, useState } from "react";
import { Cpu, Trash2 } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { Panel } from "@/components/ui/Panel";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { DataTable } from "@/components/analytics/shared";
import { FormError, ModalFooter, useSubmit, type PlanningScope } from "@/components/planning/shared";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { num, planningApi, type CapacitySettings, type OutputPoint, type PlanningLine } from "@/lib/planning";

type Device = { id: string; code: string; name: string; category: string; lineId?: string | null; isSimulated: boolean };

export function SetupView({ scope }: { scope: PlanningScope }) {
  const { ctx, factoryId } = scope;
  const toast = useToast();
  const points = useResource<OutputPoint[]>(planningApi.outputPoints(factoryId));
  useOnDataChanged(() => void points.reload());
  const [adding, setAdding] = useState<PlanningLine | null>(null);
  const [editing, setEditing] = useState<PlanningLine | null>(null);
  const canSetup = ctx.capabilities.setup;

  async function remove(p: OutputPoint) {
    try { await planningApi.removeOutputPoint(p.id); announceDataChanged(); toast.push({ title: "Output counter removed" }); void points.reload(); }
    catch (e) { toast.push({ tone: "error", title: "Not removed", description: (e as Error).message }); }
  }

  return (
    <div className="space-y-4">
      <Callout tone="info">A line&apos;s verified output is counted only by the counter devices you designate here. Machine counters along the line count operations, so adding them up would overstate finished garments. Capacity needs the line&apos;s efficiency and operators; nothing is assumed.</Callout>
      {!canSetup && <Callout tone="warning">Setup is changed by a factory manager or administrator; you can read it.</Callout>}
      <Panel title="Lines" subtitle="Output counters and capacity assumptions" icon={<Cpu size={15} />} flush testId="setup-lines">
        {ctx.lines.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No production line in this factory (or in your scope).</p> : (
          <DataTable testId="setup-table" columns={["Line", "Floor", "Output counters (verified output)", "Efficiency", "Planned operators", ""]}
            rows={ctx.lines.map((l) => [
              l.name + (l.isActive ? "" : " (inactive)"), l.floorName ?? "—",
              <span key="p" className="flex flex-wrap items-center gap-1">
                {(points.data ?? []).filter((p) => p.lineId === l.id).map((p) => (
                  <Badge key={p.id} tone={p.simulated ? "accent" : "success"} title={`${p.deviceName}${p.simulated ? " (simulated)" : ""} · ${p.connectionStatus}`}>
                    {p.deviceCode}{canSetup && <button type="button" aria-label={`Remove ${p.deviceCode}`} className="ml-1" onClick={() => void remove(p)}><Trash2 size={11} /></button>}</Badge>
                ))}
                {(points.data ?? []).filter((p) => p.lineId === l.id).length === 0 && <span className="text-[var(--risk-medium)]">none — output can&apos;t be verified</span>}
                {canSetup && <Button size="sm" variant="ghost" onClick={() => setAdding(l)} data-testid="setup-add-counter">Add counter</Button>}
              </span>,
              l.efficiencyPct == null ? <span key="e" className="text-[var(--risk-medium)]">not set</span> : `${num(l.efficiencyPct, 1)} %`,
              l.plannedOperators == null ? <span key="o" className="text-fg-tertiary">from assignments</span> : l.plannedOperators,
              canSetup ? <Button key="x" size="sm" variant="ghost" onClick={() => setEditing(l)} data-testid="setup-edit">Capacity assumptions</Button> : ""
            ])} />
        )}
      </Panel>
      <AddCounter line={adding} onClose={() => setAdding(null)} factoryId={factoryId} onSaved={() => void points.reload()} />
      <CapacityForm line={editing} onClose={() => setEditing(null)} />
    </div>
  );
}

function AddCounter({ line, onClose, factoryId, onSaved }: { line: PlanningLine | null; onClose: () => void; factoryId: string; onSaved: () => void }) {
  const s = useSubmit();
  const toast = useToast();
  const devices = useResource<{ items: Device[] }>(line ? `/api/devices?factoryId=${factoryId}&category=ProductionCounter&pageSize=200` : null);
  const [deviceId, setDeviceId] = useState("");
  const [note, setNote] = useState("");
  useEffect(() => { if (line) { s.reset(); setDeviceId(""); setNote(""); } }, [line]); // eslint-disable-line react-hooks/exhaustive-deps
  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const r = await s.run(() => planningApi.addOutputPoint({ lineId: line!.id, deviceId, note: note || undefined }));
    if (!r) return;
    toast.push({ title: `${r.deviceCode} now measures ${r.lineName}'s output` });
    announceDataChanged(); onSaved(); onClose();
  }
  return (
    <Modal open={line !== null} onClose={onClose} locked={s.busy} title={`Output counter for ${line?.name ?? ""}`} description="Choose the production counter that counts finished garments leaving this line. A counter that counts machine cycles can't be used."
      footer={<ModalFooter onCancel={onClose} busy={s.busy} label="Designate" formId="planning-counter-form" disabled={!deviceId} testId="counter-save" />} testId="counter-form">
      <form id="planning-counter-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        <Field label="Counter device" error={s.fe("deviceId")}>
          {(a) => <Select id={a.id} value={deviceId} onChange={(e) => setDeviceId(e.target.value)} required data-testid="counter-device">
            <option value="">Choose a counter</option>{(devices.data?.items ?? []).map((d) => <option key={d.id} value={d.id}>{d.code} · {d.name}{d.isSimulated ? " (simulated)" : ""}</option>)}
          </Select>}
        </Field>
        <Field label="Note (optional)">{(a) => <Input id={a.id} value={note} onChange={(e) => setNote(e.target.value)} maxLength={300} placeholder="e.g. end-of-line packing table" />}</Field>
      </form>
    </Modal>
  );
}

function CapacityForm({ line, onClose }: { line: PlanningLine | null; onClose: () => void }) {
  const s = useSubmit();
  const toast = useToast();
  const cur = useResource<CapacitySettings>(line ? `/api/planning/lines/${line.id}/capacity-settings` : null);
  const [f, setF] = useState({ eff: "", ops: "", notes: "" });
  useEffect(() => { if (line) s.reset(); }, [line]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (cur.data) setF({ eff: cur.data.efficiencyPct == null ? "" : String(cur.data.efficiencyPct), ops: cur.data.plannedOperators == null ? "" : String(cur.data.plannedOperators), notes: cur.data.notes ?? "" }); }, [cur.data]);
  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const r = await s.run(() => planningApi.saveCapacitySettings(line!.id, {
      efficiencyPct: f.eff ? Number(f.eff) : undefined, clearEfficiency: !f.eff || undefined, plannedOperators: f.ops ? Number(f.ops) : undefined, clearOperators: !f.ops || undefined,
      notes: f.notes, expectedVersion: cur.data && cur.data.version > 0 ? cur.data.version : undefined
    }));
    if (!r) return;
    toast.push({ title: "Capacity assumptions saved" });
    announceDataChanged(); onClose();
  }
  return (
    <Modal open={line !== null} onClose={onClose} locked={s.busy} title={`Capacity assumptions: ${line?.name ?? ""}`} testId="capacity-form"
      description="Your own planning assumptions for this line. Leave a field empty to leave it unconfigured; capacity stays incomplete until efficiency (and operators) are known."
      footer={<ModalFooter onCancel={onClose} busy={s.busy} label="Save" formId="planning-capacity-form" testId="capacity-save" />}>
      <form id="planning-capacity-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        {!cur.data ? <SkeletonRows rows={2} /> : (
          <>
            <div className="grid sm:grid-cols-2 gap-3">
              <Field label="Line efficiency (%)" error={s.fe("efficiencyPct")} hint="Output × SMV ÷ operator minutes the plant plans with.">{(a) => <Input id={a.id} type="number" step="0.1" min="1" max="150" value={f.eff} onChange={(e) => setF({ ...f, eff: e.target.value })} data-testid="capacity-eff" />}</Field>
              <Field label="Planned operators" error={s.fe("plannedOperators")} hint="Empty: use the workers assigned to the line's workstations.">{(a) => <Input id={a.id} type="number" min="1" max="500" value={f.ops} onChange={(e) => setF({ ...f, ops: e.target.value })} data-testid="capacity-ops" />}</Field>
            </div>
            <Field label="Notes">{(a) => <Input id={a.id} value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} maxLength={1000} />}</Field>
            {cur.data.updatedByEmail && <p className="text-caption text-fg-tertiary">Last changed by {cur.data.updatedByEmail}.</p>}
          </>
        )}
      </form>
    </Modal>
  );
}
