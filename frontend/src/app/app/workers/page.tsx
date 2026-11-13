"use client";

// Worker Directory: every worker the API lets the caller see (Super Admin:
// all factories; factory roles: their factories; a supervisor: their lines).
// Search, filters, sorting and pagination run on the server
// (GET /api/workers); the URL keeps the current view so it can be shared.

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { ArrowDown, ArrowUp, BarChart3, Download, FileUp, RefreshCw, Search, UserPlus, UsersRound } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { StatCard } from "@/components/ui/StatCard";
import { useToast } from "@/components/ui/Toast";
import { Pagination } from "@/components/access/shared";
import { WorkerAvatar, WorkerStatusBadge, useFactoryOrg, useMyFactories, Val } from "@/components/workers/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { P, type PagedResult } from "@/lib/access";
import { EMPLOYMENT_TYPES, WORKER_STATUSES, downloadCsv, formatDay, type Worker, type WorkerStatistics } from "@/lib/workers";

const PAGE_SIZE = 25;
type SortKey = "workerId" | "name" | "joined" | "status" | "department" | "designation";
const FILTER_KEYS = ["q", "factory", "department", "line", "status", "designation", "type"] as const;

export default function WorkersPage() {
  return (
    <RequirePermission perms={[P.WorkersView]} what="The worker directory">
      <Directory />
    </RequirePermission>
  );
}

function Directory() {
  const { locale } = useT();
  const router = useRouter();
  const search = useSearchParams();
  const toast = useToast();
  const canManage = useCan(P.WorkersManage);
  const canExport = useCan(P.WorkersDetailsView, P.WorkersManage);
  const seesEmployment = canExport;

  const q = search?.get("q") ?? "";
  const factoryParam = search?.get("factory") ?? "";
  const departmentId = search?.get("department") ?? "";
  const lineId = search?.get("line") ?? "";
  const status = search?.get("status") ?? "";
  const designation = search?.get("designation") ?? "";
  const employmentType = search?.get("type") ?? "";
  const sort = search?.get("sort") ?? "workerId";
  const page = Math.max(1, Number(search?.get("page")) || 1);

  const { factories } = useMyFactories();
  // Departments and lines are per factory: with one factory it is implicit, with several the filter picks it.
  const orgFactory = factoryParam || (factories?.length === 1 ? factories[0].id : "");
  const { departments, lines } = useFactoryOrg(orgFactory || null);

  const [draft, setDraft] = useState(q);
  const [designationDraft, setDesignationDraft] = useState(designation);
  const [exporting, setExporting] = useState(false);
  useEffect(() => setDraft(q), [q]);
  useEffect(() => setDesignationDraft(designation), [designation]);

  function setParams(next: Record<string, string | null>, resetPage = true) {
    const params = new URLSearchParams(search?.toString() ?? "");
    for (const [k, val] of Object.entries(next)) {
      if (val) params.set(k, val);
      else params.delete(k);
    }
    if (resetPage) params.delete("page");
    router.replace(`/app/workers${params.toString() ? `?${params}` : ""}`);
  }

  // Debounced text inputs.
  useEffect(() => {
    if (draft === q) return;
    const id = window.setTimeout(() => setParams({ q: draft.trim() || null }), 300);
    return () => window.clearTimeout(id);
  }, [draft]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (designationDraft === designation) return;
    const id = window.setTimeout(() => setParams({ designation: designationDraft.trim() || null }), 400);
    return () => window.clearTimeout(id);
  }, [designationDraft]); // eslint-disable-line react-hooks/exhaustive-deps

  const filterQuery = useMemo(() => {
    const p = new URLSearchParams();
    if (q) p.set("search", q);
    if (factoryParam) p.set("factoryId", factoryParam);
    if (departmentId) p.set("departmentId", departmentId);
    if (lineId) p.set("lineId", lineId);
    if (status) p.set("status", status);
    if (designation) p.set("designation", designation);
    if (employmentType) p.set("employmentType", employmentType);
    return p;
  }, [q, factoryParam, departmentId, lineId, status, designation, employmentType]);

  const listPath = useMemo(() => {
    const p = new URLSearchParams(filterQuery);
    p.set("page", String(page));
    p.set("pageSize", String(PAGE_SIZE));
    p.set("sort", sort);
    return `/api/workers?${p}`;
  }, [filterQuery, page, sort]);

  const workers = useResource<PagedResult<Worker>>(listPath);
  const stats = useResource<WorkerStatistics>(`/api/workers/statistics${factoryParam ? `?factoryId=${factoryParam}` : ""}`);
  const filtered = FILTER_KEYS.some((k) => !!search?.get(k) && !(k === "factory" && factories?.length === 1));
  const factoryName = useMemo(() => new Map((factories ?? []).map((f) => [f.id, f.code])), [factories]);

  function toggleSort(key: SortKey) {
    const current = sort.replace(/^-/, "");
    setParams({ sort: current === key && !sort.startsWith("-") ? `-${key}` : key });
  }

  async function exportCsv() {
    setExporting(true);
    try {
      const p = new URLSearchParams(filterQuery);
      p.set("sort", sort);
      const rows = await downloadCsv(`/api/workers/export?${p}`, "workers.csv");
      toast.push({ tone: "success", title: "Export ready", description: rows !== null ? `${rows} worker${rows === 1 ? "" : "s"} exported.` : undefined });
    } catch (e) {
      toast.push({ tone: "error", title: "Export failed", description: errorMessage(e) });
    } finally {
      setExporting(false);
    }
  }

  function SortHeader({ k, children, className }: { k: SortKey; children: React.ReactNode; className?: string }) {
    const active = sort.replace(/^-/, "") === k;
    const desc = sort.startsWith("-");
    return (
      <th className={className} aria-sort={active ? (desc ? "descending" : "ascending") : "none"}>
        <button type="button" onClick={() => toggleSort(k)} className="inline-flex items-center gap-1 hover:text-fg-primary">
          {children}
          {active && (desc ? <ArrowDown size={11} aria-hidden /> : <ArrowUp size={11} aria-hidden />)}
        </button>
      </th>
    );
  }

  const s = stats.data;
  const statError = stats.error ? errorMessage(stats.error) : null;

  return (
    <div className="page" data-testid="workers-page">
      <PageHeader
        eyebrow="Workforce"
        title="Worker directory"
        description="Worker records with their permanent Worker IDs. A worker needs no login; accounts are linked separately when someone needs one."
        actions={
          <>
            <Link href="/app/workers/statistics">
              <Button variant="secondary" size="sm" iconLeft={<BarChart3 size={13} />}>Statistics</Button>
            </Link>
            {canExport && (
              <Button variant="secondary" size="sm" iconLeft={<Download size={13} />} onClick={() => void exportCsv()} disabled={exporting} data-testid="workers-export">
                {exporting ? "Exporting…" : "Export CSV"}
              </Button>
            )}
            {canManage && (
              <>
                <Link href="/app/workers/import">
                  <Button variant="secondary" size="sm" iconLeft={<FileUp size={13} />} data-testid="workers-import">Import CSV</Button>
                </Link>
                <Link href="/app/workers/new">
                  <Button variant="primary" size="sm" iconLeft={<UserPlus size={13} />} data-testid="workers-new">Add worker</Button>
                </Link>
              </>
            )}
          </>
        }
      />

      <div className="mt-6 grid gap-3 grid-cols-2 lg:grid-cols-4" data-testid="workers-stats">
        <StatCard label="Total workers" value={s?.total ?? 0} loading={stats.loading} error={statError} icon={<UsersRound size={14} />}
          hint={factoryParam ? "In the selected factory" : "In your scope"} />
        <StatCard label="Active" value={s?.active ?? 0} tone="good" loading={stats.loading} error={statError} href="/app/workers?status=Active" />
        <StatCard label="Inactive / suspended" value={(s?.inactive ?? 0) + (s?.suspended ?? 0)} tone={(s?.suspended ?? 0) > 0 ? "warn" : "neutral"}
          loading={stats.loading} error={statError} hint={s ? `${s.inactive} inactive · ${s.suspended} suspended` : undefined} />
        <StatCard label="Left the company" value={(s?.resigned ?? 0) + (s?.terminated ?? 0)} loading={stats.loading} error={statError}
          hint={s ? `${s.resigned} resigned · ${s.terminated} terminated` : undefined} />
      </div>

      <Panel
        className="mt-4"
        flush
        title="Workers"
        icon={<UsersRound size={14} />}
        subtitle={workers.data ? `${workers.data.total} ${workers.data.total === 1 ? "worker" : "workers"}${filtered ? " match" : ""}` : undefined}
        right={
          <Button variant="secondary" size="sm" onClick={() => { void workers.reload(); void stats.reload(); }} disabled={workers.refreshing} aria-label="Refresh workers">
            <RefreshCw size={13} className={cn(workers.refreshing && "animate-spin")} />
          </Button>
        }
      >
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <div className="relative flex-1 min-w-[220px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
            <input
              type="search"
              value={draft}
              onChange={(e) => setDraft(e.target.value)}
              placeholder={seesEmployment ? "Search Worker ID, name or employee number" : "Search Worker ID or name"}
              aria-label="Search workers"
              className="control !h-8 !pl-8 !text-caption"
              data-testid="workers-search"
            />
          </div>
          {(factories?.length ?? 0) > 1 && (
            <FilterSelect label="Factory" value={factoryParam} onChange={(val) => setParams({ factory: val || null, department: null, line: null })} testId="workers-factory-filter">
              <option value="">All factories</option>
              {(factories ?? []).map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
            </FilterSelect>
          )}
          {orgFactory && (
            <>
              <FilterSelect label="Department" value={departmentId} onChange={(val) => setParams({ department: val || null })} testId="workers-department-filter">
                <option value="">All</option>
                {(departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
              </FilterSelect>
              <FilterSelect label="Line" value={lineId} onChange={(val) => setParams({ line: val || null })} testId="workers-line-filter">
                <option value="">All</option>
                {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
                <option value="none">No line</option>
              </FilterSelect>
            </>
          )}
          <FilterSelect label="Status" value={status} onChange={(val) => setParams({ status: val || null })} testId="workers-status-filter">
            <option value="">Any</option>
            {WORKER_STATUSES.map((st) => <option key={st} value={st}>{st}</option>)}
          </FilterSelect>
          {seesEmployment && (
            <FilterSelect label="Type" value={employmentType} onChange={(val) => setParams({ type: val || null })}>
              <option value="">Any</option>
              {EMPLOYMENT_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
            </FilterSelect>
          )}
          <input
            type="text"
            value={designationDraft}
            onChange={(e) => setDesignationDraft(e.target.value)}
            placeholder="Designation"
            aria-label="Filter by designation (exact)"
            className="control !h-8 !w-40 !text-caption"
            data-testid="workers-designation-filter"
          />
          {filtered && (
            <Button size="sm" variant="ghost" onClick={() => { setDraft(""); setDesignationDraft(""); setParams(Object.fromEntries(FILTER_KEYS.map((k) => [k, null]))); }}>
              Clear filters
            </Button>
          )}
        </div>

        {workers.loading ? (
          <div className="p-4"><SkeletonRows rows={8} rowHeight="h-12" /></div>
        ) : workers.error && !workers.data ? (
          <ErrorState title="Couldn't load workers" body={errorMessage(workers.error)} retryLabel="Try again" onRetry={() => void workers.reload()} className="m-4" />
        ) : (workers.data?.items.length ?? 0) === 0 ? (
          <EmptyState
            className="m-4"
            icon={<UsersRound size={18} />}
            title={filtered ? "No workers match" : "No workers yet"}
            body={filtered ? "Change or clear the filters." : canManage ? "Register the first worker with Add worker, or import a CSV file." : "Workers in your scope will appear here."}
          />
        ) : (
          <>
            <div className="overflow-x-auto" data-testid="workers-table">
              <table className="data-table">
                <thead>
                  <tr>
                    <SortHeader k="workerId">Worker ID</SortHeader>
                    <SortHeader k="name">Name</SortHeader>
                    <SortHeader k="department">Department</SortHeader>
                    <SortHeader k="designation">Designation</SortHeader>
                    <th>Line</th>
                    {(factories?.length ?? 0) > 1 && !factoryParam && <th>Factory</th>}
                    <SortHeader k="status">Status</SortHeader>
                    {seesEmployment && <SortHeader k="joined">Joined</SortHeader>}
                  </tr>
                </thead>
                <tbody>
                  {workers.data!.items.map((w) => (
                    <tr key={w.id} data-testid="worker-row" data-worker-code={w.workerCode} className="cursor-pointer" onClick={() => router.push(`/app/workers/${w.id}`)}>
                      <td className="whitespace-nowrap">
                        <Link href={`/app/workers/${w.id}`} onClick={(e) => e.stopPropagation()} className="font-mono text-fg-primary hover:underline">{w.workerCode}</Link>
                      </td>
                      <td>
                        <span className="flex items-center gap-2.5 min-w-0">
                          <WorkerAvatar worker={w} size={28} />
                          <span className="min-w-0">
                            <span className="block text-fg-primary font-medium truncate">{w.fullName}</span>
                            {w.employeeCode && <span className="block text-caption text-fg-tertiary font-mono truncate">{w.employeeCode}</span>}
                          </span>
                        </span>
                      </td>
                      <td className="text-caption text-fg-secondary"><Val>{w.department?.name}</Val></td>
                      <td className="text-caption text-fg-secondary"><Val>{w.designation}</Val></td>
                      <td className="text-caption text-fg-secondary whitespace-nowrap">
                        <Val>{w.line?.name ?? w.lineId}</Val>
                        {w.workstation && <span className="block text-fg-tertiary">{w.workstation}</span>}
                      </td>
                      {(factories?.length ?? 0) > 1 && !factoryParam && <td className="text-caption text-fg-secondary">{factoryName.get(w.factoryId) ?? "—"}</td>}
                      <td><WorkerStatusBadge status={w.status} /></td>
                      {seesEmployment && <td className="whitespace-nowrap text-caption text-fg-secondary">{formatDay(w.joinedOn, locale)}</td>}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={workers.data!.page} pageSize={workers.data!.pageSize} total={workers.data!.total} onPage={(p) => setParams({ page: String(p) }, false)} />
          </>
        )}
      </Panel>
    </div>
  );
}
