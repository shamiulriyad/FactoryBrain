"use client";

// Plant configuration: lines, the shift calendar and styles with their
// operation bulletins (the factory-approved standard minutes). Everyone can
// read it; only Admins change it, and the API enforces that.

import { useEffect, useState } from "react";
import { Pencil, Plus, Trash2 } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { announceDataChanged } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { fmt } from "@/lib/production";
import { createLine, createShift, createStyle, updateLine, updateShift, updateStyle, type StyleOperationBody } from "@/services/production.api";
import { WEEKDAYS, type ProductionConfig, type ProductionLine, type ProductionShift, type ProductionStyle } from "@/services/contracts";
import { KeyValue, useMasterData } from "./shared";

export function SetupPanel({ config, admin }: { config: ProductionConfig | null; admin: boolean }) {
  const md = useMasterData(true);
  const [line, setLine] = useState<ProductionLine | "new" | null>(null);
  const [shift, setShift] = useState<ProductionShift | "new" | null>(null);
  const [style, setStyle] = useState<ProductionStyle | "new" | null>(null);
  const saved = () => { void md.reload(); announceDataChanged(); };

  return (
    <div className="space-y-4" data-testid="production-setup">
      {!admin && <Callout tone="info">Only an Admin can change the plant configuration. You can read it here.</Callout>}
      {config && (
        <Panel title="Plant settings" subtitle="Read once from the server environment (PRODUCTION_* variables).">
          {config.configurationError && <Callout tone="warning" className="mb-3">{config.configurationError}</Callout>}
          <dl className="grid grid-cols-2 md:grid-cols-4 gap-3">
            <KeyValue label="Time zone">{config.timeZone} ({config.utcOffsetNow})</KeyValue>
            <KeyValue label="Separate approver">{config.requireSeparateApprover ? "Required" : "Not required"}</KeyValue>
            <KeyValue label="Backfill window">{config.maxBackfillDays} days</KeyValue>
            <KeyValue label="Longest interval">{config.maxIntervalHours} h</KeyValue>
            <KeyValue label="Observed-rate window">{config.rateLookbackDays} production days</KeyValue>
            <KeyValue label="Medium-risk slack">{config.riskSlackPct} %</KeyValue>
            <KeyValue label="Time studies valid for">{config.observationMaxAgeDays} days</KeyValue>
          </dl>
          <h3 className="mt-4 text-body font-semibold text-fg-primary">Data sources</h3>
          <ul className="mt-2 space-y-1.5" data-testid="production-sources">
            {config.dataSources.map((d) => (
              <li key={d.source} className="text-caption text-fg-secondary">
                <Badge tone={d.status === "not_connected" ? "neutral" : d.status === "receiving" ? "success" : "info"}>{d.source}: {d.status.replace("_", " ")}</Badge>{" "}
                {d.description} <span className="text-fg-tertiary">({d.recordsLast24h} records in 24 h)</span>
              </li>
            ))}
          </ul>
        </Panel>
      )}

      <Panel title="Lines" right={admin && <Button size="sm" variant="primary" iconLeft={<Plus size={13} />} onClick={() => setLine("new")} data-testid="line-new">Add line</Button>} flush>
        {md.lines.loading ? <div className="p-4"><SkeletonRows rows={2} /></div> : (md.lines.data ?? []).length === 0 ? (
          <p className="p-4 text-caption text-fg-tertiary">No lines yet.</p>
        ) : (
          <table className="data-table" data-testid="lines-table">
            <thead><tr><th>Id</th><th>Name</th><th className="num">Workstations</th><th>Status</th><th></th></tr></thead>
            <tbody>
              {md.lines.data!.map((l) => (
                <tr key={l.id}>
                  <td className="font-mono text-[12px]">{l.id}</td><td>{l.name}</td><td className="num">{l.workstations}</td>
                  <td>{l.isActive ? <Badge tone="success">active</Badge> : <Badge>inactive</Badge>}</td>
                  <td>{admin && <Button size="sm" variant="ghost" onClick={() => setLine(l)} aria-label={`Edit ${l.name}`}><Pencil size={13} /></Button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Panel>

      <Panel title="Shift calendar" subtitle={`Plant-local times (${config?.timeZone ?? "…"}). A shift ending at or before its start runs overnight and belongs to the day it starts.`}
        right={admin && <Button size="sm" variant="primary" iconLeft={<Plus size={13} />} onClick={() => setShift("new")} data-testid="shift-new">Add shift</Button>} flush>
        {md.shifts.loading ? <div className="p-4"><SkeletonRows rows={2} /></div> : (md.shifts.data ?? []).length === 0 ? (
          <p className="p-4 text-caption text-fg-tertiary">No shifts yet: plans have no working time until one is added.</p>
        ) : (
          <table className="data-table" data-testid="shifts-table">
            <thead><tr><th>Code</th><th>Name</th><th>Time</th><th>Break</th><th className="num">Working min</th><th>Days</th><th>Status</th><th></th></tr></thead>
            <tbody>
              {md.shifts.data!.map((s) => (
                <tr key={s.code}>
                  <td className="font-mono text-[12px]">{s.code}</td><td>{s.name}</td>
                  <td className="whitespace-nowrap">{s.start}–{s.end}{s.overnight && <Badge tone="info" className="ml-1">overnight</Badge>}</td>
                  <td>{s.breakMinutes > 0 ? `${s.breakStart} · ${s.breakMinutes} min` : "—"}</td>
                  <td className="num">{fmt(s.workingMinutes)}</td>
                  <td className="text-caption">{s.weekdays.join(" ")}</td>
                  <td>{s.isActive ? <Badge tone="success">active</Badge> : <Badge>inactive</Badge>}</td>
                  <td>{admin && <Button size="sm" variant="ghost" onClick={() => setShift(s)} aria-label={`Edit ${s.name}`}><Pencil size={13} /></Button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Panel>

      <Panel title="Styles and operation bulletins" subtitle="The approved SMV drives targets and efficiency; the bulletin (operations, SMV, machine type, predecessors) drives line balancing."
        right={admin && <Button size="sm" variant="primary" iconLeft={<Plus size={13} />} onClick={() => setStyle("new")} data-testid="style-new">Add style</Button>} flush>
        {md.styles.loading ? <div className="p-4"><SkeletonRows rows={2} /></div> : (md.styles.data ?? []).length === 0 ? (
          <p className="p-4 text-caption text-fg-tertiary">No styles yet.</p>
        ) : (
          <table className="data-table" data-testid="styles-table">
            <thead><tr><th>Code</th><th>Name</th><th className="num">SMV used</th><th>Basis</th><th className="num">Operations</th><th>Status</th><th></th></tr></thead>
            <tbody>
              {md.styles.data!.map((s) => (
                <tr key={s.id}>
                  <td className="font-mono text-[12px]">{s.code}</td><td>{s.name}<span className="block text-caption text-fg-tertiary">{s.productType}</span></td>
                  <td className="num">{s.effectiveSmv == null ? "—" : fmt(s.effectiveSmv, 2)}</td>
                  <td>{s.smvBasis === "approved" ? "approved" : s.smvBasis === "operation_bulletin" ? <Badge tone="warning">bulletin total</Badge> : <Badge tone="danger">missing</Badge>}</td>
                  <td className="num">{s.operations.length}<span className="block text-caption text-fg-tertiary">Σ {fmt(s.operationSmvTotal, 2)} min</span></td>
                  <td>{s.isActive ? <Badge tone="success">active</Badge> : <Badge>inactive</Badge>}</td>
                  <td>{admin && <Button size="sm" variant="ghost" onClick={() => setStyle(s)} aria-label={`Edit ${s.code}`}><Pencil size={13} /></Button>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Panel>

      <LineModal value={line} onClose={() => setLine(null)} onSaved={saved} />
      <ShiftModal value={shift} onClose={() => setShift(null)} onSaved={saved} />
      <StyleModal value={style} onClose={() => setStyle(null)} onSaved={saved} />
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
    try {
      await fn();
      toast.push({ title });
      after();
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
    }
  }
  const fe = (k: string) => (isApiError(error) ? error.fieldErrors[k]?.join(" ") ?? null : null);
  return { busy, error, run, fe, clear: () => setError(null) };
}

function ErrorBox({ error }: { error: unknown }) {
  return error ? <Callout tone="danger" title="Not saved" testId="form-error">{errorMessage(error)}</Callout> : null;
}

function SaveFooter({ onClose, busy, testId }: { onClose: () => void; busy: boolean; testId: string }) {
  return (
    <>
      <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
      <Button variant="primary" type="submit" form="setup-form" disabled={busy} data-testid={testId}>{busy ? "Saving…" : "Save"}</Button>
    </>
  );
}

function LineModal({ value, onClose, onSaved }: { value: ProductionLine | "new" | null; onClose: () => void; onSaved: () => void }) {
  const s = useSave();
  const editing = value && value !== "new" ? value : null;
  const [f, setF] = useState({ id: "", name: "", workstations: "", isActive: true });
  useEffect(() => {
    if (!value) return;
    s.clear();
    setF(editing ? { id: editing.id, name: editing.name, workstations: String(editing.workstations), isActive: editing.isActive } : { id: "", name: "", workstations: "", isActive: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);
  return (
    <Modal open={!!value} onClose={onClose} locked={s.busy} title={editing ? `Edit ${editing.name}` : "Add a line"} testId="line-form"
      footer={<SaveFooter onClose={onClose} busy={s.busy} testId="line-save" />}>
      <form id="setup-form" className="space-y-3" onSubmit={(e) => {
        e.preventDefault();
        void s.run(() => editing
          ? updateLine(editing.id, { name: f.name, workstations: Number(f.workstations), isActive: f.isActive, expectedVersion: editing.version })
          : createLine({ id: f.id.trim(), name: f.name.trim(), workstations: Number(f.workstations) }), editing ? "Line updated" : "Line added", () => { onSaved(); onClose(); });
      }}>
        <ErrorBox error={s.error} />
        {!editing && (
          <Field label="Id" hint="Lowercase slug, e.g. line-3. QC inspections use the same id." error={s.fe("id")}>
            {(a) => <Input id={a.id} aria-describedby={a.describedBy} value={f.id} onChange={(e) => setF({ ...f, id: e.target.value })} required maxLength={32} data-testid="line-id" />}
          </Field>
        )}
        <Field label="Name" error={s.fe("name")}>{(a) => <Input id={a.id} value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} required maxLength={80} data-testid="line-name" />}</Field>
        <Field label="Declared workstations" hint="The capacity a line balance may use." error={s.fe("workstations")}>
          {(a) => <Input id={a.id} aria-describedby={a.describedBy} type="number" min={1} max={300} value={f.workstations} onChange={(e) => setF({ ...f, workstations: e.target.value })} required data-testid="line-workstations" />}
        </Field>
        {editing && <label className="flex items-center gap-2 text-body"><input type="checkbox" checked={f.isActive} onChange={(e) => setF({ ...f, isActive: e.target.checked })} /> Active</label>}
      </form>
    </Modal>
  );
}

function ShiftModal({ value, onClose, onSaved }: { value: ProductionShift | "new" | null; onClose: () => void; onSaved: () => void }) {
  const s = useSave();
  const editing = value && value !== "new" ? value : null;
  const [f, setF] = useState({ code: "", name: "", start: "08:00", end: "17:00", breakStart: "", breakMinutes: "0", weekdays: ["sat", "sun", "mon", "tue", "wed", "thu"] as string[], isActive: true });
  useEffect(() => {
    if (!value) return;
    s.clear();
    if (editing) setF({ code: editing.code, name: editing.name, start: editing.start, end: editing.end, breakStart: editing.breakStart ?? "", breakMinutes: String(editing.breakMinutes), weekdays: editing.weekdays, isActive: editing.isActive });
    else setF({ code: "", name: "", start: "08:00", end: "17:00", breakStart: "13:00", breakMinutes: "60", weekdays: ["sat", "sun", "mon", "tue", "wed", "thu"], isActive: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);
  const body = { name: f.name.trim(), start: f.start, end: f.end, breakStart: f.breakStart || undefined, breakMinutes: Number(f.breakMinutes || 0), weekdays: f.weekdays, isActive: f.isActive };
  return (
    <Modal open={!!value} onClose={onClose} locked={s.busy} title={editing ? `Edit shift ${editing.name}` : "Add a shift"} testId="shift-form"
      description="Every instant may belong to at most one shift; overlapping shifts are refused."
      footer={<SaveFooter onClose={onClose} busy={s.busy} testId="shift-save" />}>
      <form id="setup-form" className="space-y-3" onSubmit={(e) => {
        e.preventDefault();
        void s.run(() => editing ? updateShift(editing.code, { ...body, expectedVersion: editing.version }) : createShift({ ...body, code: f.code.trim() }),
          editing ? "Shift updated" : "Shift added", () => { onSaved(); onClose(); });
      }}>
        <ErrorBox error={s.error} />
        <div className="grid grid-cols-2 gap-3">
          {!editing && <Field label="Code" error={s.fe("code")}>{(a) => <Input id={a.id} value={f.code} onChange={(e) => setF({ ...f, code: e.target.value })} required maxLength={16} data-testid="shift-code" />}</Field>}
          <Field label="Name" error={s.fe("name")}>{(a) => <Input id={a.id} value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} required maxLength={60} data-testid="shift-name" />}</Field>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <Field label="Start" error={s.fe("start")}>{(a) => <Input id={a.id} type="time" value={f.start} onChange={(e) => setF({ ...f, start: e.target.value })} required data-testid="shift-start" />}</Field>
          <Field label="End" error={s.fe("end")}>{(a) => <Input id={a.id} type="time" value={f.end} onChange={(e) => setF({ ...f, end: e.target.value })} required data-testid="shift-end" />}</Field>
          <Field label="Break start" error={s.fe("breakStart")}>{(a) => <Input id={a.id} type="time" value={f.breakStart} onChange={(e) => setF({ ...f, breakStart: e.target.value })} data-testid="shift-break-start" />}</Field>
          <Field label="Break (min)" error={s.fe("breakMinutes")}>{(a) => <Input id={a.id} type="number" min={0} max={240} value={f.breakMinutes} onChange={(e) => setF({ ...f, breakMinutes: e.target.value })} data-testid="shift-break" />}</Field>
        </div>
        <fieldset>
          <legend className="field-label">Working days</legend>
          <div className="flex flex-wrap gap-3">
            {WEEKDAYS.map((d) => (
              <label key={d} className="flex items-center gap-1.5 text-body">
                <input type="checkbox" checked={f.weekdays.includes(d)} onChange={(e) => setF({ ...f, weekdays: e.target.checked ? [...f.weekdays, d] : f.weekdays.filter((x) => x !== d) })} /> {d}
              </label>
            ))}
          </div>
          {s.fe("weekdays") && <p className="field-error" role="alert">{s.fe("weekdays")}</p>}
        </fieldset>
        {editing && <label className="flex items-center gap-2 text-body"><input type="checkbox" checked={f.isActive} onChange={(e) => setF({ ...f, isActive: e.target.checked })} /> Active</label>}
      </form>
    </Modal>
  );
}

type OpRow = { id?: string; sequence: string; name: string; smv: string; machineType: string; predecessors: string };

function StyleModal({ value, onClose, onSaved }: { value: ProductionStyle | "new" | null; onClose: () => void; onSaved: () => void }) {
  const s = useSave();
  const editing = value && value !== "new" ? value : null;
  const [f, setF] = useState({ code: "", name: "", productType: "", approvedSmv: "", isActive: true });
  const [ops, setOps] = useState<OpRow[]>([]);
  useEffect(() => {
    if (!value) return;
    s.clear();
    setF(editing
      ? { code: editing.code, name: editing.name, productType: editing.productType ?? "", approvedSmv: editing.approvedSmv == null ? "" : String(editing.approvedSmv), isActive: editing.isActive }
      : { code: "", name: "", productType: "", approvedSmv: "", isActive: true });
    setOps((editing?.operations ?? []).map((o) => ({ id: o.id, sequence: String(o.sequence), name: o.name, smv: String(o.smv), machineType: o.machineType ?? "", predecessors: o.predecessors.join(", ") })));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);
  const setOp = (i: number, patch: Partial<OpRow>) => setOps((list) => list.map((o, j) => (j === i ? { ...o, ...patch } : o)));
  const operations: StyleOperationBody[] = ops.map((o) => ({
    id: o.id, sequence: Number(o.sequence), name: o.name.trim(), smv: Number(o.smv), machineType: o.machineType.trim() || undefined,
    predecessors: o.predecessors.split(/[,\s]+/).filter(Boolean).map(Number)
  }));
  const smv = f.approvedSmv.trim() === "" ? null : Number(f.approvedSmv);

  return (
    <Modal open={!!value} onClose={onClose} locked={s.busy} className="sm:max-w-3xl" testId="style-form"
      title={editing ? `Edit style ${editing.code}` : "Add a style"}
      description="Changing the bulletin keeps operation ids (time studies and layouts stay linked); line-balance proposals computed from the old bulletin can no longer be applied."
      footer={<SaveFooter onClose={onClose} busy={s.busy} testId="style-save" />}>
      <form id="setup-form" className="space-y-3" onSubmit={(e) => {
        e.preventDefault();
        void s.run(() => editing
          ? updateStyle(editing.id, { name: f.name, productType: f.productType, approvedSmv: smv ?? undefined, clearApprovedSmv: smv === null, isActive: f.isActive, operations, expectedVersion: editing.version })
          : createStyle({ code: f.code.trim(), name: f.name.trim(), productType: f.productType.trim() || undefined, approvedSmv: smv, operations }),
        editing ? "Style updated" : "Style added", () => { onSaved(); onClose(); });
      }}>
        <ErrorBox error={s.error} />
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          {!editing && <Field label="Code" error={s.fe("code")}>{(a) => <Input id={a.id} value={f.code} onChange={(e) => setF({ ...f, code: e.target.value })} required maxLength={40} data-testid="style-code" />}</Field>}
          <Field label="Name" error={s.fe("name")}>{(a) => <Input id={a.id} value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} required maxLength={120} data-testid="style-name" />}</Field>
          <Field label="Product type" error={s.fe("productType")}>{(a) => <Input id={a.id} value={f.productType} onChange={(e) => setF({ ...f, productType: e.target.value })} maxLength={64} />}</Field>
          <Field label="Approved SMV (min)" hint="Blank: use the bulletin total" error={s.fe("approvedSmv")}>
            {(a) => <Input id={a.id} aria-describedby={a.describedBy} type="number" step="0.001" min={0} value={f.approvedSmv} onChange={(e) => setF({ ...f, approvedSmv: e.target.value })} data-testid="style-smv" />}
          </Field>
        </div>
        {editing && <label className="flex items-center gap-2 text-body"><input type="checkbox" checked={f.isActive} onChange={(e) => setF({ ...f, isActive: e.target.checked })} /> Active</label>}
        <div>
          <div className="flex items-center justify-between">
            <p className="field-label">Operation bulletin</p>
            <Button size="sm" variant="secondary" iconLeft={<Plus size={13} />} data-testid="style-add-op"
              onClick={() => setOps((l) => [...l, { sequence: String((l.reduce((m, o) => Math.max(m, Number(o.sequence) || 0), 0)) + 1), name: "", smv: "", machineType: "", predecessors: "" }])}>
              Add operation
            </Button>
          </div>
          {s.fe("operations") && <Callout tone="danger" className="mt-2">{s.fe("operations")}</Callout>}
          {ops.length === 0 ? <p className="mt-2 text-caption text-fg-tertiary">No operations: targets can still use the approved SMV, but line balancing and bottleneck analysis need a bulletin.</p> : (
            <div className="mt-2 overflow-x-auto">
              <table className="data-table" data-testid="style-ops">
                <thead><tr><th className="num">Seq</th><th>Operation</th><th className="num">SMV</th><th>Machine</th><th>After (seq)</th><th></th></tr></thead>
                <tbody>
                  {ops.map((o, i) => (
                    <tr key={o.id ?? `new-${i}`}>
                      <td><Input aria-label="Sequence" type="number" min={1} className="!w-16" value={o.sequence} onChange={(e) => setOp(i, { sequence: e.target.value })} /></td>
                      <td><Input aria-label="Operation name" value={o.name} onChange={(e) => setOp(i, { name: e.target.value })} maxLength={120} required data-testid="op-name" /></td>
                      <td><Input aria-label="SMV" type="number" step="0.001" min={0} className="!w-24" value={o.smv} onChange={(e) => setOp(i, { smv: e.target.value })} required data-testid="op-smv" /></td>
                      <td><Input aria-label="Machine type" className="!w-24" value={o.machineType} onChange={(e) => setOp(i, { machineType: e.target.value })} maxLength={32} placeholder="SNLS" /></td>
                      <td><Input aria-label="Predecessors" className="!w-28" value={o.predecessors} onChange={(e) => setOp(i, { predecessors: e.target.value })} placeholder="1, 2" data-testid="op-preds" /></td>
                      <td><Button size="sm" variant="ghost" aria-label="Remove operation" onClick={() => setOps((l) => l.filter((_, j) => j !== i))}><Trash2 size={13} /></Button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </form>
    </Modal>
  );
}
