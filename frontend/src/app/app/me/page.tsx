"use client";

// A worker's own page: their worker profile and assigned workstation
// (GET /api/workers/me). Attendance, shift, activity and break records are
// added here when those modules ship; the permission (self.view) is in place.

import Link from "next/link";
import { CalendarClock, History, IdCard, MapPin } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useResource } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { useT } from "@/lib/useT";
import { P } from "@/lib/access";
import { formatDay, type Worker } from "@/lib/workers";
import { WorkerAvatar, WorkerStatusBadge } from "@/components/workers/shared";
import { AssignmentHistoryTable } from "@/components/assignments/shared";
import { NOT_PRESENCE_NOTE, type Assignment } from "@/lib/assignments";
import { OvertimeTable, SessionTable } from "@/components/attendance/shared";
import { clock, minutes, shiftWindow, type WorkerAttendance } from "@/lib/attendance";

export default function MyProfilePage() {
  return (
    <RequirePermission perms={[P.SelfView]} what="Your worker profile">
      <Mine />
    </RequirePermission>
  );
}

function Mine() {
  const { locale } = useT();
  const me = useResource<Worker>("/api/workers/me");
  const notLinked = isApiError(me.error) && me.error.status === 404;

  return (
    <div className="page page-narrow" data-testid="me-page">
      <PageHeader eyebrow="My workspace" title={me.data?.fullName ?? "My profile"} description="Your worker profile, workstation and records." />
      <div className="mt-6 space-y-4">
        {me.loading ? (
          <SkeletonRows rows={3} rowHeight="h-14" />
        ) : notLinked ? (
          <EmptyState icon={<IdCard size={18} />} title="No worker profile linked" body="Your login isn't linked to a worker profile yet. Ask HR or your Factory Admin." />
        ) : me.error || !me.data ? (
          <ErrorState title="Couldn't load your profile" body={me.error ? errorMessage(me.error) : undefined} retryLabel="Try again" onRetry={() => void me.reload()} />
        ) : (
          <>
            <Panel title="Profile" icon={<IdCard size={14} />}>
              <dl className="grid gap-3 sm:grid-cols-3 text-caption">
                <Item label="Worker ID"><span className="inline-flex items-center gap-2"><WorkerAvatar worker={me.data} size={28} /><span className="font-mono">{me.data.workerCode}</span></span></Item>
                <Item label="Employee number"><span className="font-mono">{me.data.employeeCode ?? "—"}</span></Item>
                <Item label="Status"><WorkerStatusBadge status={me.data.status} /></Item>
                <Item label="Department">{me.data.department?.name ?? "—"}</Item>
                <Item label="Designation">{me.data.designation ?? "—"}</Item>
                <Item label="Grade">{me.data.grade ?? "—"}</Item>
                <Item label="Joined">{formatDay(me.data.joinedOn, locale)}</Item>
                <Item label="Employment type">{me.data.employmentType ?? "—"}</Item>
                <Item label="Phone">{me.data.phone ?? "—"}</Item>
              </dl>
            </Panel>
            <Panel title="Assigned workstation" icon={<MapPin size={14} />}>
              <p className="text-body text-fg-primary" data-testid="my-workstation">
                {me.data.workstation
                  ? <>Workstation <strong className="font-mono">{me.data.workstation}</strong> · line <strong>{me.data.line?.name ?? me.data.lineId}</strong></>
                  : me.data.lineId ? <>Line <strong>{me.data.line?.name ?? me.data.lineId}</strong>, no workstation assigned.</> : "Not assigned yet."}
              </p>
            </Panel>
            <MyAssignments />
            <MyAttendance />
          </>
        )}
        <p className="text-caption text-fg-tertiary">Account and password: <Link href="/app/account" className="text-accent hover:underline">Account settings</Link>.</p>
      </div>
    </div>
  );
}

/** The signed-in worker's own shift, recent attendance, breaks and overtime (GET /api/attendance/me). */
function MyAttendance() {
  const mine = useResource<WorkerAttendance>("/api/attendance/me");
  return (
    <Panel title="Attendance, shift & breaks" icon={<CalendarClock size={14} />} testId="my-attendance" flush>
      {mine.loading ? <div className="p-4"><SkeletonRows rows={3} /></div>
        : mine.error || !mine.data ? <p className="p-4 text-caption text-fg-tertiary">{mine.error ? errorMessage(mine.error) : "No attendance yet."}</p>
        : (
          <div className="divide-y divide-border-subtle">
            <div className="p-4 grid gap-3 sm:grid-cols-3 text-caption">
              <Item label="Current shift">{mine.data.currentShift ? <><span className="font-mono">{mine.data.currentShift.shift.code}</span> · {shiftWindow(mine.data.currentShift.shift)}</> : "No shift assigned"}</Item>
              <Item label="Right now">{mine.data.openSession ? `Checked in since ${clock(mine.data.openSession.checkInAt)}${mine.data.openSession.onBreak ? " (on a break)" : ""}` : "Not checked in"}</Item>
              <Item label="Last 30 days">{`${mine.data.sessions.length} session${mine.data.sessions.length === 1 ? "" : "s"} · ${minutes(mine.data.sessions.reduce((n, x) => n + x.figures.netWorkingMinutes, 0))} worked`}</Item>
            </div>
            {mine.data.sessions.length > 0 && <SessionTable rows={mine.data.sessions} showWorker={false} testId="my-sessions" />}
            {mine.data.overtime.length > 0 && <OvertimeTable rows={mine.data.overtime} testId="my-overtime" />}
          </div>
        )}
    </Panel>
  );
}

/** The signed-in worker's own assignment history (GET /api/workers/me/assignments). */
function MyAssignments() {
  const history = useResource<Assignment[]>("/api/workers/me/assignments");
  if (history.loading || history.error || !history.data || history.data.length === 0) return null;
  return (
    <Panel title="My assignment history" icon={<History size={14} />} subtitle={NOT_PRESENCE_NOTE} flush>
      <AssignmentHistoryTable rows={history.data} testId="my-assignments" />
    </Panel>
  );
}

function Item({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <dt className="text-fg-tertiary">{label}</dt>
      <dd className="mt-0.5 text-fg-primary">{children}</dd>
    </div>
  );
}
