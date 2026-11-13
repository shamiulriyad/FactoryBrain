"use client";

// Worker summary: counts over the workers the caller can see (GET
// /api/workers/statistics), by status, department, line, designation and
// employment type. Every number comes from the database; nothing is estimated.

import Link from "next/link";
import { useState } from "react";
import { BarChart3, RefreshCw, UsersRound } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { FilterSelect } from "@/components/ui/Field";
import { StatCard } from "@/components/ui/StatCard";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useMyFactories } from "@/components/workers/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { cn } from "@/lib/cn";
import { P } from "@/lib/access";
import type { WorkerCount, WorkerStatistics } from "@/lib/workers";

export default function WorkerStatisticsPage() {
  return (
    <RequirePermission perms={[P.WorkersView]} what="Worker statistics">
      <Statistics />
    </RequirePermission>
  );
}

function Statistics() {
  const { factories } = useMyFactories();
  const [factory, setFactory] = useState("");
  const seesEmployment = useCan(P.WorkersDetailsView, P.WorkersManage);
  const stats = useResource<WorkerStatistics>(`/api/workers/statistics${factory ? `?factoryId=${factory}` : ""}`);
  const s = stats.data;
  const filterParam = factory ? `&factory=${factory}` : "";

  return (
    <div className="page" data-testid="worker-statistics-page">
      <PageHeader
        eyebrow={<Link href="/app/workers" className="hover:underline">← Worker directory</Link>}
        title="Worker statistics"
        description="Headcount over the workers you can see. Active means currently employed and working; inactive, suspended and separated workers are kept on record."
        actions={
          <>
            {(factories?.length ?? 0) > 1 && (
              <FilterSelect label="Factory" value={factory} onChange={setFactory}>
                <option value="">All factories</option>
                {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
              </FilterSelect>
            )}
            <Button variant="secondary" size="sm" aria-label="Refresh" onClick={() => void stats.reload()}>
              <RefreshCw size={13} className={cn(stats.refreshing && "animate-spin")} />
            </Button>
          </>
        }
      />

      {stats.loading ? (
        <div className="mt-6"><SkeletonRows rows={4} rowHeight="h-24" /></div>
      ) : stats.error || !s ? (
        <ErrorState className="mt-6" title="Couldn't load statistics" body={stats.error ? errorMessage(stats.error) : undefined} retryLabel="Try again" onRetry={() => void stats.reload()} />
      ) : s.total === 0 ? (
        <EmptyState className="mt-6" icon={<UsersRound size={18} />} title="No workers yet" body="Statistics appear once workers are registered." />
      ) : (
        <>
          <div className="mt-6 grid gap-3 grid-cols-2 md:grid-cols-3 xl:grid-cols-6" data-testid="worker-stat-cards">
            <StatCard label="Total workers" value={s.total} href={`/app/workers?${filterParam.slice(1)}`} />
            <StatCard label="Active" value={s.active} tone="good" href={`/app/workers?status=Active${filterParam}`} />
            <StatCard label="Inactive" value={s.inactive} href={`/app/workers?status=Inactive${filterParam}`} />
            <StatCard label="Suspended" value={s.suspended} tone={s.suspended > 0 ? "warn" : "neutral"} href={`/app/workers?status=Suspended${filterParam}`} />
            <StatCard label="Resigned" value={s.resigned} href={`/app/workers?status=Resigned${filterParam}`} />
            <StatCard label="Terminated" value={s.terminated} tone={s.terminated > 0 ? "bad" : "neutral"} href={`/app/workers?status=Terminated${filterParam}`} />
          </div>

          <div className="mt-4 grid gap-4 lg:grid-cols-2">
            <Breakdown title="Workers by department" rows={s.byDepartment} total={s.total} link={(k) => (k === "none" ? null : `/app/workers?department=${k}${filterParam}`)} testId="stats-by-department" />
            <Breakdown title="Workers by production line" rows={s.byLine} total={s.total} link={(k) => `/app/workers?line=${k}${filterParam}`} testId="stats-by-line" />
            <Breakdown title="Workers by designation" rows={s.byDesignation} total={s.total} link={(k) => (k === "none" ? null : `/app/workers?designation=${encodeURIComponent(k)}${filterParam}`)} testId="stats-by-designation" />
            {seesEmployment && s.byEmploymentType.length > 0 && (
              <Breakdown title="Workers by employment type" rows={s.byEmploymentType} total={s.total} link={(k) => `/app/workers?type=${k}${filterParam}`} testId="stats-by-type" />
            )}
            {s.byFactory.length > 1 && <Breakdown title="Workers by factory" rows={s.byFactory} total={s.total} link={(k) => `/app/workers?factory=${k}`} testId="stats-by-factory" />}
          </div>
        </>
      )}
    </div>
  );
}

/** One breakdown as a table with an inline bar (total, and the active share inside it). */
function Breakdown({ title, rows, total, link, testId }: {
  title: string; rows: WorkerCount[]; total: number; link: (key: string) => string | null; testId: string;
}) {
  const max = Math.max(1, ...rows.map((r) => r.total));
  return (
    <Panel flush title={title} icon={<BarChart3 size={14} />} testId={testId}>
      {rows.length === 0 ? (
        <p className="p-4 text-caption text-fg-tertiary">No data.</p>
      ) : (
        <table className="data-table">
          <thead><tr><th>{title.replace("Workers by ", "").replace(/^\w/, (c) => c.toUpperCase())}</th><th className="num">Workers</th><th className="num">Active</th><th className="w-[40%]"><span className="sr-only">Share</span></th></tr></thead>
          <tbody>
            {rows.map((r) => {
              const href = link(r.key);
              return (
                <tr key={r.key}>
                  <td className={cn(r.key === "none" && "text-fg-tertiary italic")}>
                    {href ? <Link href={href} className="hover:underline">{r.label}</Link> : r.label}
                  </td>
                  <td className="num tabular-nums">{r.total}</td>
                  <td className="num tabular-nums text-fg-secondary">{r.active}</td>
                  <td>
                    <div className="h-2 rounded-full bg-surface-2 overflow-hidden" role="img" aria-label={`${r.total} of ${total} workers, ${r.active} active`}>
                      <div className="h-full bg-[var(--accent-soft)] relative" style={{ width: `${(r.total / max) * 100}%` }}>
                        <div className="absolute inset-y-0 left-0 bg-[var(--accent)]" style={{ width: `${r.total ? (r.active / r.total) * 100 : 0}%` }} />
                      </div>
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </Panel>
  );
}
