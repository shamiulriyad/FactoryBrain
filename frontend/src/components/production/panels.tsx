"use client";

// Orders, plans and output history tabs of /app/production. Lists are paged
// and filtered by the API; rows link to their detail pages.

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ChevronLeft, ChevronRight, ClipboardList, Plus, RefreshCw, Search } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { EmptyState } from "@/components/ui/EmptyState";
import { SkeletonRows } from "@/components/ui/Status";
import { useOnDataChanged, useResource, type Resource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { ORDER_STATUS_LABEL, PLAN_STATUS_LABEL, SOURCE_LABEL, STAGE_LABEL, fmt, fmtPct, plantDateTime, plantTime } from "@/lib/production";
import { productionPaths } from "@/services/production.api";
import {
  ORDER_STATUSES,
  OUTPUT_SOURCES,
  PLAN_STATUSES,
  PRODUCTION_STAGES,
  type OutputEvent,
  type Paged,
  type PlanListItem,
  type ProductionLine,
  type ProductionOrderListItem,
  type ProductionShift,
  type ProductionStyle
} from "@/services/contracts";
import { CorrectOutputModal, OrderFormModal, PlanFormModal } from "./forms";
import { PlanStatusBadge, RiskLevelBadge } from "./shared";

const PAGE_SIZE = 20;

function Pager<T>({ data, onPage, testId }: { data: Paged<T>; onPage: (p: number) => void; testId?: string }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-2 px-4 py-3 border-t border-border-subtle text-caption text-fg-tertiary">
      <span data-testid={testId}>Page {data.page} of {Math.max(1, data.totalPages)} · {data.total} total</span>
      <div className="flex gap-2">
        <Button size="sm" variant="secondary" disabled={data.page <= 1} onClick={() => onPage(data.page - 1)} aria-label="Previous page"><ChevronLeft size={13} /></Button>
        <Button size="sm" variant="secondary" disabled={data.page >= data.totalPages} onClick={() => onPage(data.page + 1)} aria-label="Next page"><ChevronRight size={13} /></Button>
      </div>
    </div>
  );
}

function ListState<T>({ res, empty, children }: { res: Resource<Paged<T>>; empty: React.ReactNode; children: (d: Paged<T>) => React.ReactNode }) {
  if (res.loading) return <SkeletonRows rows={5} />;
  if (res.error && !res.data)
    return <Callout tone="danger" title="Could not load the list" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>{errorMessage(res.error)}</Callout>;
  if (!res.data) return null;
  return (
    <>
      {res.error && <Callout tone="warning">Showing the last loaded list: {errorMessage(res.error)}</Callout>}
      {res.data.items.length === 0 ? empty : children(res.data)}
    </>
  );
}

// ── Orders ──────────────────────────────────────────────────────────────────

export function OrdersPanel({ operator, styles }: { operator: boolean; styles: ProductionStyle[] }) {
  const [q, setQ] = useState("");
  const [f, setF] = useState({ q: "", status: "open", page: 1 });
  useEffect(() => {
    const t = setTimeout(() => setF((x) => (x.q === q ? x : { ...x, q, page: 1 })), 300);
    return () => clearTimeout(t);
  }, [q]);
  const res = useResource<Paged<ProductionOrderListItem>>(productionPaths.orders({ ...f, pageSize: PAGE_SIZE }));
  useOnDataChanged(() => void res.reload());
  const [creating, setCreating] = useState(false);

  return (
    <div className="space-y-3">
      <div className="surface p-3 flex flex-wrap items-center gap-3" role="search">
        <div className="relative w-full sm:w-64">
          <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
          <Input aria-label="Search orders" placeholder="Order number or buyer…" className="pl-8" value={q} onChange={(e) => setQ(e.target.value)} maxLength={100} />
        </div>
        <FilterSelect label="Status" value={f.status} onChange={(v) => setF((x) => ({ ...x, status: v, page: 1 }))} testId="orders-status">
          <option value="">All</option>
          {ORDER_STATUSES.map((s) => <option key={s} value={s}>{ORDER_STATUS_LABEL[s]}</option>)}
        </FilterSelect>
        <div className="ml-auto flex gap-2">
          <Button size="sm" variant="ghost" onClick={() => void res.reload()} disabled={res.refreshing} aria-label="Refresh"><RefreshCw size={13} className={cn(res.refreshing && "animate-spin")} /></Button>
          {operator && <Button size="sm" variant="primary" iconLeft={<Plus size={13} />} onClick={() => setCreating(true)} data-testid="order-new">New order</Button>}
        </div>
      </div>
      <ListState res={res} empty={
        <div className="surface"><EmptyState icon={<ClipboardList size={20} />} title={f.q || f.status ? "No orders match" : "No production orders yet"}
          body={operator ? "Create an order for a style, then plan it on a line." : "Orders created by planners appear here."} /></div>
      }>
        {(data) => (
          <Panel flush ariaLabel="Production orders">
            <div className="overflow-x-auto">
              <table className="data-table" data-testid="orders-table">
                <thead><tr><th>Order</th><th>Style</th><th className="num">Quantity</th><th>Due</th><th>Status</th><th className="num">Sewn (good)</th><th className="num">Live plans</th><th>Delivery risk</th></tr></thead>
                <tbody>
                  {data.items.map((o) => (
                    <tr key={o.id} data-testid="order-row">
                      <td><Link href={`/app/production/orders/${o.id}`} className="font-mono text-[12px] text-accent hover:underline">{o.orderNumber}</Link>{o.buyer && <span className="block text-caption text-fg-tertiary">{o.buyer}</span>}</td>
                      <td>{o.styleCode}<span className="block text-caption text-fg-tertiary">{o.styleName}</span></td>
                      <td className="num">{fmt(o.quantity)}</td>
                      <td className="whitespace-nowrap">{o.dueDate}</td>
                      <td>{ORDER_STATUS_LABEL[o.status]}</td>
                      <td className="num">{fmt(o.producedGood)}<span className="block text-caption text-fg-tertiary">{fmtPct(o.progressPct, 0)}</span></td>
                      <td className="num">{o.activePlans}</td>
                      <td><RiskLevelBadge level={o.riskLevel} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pager data={data} onPage={(page) => setF((x) => ({ ...x, page }))} />
          </Panel>
        )}
      </ListState>
      <OrderFormModal open={creating} onClose={() => setCreating(false)} styles={styles} onSaved={() => void res.reload()} />
    </div>
  );
}

// ── Plans ───────────────────────────────────────────────────────────────────

export function PlansPanel({ operator, lines, shifts }: { operator: boolean; lines: ProductionLine[]; shifts: ProductionShift[] }) {
  const [f, setF] = useState({ status: "", lineId: "", page: 1 });
  const router = useRouter();
  const res = useResource<Paged<PlanListItem>>(productionPaths.plans({ ...f, pageSize: PAGE_SIZE }));
  useOnDataChanged(() => void res.reload());
  const [creating, setCreating] = useState(false);
  return (
    <div className="space-y-3">
      <div className="surface p-3 flex flex-wrap items-center gap-3" role="search">
        <FilterSelect label="Status" value={f.status} onChange={(v) => setF((x) => ({ ...x, status: v, page: 1 }))} testId="plans-status">
          <option value="">All</option>
          {PLAN_STATUSES.map((s) => <option key={s} value={s}>{PLAN_STATUS_LABEL[s]}</option>)}
        </FilterSelect>
        <FilterSelect label="Line" value={f.lineId} onChange={(v) => setF((x) => ({ ...x, lineId: v, page: 1 }))}>
          <option value="">All lines</option>
          {lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </FilterSelect>
        <div className="ml-auto flex gap-2">
          <Button size="sm" variant="ghost" onClick={() => void res.reload()} disabled={res.refreshing} aria-label="Refresh"><RefreshCw size={13} className={cn(res.refreshing && "animate-spin")} /></Button>
          {operator && <Button size="sm" variant="primary" iconLeft={<Plus size={13} />} onClick={() => setCreating(true)} data-testid="plan-new">New plan</Button>}
        </div>
      </div>
      <PlanTable res={res} onPage={(page) => setF((x) => ({ ...x, page }))} />
      <PlanFormModal open={creating} onClose={() => setCreating(false)} lines={lines} shifts={shifts}
        onSaved={(id) => router.push(`/app/production/plans/${id}`)} />
    </div>
  );
}

export function PlanTable({ res, onPage }: { res: Resource<Paged<PlanListItem>>; onPage?: (p: number) => void }) {
  return (
    <ListState res={res} empty={<div className="surface"><EmptyState icon={<ClipboardList size={20} />} title="No plans" body="Plans assign an order to a line for a date range with explicit planning inputs." /></div>}>
      {(data) => <PlanRows plans={data.items} pager={onPage ? <Pager data={data} onPage={onPage} /> : null} />}
    </ListState>
  );
}

export function PlanRows({ plans, pager }: { plans: PlanListItem[]; pager?: React.ReactNode }) {
  return (
    <Panel flush ariaLabel="Production plans">
      <div className="overflow-x-auto">
        <table className="data-table" data-testid="plans-table">
          <thead><tr><th>Plan</th><th>Order</th><th>Line</th><th>Dates</th><th className="num">Quantity</th><th className="num">Operators · target</th><th>Status</th></tr></thead>
          <tbody>
            {plans.map((p) => (
              <tr key={p.id} data-testid="plan-row">
                <td><Link href={`/app/production/plans/${p.id}`} className="font-mono text-[12px] text-accent hover:underline">{p.reference}</Link>{p.revision > 0 && <span className="block text-caption text-fg-tertiary">revision {p.revision}</span>}</td>
                <td><Link href={`/app/production/orders/${p.orderId}`} className="text-accent hover:underline">{p.orderNumber}</Link></td>
                <td>{p.lineName}</td>
                <td className="whitespace-nowrap">{p.startDate} → {p.endDate}</td>
                <td className="num">{fmt(p.plannedQuantity)}</td>
                <td className="num">{p.operators} · {fmtPct(p.targetEfficiencyPct, 0)}</td>
                <td><PlanStatusBadge status={p.status} />{p.hasPendingChange && <Badge tone="warning" className="ml-1">change pending</Badge>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {pager}
    </Panel>
  );
}

// ── History ─────────────────────────────────────────────────────────────────

export function HistoryPanel({ operator, lines, shifts, offset, fixed }: {
  operator: boolean;
  lines: ProductionLine[];
  shifts: ProductionShift[];
  offset: number;
  /** Lock filters (e.g. on a line or order page). */
  fixed?: { lineId?: string; orderId?: string; from?: string; to?: string };
}) {
  const [f, setF] = useState({ from: "", to: "", lineId: "", shift: "", stage: "", source: "", includeSuperseded: false, page: 1 });
  const path = useMemo(() => productionPaths.output({ ...f, ...fixed, pageSize: 25 }), [f, fixed]);
  const res = useResource<Paged<OutputEvent>>(path);
  useOnDataChanged(() => void res.reload());
  const [correcting, setCorrecting] = useState<OutputEvent | null>(null);
  const set = (patch: Partial<typeof f>) => setF((x) => ({ ...x, ...patch, page: patch.page ?? 1 }));

  return (
    <div className="space-y-3" data-testid="production-history">
      <div className="surface p-3 flex flex-wrap items-center gap-3" role="search">
        {!fixed?.from && (
          <>
            <label className="flex items-center gap-2 text-caption text-fg-tertiary">From <input type="date" className="control !h-8 !w-auto !text-caption" value={f.from} onChange={(e) => set({ from: e.target.value })} data-testid="history-from" /></label>
            <label className="flex items-center gap-2 text-caption text-fg-tertiary">To <input type="date" className="control !h-8 !w-auto !text-caption" value={f.to} onChange={(e) => set({ to: e.target.value })} /></label>
          </>
        )}
        {!fixed?.lineId && (
          <FilterSelect label="Line" value={f.lineId} onChange={(v) => set({ lineId: v })} testId="history-line">
            <option value="">All</option>
            {lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
          </FilterSelect>
        )}
        <FilterSelect label="Shift" value={f.shift} onChange={(v) => set({ shift: v })}>
          <option value="">All</option>
          {shifts.map((s) => <option key={s.code} value={s.code}>{s.name}</option>)}
          <option value="overtime">Outside shifts</option>
        </FilterSelect>
        <FilterSelect label="Stage" value={f.stage} onChange={(v) => set({ stage: v })} testId="history-stage">
          <option value="">All</option>
          {PRODUCTION_STAGES.map((s) => <option key={s} value={s}>{STAGE_LABEL[s]}</option>)}
        </FilterSelect>
        <FilterSelect label="Source" value={f.source} onChange={(v) => set({ source: v })}>
          <option value="">All</option>
          {OUTPUT_SOURCES.map((s) => <option key={s} value={s}>{SOURCE_LABEL[s]}</option>)}
        </FilterSelect>
        <label className="flex items-center gap-2 text-caption text-fg-tertiary">
          <input type="checkbox" checked={f.includeSuperseded} onChange={(e) => set({ includeSuperseded: e.target.checked })} data-testid="history-superseded" /> Show replaced records
        </label>
      </div>
      <ListState res={res} empty={<div className="surface"><EmptyState icon={<ClipboardList size={20} />} title="No output records match" body="Recorded and imported output appears here, newest first." /></div>}>
        {(data) => (
          <Panel flush ariaLabel="Output history">
            <div className="overflow-x-auto">
              <table className="data-table" data-testid="history-table">
                <thead><tr><th>Interval (plant time)</th><th>Line · order</th><th>Stage</th><th>Shift</th><th className="num">Good</th><th className="num">Rework · reject</th><th className="num">Operators</th><th className="num">Downtime</th><th>Source</th><th></th></tr></thead>
                <tbody>
                  {data.items.map((e) => (
                    <tr key={e.id} data-testid="history-row" className={cn(e.isSuperseded && "opacity-60")}>
                      <td className="whitespace-nowrap">{plantDateTime(e.intervalStart, offset)}–{plantTime(e.intervalEnd, offset)}<span className="block text-caption text-fg-tertiary">production date {e.productionDate}</span></td>
                      <td><span className="font-mono text-[12px]">{e.lineId}</span><span className="block text-caption"><Link href={`/app/production/orders/${e.orderId}`} className="text-accent hover:underline">{e.orderNumber}</Link></span></td>
                      <td>{STAGE_LABEL[e.stage]}</td>
                      <td>{e.shiftCode ?? <span className="text-fg-tertiary">outside</span>}</td>
                      <td className="num">{fmt(e.quantity)}</td>
                      <td className="num">{fmt(e.reworkQuantity)} · {fmt(e.rejectQuantity)}</td>
                      <td className="num">{e.operatorsPresent ?? "—"}</td>
                      <td className="num">{e.downtimeMinutes ? `${fmt(e.downtimeMinutes)} min` : "—"}{e.downtimeReason && <span className="block text-caption text-fg-tertiary">{e.downtimeReason}</span>}</td>
                      <td className="text-caption">
                        {SOURCE_LABEL[e.source]}{e.late && <Badge tone="warning" className="ml-1">late</Badge>}
                        {e.isSuperseded && <Badge className="ml-1">replaced</Badge>}
                        {e.correctionOfId && <Badge tone="info" className="ml-1" title={e.correctionReason ?? undefined}>correction</Badge>}
                        <span className="block text-fg-tertiary">{e.recordedByEmail}</span>
                      </td>
                      <td>{operator && !e.isSuperseded && <Button size="sm" variant="ghost" onClick={() => setCorrecting(e)} data-testid="history-correct">Correct</Button>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pager data={data} onPage={(page) => set({ page })} testId="history-page-info" />
          </Panel>
        )}
      </ListState>
      <CorrectOutputModal event={correcting} offset={offset} onClose={() => setCorrecting(null)} onSaved={() => void res.reload()} />
    </div>
  );
}
