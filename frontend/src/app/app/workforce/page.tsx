"use client";

// /app/workforce: one place for the people side of the factory, on server
// data only. Overview (headcount and today's attendance), workers,
// attendance, leave and overtime, and performance (workforce analytics).
// Each tab links to the full module for editing.

import Link from "next/link";
import { useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { ArrowRight, CalendarCheck, CalendarOff, Clock, RefreshCw, Search, Timer, UserCheck, UserX, Users } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Input } from "@/components/ui/Input";
import { Tabs } from "@/components/ui/Tabs";
import { StatCard } from "@/components/ui/StatCard";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useMyFactories } from "@/components/workers/shared";
import { SessionTable } from "@/components/attendance/shared";
import { DataTable, KpiGrid, pct, useDefinitions } from "@/components/analytics/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { useAuthStore, can } from "@/lib/auth";
import { P, type PagedResult } from "@/lib/access";
import { formatDate } from "@/lib/format";
import { cn } from "@/lib/cn";
import { STATUS_TONE, WORKER_STATUSES, type Worker, type WorkerStatistics } from "@/lib/workers";
import { OVERTIME_STATUS_LABEL, OVERTIME_STATUS_TONE, hours, type AttendanceSession, type AttendanceStatistics, type Overtime } from "@/lib/attendance";
import type { WorkforceAnalytics } from "@/lib/analytics";
import type { DocListItem, Paged } from "@/lib/documents";

type Tab = "overview" | "workers" | "attendance" | "leave" | "performance";
const TABS: Tab[] = ["overview", "workers", "attendance", "leave", "performance"];
const TAB_LABEL: Record<Tab, string> = { overview: "Overview", workers: "Workers", attendance: "Attendance", leave: "Leave & overtime", performance: "Performance" };

export default function WorkforcePage() {
  return (
    <RequirePermission perms={[P.WorkersView, P.AttendanceView, P.OvertimeView]} what="Workforce">
      <Workforce />
    </RequirePermission>
  );
}

function Workforce() {
  const router = useRouter();
  const search = useSearchParams();
  const urlTab = search?.get("tab") as Tab | null;
  const tab: Tab = urlTab && TABS.includes(urlTab) ? urlTab : "overview";
  const { factories } = useMyFactories();
  const factoryParam = search?.get("factory") ?? "";
  const factoryId = factoryParam || (factories?.length === 1 ? factories[0].id : "");

  function go(next: Record<string, string | null>) {
    const sp = new URLSearchParams(search?.toString() ?? "");
    for (const [k, v] of Object.entries(next)) {
      if (v) sp.set(k, v);
      else sp.delete(k);
    }
    if (sp.get("tab") === "overview") sp.delete("tab");
    const q = sp.toString();
    router.replace(`/app/workforce${q ? `?${q}` : ""}`, { scroll: false });
  }

  return (
    <div className="page" data-testid="workforce-page">
      <PageHeader
        title="Workforce"
        description="Headcount, attendance, leave, overtime and performance from the factory's records. Open a module to make changes."
        actions={(factories?.length ?? 0) > 1 ? (
          <FilterSelect label="Factory" value={factoryParam} onChange={(v) => go({ factory: v || null })}>
            <option value="">All factories</option>
            {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        ) : undefined}
      />
      <Tabs
        className="mt-4"
        label="Workforce sections"
        items={TABS.map((id) => ({ id, label: TAB_LABEL[id], testId: `workforce-tab-${id}` }))}
        value={tab}
        onChange={(id) => go({ tab: id, page: null })}
      />
      <div className="mt-4">
        {tab === "overview" && <OverviewTab factoryParam={factoryParam} go={go} />}
        {tab === "workers" && <WorkersTab factoryParam={factoryParam} />}
        {tab === "attendance" && <AttendanceTab factoryParam={factoryParam} />}
        {tab === "leave" && <LeaveTab factoryParam={factoryParam} factoryId={factoryId} />}
        {tab === "performance" && <PerformanceTab factoryParam={factoryParam} />}
      </div>
    </div>
  );
}

function NoAccess({ what }: { what: string }) {
  return <p className="p-4 text-caption text-fg-tertiary">Your role can&apos;t see {what}.</p>;
}

function ModuleLink({ href, children }: { href: string; children: React.ReactNode }) {
  return <Link href={href} className="inline-flex items-center gap-1 text-caption text-[var(--accent)] hover:underline">{children} <ArrowRight size={12} /></Link>;
}

const withFactory = (base: string, factoryParam: string, extra: Record<string, string> = {}) => {
  const p = new URLSearchParams(extra);
  if (factoryParam) p.set("factoryId", factoryParam);
  const q = p.toString();
  return `${base}${q ? `?${q}` : ""}`;
};

/* ------------------------------------------------------------------ */

function OverviewTab({ factoryParam, go }: { factoryParam: string; go: (v: Record<string, string | null>) => void }) {
  const user = useAuthStore((s) => s.user);
  const seeWorkers = can(user, P.WorkersView);
  const seeAttendance = can(user, P.AttendanceView);
  const workers = useResource<WorkerStatistics>(seeWorkers ? withFactory("/api/workers/statistics", factoryParam) : null);
  const today = useResource<AttendanceStatistics>(seeAttendance ? withFactory("/api/attendance/statistics", factoryParam) : null, { pollMs: 60_000 });
  const w = workers.data;
  const a = today.data;
  const err = (workers.error && !w ? errorMessage(workers.error) : null) ?? (today.error && !a ? errorMessage(today.error) : null);

  return (
    <div data-testid="workforce-overview">
      {err && <ErrorState className="mb-4" title="Couldn't load workforce figures" body={err} retryLabel="Try again" onRetry={() => { void workers.reload(); void today.reload(); }} />}
      <div className="grid gap-3 grid-cols-2 md:grid-cols-4 xl:grid-cols-4">
        {seeWorkers && <StatCard label="Active workers" value={w?.active ?? "—"} hint={w ? `${w.total} on record` : undefined} icon={<Users size={14} />} loading={workers.loading} href="/app/workers?status=Active" testId="wf-active" />}
        {seeAttendance && (
          <>
            <StatCard label="Present today" value={a?.present ?? "—"} hint={a ? `of ${a.scheduledWorkers} scheduled` : undefined} tone="good" icon={<UserCheck size={14} />} loading={today.loading} href="/app/attendance/records" testId="wf-present" />
            <StatCard label="Absent" value={a?.absent ?? "—"} tone={a && a.absent > 0 ? "warn" : "neutral"} icon={<UserX size={14} />} loading={today.loading} testId="wf-absent" />
            <StatCard label="Late" value={a?.late ?? "—"} tone={a && a.late > 0 ? "warn" : "neutral"} icon={<Clock size={14} />} loading={today.loading} href="/app/attendance/records?late=true" testId="wf-late" />
            <StatCard label="On leave" value={a?.onLeave ?? "—"} icon={<CalendarOff size={14} />} loading={today.loading} href="/app/documents/list?type=hr.leave&status=APPROVED" testId="wf-leave" />
            <StatCard label="Checked in now" value={a?.checkedIn ?? "—"} icon={<CalendarCheck size={14} />} loading={today.loading} testId="wf-checked-in" />
            <StatCard label="OT awaiting approval" value={a?.pendingOvertimeApprovals ?? "—"} tone={a && a.pendingOvertimeApprovals > 0 ? "warn" : "neutral"} icon={<Timer size={14} />} loading={today.loading} href="/app/attendance/overtime/approvals" testId="wf-ot-pending" />
            <StatCard label="Corrections pending" value={a?.pendingCorrections ?? "—"} tone={a && a.pendingCorrections > 0 ? "warn" : "neutral"} loading={today.loading} href="/app/attendance/corrections" testId="wf-corrections" />
          </>
        )}
      </div>
      {a?.holiday && <p className="mt-3 text-caption text-fg-tertiary">Today is a holiday ({a.holiday}): nobody is counted absent.</p>}

      {seeWorkers && w && w.total === 0 && (
        <EmptyState className="mt-6" icon={<Users size={18} />} title="No workers yet" body="Register workers (one by one or by import) under Workers; the figures here fill in from those records." />
      )}

      {seeWorkers && w && w.total > 0 && (
        <div className="mt-4 grid gap-4 lg:grid-cols-2">
          <Panel title="Headcount by department" subtitle="Active of total" right={<button type="button" className="text-caption text-[var(--accent)]" onClick={() => go({ tab: "workers" })}>Workers</button>}>
            <CountBars rows={w.byDepartment} />
          </Panel>
          <Panel title="Headcount by line" subtitle="Active of total" right={<ModuleLink href="/app/workers/statistics">Statistics</ModuleLink>}>
            <CountBars rows={w.byLine} />
          </Panel>
        </div>
      )}
    </div>
  );
}

function CountBars({ rows }: { rows: { key: string; label: string; total: number; active: number }[] }) {
  if (rows.length === 0) return <p className="p-4 text-caption text-fg-tertiary">No data.</p>;
  const max = Math.max(...rows.map((r) => r.total), 1);
  return (
    <ul className="p-4 space-y-2">
      {rows.slice(0, 12).map((r) => (
        <li key={r.key} className="grid grid-cols-[minmax(0,10rem)_1fr_auto] items-center gap-3 text-caption">
          <span className="truncate text-fg-secondary">{r.label}</span>
          <span className="h-2 rounded bg-[var(--bg-surface-2,rgba(127,127,127,0.15))] overflow-hidden">
            <span className="block h-full bg-[var(--accent)]" style={{ width: `${(r.active / max) * 100}%` }} />
          </span>
          <span className="tabular-nums text-fg-primary">{r.active} / {r.total}</span>
        </li>
      ))}
    </ul>
  );
}

/* ------------------------------------------------------------------ */

const PAGE_SIZE = 25;

function WorkersTab({ factoryParam }: { factoryParam: string }) {
  const user = useAuthStore((s) => s.user);
  const [q, setQ] = useState("");
  const [status, setStatus] = useState("Active");
  const [page, setPage] = useState(1);
  const path = useMemo(() => {
    const p = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE), sort: "name" });
    if (q.trim()) p.set("search", q.trim());
    if (status) p.set("status", status);
    if (factoryParam) p.set("factoryId", factoryParam);
    return `/api/workers?${p}`;
  }, [q, status, page, factoryParam]);
  const list = useResource<PagedResult<Worker>>(can(user, P.WorkersView) ? path : null);
  if (!can(user, P.WorkersView)) return <NoAccess what="the worker directory" />;
  const d = list.data;
  const pages = d ? Math.max(1, Math.ceil(d.total / PAGE_SIZE)) : 1;

  return (
    <Panel title="Workers" subtitle={d ? `${d.total} found` : undefined} right={<ModuleLink href="/app/workers">Worker directory</ModuleLink>} testId="workforce-workers">
      <div className="flex flex-wrap items-end gap-2 p-4 pb-2">
        <div className="relative">
          <Search size={12} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary pointer-events-none" />
          <Input className="pl-7 w-64" placeholder="Name, code or employee ID" value={q} onChange={(e) => { setQ(e.target.value); setPage(1); }} aria-label="Search workers" />
        </div>
        <FilterSelect label="Status" value={status} onChange={(v) => { setStatus(v); setPage(1); }}>
          <option value="">Any status</option>
          {WORKER_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
        </FilterSelect>
      </div>
      {list.loading && !d ? <div className="p-4"><SkeletonRows rows={6} /></div>
        : list.error && !d ? <ErrorState className="m-4" title="Couldn't load workers" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
        : d && d.items.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">{q || status ? "No worker matches." : "No workers registered yet."}</p>
        : d ? (
          <>
            <DataTable
              columns={["Code", "Name", "Designation", "Department", "Line", "Status"]}
              rows={d.items.map((w) => [
                <span key="c" className="mono-pill">{w.workerCode}</span>,
                <Link key="n" className="text-[var(--accent)] hover:underline" href={`/app/workers/${w.id}`}>{w.fullName}</Link>,
                w.designation ?? "—",
                w.department?.name ?? "—",
                w.line?.name ?? "—",
                <Badge key="s" tone={STATUS_TONE[w.status] ?? "neutral"}>{w.status}</Badge>,
              ])}
              testId="workforce-workers-table"
            />
            {pages > 1 && (
              <div className="flex items-center justify-end gap-2 p-3 text-caption">
                <Button size="sm" variant="secondary" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</Button>
                <span className="text-fg-tertiary">Page {page} of {pages}</span>
                <Button size="sm" variant="secondary" disabled={page >= pages} onClick={() => setPage(page + 1)}>Next</Button>
              </div>
            )}
          </>
        ) : null}
    </Panel>
  );
}

/* ------------------------------------------------------------------ */

function AttendanceTab({ factoryParam }: { factoryParam: string }) {
  const user = useAuthStore((s) => s.user);
  const allowed = can(user, P.AttendanceView);
  const open = useResource<PagedResult<AttendanceSession>>(allowed ? withFactory("/api/attendance", factoryParam, { status: "Open", sort: "checkIn", pageSize: "25" }) : null, { pollMs: 60_000 });
  const review = useResource<PagedResult<AttendanceSession>>(allowed ? withFactory("/api/attendance", factoryParam, { needsReview: "true", sort: "-date", pageSize: "10" }) : null);
  if (!allowed) return <NoAccess what="attendance" />;
  return (
    <div className="space-y-4" data-testid="workforce-attendance">
      <Panel title="Checked in now" subtitle={open.data ? `${open.data.total} open session${open.data.total === 1 ? "" : "s"}` : undefined}
        right={<ModuleLink href="/app/attendance">Attendance</ModuleLink>}
        icon={<UserCheck size={15} />}>
        {open.loading && !open.data ? <div className="p-4"><SkeletonRows rows={4} /></div>
          : open.error && !open.data ? <ErrorState className="m-4" title="Couldn't load sessions" body={errorMessage(open.error)} retryLabel="Try again" onRetry={() => void open.reload()} />
          : open.data && open.data.items.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">Nobody is checked in right now.</p>
          : open.data ? <SessionTable rows={open.data.items} /> : null}
      </Panel>
      <Panel title="Needs review" subtitle="Missing punches and other flagged sessions" right={<ModuleLink href="/app/attendance/records?needsReview=true">All flagged</ModuleLink>} icon={<Clock size={15} />}>
        {review.loading && !review.data ? <div className="p-4"><SkeletonRows rows={3} /></div>
          : review.data && review.data.items.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">Nothing to review.</p>
          : review.data ? <SessionTable rows={review.data.items} /> : null}
      </Panel>
    </div>
  );
}

/* ------------------------------------------------------------------ */

const hrs = (m: number | null | undefined) => (m == null ? "—" : `${hours(m)} h`);

const DOC_TONE: Record<string, "success" | "warning" | "danger" | "neutral" | "info"> = { APPROVED: "success", SUBMITTED: "info", IN_REVIEW: "info", VERIFIED: "info", REJECTED: "danger", DRAFT: "neutral" };

function LeaveTab({ factoryParam, factoryId }: { factoryParam: string; factoryId: string }) {
  const user = useAuthStore((s) => s.user);
  const seeLeave = can(user, P.DocumentsView);
  const seeOvertime = can(user, P.OvertimeView);
  const leave = useResource<Paged<DocListItem>>(seeLeave ? withFactory("/api/documents", factoryId, { type: "hr.leave", pageSize: "20" }) : null);
  const overtime = useResource<PagedResult<Overtime>>(seeOvertime ? withFactory("/api/overtime", factoryParam, { pageSize: "20" }) : null);

  return (
    <div className="grid gap-4 xl:grid-cols-2" data-testid="workforce-leave">
      <Panel title="Leave records" subtitle="Leave applications (hr.leave documents), newest first" icon={<CalendarOff size={15} />}
        right={seeLeave ? <ModuleLink href="/app/documents/list?type=hr.leave">All leave</ModuleLink> : undefined}>
        {!seeLeave ? <NoAccess what="leave documents" />
          : leave.loading && !leave.data ? <div className="p-4"><SkeletonRows rows={4} /></div>
          : leave.error && !leave.data ? <ErrorState className="m-4" title="Couldn't load leave" body={errorMessage(leave.error)} retryLabel="Try again" onRetry={() => void leave.reload()} />
          : leave.data && leave.data.items.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No leave records yet. Create one from Documents → New → Leave.</p>
          : leave.data ? (
            <DataTable
              columns={["Reference", "Title", "Period", "Status"]}
              rows={leave.data.items.map((d) => [
                <Link key="r" className="text-[var(--accent)] hover:underline" href={`/app/documents/${d.id}`}>{d.reference}</Link>,
                d.title,
                d.periodFrom ? `${formatDate(d.periodFrom)}${d.periodTo && d.periodTo !== d.periodFrom ? ` – ${formatDate(d.periodTo)}` : ""}` : "—",
                <Badge key="s" tone={DOC_TONE[d.status] ?? "neutral"}>{d.status.toLowerCase().replace("_", " ")}</Badge>,
              ])}
            />
          ) : null}
      </Panel>
      <Panel title="Overtime requests" subtitle={overtime.data ? `${overtime.data.total} in total, newest first` : undefined} icon={<Timer size={15} />}
        right={seeOvertime ? <ModuleLink href="/app/attendance/overtime">Overtime</ModuleLink> : undefined}>
        {!seeOvertime ? <NoAccess what="overtime" />
          : overtime.loading && !overtime.data ? <div className="p-4"><SkeletonRows rows={4} /></div>
          : overtime.error && !overtime.data ? <ErrorState className="m-4" title="Couldn't load overtime" body={errorMessage(overtime.error)} retryLabel="Try again" onRetry={() => void overtime.reload()} />
          : overtime.data && overtime.data.items.length === 0 ? <p className="p-4 text-caption text-fg-tertiary">No overtime requests yet.</p>
          : overtime.data ? (
            <DataTable
              columns={["Worker", "Date", "Requested", "Approved", "Actual", "Status"]}
              rows={overtime.data.items.map((o) => [
                <Link key="w" className="text-[var(--accent)] hover:underline" href={`/app/workers/${o.workerId}`}>{o.workerName}</Link>,
                formatDate(o.workDate),
                hrs(o.requestedMinutes),
                hrs(o.approvedMinutes),
                hrs(o.actualMinutes),
                <Badge key="s" tone={OVERTIME_STATUS_TONE[o.status] ?? "neutral"}>{OVERTIME_STATUS_LABEL[o.status] ?? o.status}</Badge>,
              ])}
            />
          ) : null}
      </Panel>
    </div>
  );
}

/* ------------------------------------------------------------------ */

function PerformanceTab({ factoryParam }: { factoryParam: string }) {
  const user = useAuthStore((s) => s.user);
  const allowed = can(user, P.AttendanceView, P.OvertimeView, P.WorkersView);
  const defs = useDefinitions();
  const r = useResource<WorkforceAnalytics>(allowed ? withFactory("/api/analytics/workforce", factoryParam) : null, { pollMs: 120_000 });
  const d = r.data;
  if (!allowed) return <NoAccess what="workforce analytics" />;
  if (r.error && !d) return <ErrorState title="Couldn't load workforce analytics" body={errorMessage(r.error)} retryLabel="Try again" onRetry={() => void r.reload()} />;
  if (!d) return <SkeletonRows rows={6} />;
  return (
    <div data-testid="workforce-performance">
      <div className="mb-3 flex items-center justify-between gap-2">
        <p className="text-caption text-fg-tertiary">{d.scope.from} – {d.scope.to}{d.warnings.length > 0 ? ` · ${d.warnings.join(" ")}` : ""}</p>
        <div className="flex items-center gap-3">
          <Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void r.reload()}><RefreshCw size={13} className={cn(r.refreshing && "animate-spin")} /></Button>
          <ModuleLink href="/app/analytics/workforce">Full analytics</ModuleLink>
        </div>
      </div>
      <KpiGrid kpis={d.kpis} defs={defs} />
      <Panel className="mb-4" title="By line" subtitle="Attendance, punctuality and workstation assignment per line">
        <DataTable
          columns={["Line", "Workers", "Present", "Attendance", "Late", "Early leave", "Assigned", "Assignment"]}
          rows={d.byLine.map((x) => [x.label, x.workers, x.present, pct(x.ratePct), x.late, x.earlyDeparture, x.assigned, pct(x.assignmentCoveragePct)])}
          empty="No line data in this period."
        />
      </Panel>
      <Panel title="Overtime: approved vs actual">
        <DataTable
          columns={["Requests", "Approved", "Pending", "Rejected", "Approved hours", "Actual hours", "Actual ÷ approved", "Workers"]}
          rows={[[d.overtime.requests, d.overtime.approved, d.overtime.pending, d.overtime.rejected, hrs(d.overtime.approvedMinutes), hrs(d.overtime.actualMinutes),
            pct(d.overtime.actualVsApprovedPct), d.overtime.workersWithOvertime]]}
        />
      </Panel>
    </div>
  );
}
