"use client";

// One daily plan: its items (order · line · shift · target), the capacity
// check of each, the review / approval / publication workflow with its audit
// trail, and — once published — planned versus actual per item.

import Link from "next/link";
import { useParams } from "next/navigation";
import { useEffect, useState } from "react";
import { ArrowLeft, Pencil, Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { RequirePermission } from "@/components/auth/RequireRole";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { DataTable } from "@/components/analytics/shared";
import { ActualCell, FormError, Legend, ModalFooter, PL, PLANNING_VIEW, PlanStatusBadge, PlanningTabs, ReasonDialog, StateBadge, useSubmit, usePlanningScope } from "@/components/planning/shared";
import { dhaka } from "@/lib/monitoring";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { num, planningApi, type AllocationSummary, type DailyPlan, type Paged, type PlanItem, type PlanningContext } from "@/lib/planning";
import type { ProductionOrderListItem } from "@/services/contracts";

export default function PlanEditorView() {
  return <RequirePermission perms={PLANNING_VIEW} what="Daily production plans"><Editor /></RequirePermission>;
}

type Action = "submit" | "approve" | "return" | "publish" | "revise" | "cancel";
const ACTION_TEXT: Record<Action, { label: string; reason: "none" | "optional" | "required"; tone?: "danger"; help?: string }> = {
  submit: { label: "Submit for review", reason: "optional" },
  approve: { label: "Approve", reason: "optional", help: "A plan you submitted yourself must be approved by someone else." },
  return: { label: "Return to draft", reason: "required" },
  publish: { label: "Publish", reason: "optional", help: "Supervisors and monitoring operators see a plan once it is published. Publishing a revision replaces the plan now in force." },
  revise: { label: "Start a revision", reason: "required", help: "A copy of the published plan opens as a draft; the published plan stays in force until the revision is published." },
  cancel: { label: "Cancel plan", reason: "required", tone: "danger" }
};

function Editor() {
  const id = useParams<{ id: string }>()?.id ?? "";
  const scope = usePlanningScope(`${PL}/plans/${id}`);
  const source = scope.q.get("source") === "SIMULATED" ? "SIMULATED" : "REAL";
  const res = useResource<DailyPlan>(id ? planningApi.plan(id, source) : null, { pollMs: 60_000 });
  useOnDataChanged(() => void res.reload());
  const [item, setItem] = useState<{ item?: PlanItem } | null>(null);
  const [action, setAction] = useState<Action | null>(null);
  const [removing, setRemoving] = useState<PlanItem | null>(null);
  const toast = useToast();
  const p = res.data;
  const ctx = scope.ctx.data;

  return (
    <div className="page" data-testid="planning-plan">
      <Link href={`${PL}/plans${scope.factoryId ? `?factory=${scope.factoryId}` : ""}`} className="inline-flex items-center gap-1 text-caption text-fg-tertiary hover:text-fg-primary"><ArrowLeft size={13} /> Daily plans</Link>
      {res.error && !p ? <div className="mt-3"><ErrorState title="Could not load the plan" body={res.error.message} retryLabel="Try again" onRetry={() => void res.reload()} /></div>
        : !p ? <SkeletonRows rows={6} />
        : (
          <>
            <PageHeader className="mt-2" eyebrow="Daily production plan" title={`${p.planDate} · ${p.reference}`} description={`${p.factoryName} · revision ${p.revisionNo}${p.revisionReason ? ` · ${p.revisionReason}` : ""}`}
              meta={<PlanStatusBadge status={p.status} />}
              actions={<>{(p.allowedActions.filter((a) => a in ACTION_TEXT) as Action[]).map((a) => (
                <Button key={a} variant={a === "publish" || a === "submit" || a === "approve" ? "primary" : ACTION_TEXT[a].tone === "danger" ? "danger" : "secondary"} onClick={() => setAction(a)} data-testid={`plan-${a}`}>{ACTION_TEXT[a].label}</Button>
              ))}</>} />
            <PlanningTabs />
            <p className="mb-3 text-caption text-fg-tertiary">
              Created by {p.createdByEmail}{p.submittedByEmail && <> · submitted by {p.submittedByEmail}</>}{p.approvedByEmail && <> · approved by {p.approvedByEmail}</>}{p.publishedByEmail && <> · published by {p.publishedByEmail} {dhaka(p.publishedAt)}</>}
            </p>
            {p.status === "REVISED" && <Callout tone="info" className="mb-3">A later revision replaced this plan. It stays on record.</Callout>}
            {p.warnings.length > 0 && <Callout tone="warning" className="mb-3"><ul className="list-disc pl-4">{p.warnings.map((w) => <li key={w}>{w}</li>)}</ul></Callout>}

            <Panel title="Items" subtitle="Order · line · shift · target" flush testId="plan-items"
              right={p.allowedActions.includes("edit") && <Button size="sm" variant="primary" iconLeft={<Plus size={13} />} onClick={() => setItem({})} data-testid="plan-add-item">Add item</Button>}>
              {p.items.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No items yet. Each item needs an order allocated to that line for this date.</p> : (
                <DataTable testId="plan-items-table" columns={["Order", "Line", "Shift", "Target", "Per hour", "Source", ...(p.status === "PUBLISHED" || p.status === "REVISED" ? ["Actual", "Expected by now", "State"] : []), ""]}
                  rows={p.items.map((i) => [
                    <span key="o"><Link href={`${PL}/orders/${i.orderId}?factory=${p.factoryId}`} className="font-mono text-[12px] text-accent hover:underline">{i.orderNumber}</Link>{i.buyer && <span className="block text-fg-tertiary">{i.buyer}</span>}
                      {i.warnings.map((w) => <span key={w} className="block text-[var(--risk-medium)]">{w}</span>)}</span>,
                    i.lineName, i.shiftName, num(i.targetQuantity), i.hourlyTarget == null ? "—" : num(i.hourlyTarget, 1), i.targetSource === "TARGET" ? "configured target" : "entered",
                    ...(p.status === "PUBLISHED" || p.status === "REVISED" ? [<ActualCell key="a" a={i.actual} />, i.actual?.expectedByNow == null ? "—" : num(i.actual.expectedByNow), i.actual ? <StateBadge key="s" state={i.actual.state} /> : "—"] : []),
                    p.allowedActions.includes("edit") ? <span key="x" className="flex gap-1 justify-end">
                      <Button size="sm" variant="ghost" aria-label="Edit item" onClick={() => setItem({ item: i })} data-testid="plan-edit-item"><Pencil size={12} /></Button>
                      <Button size="sm" variant="ghost" aria-label="Remove item" onClick={() => setRemoving(i)} data-testid="plan-remove-item"><Trash2 size={12} /></Button></span> : ""
                  ])} />
              )}
            </Panel>
            {(p.status === "PUBLISHED") && <div className="mt-3"><Legend /></div>}

            <Panel className="mt-4" title="Audit trail" subtitle="Every step with who and why" flush testId="plan-events">
              <DataTable columns={["When", "Action", "By", "Details", "Reason"]} rows={p.events.map((e) => [dhaka(e.at), e.action.toLowerCase().replace("_", " "), e.byEmail, e.details ?? "—", e.reason ?? "—"])} />
            </Panel>

            {ctx && <ItemModal open={item !== null} onClose={() => setItem(null)} plan={p} ctx={ctx} item={item?.item} />}
            <ReasonDialog open={action !== null} onClose={() => setAction(null)} title={action ? ACTION_TEXT[action].label : ""} description={action ? ACTION_TEXT[action].help : undefined}
              required={action ? ACTION_TEXT[action].reason === "required" : false} tone={action && ACTION_TEXT[action].tone === "danger" ? "danger" : "primary"} confirmLabel={action ? ACTION_TEXT[action].label : "Confirm"} testId="plan-action-dialog"
              onConfirm={async (reason) => {
                const r = await planningApi.planAction(p.id, action!, { reason: reason || undefined, expectedVersion: p.version });
                announceDataChanged();
                toast.push({ title: `${ACTION_TEXT[action!].label}: done` });
                if (action === "revise") window.location.assign(`${PL}/plans/${r.id}?factory=${p.factoryId}`);
              }} />
            <Modal open={removing !== null} onClose={() => setRemoving(null)} title="Remove this item?" description={removing ? `${removing.orderNumber} on ${removing.lineName}, ${removing.shiftName}. The removal is recorded in the audit trail.` : undefined}
              footer={<><Button variant="ghost" onClick={() => setRemoving(null)}>Keep</Button>
                <Button variant="danger" data-testid="plan-remove-confirm" onClick={async () => { try { await planningApi.removePlanItem(p.id, removing!.id, p.version); announceDataChanged(); } catch (e) { toast.push({ tone: "error", title: "Not removed", description: (e as Error).message }); } setRemoving(null); }}>Remove</Button></>}>
              <span />
            </Modal>
          </>
        )}
    </div>
  );
}

function ItemModal({ open, onClose, plan, ctx, item }: { open: boolean; onClose: () => void; plan: DailyPlan; ctx: PlanningContext; item?: PlanItem }) {
  const toast = useToast();
  const s = useSubmit();
  const orders = useResource<Paged<ProductionOrderListItem>>(open ? planningApi.orders({ factoryId: plan.factoryId, status: "open", pageSize: 100 }) : null);
  const [orderId, setOrderId] = useState("");
  const allocs = useResource<AllocationSummary>(open && orderId ? planningApi.orderAllocations(orderId) : null);
  const [f, setF] = useState({ lineId: "", shiftId: "", target: "", configured: false, notes: "" });
  useEffect(() => {
    if (!open) return;
    s.reset();
    setOrderId(item?.orderId ?? "");
    setF({ lineId: item?.lineId ?? "", shiftId: item?.shiftId ?? (ctx.shifts.length === 1 ? ctx.shifts[0].id : ""), target: item ? String(item.targetQuantity) : "", configured: false, notes: item?.notes ?? "" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, item]);
  // Only lines that have an open allocation of the order covering the plan date can take an item.
  const lineOptions = (allocs.data?.allocations ?? []).filter((a) => (a.status === "PLANNED" || a.status === "ACTIVE") && a.plannedStart <= plan.planDate && a.plannedEnd >= plan.planDate)
    .filter((a) => ctx.lines.find((l) => l.id === a.lineId)?.canManage);
  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const body = { orderId, lineId: f.lineId, shiftId: f.shiftId, targetQuantity: f.configured ? undefined : Number(f.target), useConfiguredTarget: f.configured || undefined, notes: f.notes };
    const saved = await s.run(() => item ? planningApi.updatePlanItem(plan.id, item.id, plan.version, body) : planningApi.addPlanItem(plan.id, plan.version, body));
    if (!saved) return;
    toast.push({ title: item ? "Item updated" : "Item added" });
    announceDataChanged();
    onClose();
  }
  return (
    <Modal open={open} onClose={onClose} locked={s.busy} title={item ? "Edit plan item" : "Add a plan item"} className="max-w-xl"
      description="An order on a line in a shift, with its target. The order must be allocated to the line for this date; the shift must work on this weekday."
      testId="plan-item-form" footer={<ModalFooter onCancel={onClose} busy={s.busy} label={item ? "Save" : "Add item"} formId="planning-item-form" testId="plan-item-save" />}>
      <form id="planning-item-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        <Field label="Order" error={s.fe("orderId")}>
          {(a) => <Select id={a.id} value={orderId} onChange={(e) => { setOrderId(e.target.value); setF((x) => ({ ...x, lineId: "" })); }} required data-testid="item-order">
            <option value="">Choose an order</option>{(orders.data?.items ?? []).map((o) => <option key={o.id} value={o.id}>{o.orderNumber} · {o.styleCode} · {num(o.quantity)} pcs</option>)}
          </Select>}
        </Field>
        <div className="grid sm:grid-cols-2 gap-3">
          <Field label="Line" error={s.fe("lineId")} hint={orderId && !allocs.loading && lineOptions.length === 0 ? "This order isn't allocated to a line you can plan on this date. Allocate it first." : undefined}>
            {(a) => <Select id={a.id} value={f.lineId} onChange={(e) => setF({ ...f, lineId: e.target.value })} required data-testid="item-line">
              <option value="">Choose a line</option>{lineOptions.map((al) => <option key={al.id} value={al.lineId}>{al.lineName} (allocated {num(al.quantity)})</option>)}
            </Select>}
          </Field>
          <Field label="Shift" error={s.fe("shiftId")}>
            {(a) => <Select id={a.id} value={f.shiftId} onChange={(e) => setF({ ...f, shiftId: e.target.value })} required data-testid="item-shift">
              <option value="">Choose a shift</option>{ctx.shifts.map((x) => <option key={x.id} value={x.id}>{x.name} ({x.start}–{x.end})</option>)}
            </Select>}
          </Field>
          <Field label="Target for this shift (pieces)" error={s.fe("targetQuantity")}>{(a) => <Input id={a.id} type="number" min={1} value={f.target} onChange={(e) => setF({ ...f, target: e.target.value })} disabled={f.configured} required={!f.configured} data-testid="item-target" />}</Field>
          <Field label="Or">{() => <label className="flex items-center gap-2 text-caption h-9"><input type="checkbox" checked={f.configured} onChange={(e) => setF({ ...f, configured: e.target.checked })} data-testid="item-configured" /> Use the configured target</label>}</Field>
        </div>
        <Field label="Notes">{(a) => <TextArea id={a.id} rows={2} value={f.notes} onChange={(e) => setF({ ...f, notes: e.target.value })} maxLength={500} />}</Field>
      </form>
    </Modal>
  );
}
