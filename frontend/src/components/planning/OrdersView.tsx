"use client";

// Production orders: register, edit and move through the lifecycle
// (draft → planned → in progress ⇄ on hold → completed / cancelled), with the
// reason of every change kept. Orders belong to a factory; the API limits
// every list and change to the caller's factories.

import Link from "next/link";
import { useEffect, useState } from "react";
import { ClipboardList, Plus } from "lucide-react";
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
import { FormError, ModalFooter, OrderStatusBadge, PL, useSubmit, type PlanningScope } from "@/components/planning/shared";
import { SearchBox } from "@/components/attendance/shared";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { ORDER_STATUS_TEXT, PRIORITIES, planningApi, num, type Paged } from "@/lib/planning";
import type { ProductionOrder, ProductionOrderListItem, ProductionStyle } from "@/services/contracts";

const STATUS_FILTERS = ["draft", "planned", "in_progress", "on_hold", "completed", "cancelled"] as const;

export function OrdersView({ scope }: { scope: PlanningScope }) {
  const { q, ctx, factoryId } = scope;
  const status = q.get("status");
  const priority = q.get("priority");
  const search = q.get("q");
  const [creating, setCreating] = useState(false);
  const styles = useResource<ProductionStyle[]>(planningApi.styles);
  const res = useResource<Paged<ProductionOrderListItem>>(planningApi.orders({ factoryId, q: search, status, priority, page: q.page, pageSize: 25, sort: "priority" }));
  useOnDataChanged(() => void res.reload());
  const canManage = ctx.capabilities.manageOrders;
  const d = res.data;

  return (
    <>
      <div className="surface p-3 mb-4 flex flex-wrap items-end gap-3" role="search">
        <SearchBox value={search} onChange={(v) => q.set({ q: v || null })} placeholder="Order number, buyer or reference…" label="Search orders" testId="orders-search" />
        <FilterSelect label="Status" value={status} onChange={(v) => q.set({ status: v || null })} testId="orders-status">
          <option value="">All</option><option value="open">Open (planned + in progress)</option>
          {STATUS_FILTERS.map((s) => <option key={s} value={s}>{ORDER_STATUS_TEXT[s]}</option>)}
        </FilterSelect>
        <FilterSelect label="Priority" value={priority} onChange={(v) => q.set({ priority: v || null })}>
          <option value="">All</option>{PRIORITIES.map((p) => <option key={p} value={p}>{p.charAt(0) + p.slice(1).toLowerCase()}</option>)}
        </FilterSelect>
        {canManage && <Button className="ml-auto" variant="primary" iconLeft={<Plus size={14} />} onClick={() => setCreating(true)} data-testid="order-new">New order</Button>}
      </div>
      {res.error && !d ? <ErrorState title="Could not load orders" body={res.error.message} retryLabel="Try again" onRetry={() => void res.reload()} />
        : !d ? <SkeletonRows rows={6} />
        : d.items.length === 0 ? (
          <div className="surface"><EmptyState icon={<ClipboardList size={20} />} title={search || status || priority ? "No orders match" : "No production orders yet"}
            body={canManage ? "Register an order, then allocate it to production lines." : "Orders registered by planners appear here."} /></div>
        ) : (
          <Panel flush ariaLabel="Production orders">
            <DataTable testId="orders-table" columns={["Order", "Style", "Buyer", "Priority", "Quantity", "Start", "Planned completion", "Delivery deadline", "Status"]}
              rows={d.items.map((o) => [
                <span key="n"><Link href={`${PL}/orders/${o.id}?factory=${factoryId}`} className="font-mono text-[12px] text-accent hover:underline" data-testid="order-row">{o.orderNumber}</Link>{o.orderReference && <span className="block text-fg-tertiary">{o.orderReference}</span>}</span>,
                `${o.styleCode} · ${o.styleName}`, o.buyer, o.priority ? o.priority.charAt(0) + o.priority.slice(1).toLowerCase() : "—", num(o.quantity),
                o.startDate ?? "—", o.plannedCompletionDate ?? "—", o.dueDate, <OrderStatusBadge key="s" status={o.status} />
              ])} />
            {Math.ceil(d.total / d.pageSize) > 1 && (
              <div className="flex items-center justify-between px-4 py-2 text-caption">
                <span>Page {d.page} of {Math.ceil(d.total / d.pageSize)} · {d.total} orders</span>
                <span className="flex gap-2">
                  <Button size="sm" variant="ghost" disabled={d.page <= 1} onClick={() => q.set({ page: String(d.page - 1) }, false)}>Previous</Button>
                  <Button size="sm" variant="ghost" disabled={d.page >= Math.ceil(d.total / d.pageSize)} onClick={() => q.set({ page: String(d.page + 1) }, false)}>Next</Button>
                </span>
              </div>
            )}
          </Panel>
        )}
      <OrderFormModal open={creating} onClose={() => setCreating(false)} factoryId={factoryId} styles={styles.data ?? []} onSaved={() => void res.reload()} />
    </>
  );
}

export function OrderFormModal({ open, onClose, factoryId, styles, order, onSaved }: {
  open: boolean; onClose: () => void; factoryId: string; styles: ProductionStyle[]; order?: ProductionOrder | null; onSaved: (o: ProductionOrder) => void;
}) {
  const toast = useToast();
  const s = useSubmit();
  const blank = { orderNumber: "", orderReference: "", buyer: "", styleId: "", productCategory: "", productDescription: "", quantity: "", startDate: "", plannedCompletionDate: "", dueDate: "", priority: "NORMAL", status: "planned", notes: "" };
  const [f, setF] = useState(blank);
  useEffect(() => {
    if (!open) return;
    s.reset();
    setF(order ? {
      orderNumber: order.orderNumber, orderReference: order.orderReference ?? "", buyer: order.buyer ?? "", styleId: order.styleId, productCategory: order.productCategory ?? "",
      productDescription: order.productDescription ?? "", quantity: String(order.quantity), startDate: order.startDate ?? "", plannedCompletionDate: order.plannedCompletionDate ?? "",
      dueDate: order.dueDate, priority: order.priority ?? "NORMAL", status: order.status, notes: order.notes ?? ""
    } : { ...blank, styleId: styles.find((x) => x.isActive)?.id ?? "" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, order]);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const body: Record<string, unknown> = {
      styleId: f.styleId, buyer: f.buyer.trim(), quantity: f.quantity ? Number(f.quantity) : undefined, dueDate: f.dueDate, notes: f.notes, orderReference: f.orderReference,
      productCategory: f.productCategory, productDescription: f.productDescription, startDate: f.startDate, plannedCompletionDate: f.plannedCompletionDate, priority: f.priority
    };
    const saved = await s.run(() => order
      ? planningApi.updateOrder(order.id, { ...body, expectedVersion: order.version })
      : planningApi.createOrder({ ...body, orderNumber: f.orderNumber.trim(), factoryId, status: f.status }));
    if (!saved) return;
    toast.push({ title: order ? "Order updated" : `Order ${saved.orderNumber} registered` });
    announceDataChanged();
    onSaved(saved);
    onClose();
  }

  return (
    <Modal open={open} onClose={onClose} locked={s.busy} title={order ? `Edit order ${order.orderNumber}` : "New production order"}
      description="A quantity of one style for one buyer, produced in this factory. Changes are audited; the status moves through the lifecycle with a reason."
      testId="order-form" className="max-w-2xl" footer={<ModalFooter onCancel={onClose} busy={s.busy} label={order ? "Save" : "Register order"} formId="planning-order-form" testId="order-save" />}>
      <form id="planning-order-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        <div className="grid sm:grid-cols-2 gap-3">
          {!order && <Field label="Production order number" error={s.fe("orderNumber")}>{(a) => <Input id={a.id} value={f.orderNumber} onChange={set("orderNumber")} maxLength={64} required data-testid="order-number" />}</Field>}
          <Field label="Buyer / client order reference" error={s.fe("orderReference")}>{(a) => <Input id={a.id} value={f.orderReference} onChange={set("orderReference")} maxLength={80} />}</Field>
          <Field label="Buyer / client" error={s.fe("buyer")} hint="Needed before the order can be planned.">{(a) => <Input id={a.id} value={f.buyer} onChange={set("buyer")} maxLength={120} data-testid="order-buyer" />}</Field>
          <Field label="Garment style" error={s.fe("styleId")} hint={styles.length === 0 ? "No styles yet: add one on the Styles page." : undefined}>
            {(a) => (
              <Select id={a.id} value={f.styleId} onChange={set("styleId")} required data-testid="order-style">
                <option value="" disabled>Choose a style</option>
                {styles.filter((x) => x.isActive || x.id === f.styleId).map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Product category" error={s.fe("productCategory")}>{(a) => <Input id={a.id} value={f.productCategory} onChange={set("productCategory")} maxLength={64} placeholder="e.g. Knit tops" />}</Field>
          <Field label="Total quantity (pieces)" error={s.fe("quantity")}>{(a) => <Input id={a.id} type="number" min={1} value={f.quantity} onChange={set("quantity")} required data-testid="order-quantity" />}</Field>
          <Field label="Order start date" error={s.fe("startDate")}>{(a) => <Input id={a.id} type="date" value={f.startDate} onChange={set("startDate")} />}</Field>
          <Field label="Planned completion" error={s.fe("plannedCompletionDate")}>{(a) => <Input id={a.id} type="date" value={f.plannedCompletionDate} onChange={set("plannedCompletionDate")} />}</Field>
          <Field label="Delivery deadline" error={s.fe("dueDate")}>{(a) => <Input id={a.id} type="date" value={f.dueDate} onChange={set("dueDate")} required data-testid="order-due" />}</Field>
          <Field label="Priority" error={s.fe("priority")}>
            {(a) => <Select id={a.id} value={f.priority} onChange={set("priority")}>{PRIORITIES.map((p) => <option key={p} value={p}>{p.charAt(0) + p.slice(1).toLowerCase()}</option>)}</Select>}
          </Field>
          {!order && (
            <Field label="Starts as" error={s.fe("status")} hint="A draft can be incomplete; a planned order needs a buyer.">
              {(a) => <Select id={a.id} value={f.status} onChange={set("status")}><option value="planned">Planned</option><option value="draft">Draft</option></Select>}
            </Field>
          )}
        </div>
        <Field label="Product description" error={s.fe("productDescription")}>{(a) => <TextArea id={a.id} rows={2} value={f.productDescription} onChange={set("productDescription")} maxLength={500} />}</Field>
        <Field label="Notes" error={s.fe("notes")}>{(a) => <TextArea id={a.id} rows={2} value={f.notes} onChange={set("notes")} maxLength={1000} />}</Field>
        <Callout tone="info">Attachments: buyer documents and tech packs are kept under <Link href="/app/merchandising" className="text-accent hover:underline">Merchandising</Link>; this form holds the order&apos;s notes.</Callout>
      </form>
    </Modal>
  );
}
