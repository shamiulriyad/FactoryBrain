"use client";

// Break management: breaks running now, overdue ones, and the history, with
// filters. Allowed minutes come from the shift's configured break (or a
// default per type); taken break time is never counted as working time.

import { useMemo, useState } from "react";
import { Coffee, RefreshCw } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Pagination } from "@/components/access/shared";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { AttendanceTabs, BreakTable, DateFilter, PunchDialog, useQueryState, type PunchAction } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { P, type PagedResult } from "@/lib/access";
import type { BreakSession } from "@/lib/attendance";

const PAGE_SIZE = 50;

export default function BreaksPage() {
  return (
    <RequirePermission perms={[P.AttendanceView]} what="Breaks">
      <Breaks />
    </RequirePermission>
  );
}

function Breaks() {
  const q = useQueryState("/app/attendance/breaks");
  const { factories } = useMyFactories();
  const canRecord = useCan(P.AttendanceRecord);
  const f = { factory: q.get("factory"), line: q.get("line"), active: q.get("active"), overdue: q.get("overdue"), from: q.get("from"), to: q.get("to") };
  const factoryId = f.factory || (factories?.length === 1 ? factories[0].id : "");
  const { lines } = useFactoryOrg(factoryId || null);
  const [punch, setPunch] = useState<PunchAction | null>(null);
  const view = f.overdue === "true" ? "overdue" : f.active === "true" ? "active" : "all";

  const path = useMemo(() => {
    const p = new URLSearchParams({ page: String(q.page), pageSize: String(PAGE_SIZE) });
    if (f.factory) p.set("factoryId", f.factory);
    if (f.line) p.set("lineId", f.line);
    if (f.active) p.set("active", f.active);
    if (f.overdue) p.set("overdue", f.overdue);
    if (f.from) p.set("from", f.from);
    if (f.to) p.set("to", f.to);
    return `/api/breaks?${p}`;
  }, [f.factory, f.line, f.active, f.overdue, f.from, f.to, q.page]); // eslint-disable-line react-hooks/exhaustive-deps
  const list = useResource<PagedResult<BreakSession>>(path, { pollMs: view === "all" ? undefined : 60_000 });

  return (
    <div className="page" data-testid="breaks-page">
      <PageHeader title="Breaks" description="Lunch, tea, prayer and personal breaks. Several breaks per shift are allowed, one at a time; a break still running at check-out ends with it."
        actions={canRecord ? (
          <div className="flex gap-2">
            <Button size="sm" variant="primary" iconLeft={<Coffee size={13} />} onClick={() => setPunch("break-start")} data-testid="break-start">Start break</Button>
            <Button size="sm" variant="secondary" onClick={() => setPunch("break-end")} data-testid="break-end">End break</Button>
          </div>
        ) : undefined} />
      <AttendanceTabs className="mt-4" />
      <Panel className="mt-4" flush title="Breaks" icon={<Coffee size={14} />}
        subtitle={list.data ? `${list.data.total} break${list.data.total === 1 ? "" : "s"}` : undefined}
        right={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void list.reload()}><RefreshCw size={13} className={cn(list.refreshing && "animate-spin")} /></Button>}>
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <FilterSelect label="Show" value={view} onChange={(v) => q.set({ active: v === "active" ? "true" : null, overdue: v === "overdue" ? "true" : null })} testId="breaks-view">
            <option value="all">All breaks</option>
            <option value="active">Running now</option>
            <option value="overdue">Overdue now</option>
          </FilterSelect>
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
          {view === "all" && (
            <>
              <DateFilter label="From" value={f.from} onChange={(v) => q.set({ from: v || null })} />
              <DateFilter label="To" value={f.to} onChange={(v) => q.set({ to: v || null })} />
            </>
          )}
        </div>
        {list.loading ? <div className="p-4"><SkeletonRows rows={6} rowHeight="h-11" /></div>
          : list.error && !list.data ? <ErrorState className="m-4" title="Couldn't load breaks" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
          : (list.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<Coffee size={18} />} title={view === "overdue" ? "No overdue breaks" : view === "active" ? "Nobody is on a break" : "No breaks recorded"} body="Breaks appear here when they are started from a reader, the simulator or manually." />
          : (
            <>
              <BreakTable rows={list.data!.items} testId="breaks-table" />
              <Pagination page={list.data!.page} pageSize={list.data!.pageSize} total={list.data!.total} onPage={(p) => q.set({ page: String(p) }, false)} />
            </>
          )}
      </Panel>
      <PunchDialog open={punch !== null} action={punch ?? "break-start"} factoryId={factoryId || undefined} onClose={() => setPunch(null)} onDone={() => { setPunch(null); void list.reload(); }} />
    </div>
  );
}
