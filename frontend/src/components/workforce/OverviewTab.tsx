"use client";

import { useState } from "react";
import Link from "next/link";
import {
  AlertTriangle,
  ArrowRight,
  CalendarClock,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  Clock,
  FileWarning,
  ShieldAlert,
  Sparkles,
  UserCheck,
  Users
} from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { StatCard } from "@/components/ui/StatCard";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { EmptyState } from "@/components/ui/EmptyState";
import { Sparkline } from "@/components/ui/Sparkline";
import { useToast } from "@/components/ui/Toast";
import { useT } from "@/lib/useT";
import { formatBDT, formatDateTime, formatNumber, bn } from "@/lib/format";
import { WORKFORCE_LINES, type WorkforceLineId } from "@/data/workforce/lines";
import { WORKFORCE_POLICY } from "@/data/workforce/policy";
import { useWorkforceStore } from "@/store/workforce.store";
import {
  compute14DayAttendanceTrend,
  computeDayAttendanceSummary,
  computeLineAbsenteeism,
  computeRuleBasedSuggestions,
  getWorkerCurrentOvertimeHours,
  getWorkerCurrentWeeklyHours,
  hasConsecutiveWorkloadAlert,
  isOverWeeklyCap
} from "@/lib/workforce";
import {
  IllustrativePolicyNote,
  LocalEditChip,
  RuleBasedChip,
  WorkloadAlertChip,
  grievanceCategoryLabel,
  leaveTypeLabel,
  lineLabel,
  type WorkforceTab
} from "./shared";

type Props = {
  operator: boolean;
  admin: boolean;
  actorEmail: string;
  onNavigate: (tab: WorkforceTab, extraParams?: Record<string, string>) => void;
};

export function OverviewTab({ operator, admin, actorEmail, onNavigate }: Props) {
  const { t, locale } = useT();
  const toast = useToast();
  const state = useWorkforceStore();
  const [auditOpen, setAuditOpen] = useState(true);

  const today = state.anchorDate;
  const todayMap = state.attendanceByDate[today];
  const daySummary = computeDayAttendanceSummary(state.workers, todayMap);
  const lineAbsenteeism = computeLineAbsenteeism(state.workers, todayMap);
  const trend14d = compute14DayAttendanceTrend(state.workers, state.attendanceByDate, today);

  const activeWorkers = state.workers.filter((w) => w.status !== "resigned");
  const totalOtHoursThisWeek = activeWorkers.reduce((acc, w) => acc + getWorkerCurrentOvertimeHours(w), 0);
  const overCapCount = activeWorkers.filter((w) => isOverWeeklyCap(getWorkerCurrentWeeklyHours(w))).length;

  const openGrievances = state.grievances.filter((g) => g.status !== "resolved");
  const pendingLeaves = state.leaveRequests.filter((r) => r.status === "pending");
  const suggestions = computeRuleBasedSuggestions(state);

  // Build ranked "Needs your attention" items
  const uncoveredLinesWithAbsent = lineAbsenteeism.filter(
    (l) => l.absent >= 2 && !state.coveredLineDates[`${today}::${l.lineId}`]
  );
  const newGrievances = state.grievances.filter((g) => g.status === "new");
  const workloadAlertWorkers = activeWorkers.filter(
    (w) =>
      hasConsecutiveWorkloadAlert(w.weeklyHoursHistory) &&
      !state.acknowledgedWorkloadWorkerIds.includes(w.id)
  );

  const fmtNum = (n: number) => formatNumber(n, locale);
  const fmtPctVal = (n: number) => (locale === "bn" ? `${bn(n.toFixed(1))}%` : `${n.toFixed(1)}%`);

  return (
    <div className="space-y-4" data-testid="workforce-overview">
      {/* 8 Clickable StatCards */}
      <div className="grid grid-cols-2 md:grid-cols-4 xl:grid-cols-8 gap-3">
        <StatCard
          label={t("workforce.overview.statHeadcount")}
          value={fmtNum(daySummary.headcount)}
          hint={t("workforce.overview.statHeadcountHint")}
          href="/app/workforce?tab=workers&status=active"
          testId="wf-stat-headcount"
        />
        <StatCard
          label={t("workforce.overview.statPresentPct")}
          value={fmtPctVal(daySummary.presentPct)}
          tone={daySummary.presentPct >= 90 ? "good" : daySummary.presentPct >= 82 ? "warn" : "bad"}
          hint={t("workforce.overview.statPresentHint", {
            n: fmtNum(daySummary.present + daySummary.late)
          })}
          href={`/app/workforce?tab=attendance&date=${today}&attStatus=present`}
          testId="wf-stat-present"
        />
        <StatCard
          label={t("workforce.overview.statAbsent")}
          value={fmtNum(daySummary.absent)}
          tone={daySummary.absent > 6 ? "bad" : daySummary.absent > 0 ? "warn" : "good"}
          hint={t("workforce.overview.statAbsentHint")}
          href={`/app/workforce?tab=attendance&date=${today}&attStatus=absent`}
          testId="wf-stat-absent"
        />
        <StatCard
          label={t("workforce.overview.statLate")}
          value={fmtNum(daySummary.late)}
          tone={daySummary.late > 4 ? "warn" : "neutral"}
          hint={t("workforce.overview.statLateHint")}
          href={`/app/workforce?tab=attendance&date=${today}&attStatus=late`}
          testId="wf-stat-late"
        />
        <StatCard
          label={t("workforce.overview.statOnLeave")}
          value={fmtNum(daySummary.leave)}
          hint={t("workforce.overview.statOnLeaveHint")}
          href={`/app/workforce?tab=attendance&date=${today}&attStatus=leave`}
          testId="wf-stat-on-leave"
        />
        <StatCard
          label={t("workforce.overview.statOvertimeWeek")}
          value={`${fmtNum(totalOtHoursThisWeek)} h`}
          tone={overCapCount > 0 ? "warn" : "neutral"}
          hint={t("workforce.overview.statOvertimeHint", { flagged: fmtNum(overCapCount) })}
          href="/app/workforce?tab=leave&section=overtime"
          testId="wf-stat-overtime"
        />
        <StatCard
          label={t("workforce.overview.statOpenGrievances")}
          value={fmtNum(openGrievances.length)}
          tone={openGrievances.length > 0 ? "warn" : "good"}
          hint={t("workforce.overview.statOpenGrievancesHint")}
          href="/app/workforce?tab=grievances&gStatus=open"
          testId="wf-stat-grievances"
        />
        <StatCard
          label={t("workforce.overview.statPendingLeave")}
          value={fmtNum(pendingLeaves.length)}
          tone={pendingLeaves.length > 0 ? "warn" : "good"}
          hint={t("workforce.overview.statPendingLeaveHint")}
          href="/app/workforce?tab=leave&lStatus=pending"
          testId="wf-stat-pending-leave"
        />
      </div>

      {/* Needs your attention + Rule-based suggestions */}
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        <Panel
          title={t("workforce.overview.needsAttentionTitle")}
          subtitle={t("workforce.overview.needsAttentionSubtitle")}
          icon={<AlertTriangle size={15} />}
          right={<RuleBasedChip label={t("workforce.ruleBasedBadge") as string} />}
          testId="wf-needs-attention-panel"
        >
          {uncoveredLinesWithAbsent.length === 0 &&
          pendingLeaves.length === 0 &&
          newGrievances.length === 0 &&
          workloadAlertWorkers.length === 0 ? (
            <EmptyState
              icon={<CheckCircle2 size={20} />}
              title={t("workforce.overview.needsAttentionEmpty") as string}
            />
          ) : (
            <ul className="space-y-2.5" data-testid="wf-attention-list">
              {/* 1. Lines with >= 2 absent workers needing cover */}
              {uncoveredLinesWithAbsent.map((line) => (
                <li
                  key={`cov-${line.lineId}`}
                  className="surface-2 p-3 flex flex-wrap items-center justify-between gap-3"
                  data-testid={`wf-attention-cover-${line.lineId}`}
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <Badge tone="danger">{line.code}</Badge>
                      <button
                        type="button"
                        onClick={() =>
                          onNavigate("attendance", {
                            date: today,
                            line: line.lineId,
                            attStatus: "absent"
                          })
                        }
                        className="text-body font-medium text-fg-primary hover:underline text-left"
                        data-testid={`wf-attention-cover-link-${line.lineId}`}
                      >
                        {locale === "bn"
                          ? `${line.nameBn}: আজ ${bn(line.absent)} জন অনুপস্থিত (${fmtPctVal(line.absentPct)})`
                          : `${line.nameEn}: ${line.absent} absent today (${line.absentPct}%)`}
                      </button>
                    </div>
                    <p className="mt-0.5 text-caption text-fg-tertiary">
                      {locale === "bn"
                        ? "ফ্লোট অপারেটর বা হেল্পার দিয়ে খালি মেশিন কভার করুন।"
                        : "Assign float operator / helper cover so bundle flow does not stall."}
                    </p>
                  </div>
                  {operator ? (
                    <Button
                      size="sm"
                      variant="primary"
                      data-testid={`wf-action-cover-${line.lineId}`}
                      onClick={() => {
                        const res = state.markLineCoverForAbsent(today, line.lineId, actorEmail);
                        toast.push({
                          title:
                            locale === "bn"
                              ? `${line.nameBn}-এ ফ্লোট কভার নির্ধারণ করা হয়েছে`
                              : `Assigned float cover on ${line.nameEn}`,
                          tone: "success",
                          onUndo: res.undo
                        });
                      }}
                    >
                      {t("workforce.overview.actionCoverLine")}
                    </Button>
                  ) : (
                    <Button
                      size="sm"
                      variant="secondary"
                      onClick={() => onNavigate("attendance", { date: today, line: line.lineId })}
                      data-testid={`wf-view-cover-${line.lineId}`}
                    >
                      {t("workforce.tabs.attendance")}
                    </Button>
                  )}
                </li>
              ))}

              {/* 2. Pending leave requests */}
              {pendingLeaves.slice(0, 3).map((req) => {
                const worker = state.workers.find((w) => w.id === req.workerId);
                return (
                  <li
                    key={req.id}
                    className="surface-2 p-3 flex flex-wrap items-center justify-between gap-3"
                    data-testid={`wf-attention-leave-${req.id}`}
                  >
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <Badge tone="warning">{req.id}</Badge>
                        <button
                          type="button"
                          onClick={() => onNavigate("leave", { lStatus: "pending" })}
                          className="text-body font-medium text-fg-primary hover:underline text-left"
                          data-testid={`wf-attention-leave-link-${req.id}`}
                        >
                          {worker
                            ? `${locale === "bn" ? worker.nameBn : worker.nameEn} (${worker.id})`
                            : req.workerId}{" "}
                          · {leaveTypeLabel(t as (k: string) => string, req.type)} ({fmtNum(req.days)}d)
                        </button>
                      </div>
                      <p className="mt-0.5 text-caption text-fg-tertiary">
                        {req.startDate} → {req.endDate}
                        {operator && ` · ${locale === "bn" ? req.reasonBn : req.reasonEn}`}
                      </p>
                    </div>
                    {operator ? (
                      <Button
                        size="sm"
                        variant="success"
                        data-testid={`wf-action-approve-leave-${req.id}`}
                        onClick={() => {
                          const res = state.decideLeaveRequest(
                            req.id,
                            "approved",
                            "Approved from Today board",
                            actorEmail
                          );
                          toast.push({
                            title: t("workforce.leave.approvedToast", { id: req.id }) as string,
                            tone: "success",
                            onUndo: res.undo
                          });
                        }}
                      >
                        {t("workforce.overview.actionApproveLeave")}
                      </Button>
                    ) : (
                      <Button
                        size="sm"
                        variant="secondary"
                        onClick={() => onNavigate("leave", { lStatus: "pending" })}
                      >
                        {t("workforce.tabs.leave")}
                      </Button>
                    )}
                  </li>
                );
              })}

              {/* 3. Unacknowledged new grievances */}
              {newGrievances.slice(0, 2).map((grv) => (
                <li
                  key={grv.id}
                  className="surface-2 p-3 flex flex-wrap items-center justify-between gap-3"
                  data-testid={`wf-attention-grievance-${grv.id}`}
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <Badge tone="danger">{grv.ticketNo}</Badge>
                      <Badge tone="neutral">
                        {grievanceCategoryLabel(t as (k: string) => string, grv.category)}
                      </Badge>
                      <button
                        type="button"
                        onClick={() => onNavigate("grievances", { focus: grv.id })}
                        className="text-body font-medium text-fg-primary hover:underline text-left"
                        data-testid={`wf-attention-grievance-link-${grv.id}`}
                      >
                        {operator
                          ? locale === "bn"
                            ? grv.subjectBn
                            : grv.subjectEn
                          : `${lineLabel(grv.lineId, locale)} · ${grievanceCategoryLabel(t as (k: string) => string, grv.category)}`}
                      </button>
                    </div>
                    <p className="mt-0.5 text-caption text-fg-tertiary">
                      {lineLabel(grv.lineId, locale)} · {grv.createdAt}
                    </p>
                  </div>
                  {operator ? (
                    <Button
                      size="sm"
                      variant="secondary"
                      data-testid={`wf-action-investigate-${grv.id}`}
                      onClick={() => {
                        const res = state.updateGrievanceStatus(
                          grv.id,
                          "investigating",
                          "Assigned to welfare officer for floor check",
                          actorEmail
                        );
                        toast.push({
                          title: t("workforce.grievances.grievanceUpdatedToast", {
                            ticket: grv.ticketNo,
                            status: "investigating"
                          }) as string,
                          tone: "info",
                          onUndo: res.undo
                        });
                      }}
                    >
                      {t("workforce.overview.actionOpenGrievance")}
                    </Button>
                  ) : (
                    <Button
                      size="sm"
                      variant="secondary"
                      onClick={() => onNavigate("grievances")}
                    >
                      {t("workforce.tabs.grievances")}
                    </Button>
                  )}
                </li>
              ))}

              {/* 4. Hours-only Workload alerts (>60h/wk for 3 consecutive weeks) */}
              {workloadAlertWorkers.slice(0, 2).map((w) => (
                <li
                  key={`wl-${w.id}`}
                  className="surface-2 p-3 flex flex-wrap items-center justify-between gap-3"
                  data-testid={`wf-attention-workload-${w.id}`}
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <WorkloadAlertChip
                        workerId={w.id}
                        label={t("workforce.workloadAlertBadge") as string}
                      />
                      <Link
                        href={`/app/workforce/workers/${encodeURIComponent(w.id)}`}
                        className="text-body font-medium text-fg-primary hover:underline"
                      >
                        {locale === "bn" ? w.nameBn : w.nameEn} ({w.id})
                      </Link>
                    </div>
                    <p className="mt-0.5 text-caption text-fg-tertiary">
                      {lineLabel(w.lineId, locale)} ·{" "}
                      {locale === "bn"
                        ? `গত ৩ সপ্তাহের কর্মঘণ্টা: ${w.weeklyHoursHistory.slice(-3).map(bn).join("ঘ, ")}ঘ (>৬০ঘ সীমা)`
                        : `Last 3 weeks: ${w.weeklyHoursHistory.slice(-3).join("h, ")}h (>60 h/wk threshold)`}
                    </p>
                  </div>
                  {operator ? (
                    <Button
                      size="sm"
                      variant="secondary"
                      data-testid={`wf-action-cap-workload-${w.id}`}
                      onClick={() => {
                        const res = state.acknowledgeWorkloadAlert(w.id, actorEmail);
                        toast.push({
                          title:
                            locale === "bn"
                              ? `${w.id}-এর সাপ্তাহিক ঘণ্টা ৫৪ ঘণ্টায় সীমিত করা হয়েছে`
                              : `Capped ${w.id} weekly schedule at 54 h`,
                          tone: "success",
                          onUndo: res.undo
                        });
                      }}
                    >
                      {t("workforce.overview.actionCapWorkload")}
                    </Button>
                  ) : (
                    <Link
                      href={`/app/workforce/workers/${encodeURIComponent(w.id)}`}
                      className="text-caption text-accent hover:underline"
                    >
                      {t("workforce.overview.actionViewProfile")}
                    </Link>
                  )}
                </li>
              ))}
            </ul>
          )}
        </Panel>

        {/* Rule-based suggestions panel */}
        <Panel
          title={t("workforce.overview.suggestionsTitle")}
          subtitle={t("workforce.overview.suggestionsSubtitle")}
          icon={<Sparkles size={15} />}
          right={<RuleBasedChip label={t("workforce.ruleBasedBadge") as string} />}
          testId="wf-suggestions-panel"
        >
          {suggestions.length === 0 ? (
            <EmptyState
              icon={<CheckCircle2 size={20} />}
              title={t("workforce.overview.suggestionsEmpty") as string}
            />
          ) : (
            <ul className="space-y-3" data-testid="wf-suggestions-list">
              {suggestions.map((sug) => (
                <li
                  key={sug.id}
                  className="surface-2 p-3.5 space-y-2"
                  data-testid={`wf-suggestion-${sug.id}`}
                >
                  <div className="flex flex-wrap items-start justify-between gap-2">
                    <div className="min-w-0 flex-1">
                      <div className="flex flex-wrap items-center gap-2">
                        <RuleBasedChip label={t("workforce.ruleBasedBadge") as string} />
                        <p className="text-body font-semibold text-fg-primary">
                          {locale === "bn" ? sug.titleBn : sug.titleEn}
                        </p>
                      </div>
                      <p className="mt-1 text-caption text-fg-secondary">
                        {locale === "bn" ? sug.rationaleBn : sug.rationaleEn}
                      </p>
                      <p className="mt-1 text-caption text-accent font-medium">
                        {locale === "bn" ? sug.impactBn : sug.impactEn}
                      </p>
                      <div className="mt-2 flex flex-wrap items-center gap-1.5">
                        {sug.workerIds.map((wid) => (
                          <Link
                            key={wid}
                            href={`/app/workforce/workers/${encodeURIComponent(wid)}`}
                            data-testid={`wf-suggestion-worker-${wid}`}
                            className="inline-flex items-center rounded border border-border-subtle bg-surface-1 px-2 py-0.5 text-[11px] font-mono text-fg-secondary hover:text-accent hover:border-[var(--accent-border)]"
                          >
                            {wid}
                          </Link>
                        ))}
                      </div>
                    </div>
                  </div>
                  {operator && (
                    <div className="flex items-center justify-end gap-2 pt-1">
                      <Button
                        size="sm"
                        variant="ghost"
                        data-testid={`wf-suggestion-dismiss-${sug.id}`}
                        onClick={() => {
                          const res = state.dismissSuggestion(sug.id, actorEmail);
                          toast.push({
                            title: t("workforce.overview.suggestionDismissedToast") as string,
                            tone: "info",
                            onUndo: res.undo
                          });
                        }}
                      >
                        {t("workforce.overview.dismissSuggestion")}
                      </Button>
                      <Button
                        size="sm"
                        variant="primary"
                        data-testid={`wf-suggestion-apply-${sug.id}`}
                        onClick={() => {
                          const res = state.applySuggestion(sug, actorEmail);
                          toast.push({
                            title: t("workforce.overview.suggestionAppliedToast") as string,
                            description: locale === "bn" ? sug.titleBn : sug.titleEn,
                            tone: "success",
                            onUndo: res.undo
                          });
                        }}
                      >
                        {t("workforce.overview.applySuggestion")}
                      </Button>
                    </div>
                  )}
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>

      {/* Absenteeism by line (clickable bars) + 14-day attendance trend */}
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-4">
        <Panel
          title={t("workforce.overview.absentByLineTitle")}
          subtitle={t("workforce.overview.absentByLineSubtitle")}
          icon={<Users size={15} />}
          testId="wf-absent-by-line-panel"
        >
          <div className="space-y-2.5">
            {lineAbsenteeism.map((row) => {
              const covered = Boolean(state.coveredLineDates[`${today}::${row.lineId}`]);
              const isHigh = row.absentPct >= 12;
              return (
                <button
                  key={row.lineId}
                  type="button"
                  onClick={() => onNavigate("workers", { line: row.lineId })}
                  data-testid={`wf-absent-bar-${row.lineId}`}
                  className="w-full text-left surface-2 p-3 rounded-md hover:border-[var(--border-strong)] transition-colors group"
                >
                  <div className="flex items-center justify-between gap-2 text-body">
                    <span className="font-medium text-fg-primary group-hover:text-accent inline-flex items-center gap-2">
                      <span>{locale === "bn" ? row.nameBn : row.nameEn}</span>
                      {covered && (
                        <Badge tone="info" testId={`wf-line-covered-${row.lineId}`}>
                          {t("workforce.overview.lineCoveredBadge")}
                        </Badge>
                      )}
                    </span>
                    <span className="tabular-nums text-caption text-fg-secondary">
                      {locale === "bn"
                        ? `অনুপস্থিত ${bn(row.absent)}/${bn(row.headcount)} (${fmtPctVal(row.absentPct)}) · উপস্থিত ${fmtPctVal(row.presentPct)}`
                        : `Absent ${row.absent}/${row.headcount} (${row.absentPct}%) · Present ${row.presentPct}%`}
                    </span>
                  </div>
                  <div className="mt-2 h-2.5 w-full rounded-full bg-surface-1 overflow-hidden flex">
                    <div
                      className={
                        isHigh
                          ? "bg-[var(--risk-high)] transition-all"
                          : row.absentPct > 5
                          ? "bg-[var(--risk-medium)] transition-all"
                          : "bg-[var(--risk-low)] transition-all"
                      }
                      style={{ width: `${Math.min(100, Math.max(4, row.absentPct * 3))}%` }}
                    />
                  </div>
                </button>
              );
            })}
          </div>
        </Panel>

        <Panel
          title={t("workforce.overview.trend14dTitle")}
          subtitle={t("workforce.overview.trend14dSubtitle")}
          icon={<CalendarClock size={15} />}
          right={
            <Sparkline
              values={trend14d.filter((p) => !p.isHoliday).map((p) => p.presentPct)}
              width={140}
              height={32}
            />
          }
          testId="wf-trend-14d-panel"
        >
          <div className="grid grid-cols-7 gap-2" data-testid="wf-trend-14d-grid">
            {trend14d.map((pt) => (
              <button
                key={pt.date}
                type="button"
                onClick={() => onNavigate("attendance", { date: pt.date })}
                data-testid={`wf-trend-day-${pt.date}`}
                className="surface-2 p-2 rounded-md text-center hover:border-[var(--accent-border)] transition-colors"
                title={`${pt.date}: ${pt.isHoliday ? "Weekly holiday (Friday)" : `${pt.presentPct}% present`}`}
              >
                <span className="block text-[11px] font-mono text-fg-tertiary">
                  {pt.date.slice(5)}
                </span>
                <span
                  className={
                    pt.isHoliday
                      ? "mt-1 block text-caption text-fg-tertiary"
                      : pt.presentPct >= 90
                      ? "mt-1 block text-body font-semibold text-[var(--risk-low)] tabular-nums"
                      : pt.presentPct >= 82
                      ? "mt-1 block text-body font-semibold text-[var(--risk-medium)] tabular-nums"
                      : "mt-1 block text-body font-semibold text-[var(--risk-high)] tabular-nums"
                  }
                >
                  {pt.isHoliday ? "Fri" : fmtPctVal(pt.presentPct)}
                </span>
                <span className="block text-[10px] text-fg-tertiary">
                  {pt.isHoliday
                    ? locale === "bn"
                      ? "ছুটি"
                      : "Off"
                    : locale === "bn"
                    ? `${bn(pt.absentCount)} অনু.`
                    : `${pt.absentCount} abs`}
                </span>
              </button>
            ))}
          </div>
          <IllustrativePolicyNote
            text={t("workforce.illustrativeNote") as string}
            className="mt-3 text-caption text-fg-tertiary flex items-center gap-1.5"
          />
        </Panel>
      </div>

      {/* Admin-only policy constants view */}
      {admin && (
        <Panel
          title={t("workforce.overview.policyCardTitle")}
          subtitle={t("workforce.overview.policyCardSubtitle")}
          right={<Badge tone="accent">Admin</Badge>}
          testId="wf-admin-policy-panel"
        >
          <p className="text-caption text-fg-secondary mb-3">
            {locale === "bn" ? WORKFORCE_POLICY.disclaimerBn : WORKFORCE_POLICY.disclaimerEn}
          </p>
          <div className="grid grid-cols-1 md:grid-cols-3 lg:grid-cols-7 gap-2.5">
            {([7, 6, 5, 4, 3, 2, 1] as const).map((g) => {
              const gw = WORKFORCE_POLICY.grades[g];
              return (
                <div key={g} className="surface-2 p-2.5 rounded-md" data-testid={`wf-policy-grade-${g}`}>
                  <p className="text-caption font-semibold text-fg-primary">
                    {locale === "bn" ? gw.labelBn : gw.labelEn}
                  </p>
                  <p className="mt-1 text-body font-semibold text-accent tabular-nums">
                    {formatBDT(gw.grossMonthlyBdt, locale)}
                  </p>
                  <p className="text-[11px] text-fg-tertiary tabular-nums">
                    Basic: {formatBDT(gw.basicBdt, locale)}
                  </p>
                </div>
              );
            })}
          </div>
        </Panel>
      )}

      {/* Collapsible local Workforce audit trail */}
      <Panel
        title={t("workforce.overview.auditTitle")}
        subtitle={t("workforce.overview.auditSubtitle")}
        right={
          <Button
            size="sm"
            variant="ghost"
            onClick={() => setAuditOpen((v) => !v)}
            data-testid="wf-audit-toggle"
            iconRight={auditOpen ? <ChevronUp size={14} /> : <ChevronDown size={14} />}
          >
            {auditOpen
              ? t("workforce.overview.auditHide")
              : t("workforce.overview.auditShow", { n: fmtNum(state.auditTrail.length) })}
          </Button>
        }
        testId="wf-audit-panel"
      >
        {auditOpen && (
          <ul className="divide-y divide-border-subtle" data-testid="wf-audit-list">
            {state.auditTrail.slice(0, 12).map((entry) => (
              <li
                key={entry.id}
                className="py-2 flex flex-wrap items-center justify-between gap-2 text-caption"
                data-testid="wf-audit-row"
              >
                <div className="min-w-0 flex items-center gap-2">
                  <Badge tone="neutral">{entry.actorEmail}</Badge>
                  <span className="text-fg-primary">
                    {locale === "bn" ? entry.actionBn : entry.actionEn}
                  </span>
                </div>
                <span className="text-fg-tertiary tabular-nums">
                  {formatDateTime(entry.isoDate, locale)}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </div>
  );
}
