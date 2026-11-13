"use client";

// Attendance dashboard: today's (or a chosen day's) figures for the caller's
// scope, who is checked in, overdue breaks, sessions needing review, and
// quick manual punches. Every figure comes from the API; nothing is
// estimated in the browser.

import Link from "next/link";
import { useMemo, useState } from "react";
import { AlertTriangle, Clock, Coffee, FlaskConical, LogIn, LogOut, RefreshCw, Timer, UserCheck, UserX, Users } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { StatCard } from "@/components/ui/StatCard";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { AttendanceTabs, BreakTable, DateFilter, PunchDialog, SessionTable, useQueryState, type PunchAction } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDate } from "@/lib/format";
import { P, type PagedResult } from "@/lib/access";
import { SIMULATED_NOTE, type AttendanceSession, type AttendanceStatistics } from "@/lib/attendance";

export default function AttendanceDashboardPage() {
  return (
    <RequirePermission perms={[P.AttendanceView]} what="Attendance">
      <Dashboard />
    </RequirePermission>
  );
}

function Dashboard() {
  const q = useQueryState("/app/attendance");
  const { factories } = useMyFactories();
  const factoryParam = q.get("factory");
  const factoryId = factoryParam || (factories?.length === 1 ? factories[0].id : "");
  const line = q.get("line");
  const date = q.get("date");
  const { lines } = useFactoryOrg(factoryId || null);
  const canRecord = useCan(P.AttendanceRecord);
  const [punch, setPunch] = useState<PunchAction | null>(null);

  const scope = useMemo(() => {
    const p = new URLSearchParams();
    if (factoryParam) p.set("factoryId", factoryParam);
    if (line) p.set("lineId", line);
    return p;
  }, [factoryParam, line]);

  const statsPath = useMemo(() => {
    const p = new URLSearchParams(scope);
    if (date) p.set("date", date);
    return `/api/attendance/statistics?${p}`;
  }, [scope, date]);
  const stats = useResource<AttendanceStatistics>(statsPath, { pollMs: 60_000 });
  const day = stats.data?.date ?? date;

  const sessionsPath = (extra: Record<string, string>) => {
    const p = new URLSearchParams(scope);
    for (const [k, v] of Object.entries(extra)) p.set(k, v);
    p.set("pageSize", "10");
    return `/api/attendance?${p}`;
  };
  const open = useResource<PagedResult<AttendanceSession>>(sessionsPath({ status: "Open", sort: "checkIn" }), { pollMs: 60_000 });
  const review = useResource<PagedResult<AttendanceSession>>(sessionsPath({ needsReview: "true", sort: "-date" }));

  function reloadAll() {
    void stats.reload();
    void open.reload();
    void review.reload();
  }

  const s = stats.data;
  const err = stats.error && !s ? errorMessage(stats.error) : null;

  return (
    <div className="page" data-testid="attendance-dashboard">
      <PageHeader
        title="Attendance"
        description="Check-ins, breaks and overtime across your scope. Work dates, lateness and overtime follow each factory's time zone (Asia/Dhaka by default)."
        actions={canRecord ? (
          <div className="flex flex-wrap gap-2">
            <Button variant="primary" size="sm" iconLeft={<LogIn size={13} />} onClick={() => setPunch("check-in")} data-testid="quick-check-in">Check in</Button>
            <Button variant="secondary" size="sm" iconLeft={<LogOut size={13} />} onClick={() => setPunch("check-out")} data-testid="quick-check-out">Check out</Button>
            <Button variant="secondary" size="sm" iconLeft={<Coffee size={13} />} onClick={() => setPunch("break-start")}>Start break</Button>
            <Button variant="secondary" size="sm" onClick={() => setPunch("break-end")}>End break</Button>
          </div>
        ) : undefined}
      />
      <AttendanceTabs className="mt-4" />

      <div className="mt-4 flex flex-wrap items-center gap-3">
        {(factories?.length ?? 0) > 1 && (
          <FilterSelect label="Factory" value={factoryParam} onChange={(v) => q.set({ factory: v || null, line: null })}>
            <option value="">All factories</option>
            {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        )}
        {factoryId && (
          <FilterSelect label="Line" value={line} onChange={(v) => q.set({ line: v || null })}>
            <option value="">All lines</option>
            {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
          </FilterSelect>
        )}
        <DateFilter label="Day" value={date} onChange={(v) => q.set({ date: v || null })} />
        <span className="text-caption text-fg-tertiary">{day ? `Showing ${formatDate(day)}` : ""}</span>
        <Button size="sm" variant="secondary" className="ml-auto" aria-label="Refresh" onClick={reloadAll}>
          <RefreshCw size={13} className={cn(stats.refreshing && "animate-spin")} />
        </Button>
      </div>

      {err && <ErrorState className="mt-4" title="Couldn't load attendance figures" body={err} retryLabel="Try again" onRetry={() => void stats.reload()} />}

      <div className="mt-4 grid gap-3 grid-cols-2 md:grid-cols-4 xl:grid-cols-6" data-testid="attendance-stats">
        <StatCard label="Scheduled" value={s?.scheduledWorkers ?? "—"} hint="Workers with a shift that day" icon={<Users size={14} />} loading={stats.loading} testId="stat-scheduled" />
        <StatCard label="Present" value={s?.present ?? "—"} tone="good" icon={<UserCheck size={14} />} loading={stats.loading} href="/app/attendance/records" testId="stat-present" />
        <StatCard label="Absent" value={s?.absent ?? "—"} hint="Scheduled, no attendance" tone={s && s.absent > 0 ? "warn" : "neutral"} icon={<UserX size={14} />} loading={stats.loading} testId="stat-absent" />
        <StatCard label="Late" value={s?.late ?? "—"} tone={s && s.late > 0 ? "warn" : "neutral"} icon={<Clock size={14} />} loading={stats.loading} href={`/app/attendance/records?late=true${day ? `&from=${day}&to=${day}` : ""}`} testId="stat-late" />
        <StatCard label="Checked in now" value={s?.checkedIn ?? "—"} icon={<LogIn size={14} />} loading={stats.loading} testId="stat-checked-in" />
        <StatCard label="Missing punches" value={s?.missingPunches ?? "—"} hint="Flagged for review" tone={s && s.missingPunches > 0 ? "bad" : "neutral"} icon={<AlertTriangle size={14} />} loading={stats.loading} href="/app/attendance/records?needsReview=true" testId="stat-missing" />
        <StatCard label="On break" value={s?.onBreak ?? "—"} icon={<Coffee size={14} />} loading={stats.loading} href="/app/attendance/breaks?active=true" testId="stat-on-break" />
        <StatCard label="Overdue breaks" value={s?.overdueBreaks ?? "—"} tone={s && s.overdueBreaks > 0 ? "bad" : "neutral"} icon={<Timer size={14} />} loading={stats.loading} href="/app/attendance/breaks?overdue=true" testId="stat-overdue" />
        <StatCard label="OT awaiting approval" value={s?.pendingOvertimeApprovals ?? "—"} tone={s && s.pendingOvertimeApprovals > 0 ? "warn" : "neutral"} loading={stats.loading} href="/app/attendance/overtime/approvals" testId="stat-ot-pending" />
        <StatCard label="OT completed" value={s?.completedOvertime ?? "—"} hint="That day" loading={stats.loading} href="/app/attendance/overtime" testId="stat-ot-completed" />
        <StatCard label="Corrections pending" value={s?.pendingCorrections ?? "—"} tone={s && s.pendingCorrections > 0 ? "warn" : "neutral"} loading={stats.loading} href="/app/attendance/corrections" testId="stat-corrections" />
        <StatCard label="Events by source" value={s ? s.deviceEvents + s.manualEvents + s.simulatedEvents : "—"}
          hint={s ? `${s.deviceEvents} reader · ${s.manualEvents} manual · ${s.simulatedEvents} simulated` : undefined}
          icon={<FlaskConical size={14} />} loading={stats.loading} testId="stat-sources" />
      </div>

      {s && s.simulatedEvents > 0 && <Callout className="mt-4" tone="warning" title="Simulated events in this view">{SIMULATED_NOTE}</Callout>}

      <div className="mt-6 grid gap-6 xl:grid-cols-2">
        <Panel flush title="Overdue breaks" icon={<Timer size={14} />} subtitle="Breaks running past their allowance right now">
          {stats.loading ? <div className="p-4"><SkeletonRows rows={3} /></div>
            : (s?.overdueBreakList.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<Coffee size={18} />} title="No overdue breaks" body="Breaks that run past their allowance appear here." />
            : <BreakTable rows={s!.overdueBreakList} testId="overdue-breaks" />}
        </Panel>
        <Panel flush title="Checked in" icon={<LogIn size={14} />} subtitle={open.data ? `${open.data.total} open session${open.data.total === 1 ? "" : "s"}` : undefined}
          right={<Link href="/app/attendance/records?status=Open" className="text-caption hover:underline">All</Link>}>
          {open.loading ? <div className="p-4"><SkeletonRows rows={3} /></div>
            : open.error && !open.data ? <ErrorState className="m-4" title="Couldn't load" body={errorMessage(open.error)} retryLabel="Try again" onRetry={() => void open.reload()} />
            : (open.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<LogIn size={18} />} title="Nobody is checked in" body="Open sessions appear here after a check-in." />
            : <SessionTable rows={open.data!.items} testId="open-sessions" />}
        </Panel>
      </div>

      <Panel className="mt-6" flush title="Needs review" icon={<AlertTriangle size={14} />}
        subtitle="Missing check-in or check-out. No time is invented: fix these with a correction."
        right={<Link href="/app/attendance/records?needsReview=true" className="text-caption hover:underline">All</Link>}>
        {review.loading ? <div className="p-4"><SkeletonRows rows={3} /></div>
          : (review.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<UserCheck size={18} />} title="Nothing to review" body="Sessions with missing punches appear here." />
          : <SessionTable rows={review.data!.items} testId="review-sessions" />}
      </Panel>

      <PunchDialog open={punch !== null} action={punch ?? "check-in"} factoryId={factoryId || undefined} onClose={() => setPunch(null)} onDone={() => { setPunch(null); reloadAll(); }} />
    </div>
  );
}
