"use client";

// Production overview from GET /api/production/summary: planned vs actual,
// achievement, line efficiency, line loading, WIP, delivery risk and
// bottlenecks for a date range. Every figure is the API's; a value the API
// could not calculate shows as "—", never as 0.

import { useEffect, useState } from "react";
import Link from "next/link";
import { Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { Factory, RefreshCw } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { StatCard } from "@/components/ui/StatCard";
import { FilterSelect } from "@/components/ui/Field";
import { Callout } from "@/components/ui/Callout";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { EmptyState } from "@/components/ui/EmptyState";
import { SkeletonRows } from "@/components/ui/Status";
import { useOnDataChanged, useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { BOTTLENECK_LABEL, addDays, fmt, fmtHours, fmtPct, fmtSigned, plantDateTime, plantToday } from "@/lib/production";
import { productionPaths } from "@/services/production.api";
import type { ProductionConfig, ProductionLine, ProductionShift, ProductionSummary, SummaryRow } from "@/services/contracts";
import { Definitions, NoteList, RiskLevelBadge, SetupGaps, WipList } from "./shared";

const RANGES = [{ days: 1, label: "Today" }, { days: 7, label: "Last 7 days" }, { days: 30, label: "Last 30 days" }];

export function ProductionOverview({ config, offset, lines, shifts, admin }: {
  config: ProductionConfig | null;
  offset: number;
  lines: ProductionLine[];
  shifts: ProductionShift[];
  admin: boolean;
}) {
  const [days, setDays] = useState(7);
  const [lineId, setLineId] = useState("");
  const [shift, setShift] = useState("");
  const [range, setRange] = useState<{ from: string; to: string } | null>(null);
  useEffect(() => {
    if (!config) return;
    const to = plantToday(offset);
    setRange({ from: addDays(to, -(days - 1)), to });
  }, [config, offset, days]);

  const res = useResource<ProductionSummary>(range ? productionPaths.summary({ ...range, lineId, shift }) : null, { pollMs: 60_000 });
  useOnDataChanged(() => void res.reload());
  const s = res.data;
  const t = s?.totals;
  const empty = s && t && t.records === 0 && t.planned === 0;

  return (
    <div className="space-y-4" data-testid="production-overview">
      {config && <SetupGaps config={config} admin={admin} />}
      <div className="surface p-3 flex flex-wrap items-center gap-3" role="search">
        <FilterSelect label="Range" value={String(days)} onChange={(v) => setDays(Number(v))} testId="overview-range">
          {RANGES.map((r) => <option key={r.days} value={r.days}>{r.label}</option>)}
        </FilterSelect>
        <FilterSelect label="Line" value={lineId} onChange={setLineId} testId="overview-line">
          <option value="">All lines</option>
          {lines.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </FilterSelect>
        <FilterSelect label="Shift" value={shift} onChange={setShift} testId="overview-shift">
          <option value="">All shifts</option>
          {shifts.map((x) => <option key={x.code} value={x.code}>{x.name}</option>)}
          <option value="overtime">Overtime / outside shifts</option>
        </FilterSelect>
        {s && (
          <span className="text-caption text-fg-tertiary">
            {s.fromDate} → {s.toDate} · production dates in {s.timeZone} · compared through {plantDateTime(s.comparedThrough, offset)}
          </span>
        )}
        <Button size="sm" variant="ghost" className="ml-auto" onClick={() => void res.reload()} disabled={res.refreshing} aria-label="Refresh">
          <RefreshCw size={13} className={cn(res.refreshing && "animate-spin")} />
        </Button>
      </div>

      {res.error && s && <Callout tone="warning">Showing the last loaded figures: {errorMessage(res.error)}</Callout>}
      {res.loading || !range ? (
        <SkeletonRows rows={4} rowHeight="h-24" />
      ) : res.error && !s ? (
        <Callout tone="danger" title="Could not load the production summary" action={<Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>
          {errorMessage(res.error)}
        </Callout>
      ) : empty ? (
        <div className="surface" data-testid="overview-empty">
          <EmptyState icon={<Factory size={20} />} title="No planned or recorded output in this range"
            body="Figures appear once a plan is active and output is recorded or imported. Nothing is estimated or filled in." />
        </div>
      ) : s && t ? (
        <>
          <div className="grid grid-cols-2 lg:grid-cols-3 xl:grid-cols-6 gap-3">
            <StatCard label="Good output (sewing)" value={fmt(t.actual)} testId="stat-actual"
              hint={`planned ${fmt(t.planned)} · variance ${fmtSigned(t.variance)} pcs`} />
            <StatCard label="Achievement" value={fmtPct(t.achievementPct)} testId="stat-achievement"
              tone={t.achievementPct == null ? "neutral" : t.achievementPct >= 95 ? "good" : t.achievementPct >= 80 ? "warn" : "bad"}
              hint="actual ÷ planned, same window" />
            <StatCard label="Line efficiency" value={fmtPct(t.efficiencyPct)} testId="stat-efficiency"
              hint={`${fmt(t.earnedMinutes)} earned ÷ ${fmt(t.availableMinutes)} available min · ${fmtPct(t.efficiencyExcludingDowntimePct)} excl. downtime`} />
            <StatCard label="Line loading" value={fmtPct(t.capacityUtilizationPct)} testId="stat-loading"
              hint={`${fmtHours(t.scheduledPlanLineMinutes)} planned of ${fmtHours(t.calendarLineMinutes)} shift time`} />
            <StatCard label="Downtime" value={`${fmt(t.downtimeMinutes)} min`} hint={`${t.records} output records`} />
            <StatCard label="Rework · reject" value={`${fmt(t.rework)} · ${fmt(t.reject)}`} hint={t.unplannedActual > 0 ? `${fmt(t.unplannedActual)} pcs without a live plan` : "pieces at sewing output"} />
          </div>

          <NoteList title="Data quality" items={s.quality.notes} tone="warning" testId="overview-quality" />

          <Panel title="Planned vs actual by production date"
            subtitle={`Good sewing output and planned output (pieces) per plant-local production date, ${s.fromDate} to ${s.toDate}. Source: recorded output and active plans.`}>
            <DailyChart daily={s.daily} />
          </Panel>

          <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
            <Panel title="By line" flush><RowsTable rows={s.byLine} link={(r) => `/app/production/lines/${encodeURIComponent(r.key)}`} testId="overview-by-line" /></Panel>
            <Panel title="By shift" flush><RowsTable rows={s.byShift} testId="overview-by-shift" /></Panel>
          </div>
          <Panel title="By order" flush><RowsTable rows={s.byOrder} link={(r) => `/app/production/orders/${r.key}`} testId="overview-by-order" /></Panel>

          <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
            <Panel title="Delivery risk" subtitle="Orders with an active plan. Rule-based levels from the observed rate and remaining scheduled time, not probabilities.">
              {s.orderRisks.length === 0 ? <p className="text-caption text-fg-tertiary">No order has an active plan.</p> : (
                <ul className="divide-y divide-border-subtle" data-testid="overview-risks">
                  {s.orderRisks.map((r) => (
                    <li key={r.orderId} className="py-2 flex flex-wrap items-center justify-between gap-2">
                      <span className="min-w-0">
                        <Link href={`/app/production/orders/${r.orderId}`} className="font-mono text-[12px] text-accent hover:underline">{r.orderNumber}</Link>
                        <span className="ml-2 text-caption text-fg-tertiary">due {r.dueDate} · {fmt(r.remainingQuantity)} pcs left</span>
                      </span>
                      <span className="flex items-center gap-2 text-caption text-fg-secondary">
                        <span>needs {fmt(r.requiredRatePerLineHour, 1)}/h · observed {fmt(r.observedRatePerLineHour, 1)}/h</span>
                        <RiskLevelBadge level={r.level} />
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </Panel>
            <Panel title="Bottlenecks" subtitle="Per line and style with an active plan.">
              {s.bottlenecks.length === 0 ? <p className="text-caption text-fg-tertiary">No active plans.</p> : (
                <ul className="space-y-2" data-testid="overview-bottlenecks">
                  {s.bottlenecks.map((b) => (
                    <li key={`${b.lineId}-${b.styleId}`} className="text-body">
                      <div className="flex flex-wrap items-center gap-2">
                        <Link href={`/app/production/lines/${encodeURIComponent(b.lineId)}`} className="font-mono text-[12px] text-accent hover:underline">{b.lineId}</Link>
                        <span className="text-fg-tertiary text-caption">{b.styleCode}</span>
                        <Badge tone={b.status === "observed" ? "info" : "neutral"}>{BOTTLENECK_LABEL[b.status]}</Badge>
                      </div>
                      <p className="text-caption text-fg-secondary mt-0.5">
                        {b.operation ? `${b.operation} · about ${fmt(b.capacityPerHour, 1)} pcs/h` : ""} {b.evidence}
                      </p>
                    </li>
                  ))}
                </ul>
              )}
            </Panel>
          </div>

          <Panel title="Work in process" subtitle="Open orders, as of now, from cumulative stage counts. Buffers are never added together.">
            {s.wip.length === 0 ? <p className="text-caption text-fg-tertiary">No stage counts for open orders.</p> : (
              <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
                {s.wip.map((w) => (
                  <div key={w.lineId}>
                    <p className="font-mono text-[12px] text-fg-primary mb-1">{w.lineId}</p>
                    <WipList buffers={w.buffers} />
                  </div>
                ))}
              </div>
            )}
          </Panel>
          <p className="text-caption text-fg-tertiary">
            Sources in this window: {Object.entries(s.sources).map(([k, v]) => `${k} ${v}`).join(" · ") || "none"}.
          </p>
          <Definitions definitions={s.definitions} />
        </>
      ) : null}
    </div>
  );
}

function RowsTable({ rows, link, testId }: { rows: SummaryRow[]; link?: (r: SummaryRow) => string; testId?: string }) {
  if (rows.length === 0) return <p className="p-4 text-caption text-fg-tertiary">Nothing in this window.</p>;
  return (
    <div className="overflow-x-auto">
      <table className="data-table" data-testid={testId}>
        <thead>
          <tr><th></th><th className="num">Actual</th><th className="num">Planned</th><th className="num">Variance</th><th className="num">Achievement</th><th className="num">Efficiency</th><th className="num">Downtime</th></tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.key}>
              <td className="text-fg-primary">{link ? <Link href={link(r)} className="text-accent hover:underline">{r.label}</Link> : r.label}</td>
              <td className="num">{fmt(r.actual)}</td>
              <td className="num">{fmt(r.planned)}</td>
              <td className={cn("num", r.variance < 0 && "text-[var(--risk-high)]")}>{fmtSigned(r.variance)}</td>
              <td className="num">{fmtPct(r.achievementPct)}</td>
              <td className="num">{fmtPct(r.efficiencyPct)}</td>
              <td className="num">{fmt(r.downtimeMinutes)} min</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function DailyChart({ daily }: { daily: ProductionSummary["daily"] }) {
  if (!daily.some((d) => d.actual > 0 || d.planned > 0)) return <p className="text-caption text-fg-tertiary">Nothing planned or recorded in this range.</p>;
  const data = daily.map((d) => ({ ...d, label: d.date.slice(5) }));
  return (
    <div className="h-60" data-testid="overview-daily-chart">
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: -8 }}>
          <CartesianGrid stroke="var(--border-subtle)" strokeDasharray="3 3" vertical={false} />
          <XAxis dataKey="label" tick={{ fill: "var(--fg-tertiary)", fontSize: 11 }} tickLine={false} axisLine={{ stroke: "var(--border-subtle)" }} />
          <YAxis tick={{ fill: "var(--fg-tertiary)", fontSize: 11 }} tickLine={false} axisLine={false} width={48} unit=" pcs" />
          <Legend wrapperStyle={{ fontSize: 12 }} />
          <Tooltip
            cursor={{ fill: "var(--surface-2)" }}
            content={({ active, payload }) => {
              const p = active && (payload?.[0]?.payload as (typeof data)[number] | undefined);
              if (!p) return null;
              return (
                <div className="surface px-3 py-2 text-caption shadow-md">
                  <p className="font-medium text-fg-primary">{p.date}</p>
                  <p className="text-fg-secondary">Actual {fmt(p.actual)} · planned {fmt(p.planned)} pcs</p>
                  <p className="text-fg-tertiary">Achievement {fmtPct(p.achievementPct)} · efficiency {fmtPct(p.efficiencyPct)} · downtime {fmt(p.downtimeMinutes)} min</p>
                </div>
              );
            }}
          />
          <Bar dataKey="planned" name="Planned" fill="var(--border-strong)" isAnimationActive={false} radius={[3, 3, 0, 0]} />
          <Bar dataKey="actual" name="Actual" fill="var(--accent)" isAnimationActive={false} radius={[3, 3, 0, 0]} />
        </BarChart>
      </ResponsiveContainer>
    </div>
  );
}
