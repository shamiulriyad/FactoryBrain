"use client";

// Line allocations: split an order (or part of it) over one or more
// production lines for a date range. The API refuses allocations that
// exceed the order quantity unless a factory-level user records an explicit,
// reasoned override, and supervisors can only touch their own lines.

import Link from "next/link";
import { useEffect, useState } from "react";
import { Gauge, Plus } from "lucide-react";
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
import { EstimateCard } from "@/components/planning/CapacityView";
import { FormError, ModalFooter, PL, ReasonDialog, useSubmit, type PlanningScope } from "@/components/planning/shared";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { ALLOCATION_STATUS_TEXT, PRIORITIES, addDays, num, planningApi, type Allocation, type AllocationDetail, type CapacityEstimate, type Paged } from "@/lib/planning";
import type { ProductionOrderListItem } from "@/services/contracts";
import { useCan } from "@/lib/auth";
import { P } from "@/lib/access";

export function AllocationsView({ scope }: { scope: PlanningScope }) {
  const { q, ctx, factoryId } = scope;
  const status = q.get("status");
  const line = q.get("line");
  const orderId = q.get("order");
  const [form, setForm] = useState<{ order?: string; allocation?: Allocation } | null>(null);
  const [cancel, setCancel] = useState<Allocation | null>(null);
  const [capacity, setCapacity] = useState<Allocation | null>(null);
  const res = useResource<Paged<Allocation>>(planningApi.allocations({ factoryId, status, lineId: line, orderId, pageSize: 100 }));
  useOnDataChanged(() => void res.reload());
  const canAllocate = ctx.capabilities.manageAllocations;
  const d = res.data;
  const toast = useToast();

  async function setStatus(a: Allocation, to: string) {
    try { await planningApi.allocationStatus(a.id, { status: to, expectedVersion: a.version }); announceDataChanged(); toast.push({ title: `Allocation ${ALLOCATION_STATUS_TEXT[to as Allocation["status"]].toLowerCase()}` }); }
    catch (e) { toast.push({ tone: "error", title: "Not changed", description: (e as Error).message }); }
  }

  return (
    <>
      <div className="surface p-3 mb-4 flex flex-wrap items-end gap-3">
        <FilterSelect label="Line" value={line} onChange={(v) => q.set({ line: v || null })} testId="alloc-line">
          <option value="">All lines</option>{ctx.lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </FilterSelect>
        <FilterSelect label="Status" value={status} onChange={(v) => q.set({ status: v || null })} testId="alloc-status">
          <option value="">All</option>{Object.entries(ALLOCATION_STATUS_TEXT).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </FilterSelect>
        {orderId && <Button size="sm" variant="ghost" onClick={() => q.set({ order: null })}>Showing one order · clear</Button>}
        {canAllocate && <Button className="ml-auto" variant="primary" iconLeft={<Plus size={14} />} onClick={() => setForm({ order: orderId ?? undefined })} data-testid="alloc-new">Allocate an order</Button>}
      </div>
      {res.error && !d ? <ErrorState title="Could not load allocations" body={res.error.message} retryLabel="Try again" onRetry={() => void res.reload()} />
        : !d ? <SkeletonRows rows={6} />
        : d.items.length === 0 ? <div className="surface"><EmptyState title="No allocations" body={canAllocate ? "Allocate an order to a line to start planning its output." : "Allocations made by planners appear here (supervisors see their own lines)."} /></div>
        : (
          <Panel flush ariaLabel="Line allocations">
            <DataTable testId="alloc-table" columns={["Order", "Line", "Quantity", "From", "To", "Priority", "Supervisor", "Status", ""]}
              rows={d.items.map((a) => [
                <span key="o"><Link href={`${PL}/orders/${a.orderId}?factory=${factoryId}`} className="font-mono text-[12px] text-accent hover:underline">{a.orderNumber}</Link>
                  {a.overAllocated && <Badge tone="warning" title={`${a.overrideByEmail}: ${a.overrideReason}`}>over-allocated</Badge>}</span>,
                a.lineName, num(a.quantity), a.plannedStart, a.plannedEnd, a.priority.charAt(0) + a.priority.slice(1).toLowerCase(), a.supervisorEmail ?? "—",
                <Badge key="s" tone={a.status === "ACTIVE" ? "accent" : a.status === "COMPLETED" ? "success" : a.status === "CANCELLED" ? "danger" : "info"}>{ALLOCATION_STATUS_TEXT[a.status]}</Badge>,
                <span key="x" className="flex gap-1 justify-end">
                  <Button size="sm" variant="ghost" iconLeft={<Gauge size={12} />} onClick={() => setCapacity(a)} data-testid="alloc-capacity">Capacity</Button>
                  {(a.status === "PLANNED" || a.status === "ACTIVE") && ctx.lines.find((l) => l.id === a.lineId)?.canManage && canAllocate && (
                    <>
                      <Button size="sm" variant="ghost" onClick={() => setForm({ allocation: a })} data-testid="alloc-edit">Edit</Button>
                      {a.status === "PLANNED" && <Button size="sm" variant="ghost" onClick={() => void setStatus(a, "ACTIVE")}>Start</Button>}
                      {a.status === "ACTIVE" && <Button size="sm" variant="ghost" onClick={() => void setStatus(a, "COMPLETED")}>Complete</Button>}
                      <Button size="sm" variant="ghost" onClick={() => setCancel(a)} data-testid="alloc-cancel">Cancel</Button>
                    </>
                  )}
                </span>
              ])} />
          </Panel>
        )}
      <AllocationFormModal open={form !== null} onClose={() => setForm(null)} scope={scope} allocation={form?.allocation} orderId={form?.order} onSaved={() => void res.reload()} />
      <ReasonDialog open={cancel !== null} onClose={() => setCancel(null)} title={`Cancel the allocation of ${cancel?.orderNumber ?? ""} on ${cancel?.lineName ?? ""}`}
        description="The quantity is released back to the order. The change and its reason stay in the history." confirmLabel="Cancel allocation" tone="danger" testId="alloc-cancel-dialog"
        onConfirm={async (reason) => { await planningApi.allocationStatus(cancel!.id, { status: "CANCELLED", reason, expectedVersion: cancel!.version }); announceDataChanged(); }} />
      <Modal open={capacity !== null} onClose={() => setCapacity(null)} title="Capacity for this allocation" className="max-w-2xl" footer={<Button variant="ghost" onClick={() => setCapacity(null)}>Close</Button>}>
        {capacity && <AllocationCapacity id={capacity.id} />}
      </Modal>
      <span className="sr-only">{addDays(ctx.today, 0)}</span>
    </>
  );
}

function AllocationCapacity({ id }: { id: string }) {
  const r = useResource<CapacityEstimate>(planningApi.allocationCapacity(id));
  return r.error && !r.data ? <Callout tone="danger">{r.error.message}</Callout> : r.data ? <EstimateCard e={r.data} /> : <SkeletonRows rows={3} />;
}

export function AllocationFormModal({ open, onClose, scope, allocation, orderId, onSaved }: {
  open: boolean; onClose: () => void; scope: PlanningScope; allocation?: Allocation | null; orderId?: string; onSaved: (a: AllocationDetail) => void;
}) {
  const { ctx, factoryId } = scope;
  const toast = useToast();
  const s = useSubmit();
  const orders = useResource<Paged<ProductionOrderListItem>>(open && !allocation ? planningApi.orders({ factoryId, status: "open", pageSize: 100 }) : null);
  const canUsers = useCan(P.UsersView);
  const supervisors = useResource<{ items: { id: string; email: string; fullName?: string | null }[] }>(open && canUsers ? `/api/users?role=ProductionSupervisor&factoryId=${factoryId}&pageSize=100` : null);
  const blank = { orderId: "", lineId: "", quantity: "", start: "", end: "", priority: "NORMAL", supervisor: "", notes: "", reason: "", override: false, overrideReason: "" };
  const [f, setF] = useState(blank);
  useEffect(() => {
    if (!open) return;
    s.reset();
    setF(allocation
      ? { ...blank, orderId: allocation.orderId, lineId: allocation.lineId, quantity: String(allocation.quantity), start: allocation.plannedStart, end: allocation.plannedEnd, priority: allocation.priority,
          supervisor: allocation.supervisorUserId ?? "", notes: allocation.notes ?? "", overrideReason: allocation.overrideReason ?? "", override: allocation.overAllocated }
      : { ...blank, orderId: orderId ?? "", start: ctx.today, end: addDays(ctx.today, 6) });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, allocation, orderId]);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));
  const lines = ctx.lines.filter((l) => l.isActive && l.canManage);
  const exceeds = s.error && (s.error as { message?: string }).message?.includes("beyond") ? true : false;
  const factoryLevel = !ctx.capabilities.lineScoped;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const body = {
      orderId: f.orderId, lineId: f.lineId, quantity: Number(f.quantity), plannedStart: f.start, plannedEnd: f.end, priority: f.priority, notes: f.notes,
      supervisorUserId: f.supervisor || undefined, reason: f.reason, allowOverAllocation: f.override || undefined, overrideReason: f.override ? f.overrideReason : undefined
    };
    const saved = await s.run(() => allocation ? planningApi.updateAllocation(allocation.id, { ...body, expectedVersion: allocation.version }) : planningApi.createAllocation(body));
    if (!saved) return;
    toast.push({ title: allocation ? "Allocation updated" : "Order allocated" });
    announceDataChanged();
    onSaved(saved);
    onClose();
  }

  return (
    <Modal open={open} onClose={onClose} locked={s.busy} title={allocation ? "Edit allocation" : "Allocate an order to a line"} className="max-w-xl"
      description="The total allocated to an order can't exceed its quantity unless an authorised user records why. A line can take the same order only once in a period."
      testId="alloc-form" footer={<ModalFooter onCancel={onClose} busy={s.busy} label={allocation ? "Save" : "Allocate"} formId="planning-alloc-form" testId="alloc-save" />}>
      <form id="planning-alloc-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        <div className="grid sm:grid-cols-2 gap-3">
          <Field label="Order" error={s.fe("orderId")}>
            {(a) => (
              <Select id={a.id} value={f.orderId} onChange={set("orderId")} required disabled={Boolean(allocation)} data-testid="alloc-order">
                <option value="">Choose an order</option>
                {allocation ? <option value={allocation.orderId}>{allocation.orderNumber}</option>
                  : (orders.data?.items ?? []).map((o) => <option key={o.id} value={o.id}>{o.orderNumber} · {o.styleCode} · {num(o.quantity)} pcs</option>)}
              </Select>
            )}
          </Field>
          <Field label="Production line" error={s.fe("lineId")}>
            {(a) => (
              <Select id={a.id} value={f.lineId} onChange={set("lineId")} required disabled={Boolean(allocation)} data-testid="alloc-line-select">
                <option value="">Choose a line</option>{lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Allocated quantity (pieces)" error={s.fe("quantity")}>{(a) => <Input id={a.id} type="number" min={1} value={f.quantity} onChange={set("quantity")} required data-testid="alloc-quantity" />}</Field>
          <Field label="Priority">{(a) => <Select id={a.id} value={f.priority} onChange={set("priority")}>{PRIORITIES.map((p) => <option key={p} value={p}>{p.charAt(0) + p.slice(1).toLowerCase()}</option>)}</Select>}</Field>
          <Field label="Planned start" error={s.fe("plannedStart")}>{(a) => <Input id={a.id} type="date" value={f.start} onChange={set("start")} required data-testid="alloc-start" />}</Field>
          <Field label="Planned end" error={s.fe("plannedEnd")}>{(a) => <Input id={a.id} type="date" value={f.end} onChange={set("end")} required data-testid="alloc-end" />}</Field>
          {canUsers && (
            <Field label="Responsible supervisor" hint="Must belong to the factory and reach the line.">
              {(a) => <Select id={a.id} value={f.supervisor} onChange={set("supervisor")}><option value="">None</option>{(supervisors.data?.items ?? []).map((u) => <option key={u.id} value={u.id}>{u.fullName ?? u.email}</option>)}</Select>}
            </Field>
          )}
        </div>
        {allocation && <Field label="Reason for the change (kept on record)" error={s.fe("reason")}>{(a) => <TextArea id={a.id} rows={2} value={f.reason} onChange={set("reason")} maxLength={500} required data-testid="alloc-reason" />}</Field>}
        <Field label="Notes">{(a) => <TextArea id={a.id} rows={2} value={f.notes} onChange={set("notes")} maxLength={1000} />}</Field>
        {factoryLevel && (exceeds || f.override) && (
          <Callout tone="warning" title="Allocate beyond the order quantity">
            <label className="flex items-center gap-2 text-caption"><input type="checkbox" checked={f.override} onChange={(e) => setF((x) => ({ ...x, override: e.target.checked }))} data-testid="alloc-override" /> Allow this allocation to take the order&apos;s total above its quantity</label>
            {f.override && <Field className="mt-2" label="Why (kept on record)">{(a) => <Input id={a.id} value={f.overrideReason} onChange={set("overrideReason")} maxLength={500} required data-testid="alloc-override-reason" />}</Field>}
          </Callout>
        )}
      </form>
    </Modal>
  );
}
