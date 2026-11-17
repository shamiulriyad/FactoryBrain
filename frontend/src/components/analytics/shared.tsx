"use client";

// Factory analytics building blocks: section tabs, the filter bar shared by
// every page (kept in the URL), the scope line, KPI cards that show their
// definition, kind and denominator (and "no data" instead of zero), bar
// lists with drill-down, trend columns and plain tables.

import Link from "next/link";
import { usePathname, useSearchParams } from "next/navigation";
import { useMemo } from "react";
import { Info } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { useFactoryOrg, useMyFactories } from "@/components/workers/shared";
import { useQueryState } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { P, type PagedResult } from "@/lib/access";
import { cn } from "@/lib/cn";
import { dhaka, dhakaToday, qs } from "@/lib/monitoring";
import { KIND_LABEL, kpiText, type AnalyticsScope, type Kpi, type MetricDefinition, type TrendPoint } from "@/lib/analytics";

export const AN = "/app/analytics";

const SECTIONS: { href: string; label: string; perms: string[]; exact?: boolean }[] = [
  { href: AN, label: "Factory overview", perms: [P.ProductionView, P.MonitoringView, P.DevicesView, P.AttendanceView, P.OvertimeView, P.WorkersView, P.ReportsView], exact: true },
  { href: `${AN}/production`, label: "Production", perms: [P.ProductionView, P.MonitoringView] },
  { href: `${AN}/workforce`, label: "Workforce", perms: [P.AttendanceView, P.OvertimeView, P.WorkersView] },
  { href: `${AN}/machines`, label: "Machine performance", perms: [P.MonitoringView, P.DevicesView, P.ProductionView] },
  { href: `${AN}/activity`, label: "AI activity insights", perms: [P.MonitoringView] },
  { href: `${AN}/insights`, label: "Operational insights", perms: [P.ProductionView, P.MonitoringView, P.DevicesView, P.AttendanceView, P.OvertimeView, P.WorkersView, P.ReportsView] },
  { href: `${AN}/reports`, label: "Reports & exports", perms: [P.ProductionView, P.MonitoringView, P.DevicesView, P.AttendanceView, P.OvertimeView, P.WorkersView, P.ReportsView] }
];

export const ANALYTICS_PERMS = SECTIONS[0].perms;

function Tab({ s, active }: { s: (typeof SECTIONS)[number]; active: boolean }) {
  const allowed = useCan(...s.perms);
  if (!allowed) return null;
  return (
    <Link href={s.href} className={cn("px-3 h-9 inline-flex items-center text-caption border-b-2 -mb-px whitespace-nowrap",
      active ? "border-[var(--accent)] text-fg-primary font-medium" : "border-transparent text-fg-secondary hover:text-fg-primary")}>{s.label}</Link>
  );
}

export function AnalyticsTabs() {
  const path = usePathname() ?? "";
  const params = useSearchParams()?.toString() ?? "";
  const search = params ? `?${params}` : "";
  return (
    <nav aria-label="Analytics sections" className="mb-4 flex gap-1 overflow-x-auto border-b border-border-subtle" data-testid="analytics-tabs">
      {SECTIONS.map((s) => <Tab key={s.href} s={{ ...s, href: `${s.href}${search}` }} active={s.exact ? path === s.href : path.startsWith(s.href)} />)}
    </nav>
  );
}

export type Filters = {
  factory: string; department: string; floor: string; line: string; workstation: string; machine: string; shift: string; from: string; to: string;
  source: string; eventSource: string; family: string;
};

/** The filters from the URL (shared by every analytics page), and the API query they mean. */
export function useAnalyticsFilters(base: string) {
  const q = useQueryState(base);
  const { factories } = useMyFactories();
  const factory = q.get("factory") || (factories && factories.length === 1 ? factories[0].id : "");
  const f: Filters = {
    factory, department: q.get("department"), floor: q.get("floor"), line: q.get("line"), workstation: q.get("workstation"), machine: q.get("machine"),
    shift: q.get("shift"), from: q.get("from"), to: q.get("to"), source: q.get("source") || "REAL", eventSource: q.get("eventSource"), family: q.get("family")
  };
  const query = (extra: Record<string, string | null> = {}) => qs({
    factoryId: f.factory, departmentId: f.department, floorId: f.floor, lineId: f.line, workstationId: f.workstation, machineId: f.machine, shiftId: f.shift,
    from: f.from, to: f.to, source: f.source === "REAL" ? null : f.source, eventSource: f.eventSource, family: f.family, ...extra
  });
  return { f, set: q.set, query, factories };
}

type Show = { machine?: boolean; shift?: boolean; workstation?: boolean; eventSource?: boolean; family?: boolean };

export function AnalyticsFilterBar({ f, set, factories, show = {} }: {
  f: Filters; set: (v: Record<string, string | null>) => void; factories: { id: string; code: string; name: string }[] | null; show?: Show;
}) {
  const org = useFactoryOrg(f.factory || null);
  const ws = useResource<PagedResult<{ id: string; code: string; lineId: string }>>(show.workstation !== false && f.factory ? `/api/workstations?factoryId=${f.factory}&pageSize=200` : null);
  const machines = useResource<PagedResult<{ id: string; code: string }>>(show.machine && f.factory ? `/api/iot/machines?factoryId=${f.factory}&pageSize=500${f.source === "SIMULATED" ? "&source=SIMULATED" : ""}` : null);
  const shifts = useResource<{ id: string; name: string }[]>(show.shift && f.factory ? `/api/shifts?factoryId=${f.factory}&includeInactive=false` : null);
  const today = dhakaToday();
  return (
    <div className="mb-4 flex flex-wrap items-end gap-2" data-testid="analytics-filters">
      {factories && factories.length > 1 && (
        <FilterSelect label="Factory" value={f.factory} onChange={(v) => set({ factory: v || null, department: null, floor: null, line: null, workstation: null, machine: null, shift: null })}>
          <option value="">All my factories</option>{factories.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
        </FilterSelect>
      )}
      {f.factory && (
        <>
          <FilterSelect label="Department" value={f.department} onChange={(v) => set({ department: v || null })}>
            <option value="">All</option>{(org.departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </FilterSelect>
          <FilterSelect label="Floor" value={f.floor} onChange={(v) => set({ floor: v || null })}>
            <option value="">All</option>{(org.floors.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </FilterSelect>
          <FilterSelect label="Line" value={f.line} onChange={(v) => set({ line: v || null, workstation: null })} testId="analytics-line">
            <option value="">All</option>{(org.lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
          </FilterSelect>
          {show.workstation !== false && (
            <FilterSelect label="Workstation" value={f.workstation} onChange={(v) => set({ workstation: v || null })}>
              <option value="">All</option>{(ws.data?.items ?? []).filter((w) => !f.line || w.lineId === f.line).map((w) => <option key={w.id} value={w.id}>{w.code}</option>)}
            </FilterSelect>
          )}
          {show.machine && (
            <FilterSelect label="Machine" value={f.machine} onChange={(v) => set({ machine: v || null })}>
              <option value="">All</option>{(machines.data?.items ?? []).map((m) => <option key={m.id} value={m.id}>{m.code}</option>)}
            </FilterSelect>
          )}
          {show.shift && (shifts.data?.length ?? 0) > 0 && (
            <FilterSelect label="Shift" value={f.shift} onChange={(v) => set({ shift: v || null })}>
              <option value="">All</option>{shifts.data!.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </FilterSelect>
          )}
        </>
      )}
      <label className="text-caption text-fg-secondary flex flex-col gap-1">From
        <Input type="date" className="h-9" value={f.from} max={today} onChange={(e) => set({ from: e.target.value || null })} data-testid="analytics-from" />
      </label>
      <label className="text-caption text-fg-secondary flex flex-col gap-1">To
        <Input type="date" className="h-9" value={f.to} max={today} onChange={(e) => set({ to: e.target.value || null })} data-testid="analytics-to" />
      </label>
      {show.eventSource !== false && (
        <FilterSelect label="Event source" value={f.eventSource} onChange={(v) => set({ eventSource: v || null })}>
          <option value="">Any</option><option value="DEVICE">Devices</option><option value="MANUAL">Manual</option><option value="SIMULATOR">Simulator</option>
        </FilterSelect>
      )}
      {show.family && (
        <FilterSelect label="Activity model" value={f.family} onChange={(v) => set({ family: v || null })}>
          <option value="">Posture (generic)</option><option value="garment">Garment activity</option>
        </FilterSelect>
      )}
      <FilterSelect label="Data" value={f.source} onChange={(v) => set({ source: v === "REAL" ? null : v })} testId="analytics-source">
        <option value="REAL">Real data</option><option value="SIMULATED">Simulated data</option>
      </FilterSelect>
    </div>
  );
}

/** Scope, period, time zone, data source and warnings of a response. */
export function ScopeLine({ scope, warnings }: { scope: AnalyticsScope; warnings?: string[] }) {
  return (
    <div className="mb-4 space-y-2" data-testid="analytics-scope">
      {scope.source === "SIMULATED" && <Callout tone="demo">SIMULATED data from the simulators — not real production, machines or attendance.</Callout>}
      <p className="text-caption text-fg-tertiary">
        {scope.scope} · {scope.from === scope.to ? scope.from : `${scope.from} – ${scope.to}`} ({scope.timeZone}) · {scope.source === "SIMULATED" ? "simulated" : "real"} data
        {scope.filters.length > 0 && <> · {scope.filters.map((x) => `${x.name}: ${x.value}`).join(" · ")}</>} · generated {dhaka(scope.generatedAt)}
      </p>
      {(warnings ?? []).length > 0 && (
        <Callout tone="warning" testId="analytics-warnings"><ul className="list-disc pl-4 space-y-0.5">{warnings!.map((w) => <li key={w}>{w}</li>)}</ul></Callout>
      )}
    </div>
  );
}

/** Metric definitions, loaded once per page. */
export function useDefinitions(): Record<string, MetricDefinition> {
  const r = useResource<MetricDefinition[]>("/api/analytics/metrics");
  return useMemo(() => Object.fromEntries((r.data ?? []).map((d) => [d.id, d])), [r.data]);
}

export function KpiCard({ k, defs, testId }: { k: Kpi; defs: Record<string, MetricDefinition>; testId?: string }) {
  const text = kpiText(k);
  const d = defs[k.metricId];
  const title = d ? `${d.formula}\nWindow: ${d.window}\nIncludes: ${d.includes}\nExcludes: ${d.excludes}${d.denominator ? `\nDenominator: ${d.denominator}` : ""}\nMissing data: ${d.missingData}` : undefined;
  return (
    <div className="rounded-[var(--radius-card,12px)] border border-border-subtle bg-surface-1 px-4 py-3 min-w-0" data-testid={testId ?? `kpi-${k.metricId}`}>
      <div className="flex items-start justify-between gap-2">
        <p className="text-caption text-fg-secondary truncate" title={k.label}>{k.label}</p>
        <span className="inline-flex items-center gap-1 shrink-0">
          {k.kind === "AI_ESTIMATE" && <Badge tone="accent">AI estimate</Badge>}
          {title && <span title={title} aria-label={`How ${k.label} is calculated: ${title}`} className="text-fg-tertiary cursor-help"><Info size={13} /></span>}
        </span>
      </div>
      {text !== null
        ? <p className="text-2xl font-semibold tabular-nums">{text}</p>
        : <p className="text-body text-fg-tertiary py-1">{k.availability === "NOT_PERMITTED" ? "Not available for your role" : "No data"}</p>}
      <p className="text-caption text-fg-tertiary truncate" title={k.note ?? undefined}>
        {k.denominator != null && k.numerator != null && k.availability === "OK" ? `${k.numerator} of ${k.denominator} · ` : ""}{k.note ?? KIND_LABEL[k.kind]}
      </p>
    </div>
  );
}

export function KpiGrid({ kpis, defs, className }: { kpis: Kpi[]; defs: Record<string, MetricDefinition>; className?: string }) {
  return <section className={cn("grid gap-3 grid-cols-2 md:grid-cols-3 xl:grid-cols-4 mb-4", className)}>{kpis.map((k) => <KpiCard key={k.metricId} k={k} defs={defs} />)}</section>;
}

/** Horizontal bars; clicking a row drills down (e.g. filters to that line). */
export function BarList<T>({ rows, value, label, extra, onSelect, empty = "No data in this range.", testId }: {
  rows: T[]; value: (r: T) => number; label: (r: T) => string; extra?: (r: T) => string | null; onSelect?: (r: T) => void; empty?: string; testId?: string;
}) {
  const max = Math.max(1, ...rows.map((r) => Math.abs(value(r))));
  if (rows.length === 0) return <p className="text-caption text-fg-tertiary">{empty}</p>;
  return (
    <ul className="space-y-1.5" data-testid={testId}>
      {rows.map((r, i) => {
        const v = value(r);
        const body = (
          <>
            <span className="truncate text-left" title={label(r)}>{label(r)}</span>
            <span className="h-2 rounded-full bg-surface-2 overflow-hidden"><span className="block h-full rounded-full bg-[var(--accent)]" style={{ width: `${(Math.abs(v) / max) * 100}%` }} /></span>
            <span className="tabular-nums font-medium text-right">{v.toLocaleString("en-US")}{extra?.(r) ? <span className="text-fg-tertiary font-normal"> · {extra(r)}</span> : null}</span>
          </>
        );
        return (
          <li key={i}>
            {onSelect
              ? <button type="button" onClick={() => onSelect(r)} className="w-full grid grid-cols-[minmax(0,10rem)_1fr_auto] items-center gap-2 text-caption hover:bg-surface-2 rounded px-1" title="Drill down">{body}</button>
              : <div className="grid grid-cols-[minmax(0,10rem)_1fr_auto] items-center gap-2 text-caption px-1">{body}</div>}
          </li>
        );
      })}
    </ul>
  );
}

/** Columns over time, with an optional planned marker per point. */
export function TrendChart({ points, unit = "", testId }: { points: TrendPoint[]; unit?: string; testId?: string }) {
  const max = Math.max(1, ...points.map((p) => Math.max(p.value, p.planned ?? 0)));
  if (points.length === 0 || points.every((p) => p.value === 0 && !p.planned)) return <p className="text-caption text-fg-tertiary">No data in this range.</p>;
  return (
    <div className="flex items-end gap-1 h-40 overflow-x-auto" data-testid={testId} role="img" aria-label={points.map((p) => `${p.label}: ${p.value}${unit}`).join(", ")}>
      {points.map((p) => (
        <div key={p.key} className="relative flex flex-col items-center justify-end h-full min-w-[24px] flex-1"
          title={`${p.label}: ${p.value.toLocaleString("en-US")}${unit}${p.planned != null ? ` · planned ${p.planned.toLocaleString("en-US")}` : ""}`}>
          <span className="text-[10px] tabular-nums text-fg-tertiary">{p.value || ""}</span>
          <span className="w-full rounded-t bg-[var(--accent)]" style={{ height: `${(p.value / max) * 78}%`, minHeight: p.value ? 2 : 0 }} />
          {p.planned != null && p.planned > 0 && <span className="absolute left-0 right-0 border-t-2 border-dashed border-[var(--risk-medium)]" style={{ bottom: `calc(${(p.planned / max) * 78}% + 14px)` }} />}
          <span className="text-[10px] text-fg-tertiary mt-0.5 whitespace-nowrap">{p.label}</span>
        </div>
      ))}
    </div>
  );
}

export function DataTable({ columns, rows, empty = "No data.", testId }: { columns: string[]; rows: (string | number | null | undefined | React.ReactNode)[][]; empty?: string; testId?: string }) {
  if (rows.length === 0) return <p className="p-4 text-caption text-fg-tertiary">{empty}</p>;
  return (
    <div className="overflow-x-auto" data-testid={testId}>
      <table className="w-full text-caption">
        <thead><tr className="text-left text-fg-tertiary">{columns.map((c) => <th key={c} className="px-3 py-2 font-medium whitespace-nowrap">{c}</th>)}</tr></thead>
        <tbody>{rows.map((r, i) => <tr key={i} className="border-t border-border-subtle align-top">{r.map((c, j) => <td key={j} className="px-3 py-1.5 tabular-nums">{c ?? "—"}</td>)}</tr>)}</tbody>
      </table>
    </div>
  );
}

export const pct = (v: number | null | undefined) => (v == null ? "—" : `${v} %`);
export const hrs = (v: number | null | undefined) => (v == null ? "—" : `${v.toFixed(1)} h`);
