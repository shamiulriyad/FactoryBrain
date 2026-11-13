"use client";

// Assignment history: every assignment in scope, newest first, with
// filters (status, line, department, worker / workstation search, period).
// Nothing is ever overwritten: a transfer shows as one Transferred row and
// one new row that points back at it.

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { History, RefreshCw, Search } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Pagination } from "@/components/access/shared";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { AssignmentHistoryTable } from "@/components/assignments/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { P, type PagedResult } from "@/lib/access";
import { ASSIGNMENT_STATUSES, NOT_PRESENCE_NOTE, type Assignment } from "@/lib/assignments";

const PAGE_SIZE = 50;

export default function AssignmentHistoryPage() {
  return (
    <RequirePermission perms={[P.WorkersView]} what="Assignment history">
      <HistoryList />
    </RequirePermission>
  );
}

function HistoryList() {
  const router = useRouter();
  const search = useSearchParams();
  const { factories } = useMyFactories();
  const q = search?.get("q") ?? "";
  const factoryParam = search?.get("factory") ?? "";
  const status = search?.get("status") ?? "";
  const line = search?.get("line") ?? "";
  const department = search?.get("department") ?? "";
  const from = search?.get("from") ?? "";
  const to = search?.get("to") ?? "";
  const page = Math.max(1, Number(search?.get("page")) || 1);
  const factoryId = factoryParam || (factories?.length === 1 ? factories[0].id : "");
  const { departments, lines } = useFactoryOrg(factoryId || null);
  const [draft, setDraft] = useState(q);
  useEffect(() => setDraft(q), [q]);

  function setParams(next: Record<string, string | null>, resetPage = true) {
    const params = new URLSearchParams(search?.toString() ?? "");
    for (const [k, v] of Object.entries(next)) {
      if (v) params.set(k, v);
      else params.delete(k);
    }
    if (resetPage) params.delete("page");
    router.replace(`/app/assignments/history${params.toString() ? `?${params}` : ""}`);
  }

  useEffect(() => {
    if (draft === q) return;
    const id = window.setTimeout(() => setParams({ q: draft.trim() || null }), 300);
    return () => window.clearTimeout(id);
  }, [draft]); // eslint-disable-line react-hooks/exhaustive-deps

  const path = useMemo(() => {
    const p = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (q) p.set("search", q);
    if (factoryParam) p.set("factoryId", factoryParam);
    if (status) p.set("status", status);
    if (line) p.set("lineId", line);
    if (department) p.set("departmentId", department);
    if (from) p.set("from", new Date(from + "T00:00:00").toISOString());
    if (to) p.set("to", new Date(to + "T23:59:59").toISOString());
    return `/api/assignments?${p}`;
  }, [q, factoryParam, status, line, department, from, to, page]);
  const list = useResource<PagedResult<Assignment>>(path);
  const filtered = !!(q || status || line || department || from || to);

  return (
    <div className="page" data-testid="assignment-history-page">
      <PageHeader
        eyebrow={<Link href="/app/assignments" className="hover:underline">← Assignments</Link>}
        title="Assignment history"
        description={`Every assignment and transfer in your scope, newest first. ${NOT_PRESENCE_NOTE}`}
      />
      <Panel className="mt-6" flush title="Assignments" icon={<History size={14} />}
        subtitle={list.data ? `${list.data.total} record${list.data.total === 1 ? "" : "s"}` : undefined}
        right={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void list.reload()}><RefreshCw size={13} className={cn(list.refreshing && "animate-spin")} /></Button>}>
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <div className="relative flex-1 min-w-[200px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
            <input type="search" value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="Worker ID, name or table code"
              aria-label="Search assignments" className="control !h-8 !pl-8 !text-caption" data-testid="history-search" />
          </div>
          {(factories?.length ?? 0) > 1 && (
            <FilterSelect label="Factory" value={factoryParam} onChange={(v) => setParams({ factory: v || null, line: null, department: null })}>
              <option value="">All factories</option>
              {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
            </FilterSelect>
          )}
          <FilterSelect label="Status" value={status} onChange={(v) => setParams({ status: v || null })} testId="history-status">
            <option value="">Any</option>
            {ASSIGNMENT_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
          </FilterSelect>
          {factoryId && (
            <>
              <FilterSelect label="Line" value={line} onChange={(v) => setParams({ line: v || null })}>
                <option value="">All</option>
                {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </FilterSelect>
              <FilterSelect label="Department" value={department} onChange={(v) => setParams({ department: v || null })}>
                <option value="">All</option>
                {(departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
              </FilterSelect>
            </>
          )}
          <label className="flex items-center gap-2 text-caption text-fg-tertiary">
            From <input type="date" value={from} onChange={(e) => setParams({ from: e.target.value || null })} className="control !h-8 !w-auto !text-caption" />
          </label>
          <label className="flex items-center gap-2 text-caption text-fg-tertiary">
            To <input type="date" value={to} onChange={(e) => setParams({ to: e.target.value || null })} className="control !h-8 !w-auto !text-caption" />
          </label>
          {filtered && <Button size="sm" variant="ghost" onClick={() => { setDraft(""); setParams({ q: null, status: null, line: null, department: null, from: null, to: null }); }}>Clear filters</Button>}
        </div>
        {list.loading ? (
          <div className="p-4"><SkeletonRows rows={8} rowHeight="h-11" /></div>
        ) : list.error && !list.data ? (
          <ErrorState className="m-4" title="Couldn't load assignments" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
        ) : (list.data?.items.length ?? 0) === 0 ? (
          <EmptyState className="m-4" icon={<History size={18} />} title={filtered ? "No assignments match" : "No assignments yet"} body={filtered ? "Change or clear the filters." : "Assignments appear here once workers are assigned to workstations."} />
        ) : (
          <>
            <AssignmentHistoryTable rows={list.data!.items} showWorker testId="history-table" />
            <Pagination page={list.data!.page} pageSize={list.data!.pageSize} total={list.data!.total} onPage={(p) => setParams({ page: String(p) }, false)} />
          </>
        )}
      </Panel>
    </div>
  );
}
