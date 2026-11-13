"use client";

// Attendance reports: per-worker totals for a period (up to 93 days):
// days present, lateness, early departures, missing punches, attendance,
// breaks, net working time, time after shift, and approved / actual /
// HR-confirmed payable overtime. Sortable, with CSV export.

import { Fragment, useMemo, useState } from "react";
import { BarChart3, Download, RefreshCw } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { AttendanceTabs, DateFilter, tableCls, useQueryState } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { downloadCsv } from "@/lib/workers";
import { cn } from "@/lib/cn";
import { formatDate } from "@/lib/format";
import { P } from "@/lib/access";
import { OVERTIME_NOTE, hours, todayIso, type AttendanceReport, type ReportRow } from "@/lib/attendance";

type Col = { key: keyof ReportRow; label: string; hrs?: boolean };
const COLS: Col[] = [
  { key: "daysPresent", label: "Days present" },
  { key: "lateDays", label: "Late days" },
  { key: "lateMinutes", label: "Late (min)" },
  { key: "earlyDepartureDays", label: "Early-leave days" },
  { key: "missingPunches", label: "Missing punches" },
  { key: "attendanceMinutes", label: "Attendance (h)", hrs: true },
  { key: "breakMinutes", label: "Breaks (h)", hrs: true },
  { key: "netWorkingMinutes", label: "Net working (h)", hrs: true },
  { key: "beyondShiftMinutes", label: "After shift (h)", hrs: true },
  { key: "approvedOvertimeMinutes", label: "OT approved (h)", hrs: true },
  { key: "actualOvertimeMinutes", label: "OT actual (h)", hrs: true },
  { key: "payableConfirmedMinutes", label: "OT payable, HR (h)", hrs: true },
  { key: "simulatedSessions", label: "Simulated sessions" }
];

export default function AttendanceReportsPage() {
  return (
    <RequirePermission perms={[P.AttendanceView]} what="Attendance reports">
      <Reports />
    </RequirePermission>
  );
}

function Reports() {
  const q = useQueryState("/app/attendance/reports");
  const toast = useToast();
  const { factories } = useMyFactories();
  const from = q.get("from") || todayIso(-6);
  const to = q.get("to") || todayIso();
  const factory = q.get("factory");
  const line = q.get("line");
  const department = q.get("department");
  const factoryId = factory || (factories?.length === 1 ? factories[0].id : "");
  const { departments, lines } = useFactoryOrg(factoryId || null);
  const [sort, setSort] = useState<{ key: keyof ReportRow; desc: boolean }>({ key: "workerCode", desc: false });
  const [exporting, setExporting] = useState(false);

  const params = useMemo(() => {
    const p = new URLSearchParams({ from, to });
    if (factory) p.set("factoryId", factory);
    if (line) p.set("lineId", line);
    if (department) p.set("departmentId", department);
    return p.toString();
  }, [from, to, factory, line, department]);
  const report = useResource<AttendanceReport>(`/api/attendance/report?${params}`);

  const rows = useMemo(() => {
    const xs = [...(report.data?.rows ?? [])];
    xs.sort((a, b) => {
      const va = a[sort.key] ?? "";
      const vb = b[sort.key] ?? "";
      const c = typeof va === "number" && typeof vb === "number" ? va - vb : String(va).localeCompare(String(vb));
      return sort.desc ? -c : c;
    });
    return xs;
  }, [report.data, sort]);

  async function exportCsv() {
    setExporting(true);
    try {
      const n = await downloadCsv(`/api/attendance/report/export?${params}`, `attendance-${from}-${to}.csv`);
      toast.push({ tone: "success", title: "Export ready", description: n !== null ? `${n} worker rows` : undefined });
    } catch (e) {
      toast.push({ tone: "error", title: "Export failed", description: errorMessage(e) });
    } finally {
      setExporting(false);
    }
  }

  const header = (key: keyof ReportRow, label: string, right = true) => (
    <th className={cn(tableCls.th, right && "text-right")} aria-sort={sort.key === key ? (sort.desc ? "descending" : "ascending") : "none"}>
      <button type="button" className="hover:text-fg-primary" onClick={() => setSort((s) => ({ key, desc: s.key === key ? !s.desc : right }))}>
        {label}{sort.key === key ? (sort.desc ? " ↓" : " ↑") : ""}
      </button>
    </th>
  );

  return (
    <div className="page" data-testid="attendance-reports-page">
      <PageHeader title="Attendance reports" description="Per-worker totals for a period of work dates (up to 93 days). Hours are decimal (1.25 h = 1 h 15 min)." />
      <AttendanceTabs className="mt-4" />
      <div className="mt-4 flex flex-wrap items-center gap-3">
        {(factories?.length ?? 0) > 1 && (
          <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null, line: null, department: null })}>
            <option value="">All factories</option>
            {factories!.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
          </FilterSelect>
        )}
        {factoryId && (
          <>
            <FilterSelect label="Line" value={line} onChange={(v) => q.set({ line: v || null })}>
              <option value="">All</option>
              {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </FilterSelect>
            <FilterSelect label="Department" value={department} onChange={(v) => q.set({ department: v || null })}>
              <option value="">All</option>
              {(departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </FilterSelect>
          </>
        )}
        <DateFilter label="From" value={from} onChange={(v) => q.set({ from: v || null })} />
        <DateFilter label="To" value={to} onChange={(v) => q.set({ to: v || null })} />
        <div className="ml-auto flex gap-2">
          <Button size="sm" variant="secondary" iconLeft={<Download size={13} />} onClick={() => void exportCsv()} disabled={exporting || !report.data} data-testid="report-export">{exporting ? "Exporting…" : "Export CSV"}</Button>
          <Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void report.reload()}><RefreshCw size={13} className={cn(report.refreshing && "animate-spin")} /></Button>
        </div>
      </div>

      <Panel className="mt-4" flush title="Per worker" icon={<BarChart3 size={14} />}
        subtitle={report.data ? `${formatDate(report.data.from)} – ${formatDate(report.data.to)} · ${report.data.rows.length} worker${report.data.rows.length === 1 ? "" : "s"}` : undefined}>
        {report.loading ? <div className="p-4"><SkeletonRows rows={8} /></div>
          : report.error && !report.data ? <ErrorState className="m-4" title="Couldn't build the report" body={errorMessage(report.error)} retryLabel="Try again" onRetry={() => void report.reload()} />
          : rows.length === 0 ? <EmptyState className="m-4" icon={<BarChart3 size={18} />} title="No attendance in this period" body="Pick another period or scope." />
          : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[1400px]" data-testid="report-table">
                <thead className="border-b border-border-subtle">
                  <tr>
                    {header("workerCode", "Worker", false)}
                    {header("lineId", "Line", false)}
                    {COLS.map((c) => <Fragment key={c.key}>{header(c.key, c.label)}</Fragment>)}
                  </tr>
                </thead>
                <tbody className="divide-y divide-border-subtle">
                  {rows.map((r) => (
                    <tr key={r.workerId}>
                      <td className={tableCls.td}><span className="font-mono">{r.workerCode}</span> {r.workerName}</td>
                      <td className={tableCls.td}>{r.lineId ?? "—"}</td>
                      {COLS.map((c) => <td key={c.key} className={cn(tableCls.td, "text-right tabular-nums")}>{c.hrs ? hours(r[c.key] as number) : String(r[c.key])}</td>)}
                    </tr>
                  ))}
                </tbody>
                {report.data && (
                  <tfoot className="border-t-2 border-border-subtle font-medium" data-testid="report-totals">
                    <tr>
                      <td className={tableCls.td} colSpan={2}>Total</td>
                      {COLS.map((c) => <td key={c.key} className={cn(tableCls.td, "text-right tabular-nums")}>{c.hrs ? hours(report.data!.totals[c.key] as number) : String(report.data!.totals[c.key])}</td>)}
                    </tr>
                  </tfoot>
                )}
              </table>
            </div>
          )}
      </Panel>
      <Callout className="mt-4" tone="info">{OVERTIME_NOTE}</Callout>
    </div>
  );
}
