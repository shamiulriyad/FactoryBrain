"use client";

// Production monitoring: today's published plan against verified output, line
// by line and shift by shift, with order progress, delivery forecasts,
// capacity warnings and open exceptions. Everything is read from stored
// records; where a line has no evidence of output the cell says "No data".

import Link from "next/link";
import { Activity, AlertTriangle, CalendarClock, Layers, ListChecks, Truck } from "lucide-react";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Panel } from "@/components/ui/Panel";
import { StatCard, type StatTone } from "@/components/ui/StatCard";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { DataTable, TrendChart } from "@/components/analytics/shared";
import { ActualCell, BasisBadge, ForecastBadge, Legend, PL, PlanStatusBadge, SeverityBadge, StateBadge, type PlanningScope } from "@/components/planning/shared";
import { useResource } from "@/lib/useResource";
import { dhaka } from "@/lib/monitoring";
import { ORDER_STATUS_TEXT, num, pct, planningApi, type Monitor, type MonitorOrder } from "@/lib/planning";
import type { OrderStatus } from "@/services/contracts";

export function MonitorView({ scope }: { scope: PlanningScope }) {
  const { q, ctx, factoryId } = scope;
  const date = q.get("date") || ctx.today;
  const source = q.get("source") === "SIMULATED" ? "SIMULATED" : "REAL";
  const department = q.get("department");
  const floor = q.get("floor");
  const line = q.get("line");
  const r = useResource<Monitor>(planningApi.monitor({ factoryId, date, source, departmentId: department, floorId: floor, lineId: line }), { pollMs: 60_000 });
  const m = r.data;

  return (
    <>
      <div className="mb-4 flex flex-wrap items-end gap-2" data-testid="monitor-filters">
        <label className="text-caption text-fg-secondary flex flex-col gap-1">Date (factory time)
          <Input type="date" className="h-9" value={date} onChange={(e) => q.set({ date: e.target.value || null })} data-testid="monitor-date" />
        </label>
        <FilterSelect label="Department" value={department} onChange={(v) => q.set({ department: v || null })}>
          <option value="">All</option>{ctx.departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </FilterSelect>
        <FilterSelect label="Floor" value={floor} onChange={(v) => q.set({ floor: v || null })}>
          <option value="">All</option>{ctx.floors.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
        </FilterSelect>
        <FilterSelect label="Line" value={line} onChange={(v) => q.set({ line: v || null })} testId="monitor-line">
          <option value="">All</option>{ctx.lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </FilterSelect>
        <FilterSelect label="Data" value={source} onChange={(v) => q.set({ source: v === "REAL" ? null : v })} testId="monitor-source">
          <option value="REAL">Real data</option><option value="SIMULATED">Simulated data</option>
        </FilterSelect>
      </div>

      {r.error && !m ? <ErrorState title="Could not load production monitoring" body={r.error.message} retryLabel="Try again" onRetry={() => void r.reload()} />
        : !m ? <SkeletonRows rows={8} />
        : <Body m={m} ctx={ctx} onDrill={(k, v) => q.set({ [k]: v })} />}
    </>
  );
}

function Body({ m, ctx, onDrill }: { m: Monitor; ctx: PlanningScope["ctx"]; onDrill: (key: string, value: string) => void }) {
  const s = m.summary;
  const tone = (n: number, bad = true): StatTone => (n > 0 ? (bad ? "bad" : "warn") : "good");
  const achievementTone: StatTone = s.achievementPct == null ? "neutral" : s.achievementPct >= 90 ? "good" : s.achievementPct >= 70 ? "warn" : "bad";
  return (
    <div data-testid="monitor-body">
      {m.source === "SIMULATED" && <Callout tone="demo" className="mb-3">SIMULATED data from the simulators — not real production. Manual output records are ignored.</Callout>}
      <p className="mb-3 text-caption text-fg-tertiary" data-testid="monitor-scope">
        {m.factoryName} · {m.date} ({m.timeZone}) · {m.source === "SIMULATED" ? "simulated" : "real"} data · generated {dhaka(m.generatedAt)}
        {m.planStatus && <> · plan <Link href={`${PL}/plans?factory=${m.factoryId}&date=${m.date}`} className="text-accent hover:underline">{m.planReference}</Link> revision {m.planRevision} <PlanStatusBadge status={m.planStatus} /></>}
      </p>
      {m.warnings.length > 0 && <Callout tone="warning" className="mb-3" testId="monitor-warnings"><ul className="list-disc pl-4 space-y-0.5">{m.warnings.map((w) => <li key={w}>{w}</li>)}</ul></Callout>}

      <section className="grid gap-3 grid-cols-2 md:grid-cols-4 xl:grid-cols-5 mb-4" aria-label="Summary">
        <StatCard label="Active orders" value={s.activeOrders} hint="planned + in progress" testId="kpi-active-orders" />
        <StatCard label="Delayed orders" value={s.ordersDelayed} tone={tone(s.ordersDelayed)} hint="forecast past the deadline" testId="kpi-delayed" />
        <StatCard label="Due within 7 days" value={s.ordersDueSoon} tone={tone(s.ordersDueSoon, false)} testId="kpi-due-soon" />
        <StatCard label="Target today" value={num(s.targetToday)} hint="published plan" testId="kpi-target" />
        <StatCard label="Actual today" value={s.actualToday == null ? "No data" : num(s.actualToday)} hint={s.actualToday == null ? "no counter or recorded output yet" : "lines with evidence"} testId="kpi-actual" />
        <StatCard label="Target achievement" value={pct(s.achievementPct)} tone={achievementTone} hint="lines with evidence" testId="kpi-achievement" />
        <StatCard label="Remaining today" value={s.remainingToday == null ? "—" : num(s.remainingToday)} hint="unknown output counts as remaining" testId="kpi-remaining" />
        <StatCard label="Open exceptions" value={s.openExceptions} tone={tone(s.openExceptions, false)} href={`${PL}/exceptions?factory=${m.factoryId}`} testId="kpi-exceptions" />
        <StatCard label="Capacity warnings" value={s.capacityWarnings} tone={tone(s.capacityWarnings, false)} hint="estimates, not measured" testId="kpi-capacity" />
        <StatCard label="Lines without output counter" value={s.linesWithoutOutputCounter} tone={tone(s.linesWithoutOutputCounter, false)} hint={`of ${s.linesPlanned} planned`} href={`${PL}/setup?factory=${m.factoryId}`} testId="kpi-no-counter" />
      </section>

      <div className="mb-4"><Legend /></div>

      <div className="grid gap-4 xl:grid-cols-2 mb-4">
        <Panel title="Line-wise targets and actual" subtitle="Click a department, floor or line to drill down" icon={<Layers size={15} />} testId="monitor-lines" flush>
          {m.lines.length === 0
            ? <EmptyState title="Nothing planned" body={m.planStatus ? "The published plan has no items for these filters." : "Publish a daily plan to see line targets here."} />
            : (
              <DataTable testId="monitor-lines-table" columns={["Line", "Floor", "Department", "Target", "Actual", "Achievement", "Basis", "State"]}
                rows={m.lines.map((l) => [
                  <button key="l" type="button" className="text-accent hover:underline" onClick={() => onDrill("line", l.lineId)}>{l.lineName}</button>,
                  l.floorId ? <button key="f" type="button" className="hover:underline" onClick={() => onDrill("floor", l.floorId!)}>{l.floorName}</button> : "—",
                  l.departmentId ? <button key="d" type="button" className="hover:underline" onClick={() => onDrill("department", l.departmentId!)}>{l.departmentName}</button> : "—",
                  num(l.target),
                  l.actual == null ? <span key="a" className="text-fg-tertiary">No data</span> : num(l.actual),
                  pct(l.achievementPct),
                  <BasisBadge key="b" basis={l.basis} />,
                  <StateBadge key="s" state={l.state} />
                ])} />
            )}
        </Panel>
        <Panel title="Shift-wise output" icon={<CalendarClock size={15} />} testId="monitor-shifts" flush>
          {m.shifts.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No shift is planned for these filters.</p>
            : <DataTable columns={["Shift", "Items", "Target", "Actual", "Achievement", "State"]}
              rows={m.shifts.map((x) => [x.shiftName, x.items, num(x.target), x.actual == null ? "No data" : num(x.actual), pct(x.achievementPct), <StateBadge key="s" state={x.state} />])} />}
        </Panel>
        <Panel title="Hourly output" subtitle="Verified counter pieces at the output counters (factory time)" icon={<Activity size={15} />} testId="monitor-hourly">
          <div className="p-4"><TrendChart testId="monitor-hourly-chart" points={m.hourly.map((h) => ({ key: h.hour, label: h.label, start: h.hour, value: h.pieces }))} /></div>
        </Panel>
        <Panel title="Capacity warnings and exceptions" icon={<AlertTriangle size={15} />} testId="monitor-exceptions" flush
          right={<Link href={`${PL}/exceptions?factory=${m.factoryId}`} className="text-caption text-accent hover:underline">All exceptions</Link>}>
          {m.exceptions.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No open exception. (Run the check on the Exceptions page.)</p>
            : (
              <ul className="divide-y divide-border-subtle">
                {m.exceptions.slice(0, 8).map((e) => (
                  <li key={e.id} className="px-4 py-2 text-caption">
                    <span className="flex items-center gap-2"><SeverityBadge severity={e.severity} /><span className="font-medium">{e.title}</span></span>
                    <span className="block text-fg-secondary">{e.reason}</span>
                  </li>
                ))}
              </ul>
            )}
        </Panel>
      </div>

      <div className="grid gap-4 mb-4">
        <Panel title="Active production orders" subtitle="Output counts only in shifts a published plan gave the order" icon={<Truck size={15} />} testId="monitor-orders" flush>
          {m.orders.length === 0 ? <EmptyState title="No active orders" body="Planned and in-progress orders appear here." />
            : <OrderTable orders={m.orders} />}
        </Panel>
        {m.upcomingDeadlines.length > 0 && (
          <Panel title="Upcoming delivery deadlines" subtitle="Within 14 days" icon={<CalendarClock size={15} />} testId="monitor-deadlines" flush>
            <OrderTable orders={m.upcomingDeadlines} />
          </Panel>
        )}
        <Panel title="Items in today's plan" subtitle="Order · line · shift" icon={<ListChecks size={15} />} testId="monitor-items" flush>
          {m.items.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No plan items for these filters.</p>
            : (
              <DataTable columns={["Order", "Line", "Shift", "Target", "Per hour", "Actual", "Expected by now", "Remaining", "State"]}
                rows={m.items.map((i) => [
                  <Link key="o" href={`${PL}/orders/${i.orderId}?factory=${m.factoryId}`} className="font-mono text-[12px] text-accent hover:underline">{i.orderNumber}</Link>,
                  i.lineName, i.shiftName, num(i.target), i.hourlyTarget == null ? "—" : num(i.hourlyTarget, 1), <ActualCell key="a" a={i.actual} />,
                  i.actual.expectedByNow == null ? "—" : num(i.actual.expectedByNow), i.actual.remaining == null ? "—" : num(i.actual.remaining), <StateBadge key="s" state={i.actual.state} />
                ])} />
            )}
        </Panel>
        <Panel title="Line allocation overview" subtitle="Planned and active allocations covering this date" testId="monitor-allocations" flush>
          {m.allocations.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No allocation covers this date for these filters.</p>
            : <DataTable columns={["Line", "Order", "Quantity", "From", "To", "Priority", "Status"]}
              rows={m.allocations.map((a) => [a.lineName, <Link key="o" href={`${PL}/orders/${a.orderId}?factory=${m.factoryId}`} className="font-mono text-[12px] text-accent hover:underline">{a.orderNumber}</Link>, num(a.quantity), a.plannedStart, a.plannedEnd, a.priority, a.status])} />}
        </Panel>
      </div>

      <details className="text-caption mb-6" data-testid="monitor-definitions">
        <summary className="cursor-pointer font-medium">How these numbers are calculated</summary>
        <ul className="mt-2 list-disc pl-5 space-y-1 text-fg-secondary">{m.definitions.map((d) => <li key={d}>{d}</li>)}</ul>
      </details>
      <span className="sr-only">{ctx.factoryName}</span>
    </div>
  );
}

function OrderTable({ orders }: { orders: MonitorOrder[] }) {
  return (
    <DataTable testId="monitor-orders-table" columns={["Order", "Status", "Priority", "Deadline", "Quantity", "Allocated", "Output", "Completion", "Forecast"]}
      rows={orders.map((o) => [
        <span key="o"><Link href={`${PL}/orders/${o.orderId}`} className="font-mono text-[12px] text-accent hover:underline">{o.orderNumber}</Link>{o.buyer && <span className="block text-fg-tertiary">{o.buyer}</span>}</span>,
        ORDER_STATUS_TEXT[o.status as OrderStatus] ?? o.status, o.priority,
        <span key="d">{o.deliveryDeadline}<span className="block text-fg-tertiary">{o.daysToDeadline == null ? "" : o.daysToDeadline < 0 ? `${-o.daysToDeadline} days late` : `${o.daysToDeadline} days left`}</span></span>,
        num(o.quantity), num(o.allocated),
        o.produced == null ? <span key="p" className="text-fg-tertiary">No data</span> : <span key="p">{num(o.produced)} <BasisBadge basis={o.producedBasis} /></span>,
        pct(o.completionPct),
        <span key="f" title={o.forecastNote}><ForecastBadge status={o.forecastStatus} />{o.projectedCompletion && <span className="block text-fg-tertiary">≈ {o.projectedCompletion}</span>}</span>
      ])} />
  );
}
