"use client";

// /app/workforce/workers/[id] — Individual worker profile page.
// Sub-tabs: Overview, Skills, Attendance (30d), Leave, Performance (8w),
// Training, and Notes (Operator-only).
// NON-NEGOTIABLE PRIVACY RULE: Wellbeing data never appears on this profile.
// Role checks in the UI are convenience only for this browser-only demo.

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useParams, useSearchParams } from "next/navigation";
import {
  ArrowLeft,
  Award,
  Calendar,
  CalendarPlus,
  CheckCircle2,
  FileText,
  GraduationCap,
  HeartPulse,
  ShieldCheck,
  UserX
} from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout, DemoDataBanner } from "@/components/ui/Callout";
import { Tabs } from "@/components/ui/Tabs";
import { Select, TextArea } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { Sparkline } from "@/components/ui/Sparkline";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useAuthStore, can } from "@/lib/auth";
import { P } from "@/lib/access";
import { useMounted } from "@/lib/persist";
import { useT } from "@/lib/useT";
import { formatBDT, formatDate, formatNumber, bn } from "@/lib/format";
import { addIsoDays, isFridayHoliday } from "@/data/workforce/seed";
import {
  ATTENDANCE_STATUSES,
  LEAVE_TYPES,
  MACHINE_SKILLS,
  type AttendanceStatus,
  type SkillProficiency
} from "@/data/workforce/types";
import { useWorkforceStore } from "@/store/workforce.store";
import {
  calcWorkerAvgDhuPct,
  calcWorkerAvgEfficiencyPct,
  calcWorkerEfficiencyPct,
  calcWorkerLatestEfficiencyPct,
  computeLeaveBalances,
  computeWorkerAttendanceRatePct,
  computeWorkerBonusPreview,
  hasConsecutiveWorkloadAlert
} from "@/lib/workforce";
import {
  IllustrativePolicyNote,
  LocalEditChip,
  WorkloadAlertChip,
  attendanceStatusLabel,
  attendanceStatusTone,
  leaveStatusLabel,
  leaveStatusTone,
  leaveTypeLabel,
  lineLabel,
  proficiencyLabel,
  roleLabel,
  skillLabel,
  workerStatusLabel,
  workerStatusTone
} from "@/components/workforce/shared";
import { CreateLeaveModal } from "@/components/workforce/LeaveOvertimeTab";

type ProfileSubTab =
  | "overview"
  | "skills"
  | "attendance"
  | "leave"
  | "performance"
  | "training"
  | "notes";

const SUB_TABS: readonly ProfileSubTab[] = [
  "overview",
  "skills",
  "attendance",
  "leave",
  "performance",
  "training",
  "notes"
];

export default function WorkerProfilePage() {
  const { t, locale } = useT();
  const params = useParams<{ id: string }>();
  const searchParams = useSearchParams();
  const mounted = useMounted();
  const toast = useToast();

  const user = useAuthStore((s) => s.user);
  const operator = can(user, P.WorkersAssign, P.WorkersManage);
  const admin = can(user, P.WorkersManage);
  const actorEmail = user?.email ?? "operator@bunonbrain.demo";

  const state = useWorkforceStore();
  const workerId = decodeURIComponent(params?.id ?? "");

  const [subTab, setSubTab] = useState<ProfileSubTab>("overview");
  const [selectedDay, setSelectedDay] = useState<string>("");
  const [leaveModalOpen, setLeaveModalOpen] = useState(false);
  const [batchToEnrol, setBatchToEnrol] = useState("");
  const [noteText, setNoteText] = useState("");

  useEffect(() => {
    if (mounted) {
      state.rehydrate();
    }
  }, [mounted, state]);

  const urlSub = searchParams?.get("sub") as ProfileSubTab | null;
  useEffect(() => {
    if (urlSub && SUB_TABS.includes(urlSub)) {
      if (urlSub === "notes" && !operator) {
        setSubTab("overview");
      } else {
        setSubTab(urlSub);
      }
    }
  }, [urlSub, operator]);

  useEffect(() => {
    if (!selectedDay && state.anchorDate) {
      setSelectedDay(state.anchorDate);
    }
  }, [selectedDay, state.anchorDate]);

  // Preserve previous directory filters on the back link
  const backHref = useMemo(() => {
    const sp = new URLSearchParams();
    sp.set("tab", "workers");
    const line = searchParams?.get("line");
    const skill = searchParams?.get("skill");
    const status = searchParams?.get("status");
    if (line) sp.set("line", line);
    if (skill) sp.set("skill", skill);
    if (status) sp.set("status", status);
    return `/app/workforce?${sp.toString()}`;
  }, [searchParams]);

  function changeSubTab(next: ProfileSubTab) {
    setSubTab(next);
    const url = new URL(window.location.href);
    if (next === "overview") url.searchParams.delete("sub");
    else url.searchParams.set("sub", next);
    window.history.replaceState(null, "", url);
  }

  if (!mounted) {
    return (
      <div className="page" data-testid="worker-profile-loading">
        <SkeletonRows rows={4} rowHeight="h-20" />
      </div>
    );
  }

  const worker = state.workers.find((w) => w.id.toLowerCase() === workerId.toLowerCase());

  if (!worker) {
    return (
      <div className="page" data-testid="worker-profile-not-found">
        <DemoDataBanner className="mb-4" what={t("workforce.demoBanner")} />
        <Link
          href={backHref}
          data-testid="worker-profile-back-link"
          className="inline-flex items-center gap-1.5 text-caption text-accent hover:underline mb-4"
        >
          <ArrowLeft size={14} />
          <span>{t("workforce.profile.backToWorkers")}</span>
        </Link>
        <div className="surface">
          <EmptyState
            icon={<UserX size={22} />}
            title={t("workforce.profile.notFoundTitle") as string}
            body={t("workforce.profile.notFoundBody", { id: workerId }) as string}
          />
        </div>
      </div>
    );
  }

  const initials = worker.nameEn
    .split(" ")
    .map((p) => p[0])
    .join("")
    .slice(0, 2)
    .toUpperCase();

  const att30d = computeWorkerAttendanceRatePct(
    worker.id,
    state.attendanceByDate,
    state.anchorDate
  );
  const avgEff = calcWorkerAvgEfficiencyPct(worker);
  const latestEff = calcWorkerLatestEfficiencyPct(worker);
  const avgDhu = calcWorkerAvgDhuPct(worker);
  const balances = computeLeaveBalances(worker);
  const bonus = computeWorkerBonusPreview(worker, att30d);

  const last30Dates: string[] = [];
  for (let d = 29; d >= 0; d--) {
    last30Dates.push(addIsoDays(state.anchorDate, -d));
  }
  const activeDay = selectedDay || state.anchorDate;
  const activeDayStatus: AttendanceStatus =
    state.attendanceByDate[activeDay]?.[worker.id] ??
    (worker.status === "on_leave" ? "leave" : "present");

  const workerLeaveRequests = state.leaveRequests.filter((r) => r.workerId === worker.id);
  const workerBatches = state.trainingBatches.filter((b) =>
    b.enrolledWorkerIds.includes(worker.id)
  );
  const availableBatches = state.trainingBatches.filter(
    (b) => !b.enrolledWorkerIds.includes(worker.id)
  );
  const notes = state.workerNotes.filter((n) => n.workerId === worker.id);

  const visibleSubTabs = operator
    ? SUB_TABS
    : SUB_TABS.filter((st) => st !== "notes");

  const fmtNum = (n: number) => formatNumber(n, locale);

  return (
    <div className="page" data-testid="worker-profile-page" data-worker-id={worker.id}>
      <DemoDataBanner className="mb-4" what={t("workforce.demoBanner")} />

      <div className="mb-3">
        <Link
          href={backHref}
          data-testid="worker-profile-back-link"
          className="inline-flex items-center gap-1.5 text-caption text-accent hover:underline"
        >
          <ArrowLeft size={14} />
          <span>{t("workforce.profile.backToWorkers")}</span>
        </Link>
      </div>

      {/* Worker Profile Header */}
      <div className="surface p-5 flex flex-wrap items-start justify-between gap-4">
        <div className="flex items-start gap-4">
          <div
            className="size-14 rounded-full bg-[var(--accent-soft)] border border-[var(--accent-border)] text-accent flex items-center justify-center text-[18px] font-bold shrink-0"
            aria-hidden
          >
            {initials}
          </div>
          <div>
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-mono text-caption text-accent">{worker.id}</span>
              <h1 className="text-[22px] leading-[28px] font-semibold text-fg-primary">
                {worker.nameEn} · <span className="text-fg-secondary">{worker.nameBn}</span>
              </h1>
              <Badge tone={workerStatusTone(worker.status)}>
                {workerStatusLabel(t as (k: string) => string, worker.status)}
              </Badge>
              {worker.updatedByLocal && (
                <LocalEditChip label={t("workforce.localEditBadge") as string} />
              )}
              {hasConsecutiveWorkloadAlert(worker.weeklyHoursHistory) && (
                <WorkloadAlertChip
                  workerId={worker.id}
                  label={t("workforce.workloadAlertBadge") as string}
                />
              )}
            </div>
            <p className="mt-1 text-body text-fg-secondary">
              {roleLabel(t as (k: string) => string, worker.role)} ·{" "}
              {lineLabel(worker.lineId, locale)} · Shift {worker.shift} · Grade {worker.grade}
            </p>
            <div className="mt-2 flex flex-wrap items-center gap-2 text-caption text-fg-tertiary">
              <span>
                {t("workforce.profile.tenureLabel", {
                  date: formatDate(worker.joinDate, locale)
                })}
              </span>
              {worker.firstAidTrained && (
                <Badge tone="info">
                  <HeartPulse size={11} className="mr-1 inline" />
                  {t("workforce.profile.firstAidBadge")}
                </Badge>
              )}
              {worker.dayCareEligible && (
                <Badge tone="accent">{t("workforce.profile.dayCareBadge")}</Badge>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* Sub-tabs */}
      <Tabs<ProfileSubTab>
        className="mt-5"
        label="Worker profile sections"
        value={subTab}
        onChange={changeSubTab}
        items={visibleSubTabs.map((st) => ({
          id: st,
          label: t(`workforce.profile.subTabs.${st}`),
          testId: `worker-subtab-${st}`
        }))}
      />

      <div className="mt-4" role="tabpanel">
        {/* 1. OVERVIEW SUB-TAB */}
        {subTab === "overview" && (
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-4" data-testid="worker-subpanel-overview">
            <Panel title={t("workforce.profile.subTabs.overview")}>
              <dl className="grid grid-cols-2 gap-3 text-body">
                <div>
                  <dt className="text-caption text-fg-tertiary">
                    {t("workforce.profile.homeDistrictLabel")}
                  </dt>
                  <dd className="font-medium text-fg-primary">
                    {locale === "bn" ? worker.homeDistrictBn : worker.homeDistrictEn}
                  </dd>
                </div>
                <div>
                  <dt className="text-caption text-fg-tertiary">
                    {t("workforce.profile.residenceLabel")}
                  </dt>
                  <dd className="font-medium text-fg-primary">
                    {locale === "bn" ? worker.residenceAreaBn : worker.residenceAreaEn}
                  </dd>
                </div>
                <div>
                  <dt className="text-caption text-fg-tertiary">NID</dt>
                  <dd className="text-caption text-fg-primary">
                    {worker.nidOnFile
                      ? t("workforce.profile.nidOnFileYes")
                      : t("workforce.profile.nidOnFileNo")}
                  </dd>
                </div>
                <div>
                  <dt className="text-caption text-fg-tertiary">30d Attendance</dt>
                  <dd className="font-semibold text-fg-primary tabular-nums">{att30d}%</dd>
                </div>
                {operator && (
                  <>
                    <div>
                      <dt className="text-caption text-fg-tertiary">
                        {t("workforce.workers.colPhone")}
                      </dt>
                      <dd
                        className="font-mono text-caption text-fg-primary"
                        data-testid="profile-masked-phone"
                      >
                        {worker.maskedPhone}
                      </dd>
                    </div>
                    <div>
                      <dt className="text-caption text-fg-tertiary">
                        {t("workforce.workers.colPayment")}
                      </dt>
                      <dd className="text-caption text-fg-primary">
                        Paid via: {worker.paymentChannel}
                      </dd>
                    </div>
                  </>
                )}
              </dl>
            </Panel>

            <Panel
              title={t("workforce.performance.bonusPanelTitle")}
              right={<Badge tone={admin ? "accent" : "neutral"}>{admin ? "Admin" : "Restricted"}</Badge>}
            >
              {!admin ? (
                <Callout tone="info" testId="profile-wage-hidden">
                  {t("workforce.performance.bonusHiddenNotice")}
                </Callout>
              ) : (
                <div className="space-y-2 text-body" data-testid="profile-admin-wage-box">
                  <div className="flex justify-between">
                    <span className="text-fg-secondary">
                      {t("workforce.performance.colGrossWage")} (G{worker.grade})
                    </span>
                    <span className="font-mono font-semibold text-fg-primary">
                      {formatBDT(bonus.grossMonthlyBdt, locale)}
                    </span>
                  </div>
                  <div className="flex justify-between text-caption">
                    <span className="text-fg-tertiary">
                      {t("workforce.performance.colBasicWage")}
                    </span>
                    <span className="font-mono text-fg-secondary">
                      {formatBDT(bonus.basicMonthlyBdt, locale)}
                    </span>
                  </div>
                  <div className="flex justify-between text-caption">
                    <span className="text-fg-tertiary">
                      {t("workforce.performance.colAttBonus")}
                    </span>
                    <span className="font-mono text-fg-secondary">
                      {formatBDT(bonus.attendanceBonusBdt, locale)}
                    </span>
                  </div>
                  <div className="flex justify-between text-caption">
                    <span className="text-fg-tertiary">
                      {t("workforce.performance.colEffBonus")}
                    </span>
                    <span className="font-mono text-fg-secondary">
                      {formatBDT(bonus.efficiencyBonusBdt, locale)}
                    </span>
                  </div>
                  <div className="flex justify-between pt-2 border-t border-border-subtle font-semibold">
                    <span className="text-fg-primary">
                      {t("workforce.performance.colEstTotal")}
                    </span>
                    <span className="font-mono text-accent">
                      {formatBDT(bonus.estimatedMonthlyTotalBdt, locale)}
                    </span>
                  </div>
                  <IllustrativePolicyNote text={t("workforce.illustrativeNote") as string} />
                </div>
              )}
            </Panel>
          </div>
        )}

        {/* 2. SKILLS SUB-TAB */}
        {subTab === "skills" && (
          <Panel
            title={t("workforce.profile.skillsTitle")}
            subtitle={t("workforce.profile.skillsSubtitle")}
            icon={<Award size={15} />}
            testId="worker-subpanel-skills"
          >
            <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
              {MACHINE_SKILLS.map((sk) => {
                const cur = worker.skills[sk] ?? 0;
                return (
                  <div
                    key={sk}
                    className="surface-2 p-3.5 rounded-md flex flex-wrap items-center justify-between gap-2"
                    data-testid={`profile-skill-row-${sk}`}
                  >
                    <div>
                      <p className="font-semibold text-fg-primary">
                        {skillLabel(t as (k: string) => string, sk)}
                      </p>
                      <p className="text-caption text-fg-secondary">
                        {proficiencyLabel(t as (k: string) => string, cur)}
                      </p>
                    </div>
                    {operator ? (
                      <div className="flex items-center gap-1">
                        {([0, 1, 2, 3, 4] as const).map((lvl) => (
                          <button
                            key={lvl}
                            type="button"
                            data-testid={`profile-skill-set-${sk}-${lvl}`}
                            onClick={() => {
                              if (cur === lvl) return;
                              const res = state.updateWorkerSkill(
                                worker.id,
                                sk,
                                lvl as SkillProficiency,
                                actorEmail
                              );
                              toast.push({
                                title: `Updated ${worker.id} ${sk.toUpperCase()} to ${lvl}/4`,
                                tone: "success",
                                onUndo: res.undo
                              });
                            }}
                            className={`size-8 rounded border text-caption font-semibold tabular-nums ${
                              cur === lvl
                                ? "bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)] border-transparent"
                                : "bg-surface-1 border-border-subtle text-fg-secondary hover:text-fg-primary"
                            }`}
                          >
                            {lvl}
                          </button>
                        ))}
                      </div>
                    ) : (
                      <Badge tone={cur >= 2 ? "info" : "neutral"}>{cur} / 4</Badge>
                    )}
                  </div>
                );
              })}
            </div>
          </Panel>
        )}

        {/* 3. ATTENDANCE (30D) SUB-TAB */}
        {subTab === "attendance" && (
          <Panel
            title={t("workforce.profile.attendanceTitle")}
            subtitle={t("workforce.profile.attendanceSubtitle")}
            icon={<Calendar size={15} />}
            testId="worker-subpanel-attendance"
          >
            <div className="grid grid-cols-5 sm:grid-cols-10 gap-2">
              {last30Dates.map((d) => {
                const st: AttendanceStatus =
                  state.attendanceByDate[d]?.[worker.id] ??
                  (isFridayHoliday(d) ? "holiday" : "present");
                const isSel = d === activeDay;
                return (
                  <button
                    key={d}
                    type="button"
                    onClick={() => setSelectedDay(d)}
                    data-testid={`profile-att-day-${d}`}
                    className={`surface-2 p-2 rounded-md border text-center transition-colors ${
                      isSel
                        ? "border-[var(--accent)] ring-1 ring-[var(--accent)]"
                        : "border-border-subtle hover:border-[var(--border-strong)]"
                    }`}
                  >
                    <span className="block text-[10px] font-mono text-fg-tertiary">
                      {d.slice(5)}
                    </span>
                    <span className="mt-1 block">
                      <Badge tone={attendanceStatusTone(st)}>
                        {st === "holiday" ? "Fri" : st.slice(0, 4)}
                      </Badge>
                    </span>
                  </button>
                );
              })}
            </div>

            <div className="mt-4 surface-2 p-3.5 rounded-md flex flex-wrap items-center justify-between gap-3">
              <div>
                <p className="text-body font-medium text-fg-primary">
                  {t("workforce.profile.selectedDayLabel", { date: activeDay })}
                </p>
                <p className="text-caption text-fg-secondary">
                  Status:{" "}
                  <strong>
                    {attendanceStatusLabel(t as (k: string) => string, activeDayStatus)}
                  </strong>
                </p>
              </div>
              {operator && (
                <div className="flex flex-wrap gap-1.5">
                  {ATTENDANCE_STATUSES.map((st) => (
                    <Button
                      key={st}
                      size="sm"
                      variant={activeDayStatus === st ? "primary" : "secondary"}
                      data-testid={`profile-att-set-${st}`}
                      onClick={() => {
                        const res = state.setWorkerAttendance(
                          activeDay,
                          worker.id,
                          st,
                          actorEmail
                        );
                        toast.push({
                          title: t("workforce.attendance.statusUpdatedToast", {
                            id: worker.id,
                            status: attendanceStatusLabel(t as (k: string) => string, st),
                            date: activeDay
                          }) as string,
                          tone: "success",
                          onUndo: res.undo
                        });
                      }}
                    >
                      {attendanceStatusLabel(t as (k: string) => string, st)}
                    </Button>
                  ))}
                </div>
              )}
            </div>
          </Panel>
        )}

        {/* 4. LEAVE SUB-TAB */}
        {subTab === "leave" && (
          <div className="space-y-4" data-testid="worker-subpanel-leave">
            <Panel
              title={t("workforce.profile.leaveBalancesTitle")}
              subtitle={t("workforce.illustrativeNote")}
              right={
                operator && (
                  <Button
                    size="sm"
                    variant="primary"
                    iconLeft={<CalendarPlus size={14} />}
                    onClick={() => setLeaveModalOpen(true)}
                    data-testid="profile-request-leave-btn"
                  >
                    {t("workforce.profile.requestLeaveBtn")}
                  </Button>
                )
              }
            >
              <div className="grid grid-cols-2 sm:grid-cols-5 gap-3">
                {LEAVE_TYPES.map((lt) => {
                  const b = balances[lt];
                  return (
                    <div
                      key={lt}
                      className="surface-2 p-3 rounded-md"
                      data-testid={`profile-leave-balance-${lt}`}
                    >
                      <span className="text-caption text-fg-tertiary block">
                        {leaveTypeLabel(t as (k: string) => string, lt)}
                      </span>
                      <span className="mt-1 block text-[20px] font-semibold text-fg-primary tabular-nums">
                        {fmtNum(b.remaining)}d
                      </span>
                      <span className="text-[11px] text-fg-secondary tabular-nums">
                        Used {fmtNum(b.used)} / {fmtNum(b.entitled)}d
                      </span>
                    </div>
                  );
                })}
              </div>
            </Panel>

            <Panel title={t("workforce.profile.leaveHistoryTitle")}>
              {workerLeaveRequests.length === 0 ? (
                <p className="text-caption text-fg-tertiary">No leave requests recorded.</p>
              ) : (
                <ul className="space-y-2">
                  {workerLeaveRequests.map((req) => (
                    <li
                      key={req.id}
                      className="surface-2 p-3 rounded-md flex flex-wrap items-center justify-between gap-2 text-caption"
                    >
                      <div>
                        <span className="font-mono text-accent">{req.id}</span> ·{" "}
                        <span className="font-semibold text-fg-primary">
                          {leaveTypeLabel(t as (k: string) => string, req.type)}
                        </span>{" "}
                        ({req.startDate} → {req.endDate}, {fmtNum(req.days)}d)
                        {operator && (
                          <p className="text-fg-secondary mt-0.5">
                            {locale === "bn" ? req.reasonBn : req.reasonEn}
                          </p>
                        )}
                      </div>
                      <Badge tone={leaveStatusTone(req.status)}>
                        {leaveStatusLabel(t as (k: string) => string, req.status)}
                      </Badge>
                    </li>
                  ))}
                </ul>
              )}
            </Panel>

            <CreateLeaveModal
              open={leaveModalOpen}
              onClose={() => setLeaveModalOpen(false)}
              actorEmail={actorEmail}
              defaultWorkerId={worker.id}
              onCreated={(id, undo) => {
                setLeaveModalOpen(false);
                toast.push({
                  title: t("workforce.leave.createdToast", { id }) as string,
                  tone: "success",
                  onUndo: undo
                });
              }}
            />
          </div>
        )}

        {/* 5. PERFORMANCE (8W) SUB-TAB */}
        {subTab === "performance" && (
          <Panel
            title={t("workforce.profile.performanceTitle")}
            subtitle={t("workforce.profile.performanceSubtitle")}
            right={
              <Sparkline
                values={worker.performanceWeeks.map((pw) =>
                  calcWorkerEfficiencyPct(pw.pieces, pw.smv, pw.minutesWorked)
                )}
                width={140}
                height={32}
              />
            }
            testId="worker-subpanel-performance"
          >
            <div className="mb-3 flex flex-wrap gap-4 text-caption text-fg-secondary">
              <span>
                Latest efficiency: <strong className="text-fg-primary">{latestEff}%</strong>
              </span>
              <span>
                8-week average: <strong className="text-fg-primary">{avgEff}%</strong>
              </span>
              <span>
                8-week avg DHU: <strong className="text-fg-primary">{avgDhu}%</strong>
              </span>
            </div>
            <div className="overflow-x-auto">
              <table className="w-full text-left border-collapse text-caption tabular-nums">
                <thead>
                  <tr className="border-b border-border-subtle text-fg-tertiary">
                    <th className="py-2 px-3">Week start</th>
                    <th className="py-2 px-3 text-right">Pieces</th>
                    <th className="py-2 px-3 text-right">SMV</th>
                    <th className="py-2 px-3 text-right">Minutes worked</th>
                    <th className="py-2 px-3 text-right">Efficiency %</th>
                    <th className="py-2 px-3 text-right">Target %</th>
                    <th className="py-2 px-3 text-right">DHU %</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border-subtle">
                  {worker.performanceWeeks.map((pw) => {
                    const eff = calcWorkerEfficiencyPct(pw.pieces, pw.smv, pw.minutesWorked);
                    return (
                      <tr key={pw.weekStart}>
                        <td className="py-2 px-3 font-mono">{pw.weekStart}</td>
                        <td className="py-2 px-3 text-right font-mono">{fmtNum(pw.pieces)}</td>
                        <td className="py-2 px-3 text-right font-mono">{pw.smv}</td>
                        <td className="py-2 px-3 text-right font-mono">
                          {fmtNum(pw.minutesWorked)}
                        </td>
                        <td className="py-2 px-3 text-right font-mono font-semibold text-fg-primary">
                          {eff}%
                        </td>
                        <td className="py-2 px-3 text-right font-mono text-fg-secondary">
                          {pw.targetEfficiencyPct}%
                        </td>
                        <td className="py-2 px-3 text-right font-mono">{pw.dhuPct}%</td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </Panel>
        )}

        {/* 6. TRAINING SUB-TAB */}
        {subTab === "training" && (
          <Panel
            title={t("workforce.profile.trainingTitle")}
            icon={<GraduationCap size={15} />}
            right={
              operator &&
              availableBatches.length > 0 && (
                <div className="flex items-center gap-2">
                  <Select
                    value={batchToEnrol}
                    onChange={(e) => setBatchToEnrol(e.target.value)}
                    className="!h-8 !w-52 !text-caption"
                    data-testid="profile-training-select"
                  >
                    <option value="">Select batch…</option>
                    {availableBatches.map((b) => (
                      <option key={b.id} value={b.id}>
                        {b.id} — {locale === "bn" ? b.titleBn : b.titleEn}
                      </option>
                    ))}
                  </Select>
                  <Button
                    size="sm"
                    variant="primary"
                    disabled={!batchToEnrol}
                    data-testid="profile-training-enrol-btn"
                    onClick={() => {
                      if (!batchToEnrol) return;
                      const res = state.enrollInTrainingBatch(
                        batchToEnrol,
                        worker.id,
                        actorEmail
                      );
                      setBatchToEnrol("");
                      toast.push({
                        title: `Enrolled ${worker.id} in ${batchToEnrol}`,
                        tone: "success",
                        onUndo: res.undo
                      });
                    }}
                  >
                    {t("workforce.profile.assignTrainingBtn")}
                  </Button>
                </div>
              )
            }
            testId="worker-subpanel-training"
          >
            {workerBatches.length === 0 ? (
              <p className="text-caption text-fg-tertiary">
                Not enrolled in any training batches yet.
              </p>
            ) : (
              <ul className="space-y-2.5">
                {workerBatches.map((b) => {
                  const completed = b.completedWorkerIds.includes(worker.id);
                  return (
                    <li
                      key={b.id}
                      className="surface-2 p-3 rounded-md flex flex-wrap items-center justify-between gap-2"
                      data-testid={`profile-batch-row-${b.id}`}
                    >
                      <div>
                        <p className="font-semibold text-fg-primary">
                          {b.id} · {locale === "bn" ? b.titleBn : b.titleEn}
                        </p>
                        <p className="text-caption text-fg-secondary">
                          {skillLabel(t as (k: string) => string, b.skill)} · {b.scheduledDate}
                        </p>
                      </div>
                      <div className="flex items-center gap-2">
                        <Badge tone={completed ? "success" : "info"}>
                          {completed ? "Completed" : "Enrolled"}
                        </Badge>
                        {operator && !completed && (
                          <Button
                            size="sm"
                            variant="success"
                            data-testid={`profile-batch-complete-${b.id}`}
                            onClick={() => {
                              const res = state.markTrainingCompleted(
                                b.id,
                                worker.id,
                                actorEmail
                              );
                              toast.push({
                                title: `Marked ${b.id} complete for ${worker.id}`,
                                tone: "success",
                                onUndo: res.undo
                              });
                            }}
                          >
                            {t("workforce.profile.markCompleteBtn")}
                          </Button>
                        )}
                      </div>
                    </li>
                  );
                })}
              </ul>
            )}
          </Panel>
        )}

        {/* 7. NOTES SUB-TAB (Operator-only, operational notes only) */}
        {subTab === "notes" && operator && (
          <Panel
            title={t("workforce.profile.notesTitle")}
            icon={<FileText size={15} />}
            testId="worker-subpanel-notes"
          >
            <Callout tone="info" className="mb-3">
              {t("workforce.profile.notesPrivacyCallout")}
            </Callout>

            <form
              onSubmit={(e) => {
                e.preventDefault();
                if (!noteText.trim()) return;
                const res = state.addWorkerNote(
                  worker.id,
                  noteText,
                  noteText,
                  actorEmail
                );
                setNoteText("");
                toast.push({
                  title: t("workforce.profile.noteAddedToast") as string,
                  tone: "success",
                  onUndo: res.undo
                });
              }}
              className="flex flex-wrap items-start gap-2 mb-4"
            >
              <TextArea
                rows={2}
                value={noteText}
                onChange={(e) => setNoteText(e.target.value)}
                placeholder={t("workforce.profile.addNotePlaceholder") as string}
                className="flex-1 min-w-[220px]"
                data-testid="profile-note-input"
              />
              <Button
                type="submit"
                variant="primary"
                disabled={!noteText.trim()}
                data-testid="profile-note-submit"
              >
                {t("workforce.profile.addNoteBtn")}
              </Button>
            </form>

            {notes.length === 0 ? (
              <p className="text-caption text-fg-tertiary">No operational notes yet.</p>
            ) : (
              <ul className="space-y-2" data-testid="profile-notes-list">
                {notes.map((n) => (
                  <li key={n.id} className="surface-2 p-3 rounded-md text-caption">
                    <div className="flex items-center justify-between text-fg-tertiary mb-1">
                      <span>{n.authorEmail}</span>
                      <span>{n.date}</span>
                    </div>
                    <p className="text-body text-fg-primary">
                      {locale === "bn" ? n.bodyBn : n.bodyEn}
                    </p>
                  </li>
                ))}
              </ul>
            )}
          </Panel>
        )}
      </div>
    </div>
  );
}
