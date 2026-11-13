"use client";

// Worker Assignment Dashboard: workstation occupancy in the caller's scope
// (Super Admin: everything; factory roles: their factories; supervisors:
// their lines), a visual layout of each production line, and recent
// transfers. Occupancy reflects recorded assignments only, never presence.

import Link from "next/link";
import { useMemo, useState } from "react";
import { ArrowRightLeft, History, Info, LayoutGrid, RefreshCw, Table2, UserPlus, UsersRound } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { FilterSelect } from "@/components/ui/Field";
import { StatCard } from "@/components/ui/StatCard";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useMyFactories } from "@/components/workers/shared";
import { CapacityMeter, OccupancyBadge, useLines } from "@/components/assignments/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { P, type PagedResult } from "@/lib/access";
import { NOT_PRESENCE_NOTE, OCCUPANCY, OCCUPANCY_LABEL, type AssignmentStatistics, type Occupancy, type Workstation } from "@/lib/assignments";

export default function AssignmentDashboardPage() {
  return (
    <RequirePermission perms={[P.WorkersView]} what="Workstation assignments">
      <Dashboard />
    </RequirePermission>
  );
}

const TILE: Record<Occupancy, string> = {
  Available: "border-[var(--risk-low-border)] bg-[var(--risk-low-soft)]",
  Occupied: "border-[var(--info-border)] bg-[var(--info-soft)]",
  CapacityReached: "border-[var(--risk-medium-border)] bg-[var(--risk-medium-soft)]",
  Inactive: "border-border-subtle bg-surface-2 opacity-60"
};

function Dashboard() {
  const { locale } = useT();
  const canAssign = useCan(P.WorkersAssign);
  const { factories } = useMyFactories();
  const [factory, setFactory] = useState("");
  const [line, setLine] = useState("");
  const factoryId = factory || (factories?.length === 1 ? factories[0].id : "");
  const lines = useLines(factoryId || null);

  const query = useMemo(() => {
    const p = new URLSearchParams();
    if (factory) p.set("factoryId", factory);
    if (line) p.set("lineId", line);
    return p;
  }, [factory, line]);
  const stats = useResource<AssignmentStatistics>(`/api/assignments/statistics?${query}`);
  const layout = useResource<PagedResult<Workstation>>(`/api/workstations?${new URLSearchParams([...query.entries(), ["pageSize", "200"]])}`);
  const s = stats.data;
  const err = stats.error ? errorMessage(stats.error) : null;

  const byLine = useMemo(() => {
    const groups = new Map<string, { name: string; items: Workstation[] }>();
    for (const w of layout.data?.items ?? []) {
      const g = groups.get(w.line.id) ?? { name: w.line.name, items: [] };
      g.items.push(w);
      groups.set(w.line.id, g);
    }
    return [...groups.entries()].sort((a, b) => a[1].name.localeCompare(b[1].name));
  }, [layout.data]);

  return (
    <div className="page" data-testid="assignments-page">
      <PageHeader
        eyebrow="Workforce"
        title="Workstation assignments"
        description="Which worker is assigned to which workstation, line by line. Assign, transfer and review the history."
        actions={
          <>
            {(factories?.length ?? 0) > 1 && (
              <FilterSelect label="Factory" value={factory} onChange={(v) => { setFactory(v); setLine(""); }}>
                <option value="">All factories</option>
                {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
              </FilterSelect>
            )}
            {factoryId && (
              <FilterSelect label="Line" value={line} onChange={setLine} testId="dashboard-line-filter">
                <option value="">All lines</option>
                {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </FilterSelect>
            )}
            <Link href="/app/assignments/workstations"><Button size="sm" variant="secondary" iconLeft={<Table2 size={13} />}>Workstations</Button></Link>
            <Link href="/app/assignments/history"><Button size="sm" variant="secondary" iconLeft={<History size={13} />}>History</Button></Link>
            <Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => { void stats.reload(); void layout.reload(); }}>
              <RefreshCw size={13} className={cn((stats.refreshing || layout.refreshing) && "animate-spin")} />
            </Button>
          </>
        }
      />

      <Callout className="mt-4" tone="info" title="Scheduled placement, not presence">{NOT_PRESENCE_NOTE}</Callout>

      <div className="mt-4 grid gap-3 grid-cols-2 md:grid-cols-3 xl:grid-cols-6" data-testid="assignment-stats">
        <StatCard label="Total workstations" value={s?.totalWorkstations ?? 0} loading={stats.loading} error={err} icon={<LayoutGrid size={14} />}
          hint={s ? `${s.totalCapacity} places in active workstations` : undefined} href="/app/assignments/workstations" />
        <StatCard label="Occupied" value={s?.occupiedWorkstations ?? 0} tone="good" loading={stats.loading} error={err}
          hint={s ? `${s.fullWorkstations} at capacity` : undefined} href="/app/assignments/workstations?occupancy=CapacityReached" />
        <StatCard label="Available" value={s?.availableWorkstations ?? 0} loading={stats.loading} error={err}
          hint={s && s.inactiveWorkstations > 0 ? `${s.inactiveWorkstations} inactive` : undefined} href="/app/assignments/workstations?occupancy=Available" />
        <StatCard label="Active assignments" value={s?.activeAssignments ?? 0} loading={stats.loading} error={err} href="/app/assignments/history?status=Active" />
        <StatCard label="Active workers" value={s?.activeWorkers ?? 0} loading={stats.loading} error={err} icon={<UsersRound size={14} />} />
        <StatCard label="Unassigned active workers" value={s?.unassignedActiveWorkers ?? 0} tone={(s?.unassignedActiveWorkers ?? 0) > 0 ? "warn" : "neutral"}
          loading={stats.loading} error={err} hint="Active, with no workstation" testId="stat-unassigned" />
      </div>

      <div className="mt-4 grid gap-4 xl:grid-cols-[minmax(0,1fr)_360px]">
        <Panel title="Line layout" icon={<LayoutGrid size={14} />} subtitle="Each tile is a workstation, in line order. Open one to assign, transfer or see its history."
          right={<span className="hidden sm:flex flex-wrap gap-1.5">{OCCUPANCY.map((o) => <OccupancyBadge key={o} occupancy={o} />)}</span>}
          testId="line-layout">
          {layout.loading ? (
            <SkeletonRows rows={4} rowHeight="h-16" />
          ) : layout.error ? (
            <ErrorState title="Couldn't load workstations" body={errorMessage(layout.error)} retryLabel="Try again" onRetry={() => void layout.reload()} />
          ) : byLine.length === 0 ? (
            <EmptyState icon={<LayoutGrid size={18} />} title="No workstations yet"
              body="Administrators add the workstations (tables) of each line under Workstations, then workers can be assigned." />
          ) : (
            <div className="space-y-5">
              {byLine.map(([lineId, g]) => {
                const occ = s?.byLine.find((l) => l.lineId === lineId);
                return (
                  <section key={lineId} aria-label={g.name} data-testid={`layout-line-${lineId}`}>
                    <div className="flex flex-wrap items-center gap-2 mb-2">
                      <h3 className="text-body font-semibold text-fg-primary">{g.name}</h3>
                      <span className="font-mono text-caption text-fg-tertiary">{lineId}</span>
                      {occ && <span className="text-caption text-fg-secondary">{occ.assigned} assigned · {occ.occupied} occupied · {occ.available} available{occ.inactive ? ` · ${occ.inactive} inactive` : ""}</span>}
                      <Link href={`/app/assignments/workstations?line=${lineId}`} className="ml-auto text-caption text-accent hover:underline">List</Link>
                    </div>
                    <ol className="grid gap-2 grid-cols-[repeat(auto-fill,minmax(112px,1fr))]">
                      {g.items.map((w) => (
                        <li key={w.id}>
                          <Link href={`/app/assignments/workstations/${w.id}`}
                            className={cn("block h-full rounded-[var(--radius-control)] border p-2 transition-shadow hover:shadow-md focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--accent)]", TILE[w.occupancy])}
                            title={`${w.code}: ${OCCUPANCY_LABEL[w.occupancy]}, ${w.assignedCount} of ${w.capacity}`}
                            data-testid="layout-tile" data-occupancy={w.occupancy}>
                            <span className="flex items-center justify-between gap-1">
                              <span className="font-mono text-caption font-semibold text-fg-primary">{w.code}</span>
                              <span className="text-[11px] tabular-nums text-fg-secondary">{w.assignedCount}/{w.capacity}</span>
                            </span>
                            <span className="mt-1 block min-h-[2.2em] text-[11px] leading-tight text-fg-secondary">
                              {w.workers === undefined || w.workers === null
                                ? OCCUPANCY_LABEL[w.occupancy]
                                : w.workers.length === 0
                                  ? (w.isActive ? "Free" : "Inactive")
                                  : w.workers.map((x) => x.workerCode).join(", ")}
                            </span>
                            {w.machines.length > 0 && <span className="block text-[10.5px] text-fg-tertiary truncate">{w.machines.map((m) => m.code).join(", ")}</span>}
                          </Link>
                        </li>
                      ))}
                    </ol>
                  </section>
                );
              })}
              {(layout.data?.total ?? 0) > (layout.data?.items.length ?? 0) && (
                <p className="text-caption text-fg-tertiary">Showing the first {layout.data!.items.length} of {layout.data!.total} workstations. Filter by line to see the rest.</p>
              )}
            </div>
          )}
        </Panel>

        <div className="space-y-4">
          <Panel title="Recent transfers" icon={<ArrowRightLeft size={14} />} flush testId="recent-transfers">
            {stats.loading ? (
              <div className="p-4"><SkeletonRows rows={3} rowHeight="h-10" /></div>
            ) : (s?.recentTransfers.length ?? 0) === 0 ? (
              <p className="p-4 text-caption text-fg-tertiary">No transfers recorded yet.</p>
            ) : (
              <ul className="divide-y divide-border-subtle">
                {s!.recentTransfers.map((t) => (
                  <li key={t.id} className="px-4 py-2.5 text-caption">
                    <span className="flex flex-wrap items-center gap-1.5">
                      <Link href={`/app/workers/${t.workerId}`} className="font-mono text-fg-primary hover:underline">{t.workerCode}</Link>
                      <span className="text-fg-secondary truncate">{t.workerName}</span>
                    </span>
                    <span className="block text-fg-secondary">
                      → <Link href={`/app/assignments/workstations/${t.workstation.id}`} className="font-mono hover:underline">{t.workstation.code}</Link> · {t.line.name}
                    </span>
                    <span className="block text-fg-tertiary">{formatDateTime(t.startAt, locale)}{t.reason ? ` · ${t.reason}` : ""}</span>
                  </li>
                ))}
              </ul>
            )}
          </Panel>

          <Panel title="By line" icon={<Info size={14} />} flush>
            {(s?.byLine.length ?? 0) === 0 ? (
              <p className="p-4 text-caption text-fg-tertiary">No data.</p>
            ) : (
              <table className="data-table">
                <thead><tr><th>Line</th><th className="num">Assigned</th><th>Places</th></tr></thead>
                <tbody>
                  {s!.byLine.map((l) => (
                    <tr key={l.lineId}>
                      <td><button type="button" className="hover:underline text-left" onClick={() => setLine(l.lineId)}>{l.lineName}</button></td>
                      <td className="num tabular-nums">{l.assigned}</td>
                      <td><CapacityMeter assigned={l.assigned} capacity={l.capacity} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </Panel>

          {canAssign && (
            <Callout tone="info" title="Assigning workers">
              Open a workstation tile (or a worker&apos;s profile) and choose <strong>Assign</strong>. Moving someone uses <strong>Transfer</strong>, which keeps both records.
              <Link href="/app/assignments/workstations?occupancy=Available" className="mt-2 inline-flex items-center gap-1 text-accent hover:underline"><UserPlus size={12} aria-hidden /> Available workstations</Link>
            </Callout>
          )}
        </div>
      </div>
    </div>
  );
}
