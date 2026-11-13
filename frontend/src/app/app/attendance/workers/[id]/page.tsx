"use client";

// One worker's attendance: current shift and its history, the open session,
// recent sessions with their calculated figures, breaks, overtime and
// corrections, plus manual punches and correction / overtime requests.

import Link from "next/link";
import { useMemo, useState } from "react";
import { useParams } from "next/navigation";
import { CalendarClock, Coffee, FilePen, Hourglass, LogIn, LogOut, RefreshCw } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Fact } from "@/components/workers/shared";
import {
  AttendanceTabs,
  BreakTable,
  CorrectionDialog,
  DateFilter,
  OvertimeActionDialog,
  OvertimeRequestDialog,
  OvertimeTable,
  PunchDialog,
  SessionFlags,
  SessionStatusBadge,
  SessionTable,
  ShiftAssignmentTable,
  tableCls,
  type OvertimeAction,
  type PunchAction
} from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDate, formatDateTime } from "@/lib/format";
import { P } from "@/lib/access";
import { OVERTIME_NOTE, clock, minutes, shiftWindow, todayIso, type AttendanceSession, type Overtime, type WorkerAttendance } from "@/lib/attendance";

export default function WorkerAttendancePage() {
  return (
    <RequirePermission perms={[P.AttendanceView, P.SelfView]} what="Worker attendance">
      <WorkerAttendanceView />
    </RequirePermission>
  );
}

function WorkerAttendanceView() {
  const params = useParams<{ id: string }>();
  const id = params?.id ?? "";
  const [from, setFrom] = useState(todayIso(-30));
  const [to, setTo] = useState(todayIso());
  const path = useMemo(() => `/api/attendance/${encodeURIComponent(id)}?from=${from}&to=${to}`, [id, from, to]);
  const data = useResource<WorkerAttendance>(id ? path : null);
  const canRecord = useCan(P.AttendanceRecord);
  const canCorrect = useCan(P.AttendanceManage);
  const canOvertime = useCan(P.OvertimeManage);
  const canSeeAll = useCan(P.AttendanceView);
  const [punch, setPunch] = useState<PunchAction | null>(null);
  const [correcting, setCorrecting] = useState<AttendanceSession | null | "missing">(null);
  const [otOpen, setOtOpen] = useState(false);
  const [otAction, setOtAction] = useState<{ a: OvertimeAction; o: Overtime } | null>(null);

  if (data.loading) return <div className="page"><SkeletonRows rows={8} /></div>;
  if (data.error && !data.data)
    return <div className="page"><ErrorState title="Couldn't load this worker's attendance" body={errorMessage(data.error)} retryLabel="Try again" onRetry={() => void data.reload()} /></div>;
  const w = data.data!;
  const who = { id: w.workerId, workerCode: w.workerCode, workerName: w.workerName };
  const open = w.openSession;
  const reload = () => void data.reload();

  return (
    <div className="page" data-testid="worker-attendance-page">
      <PageHeader
        eyebrow={canSeeAll ? <Link href="/app/attendance/records" className="hover:underline">← Attendance records</Link> : undefined}
        title={<span><span className="font-mono">{w.workerCode}</span> · {w.workerName}</span>}
        description={`Times in your browser's time zone; work dates in the factory's (${w.timeZoneId}).`}
        actions={
          <div className="flex flex-wrap gap-2">
            {canRecord && w.workerStatus === "Active" && (open
              ? <>
                  <Button size="sm" variant="primary" iconLeft={<LogOut size={13} />} onClick={() => setPunch("check-out")} data-testid="worker-check-out">Check out</Button>
                  {open.onBreak
                    ? <Button size="sm" variant="secondary" onClick={() => setPunch("break-end")}>End break</Button>
                    : <Button size="sm" variant="secondary" iconLeft={<Coffee size={13} />} onClick={() => setPunch("break-start")}>Start break</Button>}
                </>
              : <Button size="sm" variant="primary" iconLeft={<LogIn size={13} />} onClick={() => setPunch("check-in")} data-testid="worker-check-in">Check in</Button>)}
            {canCorrect && <Button size="sm" variant="secondary" iconLeft={<FilePen size={13} />} onClick={() => setCorrecting("missing")}>Add missing day</Button>}
            {canOvertime && <Button size="sm" variant="secondary" iconLeft={<Hourglass size={13} />} onClick={() => setOtOpen(true)}>Request overtime</Button>}
            <Button size="sm" variant="secondary" aria-label="Refresh" onClick={reload}><RefreshCw size={13} className={cn(data.refreshing && "animate-spin")} /></Button>
          </div>
        }
      />
      {canSeeAll && <AttendanceTabs className="mt-4" />}

      <div className="mt-4 grid gap-4 lg:grid-cols-3">
        <Panel title="Current shift" icon={<CalendarClock size={14} />} testId="current-shift">
          {w.currentShift ? (
            <dl className="grid grid-cols-2 gap-3">
              <Fact label="Shift"><span className="font-mono">{w.currentShift.shift.code}</span> {w.currentShift.shift.name}</Fact>
              <Fact label="Hours">{shiftWindow(w.currentShift.shift)}</Fact>
              <Fact label="Since">{formatDate(w.currentShift.effectiveFrom)}</Fact>
              <Fact label="Until">{w.currentShift.effectiveTo ? formatDate(w.currentShift.effectiveTo) : "open-ended"}</Fact>
            </dl>
          ) : <p className="text-caption text-fg-tertiary">No shift assigned. Attendance is still recorded, without lateness or a schedule.</p>}
        </Panel>
        <Panel title="Right now" className="lg:col-span-2" testId="open-session">
          {open ? (
            <div className="space-y-2">
              <div className="flex flex-wrap items-center gap-2"><SessionStatusBadge status={open.status} /><SessionFlags s={open} /></div>
              <p className="text-body">Checked in at <strong>{clock(open.checkInAt)}</strong> ({formatDate(open.workDate)}){open.shift ? `, shift ${open.shift.code} ${shiftWindow(open.shift)}` : ", no shift scheduled"}.</p>
              <p className="text-caption text-fg-tertiary">Worked so far (net of breaks): {minutes(open.figures.netWorkingMinutes)} · breaks {minutes(open.figures.breakMinutes)}</p>
            </div>
          ) : <p className="text-caption text-fg-tertiary">Not checked in.</p>}
        </Panel>
      </div>

      <div className="mt-6 flex flex-wrap items-center gap-3">
        <DateFilter label="From" value={from} onChange={(v) => v && setFrom(v)} />
        <DateFilter label="To" value={to} onChange={(v) => v && setTo(v)} />
        <span className="text-caption text-fg-tertiary">Work dates</span>
      </div>

      <Panel className="mt-3" flush title="Sessions" subtitle={`${w.sessions.length} in the period`}>
        {w.sessions.length === 0 ? <EmptyState className="m-4" title="No sessions in this period" body="Change the dates, or record a check-in." />
          : <SessionTable rows={w.sessions} showWorker={false} onCorrect={canCorrect ? setCorrecting : undefined} testId="worker-sessions" />}
      </Panel>

      <div className="mt-6 grid gap-6 xl:grid-cols-2">
        <Panel flush title="Breaks" icon={<Coffee size={14} />}>
          {w.breaks.length === 0 ? <EmptyState className="m-4" title="No breaks in this period" body="Breaks are not counted as working time." />
            : <BreakTable rows={w.breaks} showWorker={false} testId="worker-breaks" />}
        </Panel>
        <Panel flush title="Corrections" icon={<FilePen size={14} />}>
          {w.corrections.length === 0 ? <EmptyState className="m-4" title="No corrections" body="Requested corrections and their decisions appear here." /> : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[560px]" data-testid="worker-corrections">
                <thead className="border-b border-border-subtle"><tr><th className={tableCls.th}>Day</th><th className={tableCls.th}>Proposed</th><th className={tableCls.th}>Original</th><th className={tableCls.th}>Status</th><th className={tableCls.th}>Reason</th></tr></thead>
                <tbody className="divide-y divide-border-subtle">
                  {w.corrections.map((c) => (
                    <tr key={c.id}>
                      <td className={tableCls.td}>{c.workDate ? formatDate(c.workDate) : "—"}</td>
                      <td className={tableCls.td}>{clock(c.proposedCheckIn)} – {clock(c.proposedCheckOut)}</td>
                      <td className={tableCls.td}>{clock(c.originalCheckIn)} – {clock(c.originalCheckOut)}</td>
                      <td className={tableCls.td}><Badge tone={c.status === "Approved" ? "success" : c.status === "Rejected" ? "danger" : "warning"}>{c.status}</Badge></td>
                      <td className={cn(tableCls.td, "max-w-[200px] truncate")} title={c.reason}>{c.reason}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Panel>
      </div>

      <Panel className="mt-6" flush title="Overtime" icon={<Hourglass size={14} />} subtitle="Requested, approved, actual and payable minutes are kept separately">
        <Callout className="m-4" tone="info">{OVERTIME_NOTE}</Callout>
        {w.overtime.length === 0 ? <EmptyState className="m-4" title="No overtime in this period" body="Overtime requests for this worker appear here." />
          : <OvertimeTable rows={w.overtime} onAction={(a, o) => setOtAction({ a, o })} testId="worker-overtime" />}
      </Panel>

      <Panel className="mt-6" flush title="Shift history" icon={<CalendarClock size={14} />} subtitle={`Last change ${w.shiftHistory[0] ? formatDateTime(w.shiftHistory[0].createdAt) : "—"}`}>
        {w.shiftHistory.length === 0 ? <EmptyState className="m-4" title="No shift assignments" body="Assign a shift from Shift assignments." />
          : <ShiftAssignmentTable rows={w.shiftHistory} showWorker={false} testId="worker-shift-history" />}
      </Panel>

      <PunchDialog open={punch !== null} action={punch ?? "check-in"} worker={who} onClose={() => setPunch(null)} onDone={() => { setPunch(null); reload(); }} />
      <CorrectionDialog open={correcting !== null} session={correcting === "missing" ? null : correcting} worker={who} onClose={() => setCorrecting(null)} onDone={() => { setCorrecting(null); reload(); }} />
      <OvertimeRequestDialog open={otOpen} worker={who} onClose={() => setOtOpen(false)} onDone={() => { setOtOpen(false); reload(); }} />
      <OvertimeActionDialog open={!!otAction} action={otAction?.a ?? "approve"} overtime={otAction?.o ?? null} onClose={() => setOtAction(null)} onDone={() => { setOtAction(null); reload(); }} />
    </div>
  );
}
