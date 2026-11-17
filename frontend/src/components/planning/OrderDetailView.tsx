"use client";

// One production order: its specification, lifecycle (with the reason and
// author of every change), allocations over lines, and its progress and
// delivery forecast from the plans that scheduled it and the counters that
// measured it.

import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { ArrowLeft, Pencil, Plus } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { RequirePermission } from "@/components/auth/RequireRole";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { DataTable } from "@/components/analytics/shared";
import { AllocationFormModal } from "@/components/planning/AllocationsView";
import { OrderFormModal } from "@/components/planning/OrdersView";
import { BasisBadge, ForecastBadge, Legend, OrderStatusBadge, PL, PLANNING_VIEW, PlanningTabs, ReasonDialog, usePlanningScope, useCanPlan } from "@/components/planning/shared";
import { KeyValue } from "@/components/production/shared";
import { announceDataChanged, useOnDataChanged, useResource } from "@/lib/useResource";
import { dhaka } from "@/lib/monitoring";
import { ORDER_STATUS_TEXT, num, pct, planningApi, type AllocationSummary, type OrderProgress, type OrderTransitions } from "@/lib/planning";
import type { OrderDetail, OrderStatus, ProductionStyle } from "@/services/contracts";

export default function OrderDetailView() {
  return <RequirePermission perms={PLANNING_VIEW} what="Production orders"><Detail /></RequirePermission>;
}

function Detail() {
  const id = useParams<{ id: string }>()?.id ?? "";
  const base = `${PL}/orders/${id}`;
  const scope = usePlanningScope(base);
  const can = useCanPlan();
  const detail = useResource<OrderDetail>(id ? `/api/production/orders/${id}` : null);
  const transitions = useResource<OrderTransitions>(id ? planningApi.orderTransitions(id) : null);
  const progress = useResource<OrderProgress>(id ? planningApi.orderProgress(id) : null, { pollMs: 60_000 });
  const allocations = useResource<AllocationSummary>(id ? planningApi.orderAllocations(id) : null);
  const styles = useResource<ProductionStyle[]>(planningApi.styles);
  const [editing, setEditing] = useState(false);
  const [allocating, setAllocating] = useState(false);
  const [to, setTo] = useState<OrderStatus | null>(null);
  const reloadAll = () => { void detail.reload(); void transitions.reload(); void progress.reload(); void allocations.reload(); };
  useOnDataChanged(reloadAll);
  const d = detail.data;
  const o = d?.order;
  const factoryId = o?.factoryId ?? scope.factoryId;
  const ctxScope = scope.ctx.data && o ? { ...scope, factoryId, ctx: scope.ctx.data } : null;

  return (
    <div className="page" data-testid="planning-order">
      <Link href={`${PL}/orders${scope.factoryId ? `?factory=${scope.factoryId}` : ""}`} className="inline-flex items-center gap-1 text-caption text-fg-tertiary hover:text-fg-primary"><ArrowLeft size={13} /> Orders</Link>
      {detail.error && !d ? <div className="mt-3"><ErrorState title="Could not load the order" body={detail.error.message} retryLabel="Try again" onRetry={reloadAll} /></div>
        : !d || !o ? <SkeletonRows rows={6} />
        : (
          <>
            <PageHeader className="mt-2" eyebrow="Production order" title={o.orderNumber}
              description={`${d.style.code} · ${d.style.name}${o.buyer ? ` · ${o.buyer}` : ""}${o.orderReference ? ` · ref ${o.orderReference}` : ""}`}
              meta={<OrderStatusBadge status={o.status} />}
              actions={can.orders && (
                <>
                  <Button variant="secondary" iconLeft={<Pencil size={14} />} onClick={() => setEditing(true)} disabled={o.status === "completed" || o.status === "cancelled"} data-testid="order-edit">Edit</Button>
                  {(o.status === "planned" || o.status === "in_progress") && can.allocations && <Button variant="secondary" iconLeft={<Plus size={14} />} onClick={() => setAllocating(true)} data-testid="order-allocate">Allocate to a line</Button>}
                </>
              )} />
            <PlanningTabs />

            {can.orders && (o.allowedStatuses ?? []).length > 0 && (
              <div className="mb-4 flex flex-wrap items-center gap-2" data-testid="order-transitions">
                <span className="text-caption text-fg-secondary">Move to:</span>
                {(o.allowedStatuses ?? []).map((s) => <Button key={s} size="sm" variant={s === "cancelled" ? "danger" : "secondary"} onClick={() => setTo(s)} data-testid={`order-to-${s}`}>{ORDER_STATUS_TEXT[s]}</Button>)}
              </div>
            )}

            <div className="grid gap-3 grid-cols-2 lg:grid-cols-6 mb-4">
              <Tile label="Quantity" value={`${num(o.quantity)} pcs`} />
              <Tile label="Allocated" value={allocations.data ? `${num(allocations.data.allocated)} pcs` : "…"} sub={allocations.data ? `${num(allocations.data.remaining)} unallocated` : undefined} />
              <Tile label="Output" value={progress.data?.order.produced == null ? "No data" : `${num(progress.data.order.produced)} pcs`} sub={progress.data?.order.producedBasis ? undefined : undefined} />
              <Tile label="Completion" value={pct(progress.data?.order.completionPct)} />
              <Tile label="Delivery deadline" value={o.dueDate} sub={progress.data?.order.daysToDeadline == null ? undefined : progress.data.order.daysToDeadline < 0 ? `${-progress.data.order.daysToDeadline} days late` : `${progress.data.order.daysToDeadline} days left`} />
              <Tile label="Priority" value={(o.priority ?? "NORMAL").charAt(0) + (o.priority ?? "NORMAL").slice(1).toLowerCase()} />
            </div>

            <div className="grid gap-4 xl:grid-cols-2">
              <Panel title="Specification" testId="order-spec">
                <div className="p-4 grid grid-cols-2 gap-3">
                  <KeyValue label="Style">{d.style.code} · {d.style.name}</KeyValue>
                  <KeyValue label="Product category">{o.productCategory ?? "—"}</KeyValue>
                  <KeyValue label="Start date">{o.startDate ?? "—"}</KeyValue>
                  <KeyValue label="Planned completion">{o.plannedCompletionDate ?? "—"}</KeyValue>
                  <KeyValue label="Factory">{o.factoryCode ?? "—"}</KeyValue>
                  <KeyValue label="SMV">{d.style.effectiveSmv == null ? "missing" : `${num(d.style.effectiveSmv, 3)} min`}</KeyValue>
                  <div className="col-span-2"><KeyValue label="Description">{o.productDescription ?? "—"}</KeyValue></div>
                  <div className="col-span-2"><KeyValue label="Notes">{o.notes ?? "—"}</KeyValue></div>
                </div>
              </Panel>
              <Panel title="Status history" subtitle="Every change with who and why" testId="order-history" flush>
                {!transitions.data ? <SkeletonRows rows={3} /> : (
                  <DataTable columns={["When", "From", "To", "By", "Reason"]}
                    rows={transitions.data.history.map((h) => [dhaka(h.changedAt), h.from ? ORDER_STATUS_TEXT[h.from as OrderStatus] ?? h.from : "—", ORDER_STATUS_TEXT[h.to as OrderStatus] ?? h.to, h.changedByEmail, h.reason ?? "—"])} />
                )}
              </Panel>
              <Panel title="Allocations" subtitle="Where this order is produced" testId="order-allocations" flush
                right={<Link href={`${PL}/allocations?factory=${factoryId}&order=${id}`} className="text-caption text-accent hover:underline">Manage</Link>}>
                {!allocations.data ? <SkeletonRows rows={3} /> : allocations.data.allocations.length === 0
                  ? <p className="p-4 text-caption text-fg-tertiary">Not allocated to any line yet.</p>
                  : (
                    <DataTable columns={["Line", "Quantity", "From", "To", "Status"]}
                      rows={allocations.data.allocations.map((a) => [a.lineName, num(a.quantity), a.plannedStart, a.plannedEnd, a.status.charAt(0) + a.status.slice(1).toLowerCase()])} />
                  )}
                {allocations.data && allocations.data.excess > 0 && <Callout tone="warning" className="m-3">Allocations exceed the order quantity by {num(allocations.data.excess)} pieces (an authorised override is on record).</Callout>}
              </Panel>
              <Panel title="Progress and forecast" subtitle="From published plans and output counters" testId="order-progress">
                <div className="p-4 space-y-3">
                  {!progress.data ? <SkeletonRows rows={3} /> : (
                    <>
                      <p className="flex flex-wrap items-center gap-2 text-caption"><ForecastBadge status={progress.data.forecast.status} /><BasisBadge basis={progress.data.order.producedBasis} />
                        {progress.data.forecast.projectedCompletion && <span>projected completion ≈ {progress.data.forecast.projectedCompletion}</span>}</p>
                      <p className="text-caption text-fg-secondary">{progress.data.forecast.note}</p>
                      {progress.data.warnings.map((w) => <p key={w} className="text-caption text-fg-tertiary">{w}</p>)}
                      {progress.data.days.length > 0 && (
                        <DataTable columns={["Date", "Target", "Output", "Achievement", "Basis"]}
                          rows={progress.data.days.map((x) => [x.date, num(x.target), x.actual == null ? "No data" : num(x.actual), pct(x.achievementPct), <BasisBadge key="b" basis={x.basis} />])} />
                      )}
                    </>
                  )}
                </div>
              </Panel>
            </div>
            <div className="mt-4"><Legend /></div>

            <OrderFormModal open={editing} onClose={() => setEditing(false)} factoryId={factoryId} styles={styles.data ?? []} order={o} onSaved={reloadAll} />
            {ctxScope && <AllocationFormModal open={allocating} onClose={() => setAllocating(false)} scope={ctxScope} orderId={id} onSaved={reloadAll} />}
            <ReasonDialog open={to !== null} onClose={() => setTo(null)} title={to ? `Set the order ${ORDER_STATUS_TEXT[to].toLowerCase()}` : ""}
              required={to === "on_hold" || to === "cancelled"} confirmLabel={to ? `Set ${ORDER_STATUS_TEXT[to].toLowerCase()}` : "Confirm"} tone={to === "cancelled" ? "danger" : "primary"}
              description={to === "cancelled" ? "Its open allocations are cancelled too. This can't be undone." : to === "completed" ? "Its open allocations are completed." : undefined} testId="order-status-dialog"
              onConfirm={async (reason) => { await planningApi.changeOrderStatus(id, { status: to!, reason: reason || undefined, expectedVersion: o.version }); announceDataChanged(); }} />
          </>
        )}
    </div>
  );
}

function Tile({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return <div className="surface p-4"><p className="text-caption text-fg-tertiary">{label}</p><p className="text-xl font-semibold tabular-nums">{value}</p>{sub && <p className="text-caption text-fg-tertiary">{sub}</p>}</div>;
}
