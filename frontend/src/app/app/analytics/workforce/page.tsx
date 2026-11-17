"use client";

// Workforce analytics: attendance trends, shifts, departments, lines,
// overtime (approved vs actual), assignments — and the counts that look
// alike but are different things, side by side and never added together.

import { CalendarCheck, Clock, Layers, Users } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { AN, AnalyticsFilterBar, AnalyticsTabs, DataTable, KpiGrid, ScopeLine, TrendChart, pct, useAnalyticsFilters, useDefinitions } from "@/components/analytics/shared";
import { useResource } from "@/lib/useResource";
import { P } from "@/lib/access";
import { KIND_LABEL, type WorkforceAnalytics, type WorkforceGroupRow } from "@/lib/analytics";

export default function WorkforceAnalyticsPage() {
  return (
    <RequirePermission perms={[P.AttendanceView, P.OvertimeView, P.WorkersView]} what="Workforce analytics">
      <Workforce />
    </RequirePermission>
  );
}

const groupRows = (rows: WorkforceGroupRow[], onSelect?: (r: WorkforceGroupRow) => void) => rows.map((r) => [
  onSelect ? <button key={r.key} type="button" className="text-[var(--accent)]" onClick={() => onSelect(r)}>{r.label}</button> : r.label,
  r.workers, r.scheduledStarted, r.scheduledPresent, r.present, pct(r.ratePct), r.late, r.earlyDeparture, r.assigned, pct(r.assignmentCoveragePct),
  r.plannedOperators > 0 ? `${r.presentToday} / ${r.plannedOperators}` : "—", r.staffingNote,
]);
const groupColumns = ["Name", "Workers", "Shift started", "Scheduled present", "Present", "Attendance", "Late", "Early leave", "Assigned", "Assignment", "Today / planned", "Staffing"];

function Workforce() {
  const { f, set, query, factories } = useAnalyticsFilters(`${AN}/workforce`);
  const defs = useDefinitions();
  const r = useResource<WorkforceAnalytics>(`/api/analytics/workforce${query()}`, { pollMs: 60_000 });
  const d = r.data;
  return (
    <div className="page" data-testid="analytics-workforce">
      <PageHeader eyebrow="Factory analytics" title="Workforce analytics"
        description="Attendance from punches and shift schedules, overtime and workstation assignments. A camera detection is never an attendance record." />
      <AnalyticsTabs />
      <AnalyticsFilterBar f={f} set={set} factories={factories} show={{ shift: true }} />
      {r.error && !d ? <ErrorState title="Could not load workforce analytics" body={r.error.message} retryLabel="Try again" onRetry={() => void r.reload()} />
        : !d ? <SkeletonRows rows={8} />
        : (
          <>
            <ScopeLine scope={d.scope} warnings={d.warnings} />
            <KpiGrid kpis={d.kpis} defs={defs} />
            <Panel className="mb-4" title="Different things, kept apart" subtitle="These counts are never added together or turned into a score" icon={<Layers size={15} />} testId="workforce-concepts">
              <div className="p-4 grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
                {d.concepts.map((c) => (
                  <div key={c.id} className="rounded border border-border-subtle p-3">
                    <p className="flex items-center justify-between gap-2 text-caption text-fg-secondary">{c.label}<Badge tone={c.kind === "AI_ESTIMATE" ? "accent" : "neutral"}>{KIND_LABEL[c.kind]}</Badge></p>
                    <p className="text-xl font-semibold tabular-nums">{c.value.toLocaleString("en-US")}</p>
                    <p className="text-caption text-fg-tertiary">{c.note}</p>
                  </div>
                ))}
              </div>
            </Panel>
            <div className="grid gap-4 lg:grid-cols-2 mb-4">
              <Panel title="Daily attendance" subtitle="Scheduled workers who checked in (%)" icon={<CalendarCheck size={15} />}>
                <div className="p-4"><TrendChart points={d.daily.map((x) => ({ key: x.date, label: x.date.slice(5), start: x.date, value: x.ratePct ?? 0 }))} unit=" %" /></div>
              </Panel>
              <Panel title="Late arrivals and early departures" icon={<Clock size={15} />}>
                <DataTable columns={["Date", "Late", "Early departure", "Break min", "Over-break min", "Needs review"]}
                  rows={d.daily.map((x) => [x.date, x.late, x.earlyDeparture, x.breakMinutes, x.overBreakMinutes, x.needsReview])} />
              </Panel>
            </div>
            <Panel className="mb-4" title="Daily detail" testId="workforce-daily">
              <DataTable columns={["Date", "Scheduled", "Shift started", "Scheduled present", "Present", "Absent", "Attendance"]}
                rows={d.daily.map((x) => [x.date, x.scheduled, x.scheduledStarted, x.scheduledPresent, x.present, x.absent, pct(x.ratePct)])} />
            </Panel>
            <Panel className="mb-4" title="By line" subtitle="Click a line to drill down · planned operators come from active production plans" icon={<Users size={15} />} testId="workforce-lines">
              <DataTable columns={groupColumns} rows={groupRows(d.byLine, (x) => set({ line: x.key }))} empty="No line data." />
            </Panel>
            <div className="grid gap-4 xl:grid-cols-2 mb-4">
              <Panel title="By shift"><DataTable columns={groupColumns} rows={groupRows(d.byShift, (x) => set({ shift: x.key }))} empty="No shift data." /></Panel>
              <Panel title="By department"><DataTable columns={groupColumns} rows={groupRows(d.byDepartment, (x) => set({ department: x.key }))} empty="No department data." /></Panel>
            </div>
            <div className="grid gap-4 lg:grid-cols-2">
              <Panel title="Overtime: approved vs actual" subtitle="Actual comes from punches inside the approved window" testId="workforce-overtime">
                <DataTable columns={["Requests", "Approved", "Pending", "Rejected", "Approved", "Actual", "Actual ÷ approved", "Workers"]}
                  rows={[[d.overtime.requests, d.overtime.approved, d.overtime.pending, d.overtime.rejected, `${Math.round(d.overtime.approvedMinutes / 6) / 10} h`,
                    `${Math.round(d.overtime.actualMinutes / 6) / 10} h`, pct(d.overtime.actualVsApprovedPct), d.overtime.workersWithOvertime]]} />
              </Panel>
              <Panel title="Daily overtime">
                <div className="p-4"><TrendChart points={d.overtimeDaily.map((x) => ({ key: x.date, label: x.date.slice(5), start: x.date, value: Math.round(x.actualMinutes / 6) / 10,
                  planned: Math.round(x.approvedMinutes / 6) / 10 }))} unit=" h" /></div>
                <p className="px-4 pb-3 text-caption text-fg-tertiary">Bars: actual hours · dashed: approved hours.</p>
              </Panel>
            </div>
          </>
        )}
    </div>
  );
}
