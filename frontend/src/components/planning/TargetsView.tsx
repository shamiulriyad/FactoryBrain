"use client";

// Production targets at factory, department, floor, line, shift, workstation
// and machine level (daily, per shift or hourly), optionally for one order or
// allocation. A change never overwrites: it adds a version that takes over on
// its effective date, and every version keeps who changed it and why.
// Factory-, department- and floor-level targets wait for a second person.

import { useEffect, useState } from "react";
import { History, Plus, Target as TargetIcon } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { Field, FilterSelect, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { Panel } from "@/components/ui/Panel";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { DataTable } from "@/components/analytics/shared";
import { ApprovalBadge, BasisBadge, FormError, ModalFooter, ReasonDialog, useCanPlan, useSubmit, type PlanningScope } from "@/components/planning/shared";
import { dhaka } from "@/lib/monitoring";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import {
  TARGET_LEVELS, TARGET_LEVEL_TEXT, TARGET_PERIODS, TARGET_PERIOD_TEXT, addDays, num, pct, planningApi,
  type Paged, type Target, type TargetDetail, type TargetLevel
} from "@/lib/planning";
import type { ProductionOrderListItem } from "@/services/contracts";

export function TargetsView({ scope }: { scope: PlanningScope }) {
  const { q, ctx, factoryId } = scope;
  const can = useCanPlan();
  const level = q.get("level");
  const line = q.get("line");
  const date = q.get("date");
  const showHistory = q.get("history") === "1";
  const [form, setForm] = useState<{ revise?: Target } | null>(null);
  const [detail, setDetail] = useState<string | null>(null);
  const [decide, setDecide] = useState<{ t: Target; approve: boolean } | null>(null);
  const [ending, setEnding] = useState<Target | null>(null);
  const res = useResource<Paged<Target>>(planningApi.targets({ factoryId, level, lineId: line, date, includeHistory: showHistory, withPerformance: Boolean(date), pageSize: 100 }));
  useOnDataChanged(() => void res.reload());
  const d = res.data;
  const canManage = ctx.capabilities.manageTargets;
  const canApprove = can.approve;

  return (
    <>
      <div className="surface p-3 mb-4 flex flex-wrap items-end gap-3">
        <FilterSelect label="Level" value={level} onChange={(v) => q.set({ level: v || null })} testId="targets-level">
          <option value="">All levels</option>{TARGET_LEVELS.map((l) => <option key={l} value={l}>{TARGET_LEVEL_TEXT[l]}</option>)}
        </FilterSelect>
        <FilterSelect label="Line" value={line} onChange={(v) => q.set({ line: v || null })}>
          <option value="">All</option>{ctx.lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </FilterSelect>
        <label className="text-caption text-fg-secondary flex flex-col gap-1">In force on (and performance)
          <Input type="date" className="h-9" value={date} onChange={(e) => q.set({ date: e.target.value || null })} data-testid="targets-date" /></label>
        <label className="text-caption text-fg-secondary flex items-center gap-2 h-9"><input type="checkbox" checked={showHistory} onChange={(e) => q.set({ history: e.target.checked ? "1" : null })} /> Show earlier versions</label>
        {canManage && <Button className="ml-auto" variant="primary" iconLeft={<Plus size={14} />} onClick={() => setForm({})} data-testid="target-new">New target</Button>}
      </div>
      {res.error && !d ? <ErrorState title="Could not load targets" body={res.error.message} retryLabel="Try again" onRetry={() => void res.reload()} />
        : !d ? <SkeletonRows rows={6} />
        : d.items.length === 0 ? <div className="surface"><EmptyState icon={<TargetIcon size={20} />} title="No targets" body={canManage ? "Set a daily, shift or hourly target for a line, floor, department or the factory." : "Targets set by planners appear here."} /></div>
        : (
          <Panel flush ariaLabel="Production targets">
            <DataTable testId="targets-table" columns={["Scope", "Target", "Version", "Effective", ...(date ? ["Actual that day", "Achievement"] : []), "Approval", ""]}
              rows={d.items.map((t) => [
                <span key="s"><b>{TARGET_LEVEL_TEXT[t.level]}</b> {scopeText(t)}{t.orderNumber && <span className="block text-fg-tertiary">order {t.orderNumber}</span>}{t.shiftName && t.level !== "SHIFT" && <span className="block text-fg-tertiary">{t.shiftName} shift</span>}</span>,
                <span key="q" className="tabular-nums">{num(t.quantity)} <span className="text-fg-tertiary">{TARGET_PERIOD_TEXT[t.period]}</span></span>,
                <button key="v" type="button" className="text-accent hover:underline" onClick={() => setDetail(t.id)} data-testid="target-history">v{t.revision}{t.isSuperseded ? " (replaced)" : ""}</button>,
                `${t.effectiveFrom} → ${t.effectiveTo ?? "open"}`,
                ...(date ? [t.performance?.actual == null ? <span key="a" title={t.performance?.note} className="text-fg-tertiary">No data</span> : <span key="a" title={t.performance.note}>{num(t.performance.actual)} <BasisBadge basis={t.performance.basis === "COUNTER" ? "COUNTER" : "RECORDED"} /></span>, pct(t.performance?.achievementPct)] : []),
                <ApprovalBadge key="p" t={t} />,
                <span key="x" className="flex gap-1 justify-end">
                  {canApprove && t.approvalStatus === "PENDING" && (<><Button size="sm" variant="ghost" onClick={() => setDecide({ t, approve: true })} data-testid="target-approve">Approve</Button><Button size="sm" variant="ghost" onClick={() => setDecide({ t, approve: false })}>Reject</Button></>)}
                  {canManage && !t.isSuperseded && (t.approvalStatus === "NOT_REQUIRED" || t.approvalStatus === "APPROVED") && (
                    <><Button size="sm" variant="ghost" onClick={() => setForm({ revise: t })} data-testid="target-revise">Revise</Button><Button size="sm" variant="ghost" onClick={() => setEnding(t)}>End</Button></>
                  )}
                </span>
              ])} />
          </Panel>
        )}
      <p className="mt-2 text-caption text-fg-tertiary">When several targets cover a line and shift, the most specific wins: allocation, then order, then shift-specific, then the general line target. Actual comes from the line&apos;s designated output counters.</p>
      <TargetFormModal open={form !== null} onClose={() => setForm(null)} scope={scope} revise={form?.revise} />
      <TargetHistory id={detail} onClose={() => setDetail(null)} />
      <ReasonDialog open={decide !== null} onClose={() => setDecide(null)} required={decide ? !decide.approve : false} title={decide?.approve ? "Approve this target" : "Reject this target"}
        description={decide ? `${TARGET_LEVEL_TEXT[decide.t.level]} target ${num(decide.t.quantity)} ${TARGET_PERIOD_TEXT[decide.t.period]}, from ${decide.t.effectiveFrom}. Created by ${decide.t.createdByEmail}.` : undefined}
        confirmLabel={decide?.approve ? "Approve" : "Reject"} tone={decide?.approve ? "primary" : "danger"} label={decide?.approve ? "Note (optional)" : "Why it is rejected"} testId="target-decision"
        onConfirm={async (note) => { await planningApi.decideTarget(decide!.t.id, { approve: decide!.approve, note: note || undefined, expectedVersion: decide!.t.version }); announceDataChanged(); }} />
      <EndDialog t={ending} today={ctx.today} onClose={() => setEnding(null)} />
    </>
  );
}

function scopeText(t: Target): string {
  return [t.lineName, t.departmentName, t.floorName, t.workstationCode, t.machineCode].filter(Boolean).join(" · ");
}

function EndDialog({ t, today, onClose }: { t: Target | null; today: string; onClose: () => void }) {
  const [end, setEnd] = useState(today);
  useEffect(() => { if (t) setEnd(today); }, [t, today]);
  return (
    <ReasonDialog open={t !== null} onClose={onClose} title="End this target" description={
      <label className="flex flex-col gap-1 text-caption">Last day in force (today or later; history is kept)<Input type="date" value={end} min={today} onChange={(e) => setEnd(e.target.value)} data-testid="target-end-date" /></label>}
      confirmLabel="End target" tone="danger" onConfirm={async (reason) => { await planningApi.endTarget(t!.id, { endDate: end, reason, expectedVersion: t!.version }); announceDataChanged(); }} />
  );
}

function TargetHistory({ id, onClose }: { id: string | null; onClose: () => void }) {
  const r = useResource<TargetDetail>(id ? planningApi.target(id) : null);
  const d = r.data;
  return (
    <Modal open={id !== null} onClose={onClose} title="Target history" className="max-w-3xl" description="Every version and every action on it, with who and why. Nothing is overwritten."
      footer={<Button variant="ghost" onClick={onClose}>Close</Button>} testId="target-history-modal">
      {!d ? <SkeletonRows rows={4} /> : (
        <div className="space-y-4">
          <DataTable columns={["Version", "Quantity", "Effective", "Approval", "Created by", "Reason"]}
            rows={d.versions.map((v) => [`v${v.revision}${v.isSuperseded ? " (replaced)" : ""}`, `${num(v.quantity)} ${TARGET_PERIOD_TEXT[v.period]}`, `${v.effectiveFrom} → ${v.effectiveTo ?? "open"}`, <ApprovalBadge key="a" t={v} />, v.createdByEmail, v.changeReason ?? "—"])} />
          <DataTable columns={["When", "Action", "Version", "Quantity", "By", "Reason"]}
            rows={d.revisions.map((r) => [dhaka(r.at), r.action.toLowerCase(), `v${r.revision}`, r.previousQuantity == null ? num(r.newQuantity) : `${num(r.previousQuantity)} → ${num(r.newQuantity)}`, r.byEmail, r.reason ?? "—"])} />
        </div>
      )}
    </Modal>
  );
}

function TargetFormModal({ open, onClose, scope, revise }: { open: boolean; onClose: () => void; scope: PlanningScope; revise?: Target }) {
  const { ctx, factoryId } = scope;
  const toast = useToast();
  const s = useSubmit();
  const orders = useResource<Paged<ProductionOrderListItem>>(open && !revise ? planningApi.orders({ factoryId, status: "open", pageSize: 100 }) : null);
  const blank = { level: "LINE" as TargetLevel, period: "DAILY", departmentId: "", floorId: "", lineId: "", shiftId: "", orderId: "", quantity: "", from: "", to: "", notes: "", reason: "" };
  const [f, setF] = useState(blank);
  useEffect(() => {
    if (!open) return;
    s.reset();
    setF(revise ? { ...blank, level: revise.level, period: revise.period, quantity: String(revise.quantity), from: addDays(ctx.today, 1), to: revise.effectiveTo ?? "", notes: revise.notes ?? "" }
      : { ...blank, from: ctx.today, lineId: ctx.lines.length === 1 ? ctx.lines[0].id : "" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, revise]);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));
  const lineLevel = f.level === "LINE" || f.level === "WORKSTATION" || f.level === "MACHINE";
  const levels = ctx.capabilities.lineScoped ? (["LINE"] as TargetLevel[]) : TARGET_LEVELS.filter((l) => l !== "WORKSTATION" && l !== "MACHINE");

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const saved = await s.run(() => revise
      ? planningApi.reviseTarget(revise.id, { quantity: Number(f.quantity), effectiveFrom: f.from, effectiveTo: f.to || undefined, notes: f.notes, reason: f.reason, expectedVersion: revise.version })
      : planningApi.createTarget({
        factoryId, level: f.level, period: f.period, quantity: Number(f.quantity), effectiveFrom: f.from, effectiveTo: f.to || undefined, notes: f.notes, reason: f.reason,
        departmentId: f.level === "DEPARTMENT" ? f.departmentId : undefined, floorId: f.level === "FLOOR" ? f.floorId : undefined, lineId: f.level === "LINE" ? f.lineId : undefined,
        shiftId: f.level === "SHIFT" || (f.level === "LINE" && f.shiftId) ? f.shiftId : undefined, orderId: f.level === "LINE" && f.orderId ? f.orderId : undefined
      }));
    if (!saved) return;
    const pending = saved.target.approvalStatus === "PENDING";
    toast.push({ title: revise ? "Target revised" : "Target created", description: pending ? "It applies once a second person approves it." : undefined });
    announceDataChanged();
    onClose();
  }

  return (
    <Modal open={open} onClose={onClose} locked={s.busy} title={revise ? "Revise target" : "New production target"} className="max-w-xl"
      description={revise ? "The current version stays on record and ends the day before the new one starts." : "A target never overwrites an earlier one: changes become new versions with an effective date."}
      testId="target-form" footer={<ModalFooter onCancel={onClose} busy={s.busy} label={revise ? "Revise" : "Create target"} formId="planning-target-form" testId="target-save" />}>
      <form id="planning-target-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        {!revise && (
          <div className="grid sm:grid-cols-2 gap-3">
            <Field label="Level" error={s.fe("level")}>{(a) => <Select id={a.id} value={f.level} onChange={set("level")} data-testid="target-level">{levels.map((l) => <option key={l} value={l}>{TARGET_LEVEL_TEXT[l]}</option>)}</Select>}</Field>
            <Field label="Per" error={s.fe("period")}>{(a) => <Select id={a.id} value={f.period} onChange={set("period")}>{TARGET_PERIODS.map((p) => <option key={p} value={p}>{TARGET_PERIOD_TEXT[p]}</option>)}</Select>}</Field>
            {f.level === "DEPARTMENT" && <Field label="Department" error={s.fe("departmentId")}>{(a) => <Select id={a.id} value={f.departmentId} onChange={set("departmentId")} required><option value="">Choose</option>{ctx.departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</Select>}</Field>}
            {f.level === "FLOOR" && <Field label="Floor" error={s.fe("floorId")}>{(a) => <Select id={a.id} value={f.floorId} onChange={set("floorId")} required><option value="">Choose</option>{ctx.floors.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</Select>}</Field>}
            {f.level === "LINE" && <Field label="Line" error={s.fe("lineId")}>{(a) => <Select id={a.id} value={f.lineId} onChange={set("lineId")} required data-testid="target-line"><option value="">Choose</option>{ctx.lines.filter((l) => l.canManage).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}</Select>}</Field>}
            {(f.level === "SHIFT" || lineLevel) && f.level !== "WORKSTATION" && f.level !== "MACHINE" && (
              <Field label={f.level === "SHIFT" ? "Shift" : "Shift (optional)"} error={s.fe("shiftId")}>{(a) => <Select id={a.id} value={f.shiftId} onChange={set("shiftId")} required={f.level === "SHIFT"}><option value="">{f.level === "SHIFT" ? "Choose" : "Any shift"}</option>{ctx.shifts.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}</Select>}</Field>
            )}
            {f.level === "LINE" && <Field label="Only for order (optional)" error={s.fe("orderId")}>{(a) => <Select id={a.id} value={f.orderId} onChange={set("orderId")}><option value="">Any order</option>{(orders.data?.items ?? []).map((o) => <option key={o.id} value={o.id}>{o.orderNumber}</option>)}</Select>}</Field>}
          </div>
        )}
        <div className="grid sm:grid-cols-3 gap-3">
          <Field label="Target quantity (pieces)" error={s.fe("quantity")}>{(a) => <Input id={a.id} type="number" min={1} value={f.quantity} onChange={set("quantity")} required data-testid="target-quantity" />}</Field>
          <Field label="Effective from" error={s.fe("effectiveFrom")}>{(a) => <Input id={a.id} type="date" value={f.from} onChange={set("from")} min={revise ? addDays(ctx.today, 0) : undefined} required data-testid="target-from" />}</Field>
          <Field label="Until (optional)" error={s.fe("effectiveTo")}>{(a) => <Input id={a.id} type="date" value={f.to} onChange={set("to")} />}</Field>
        </div>
        <Field label={revise ? "Reason for the revision (kept on record)" : "Reason / basis (optional)"} error={s.fe("reason")}>{(a) => <TextArea id={a.id} rows={2} value={f.reason} onChange={set("reason")} maxLength={500} required={Boolean(revise)} data-testid="target-reason" />}</Field>
        {!revise && (f.level === "FACTORY" || f.level === "DEPARTMENT" || f.level === "FLOOR") && <Callout tone="info">Factory-, department- and floor-level targets apply once a second person approves them.</Callout>}
      </form>
      <span className="sr-only"><History size={1} /></span>
    </Modal>
  );
}
