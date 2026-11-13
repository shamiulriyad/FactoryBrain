"use client";

// Overtime approval dashboard: requests waiting for a decision, approved
// overtime in progress, and completed overtime awaiting HR's payable
// confirmation. You can't decide on your own requests (the API refuses it).

import { useState } from "react";
import { CheckCheck, Hourglass, RefreshCw, Timer } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { StatCard } from "@/components/ui/StatCard";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useMyFactories } from "@/components/workers/shared";
import { AttendanceTabs, OvertimeActionDialog, OvertimeTable, useQueryState, type OvertimeAction } from "@/components/attendance/shared";
import { useResource, type Resource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { P, type PagedResult } from "@/lib/access";
import { OVERTIME_NOTE, minutes, type Overtime } from "@/lib/attendance";

export default function OvertimeApprovalsPage() {
  return (
    <RequirePermission perms={[P.OvertimeApprove]} what="Overtime approvals">
      <Approvals />
    </RequirePermission>
  );
}

function Approvals() {
  const q = useQueryState("/app/attendance/overtime/approvals");
  const { factories } = useMyFactories();
  const factory = q.get("factory");
  const canConfirm = useCan(P.AttendanceManage);
  const base = factory ? `&factoryId=${factory}` : "";
  const pending = useResource<PagedResult<Overtime>>(`/api/overtime?status=PendingApproval&pageSize=100${base}`, { pollMs: 60_000 });
  const running = useResource<PagedResult<Overtime>>(`/api/overtime?status=InProgress&pageSize=50${base}`, { pollMs: 60_000 });
  const approved = useResource<PagedResult<Overtime>>(`/api/overtime?status=Approved&pageSize=50${base}`);
  const completed = useResource<PagedResult<Overtime>>(`/api/overtime?status=Completed&pageSize=100${base}`);
  const [acting, setActing] = useState<{ a: OvertimeAction; o: Overtime } | null>(null);
  const toConfirm = (completed.data?.items ?? []).filter((o) => o.payableMinutes === null || o.payableMinutes === undefined);
  const pendingMinutes = (pending.data?.items ?? []).reduce((n, o) => n + o.requestedMinutes, 0);

  function reload() {
    for (const r of [pending, running, approved, completed]) void r.reload();
  }

  return (
    <div className="page" data-testid="overtime-approvals-page">
      <PageHeader title="Overtime approvals" description="Decide pending requests, follow approved overtime as it happens, and confirm payable minutes once it's done."
        actions={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={reload}><RefreshCw size={13} className={cn(pending.refreshing && "animate-spin")} /></Button>} />
      <AttendanceTabs className="mt-4" />
      <div className="mt-4 flex flex-wrap items-center gap-3">
        {(factories?.length ?? 0) > 1 && (
          <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null })}>
            <option value="">All factories</option>
            {factories!.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
          </FilterSelect>
        )}
      </div>
      <div className="mt-4 grid gap-3 grid-cols-2 lg:grid-cols-4">
        <StatCard label="Awaiting decision" value={pending.data?.total ?? "—"} hint={pending.data ? `${minutes(pendingMinutes)} requested` : undefined} tone={(pending.data?.total ?? 0) > 0 ? "warn" : "neutral"} loading={pending.loading} icon={<Hourglass size={14} />} testId="ot-stat-pending" />
        <StatCard label="Approved, not started" value={approved.data?.total ?? "—"} loading={approved.loading} />
        <StatCard label="In progress" value={running.data?.total ?? "—"} loading={running.loading} icon={<Timer size={14} />} />
        <StatCard label="Payable to confirm" value={completed.data ? toConfirm.length : "—"} hint="Completed, not yet confirmed by HR" tone={toConfirm.length > 0 ? "warn" : "neutral"} loading={completed.loading} icon={<CheckCheck size={14} />} />
      </div>
      <Callout className="mt-4" tone="info">{OVERTIME_NOTE}</Callout>

      <Section title="Awaiting your decision" r={pending} empty="No requests are waiting." onAction={(a, o) => setActing({ a, o })} testId="ot-pending" />
      <Section title="In progress" r={running} empty="No approved overtime is running now." onAction={(a, o) => setActing({ a, o })} testId="ot-running" />
      <Section title="Approved, not started" r={approved} empty="Nothing approved is waiting to start." onAction={(a, o) => setActing({ a, o })} testId="ot-approved" />
      <Panel className="mt-6" flush title="Completed: payable minutes to confirm" subtitle={canConfirm ? "HR confirms up to the lower of approved and actual minutes." : "HR / attendance officers confirm payable minutes."}>
        {completed.loading ? <div className="p-4"><SkeletonRows rows={3} /></div>
          : completed.error && !completed.data ? <ErrorState className="m-4" title="Couldn't load" body={errorMessage(completed.error)} retryLabel="Try again" onRetry={() => void completed.reload()} />
          : toConfirm.length === 0 ? <EmptyState className="m-4" icon={<CheckCheck size={18} />} title="Nothing to confirm" body="Completed overtime with unconfirmed payable minutes appears here." />
          : <OvertimeTable rows={toConfirm} onAction={(a, o) => setActing({ a, o })} testId="ot-to-confirm" />}
      </Panel>
      <OvertimeActionDialog open={!!acting} action={acting?.a ?? "approve"} overtime={acting?.o ?? null} onClose={() => setActing(null)} onDone={() => { setActing(null); reload(); }} />
    </div>
  );
}

function Section({ title, r, empty, onAction, testId }: { title: string; r: Resource<PagedResult<Overtime>>; empty: string; onAction: (a: OvertimeAction, o: Overtime) => void; testId: string }) {
  return (
    <Panel className="mt-6" flush title={title} subtitle={r.data ? `${r.data.total}` : undefined}>
      {r.loading ? <div className="p-4"><SkeletonRows rows={3} /></div>
        : r.error && !r.data ? <ErrorState className="m-4" title="Couldn't load" body={errorMessage(r.error)} retryLabel="Try again" onRetry={() => void r.reload()} />
        : (r.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" title={empty} body="" />
        : <OvertimeTable rows={r.data!.items} onAction={onAction} testId={testId} />}
    </Panel>
  );
}
