"use client";

// Attendance records: every session in scope with search, filters (factory,
// line, department, period, status, review, late, simulated), sorting,
// pagination and a CSV export of the period. Corrections are requested from
// a row and applied only after a second person approves.

import { useMemo, useState } from "react";
import { CalendarCheck, Download, RefreshCw } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { Pagination } from "@/components/access/shared";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { AttendanceTabs, CorrectionDialog, DateFilter, SearchBox, SessionTable, useQueryState } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { errorMessage } from "@/lib/api";
import { downloadCsv } from "@/lib/workers";
import { cn } from "@/lib/cn";
import { P, type PagedResult } from "@/lib/access";
import { SESSION_STATUSES, SESSION_STATUS_LABEL, todayIso, type AttendanceSession } from "@/lib/attendance";

const PAGE_SIZE = 50;
const SORTS = [
  { id: "-date", label: "Newest day" },
  { id: "date", label: "Oldest day" },
  { id: "worker", label: "Worker" },
  { id: "checkIn", label: "Check-in time" },
  { id: "-late", label: "Most late" }
];

export default function AttendanceRecordsPage() {
  return (
    <RequirePermission perms={[P.AttendanceView]} what="Attendance records">
      <Records />
    </RequirePermission>
  );
}

function Records() {
  const q = useQueryState("/app/attendance/records");
  const toast = useToast();
  const { factories } = useMyFactories();
  const canCorrect = useCan(P.AttendanceManage);
  const [correcting, setCorrecting] = useState<AttendanceSession | null>(null);
  const [exporting, setExporting] = useState(false);

  const f = {
    q: q.get("q"), factory: q.get("factory"), line: q.get("line"), department: q.get("department"), from: q.get("from"), to: q.get("to"),
    status: q.get("status"), needsReview: q.get("needsReview"), late: q.get("late"), simulated: q.get("simulated"), sort: q.get("sort") || "-date"
  };
  const factoryId = f.factory || (factories?.length === 1 ? factories[0].id : "");
  const { departments, lines } = useFactoryOrg(factoryId || null);

  const params = useMemo(() => {
    const p = new URLSearchParams();
    if (f.q) p.set("search", f.q);
    if (f.factory) p.set("factoryId", f.factory);
    if (f.line) p.set("lineId", f.line);
    if (f.department) p.set("departmentId", f.department);
    if (f.from) p.set("from", f.from);
    if (f.to) p.set("to", f.to);
    if (f.status) p.set("status", f.status);
    if (f.needsReview) p.set("needsReview", f.needsReview);
    if (f.late) p.set("late", f.late);
    if (f.simulated) p.set("simulated", f.simulated);
    return p;
  }, [f.q, f.factory, f.line, f.department, f.from, f.to, f.status, f.needsReview, f.late, f.simulated]); // eslint-disable-line react-hooks/exhaustive-deps

  const path = useMemo(() => {
    const p = new URLSearchParams(params);
    p.set("sort", f.sort);
    p.set("page", String(q.page));
    p.set("pageSize", String(PAGE_SIZE));
    return `/api/attendance?${p}`;
  }, [params, f.sort, q.page]);
  const list = useResource<PagedResult<AttendanceSession>>(path);
  const filtered = Object.entries(f).some(([k, v]) => k !== "sort" && !!v);

  async function exportCsv() {
    setExporting(true);
    try {
      const p = new URLSearchParams();
      if (f.factory) p.set("factoryId", f.factory);
      if (f.line) p.set("lineId", f.line);
      if (f.department) p.set("departmentId", f.department);
      p.set("from", f.from || todayIso(-30));
      p.set("to", f.to || todayIso());
      const rows = await downloadCsv(`/api/attendance/report/export?${p}`, "attendance-report.csv");
      toast.push({ tone: "success", title: "Export ready", description: rows !== null ? `${rows} worker rows (per-worker totals for the period).` : undefined });
    } catch (e) {
      toast.push({ tone: "error", title: "Export failed", description: errorMessage(e) });
    } finally {
      setExporting(false);
    }
  }

  return (
    <div className="page" data-testid="attendance-records-page">
      <PageHeader title="Attendance records" description="One row per attendance session (work date in the factory's time zone). Raw punches are kept separately and never changed." />
      <AttendanceTabs className="mt-4" />
      <Panel className="mt-4" flush title="Sessions" icon={<CalendarCheck size={14} />}
        subtitle={list.data ? `${list.data.total} session${list.data.total === 1 ? "" : "s"}` : undefined}
        right={
          <div className="flex gap-2">
            <Button size="sm" variant="secondary" iconLeft={<Download size={13} />} onClick={() => void exportCsv()} disabled={exporting} data-testid="records-export">
              {exporting ? "Exporting…" : "Export CSV"}
            </Button>
            <Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void list.reload()}><RefreshCw size={13} className={cn(list.refreshing && "animate-spin")} /></Button>
          </div>
        }>
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <SearchBox value={f.q} onChange={(v) => q.set({ q: v || null })} placeholder="Worker ID or name" label="Search attendance" testId="records-search" />
          {(factories?.length ?? 0) > 1 && (
            <FilterSelect label="Factory" value={f.factory} onChange={(v) => q.set({ factory: v || null, line: null, department: null })}>
              <option value="">All factories</option>
              {factories!.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
            </FilterSelect>
          )}
          {factoryId && (
            <>
              <FilterSelect label="Line" value={f.line} onChange={(v) => q.set({ line: v || null })}>
                <option value="">All</option>
                {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </FilterSelect>
              <FilterSelect label="Department" value={f.department} onChange={(v) => q.set({ department: v || null })}>
                <option value="">All</option>
                {(departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
              </FilterSelect>
            </>
          )}
          <FilterSelect label="Status" value={f.status} onChange={(v) => q.set({ status: v || null })} testId="records-status">
            <option value="">Any</option>
            {SESSION_STATUSES.map((s) => <option key={s} value={s}>{SESSION_STATUS_LABEL[s]}</option>)}
          </FilterSelect>
          <FilterSelect label="Review" value={f.needsReview} onChange={(v) => q.set({ needsReview: v || null })}>
            <option value="">Any</option>
            <option value="true">Needs review</option>
            <option value="false">OK</option>
          </FilterSelect>
          <FilterSelect label="Late" value={f.late} onChange={(v) => q.set({ late: v || null })}>
            <option value="">Any</option>
            <option value="true">Late only</option>
            <option value="false">On time</option>
          </FilterSelect>
          <FilterSelect label="Source" value={f.simulated} onChange={(v) => q.set({ simulated: v || null })}>
            <option value="">Any</option>
            <option value="false">Real only</option>
            <option value="true">Simulated only</option>
          </FilterSelect>
          <DateFilter label="From" value={f.from} onChange={(v) => q.set({ from: v || null })} />
          <DateFilter label="To" value={f.to} onChange={(v) => q.set({ to: v || null })} />
          <FilterSelect label="Sort" value={f.sort} onChange={(v) => q.set({ sort: v === "-date" ? null : v }, false)}>
            {SORTS.map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
          </FilterSelect>
          {filtered && <Button size="sm" variant="ghost" onClick={() => q.set({ q: null, factory: null, line: null, department: null, from: null, to: null, status: null, needsReview: null, late: null, simulated: null })}>Clear filters</Button>}
        </div>
        {list.loading ? (
          <div className="p-4"><SkeletonRows rows={8} rowHeight="h-11" /></div>
        ) : list.error && !list.data ? (
          <ErrorState className="m-4" title="Couldn't load attendance" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
        ) : (list.data?.items.length ?? 0) === 0 ? (
          <EmptyState className="m-4" icon={<CalendarCheck size={18} />} title={filtered ? "No sessions match" : "No attendance yet"}
            body={filtered ? "Change or clear the filters." : "Sessions appear here after check-ins from readers, the simulator or manual entry."} />
        ) : (
          <>
            <SessionTable rows={list.data!.items} onCorrect={canCorrect ? setCorrecting : undefined} testId="records-table" />
            <Pagination page={list.data!.page} pageSize={list.data!.pageSize} total={list.data!.total} onPage={(p) => q.set({ page: String(p) }, false)} />
          </>
        )}
      </Panel>
      <CorrectionDialog open={!!correcting} session={correcting} onClose={() => setCorrecting(null)} onDone={() => { setCorrecting(null); void list.reload(); }} />
    </div>
  );
}
