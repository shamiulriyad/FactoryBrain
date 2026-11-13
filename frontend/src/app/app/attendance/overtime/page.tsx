"use client";

// Overtime requests: every request in scope with search, filters and
// pagination, plus new requests. Requested, approved, actual and payable
// minutes are separate columns: approval caps the minutes, actual minutes
// come from attendance inside the approved window, and payable minutes are
// confirmed by HR (never calculated into pay automatically).

import { useMemo, useState } from "react";
import { Hourglass, Plus, RefreshCw } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Pagination } from "@/components/access/shared";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { AttendanceTabs, DateFilter, OvertimeActionDialog, OvertimeRequestDialog, OvertimeTable, SearchBox, useQueryState, type OvertimeAction } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { P, type PagedResult } from "@/lib/access";
import { OVERTIME_NOTE, OVERTIME_STATUSES, OVERTIME_STATUS_LABEL, type Overtime } from "@/lib/attendance";

export default function OvertimePage() {
  return (
    <RequirePermission perms={[P.OvertimeView, P.OvertimeApprove]} what="Overtime">
      <OvertimeList />
    </RequirePermission>
  );
}

function OvertimeList() {
  const q = useQueryState("/app/attendance/overtime");
  const { factories } = useMyFactories();
  const canRequest = useCan(P.OvertimeManage);
  const f = { q: q.get("q"), factory: q.get("factory"), line: q.get("line"), status: q.get("status"), from: q.get("from"), to: q.get("to") };
  const factoryId = f.factory || (factories?.length === 1 ? factories[0].id : "");
  const { lines } = useFactoryOrg(factoryId || null);
  const [requesting, setRequesting] = useState(false);
  const [acting, setActing] = useState<{ a: OvertimeAction; o: Overtime } | null>(null);

  const path = useMemo(() => {
    const p = new URLSearchParams({ page: String(q.page), pageSize: "50" });
    if (f.q) p.set("search", f.q);
    if (f.factory) p.set("factoryId", f.factory);
    if (f.line) p.set("lineId", f.line);
    if (f.status) p.set("status", f.status);
    if (f.from) p.set("from", f.from);
    if (f.to) p.set("to", f.to);
    return `/api/overtime?${p}`;
  }, [f.q, f.factory, f.line, f.status, f.from, f.to, q.page]); // eslint-disable-line react-hooks/exhaustive-deps
  const list = useResource<PagedResult<Overtime>>(path);
  const filtered = !!(f.q || f.line || f.status || f.from || f.to);

  return (
    <div className="page" data-testid="overtime-page">
      <PageHeader title="Overtime requests" description="From request to approval to actual attendance to HR-confirmed payable minutes."
        actions={canRequest ? <Button variant="primary" size="sm" iconLeft={<Plus size={13} />} onClick={() => setRequesting(true)} data-testid="overtime-new">Request overtime</Button> : undefined} />
      <AttendanceTabs className="mt-4" />
      <Callout className="mt-4" tone="info">{OVERTIME_NOTE}</Callout>
      <Panel className="mt-4" flush title="Requests" icon={<Hourglass size={14} />}
        subtitle={list.data ? `${list.data.total} request${list.data.total === 1 ? "" : "s"}` : undefined}
        right={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void list.reload()}><RefreshCw size={13} className={cn(list.refreshing && "animate-spin")} /></Button>}>
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <SearchBox value={f.q} onChange={(v) => q.set({ q: v || null })} placeholder="Worker ID or name" label="Search overtime" testId="overtime-search" />
          {(factories?.length ?? 0) > 1 && (
            <FilterSelect label="Factory" value={f.factory} onChange={(v) => q.set({ factory: v || null, line: null })}>
              <option value="">All factories</option>
              {factories!.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
            </FilterSelect>
          )}
          {factoryId && (
            <FilterSelect label="Line" value={f.line} onChange={(v) => q.set({ line: v || null })}>
              <option value="">All</option>
              {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </FilterSelect>
          )}
          <FilterSelect label="Status" value={f.status} onChange={(v) => q.set({ status: v || null })} testId="overtime-status-filter">
            <option value="">Any</option>
            {OVERTIME_STATUSES.map((s) => <option key={s} value={s}>{OVERTIME_STATUS_LABEL[s]}</option>)}
          </FilterSelect>
          <DateFilter label="From" value={f.from} onChange={(v) => q.set({ from: v || null })} />
          <DateFilter label="To" value={f.to} onChange={(v) => q.set({ to: v || null })} />
          {filtered && <Button size="sm" variant="ghost" onClick={() => q.set({ q: null, line: null, status: null, from: null, to: null })}>Clear filters</Button>}
        </div>
        {list.loading ? <div className="p-4"><SkeletonRows rows={6} rowHeight="h-11" /></div>
          : list.error && !list.data ? <ErrorState className="m-4" title="Couldn't load overtime" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
          : (list.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<Hourglass size={18} />} title={filtered ? "No requests match" : "No overtime requests"} body={filtered ? "Change or clear the filters." : "Requests appear here once a supervisor or HR asks for overtime."} />
          : (
            <>
              <OvertimeTable rows={list.data!.items} onAction={(a, o) => setActing({ a, o })} testId="overtime-table" />
              <Pagination page={list.data!.page} pageSize={list.data!.pageSize} total={list.data!.total} onPage={(p) => q.set({ page: String(p) }, false)} />
            </>
          )}
      </Panel>
      <OvertimeRequestDialog open={requesting} factoryId={factoryId || undefined} onClose={() => setRequesting(false)} onDone={() => { setRequesting(false); void list.reload(); }} />
      <OvertimeActionDialog open={!!acting} action={acting?.a ?? "approve"} overtime={acting?.o ?? null} onClose={() => setActing(null)} onDone={() => { setActing(null); void list.reload(); }} />
    </div>
  );
}
