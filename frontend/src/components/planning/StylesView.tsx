"use client";

// Garment styles: reusable specifications with their size range, colours,
// complexity, quality checkpoints and the operation sequence (standard minutes
// and machine type per operation). Styles differ, so nothing is assumed:
// every field is optional except the code and name, and the machine types a
// style needs come from the operations the planner configures.

import { useEffect, useState } from "react";
import { Plus, Shirt, Trash2 } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { Panel } from "@/components/ui/Panel";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { DataTable } from "@/components/analytics/shared";
import { FormError, ModalFooter, useSubmit, type PlanningScope } from "@/components/planning/shared";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { num, planningApi } from "@/lib/planning";
import type { ProductionStyle } from "@/services/contracts";

const COMPLEXITY: Record<string, string> = { LOW: "Low", MEDIUM: "Medium", HIGH: "High", VERY_HIGH: "Very high" };

export function StylesView({ scope }: { scope: PlanningScope }) {
  const res = useResource<ProductionStyle[]>(planningApi.styles);
  useOnDataChanged(() => void res.reload());
  const [editing, setEditing] = useState<ProductionStyle | "new" | null>(null);
  const canManage = scope.ctx.capabilities.manageStyles;
  const d = res.data;
  return (
    <>
      <div className="mb-4 flex justify-end">
        {canManage && <Button variant="primary" iconLeft={<Plus size={14} />} onClick={() => setEditing("new")} data-testid="style-new">New style</Button>}
      </div>
      {res.error && !d ? <ErrorState title="Could not load styles" body={res.error.message} retryLabel="Try again" onRetry={() => void res.reload()} />
        : !d ? <SkeletonRows rows={5} />
        : d.length === 0 ? <div className="surface"><EmptyState icon={<Shirt size={20} />} title="No garment styles yet" body={canManage ? "Add a style with its operation sequence, standard minutes and machine types." : "Styles are configured by planners."} /></div>
        : (
          <Panel flush ariaLabel="Garment styles">
            <DataTable testId="styles-table" columns={["Style", "Category", "Complexity", "Sizes", "Colours", "SMV", "Operations", "Machine types needed", "Status"]}
              rows={d.map((s) => [
                <button key="c" type="button" className="text-left text-accent hover:underline" onClick={() => setEditing(s)} data-testid="style-row"><span className="font-mono text-[12px]">{s.code}</span><span className="block text-fg-secondary">{s.name}</span></button>,
                s.garmentCategory ?? "—", s.complexity ? COMPLEXITY[s.complexity] : "Not assessed", (s.sizeRange ?? []).join(", ") || "—", (s.colorVariants ?? []).join(", ") || "—",
                s.effectiveSmv == null ? <Badge key="m" tone="warning">missing</Badge> : <span key="m">{num(s.effectiveSmv, 2)} min<span className="block text-fg-tertiary">{s.smvBasis === "approved" ? "approved" : "from operations"}</span></span>,
                s.operations.length, (s.requiredMachineTypes ?? []).join(", ") || "—", s.isActive ? <Badge key="a" tone="success">Active</Badge> : <Badge key="a">Inactive</Badge>
              ])} />
          </Panel>
        )}
      <StyleEditor open={editing !== null} onClose={() => setEditing(null)} style={editing && editing !== "new" ? editing : null} canManage={canManage} />
    </>
  );
}

type OpRow = { id?: string; sequence: string; name: string; smv: string; machineType: string; predecessors: string };

function StyleEditor({ open, onClose, style, canManage }: { open: boolean; onClose: () => void; style: ProductionStyle | null; canManage: boolean }) {
  const toast = useToast();
  const s = useSubmit();
  const blank = { code: "", name: "", garmentCategory: "", description: "", sizeRange: "", colorVariants: "", complexity: "", approvedSmv: "", qualityCheckpoints: "", isActive: true };
  const [f, setF] = useState(blank);
  const [ops, setOps] = useState<OpRow[]>([]);
  useEffect(() => {
    if (!open) return;
    s.reset();
    setF(style ? {
      code: style.code, name: style.name, garmentCategory: style.garmentCategory ?? "", description: style.description ?? "", sizeRange: (style.sizeRange ?? []).join(", "),
      colorVariants: (style.colorVariants ?? []).join(", "), complexity: style.complexity ?? "", approvedSmv: style.approvedSmv == null ? "" : String(style.approvedSmv),
      qualityCheckpoints: (style.qualityCheckpoints ?? []).join("\n"), isActive: style.isActive
    } : blank);
    setOps(style ? style.operations.map((o) => ({ id: o.id, sequence: String(o.sequence), name: o.name, smv: String(o.smv), machineType: o.machineType ?? "", predecessors: o.predecessors.join(", ") })) : []);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, style]);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));
  const list = (v: string, sep: RegExp) => v.split(sep).map((x) => x.trim()).filter(Boolean);
  const setOp = (i: number, k: keyof OpRow, v: string) => setOps((o) => o.map((x, j) => (j === i ? { ...x, [k]: v } : x)));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const body: Record<string, unknown> = {
      code: f.code.trim(), name: f.name.trim(), garmentCategory: f.garmentCategory, description: f.description, sizeRange: list(f.sizeRange, /[,\n]/), colorVariants: list(f.colorVariants, /[,\n]/),
      complexity: f.complexity, qualityCheckpoints: list(f.qualityCheckpoints, /\n/), isActive: f.isActive,
      operations: ops.map((o) => ({ id: o.id, sequence: Number(o.sequence), name: o.name.trim(), smv: Number(o.smv), machineType: o.machineType.trim() || undefined, predecessors: list(o.predecessors, /[,\s]+/).map(Number) })),
      ...(f.approvedSmv ? { approvedSmv: Number(f.approvedSmv) } : { clearApprovedSmv: Boolean(style?.approvedSmv) })
    };
    const saved = await s.run(() => style ? planningApi.updateStyle(style.id, { ...body, expectedVersion: style.version }) : planningApi.createStyle(body));
    if (!saved) return;
    toast.push({ title: style ? "Style updated" : `Style ${saved.code} created` });
    announceDataChanged();
    onClose();
  }

  const smvTotal = ops.reduce((n, o) => n + (Number(o.smv) || 0), 0);
  return (
    <Modal open={open} onClose={onClose} locked={s.busy} title={style ? `Style ${style.code}` : "New garment style"} className="max-w-4xl"
      description="Specification and operation sequence. The style's SMV is the approved value, or the sum of its operations when none is approved."
      testId="style-form" footer={canManage ? <ModalFooter onCancel={onClose} busy={s.busy} label={style ? "Save" : "Create style"} formId="planning-style-form" testId="style-save" /> : <Button variant="ghost" onClick={onClose}>Close</Button>}>
      <form id="planning-style-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        {!canManage && <Callout tone="info">You can read styles; planners with the style permission change them.</Callout>}
        <fieldset disabled={!canManage || s.busy} className="space-y-3">
          <div className="grid sm:grid-cols-3 gap-3">
            <Field label="Style code" error={s.fe("code")}>{(a) => <Input id={a.id} value={f.code} onChange={set("code")} maxLength={40} required data-testid="style-code" />}</Field>
            <Field label="Style name" error={s.fe("name")}>{(a) => <Input id={a.id} value={f.name} onChange={set("name")} maxLength={120} required data-testid="style-name" />}</Field>
            <Field label="Garment category" error={s.fe("garmentCategory")}>{(a) => <Input id={a.id} value={f.garmentCategory} onChange={set("garmentCategory")} maxLength={64} placeholder="e.g. Woven shirts" />}</Field>
            <Field label="Size range" error={s.fe("sizeRange")} hint="Comma-separated, as the buyer names them.">{(a) => <Input id={a.id} value={f.sizeRange} onChange={set("sizeRange")} placeholder="S, M, L, XL" />}</Field>
            <Field label="Colour variants" error={s.fe("colorVariants")}>{(a) => <Input id={a.id} value={f.colorVariants} onChange={set("colorVariants")} placeholder="White, Navy" />}</Field>
            <Field label="Production complexity" error={s.fe("complexity")} hint="Your estimate; leave blank if not assessed.">
              {(a) => <Select id={a.id} value={f.complexity} onChange={set("complexity")}><option value="">Not assessed</option>{Object.entries(COMPLEXITY).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</Select>}
            </Field>
            <Field label="Approved SMV (minutes)" error={s.fe("approvedSmv")} hint={`Without it the operations' total (${num(smvTotal, 2)} min) is used.`}>{(a) => <Input id={a.id} type="number" step="0.01" min="0" value={f.approvedSmv} onChange={set("approvedSmv")} />}</Field>
            <Field label="Active">{() => <label className="flex items-center gap-2 text-caption h-9"><input type="checkbox" checked={f.isActive} onChange={(e) => setF((x) => ({ ...x, isActive: e.target.checked }))} /> Available for new orders</label>}</Field>
          </div>
          <Field label="Description" error={s.fe("description")}>{(a) => <TextArea id={a.id} rows={2} value={f.description} onChange={set("description")} maxLength={1000} />}</Field>
          <Field label="Quality checkpoints (one per line, in order)" error={s.fe("qualityCheckpoints")}>{(a) => <TextArea id={a.id} rows={3} value={f.qualityCheckpoints} onChange={set("qualityCheckpoints")} placeholder={"After collar attach\nFinal measurement"} />}</Field>

          <div>
            <div className="flex items-center justify-between mb-1">
              <p className="field-label">Operation sequence</p>
              <Button size="sm" variant="secondary" iconLeft={<Plus size={12} />} data-testid="style-add-op"
                onClick={() => setOps((o) => [...o, { sequence: String((Math.max(0, ...o.map((x) => Number(x.sequence) || 0)) || 0) + 10), name: "", smv: "", machineType: "", predecessors: "" }])}>Add operation</Button>
            </div>
            {s.fe("operations") && <p className="field-error" role="alert">{s.fe("operations")}</p>}
            {ops.length === 0 ? <p className="text-caption text-fg-tertiary">No operations. Without them (and without an approved SMV) capacity can&apos;t be estimated for this style.</p> : (
              <div className="overflow-x-auto">
                <table className="w-full text-caption" data-testid="style-ops">
                  <thead><tr className="text-left text-fg-tertiary"><th className="px-1 py-1">No.</th><th>Operation</th><th>SMV (min)</th><th>Machine type</th><th>Needs (op. numbers)</th><th /></tr></thead>
                  <tbody>
                    {ops.map((o, i) => (
                      <tr key={o.id ?? i}>
                        <td className="pr-1"><Input aria-label="Operation number" className="h-8 w-16" type="number" value={o.sequence} onChange={(e) => setOp(i, "sequence", e.target.value)} /></td>
                        <td className="pr-1"><Input aria-label="Operation name" className="h-8" value={o.name} onChange={(e) => setOp(i, "name", e.target.value)} maxLength={120} /></td>
                        <td className="pr-1"><Input aria-label="Operation SMV" className="h-8 w-24" type="number" step="0.01" min="0" value={o.smv} onChange={(e) => setOp(i, "smv", e.target.value)} /></td>
                        <td className="pr-1"><Input aria-label="Machine type" className="h-8 w-28" value={o.machineType} onChange={(e) => setOp(i, "machineType", e.target.value)} maxLength={32} placeholder="SNLS, OL, manual" /></td>
                        <td className="pr-1"><Input aria-label="Predecessors" className="h-8 w-28" value={o.predecessors} onChange={(e) => setOp(i, "predecessors", e.target.value)} placeholder="10, 20" /></td>
                        <td><Button size="sm" variant="ghost" aria-label="Remove operation" onClick={() => setOps((x) => x.filter((_, j) => j !== i))}><Trash2 size={12} /></Button></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </fieldset>
      </form>
    </Modal>
  );
}
