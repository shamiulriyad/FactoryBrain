// Pure, deterministic calculation helpers for the Workforce demo module.
// No LLM calls, no side effects, no access to window/localStorage.
// Wellbeing calculations are strictly isolated from performance/wage/bonus math.

import { WORKFORCE_LINES, type WorkforceLineId } from "@/data/workforce/lines";
import {
  WORKFORCE_POLICY,
  getGradeWage,
  getHourlyBasicBdt,
  getOvertimeHourlyRateBdt
} from "@/data/workforce/policy";
import { addIsoDays, isFridayHoliday } from "@/data/workforce/seed";
import {
  MACHINE_SKILLS,
  type AttendanceStatus,
  type LeaveType,
  type MachineSkill,
  type PulseSurveyResponse,
  type WorkerRecord,
  type WorkforcePersistedData
} from "@/data/workforce/types";

/**
 * Standard RMG efficiency formula:
 *   efficiency % = (pieces * SMV / minutesWorked) * 100
 * Safe against zero or negative minutesWorked.
 */
export function calcWorkerEfficiencyPct(pieces: number, smv: number, minutesWorked: number): number {
  if (!Number.isFinite(pieces) || !Number.isFinite(smv) || !Number.isFinite(minutesWorked) || minutesWorked <= 0) {
    return 0;
  }
  const earnedMinutes = Math.max(0, pieces) * Math.max(0, smv);
  const pct = (earnedMinutes / minutesWorked) * 100;
  return Math.round(Math.min(150, Math.max(0, pct)) * 10) / 10;
}

export function calcWorkerLatestEfficiencyPct(worker: WorkerRecord): number {
  const weeks = worker.performanceWeeks;
  if (!weeks || weeks.length === 0) return 0;
  const last = weeks[weeks.length - 1];
  return calcWorkerEfficiencyPct(last.pieces, last.smv, last.minutesWorked);
}

export function calcWorkerAvgEfficiencyPct(worker: WorkerRecord): number {
  const weeks = worker.performanceWeeks;
  if (!weeks || weeks.length === 0) return 0;
  const sum = weeks.reduce((acc, w) => acc + calcWorkerEfficiencyPct(w.pieces, w.smv, w.minutesWorked), 0);
  return Math.round((sum / weeks.length) * 10) / 10;
}

export function calcWorkerAvgDhuPct(worker: WorkerRecord): number {
  const weeks = worker.performanceWeeks;
  if (!weeks || weeks.length === 0) return 0;
  const sum = weeks.reduce((acc, w) => acc + (Number.isFinite(w.dhuPct) ? w.dhuPct : 0), 0);
  return Math.round((sum / weeks.length) * 10) / 10;
}

export type DayAttendanceSummary = {
  headcount: number;
  present: number;
  absent: number;
  late: number;
  leave: number;
  halfDay: number;
  holiday: number;
  presentPct: number;
  absentPct: number;
};

/**
 * Aggregate attendance for a single date across active/on-leave workers.
 * Resigned workers are excluded from active roll-call headcount.
 */
export function computeDayAttendanceSummary(
  workers: readonly WorkerRecord[],
  dayMap: Record<string, AttendanceStatus> | undefined,
  lineFilter?: string,
  shiftFilter?: string
): DayAttendanceSummary {
  const eligible = workers.filter(
    (w) =>
      w.status !== "resigned" &&
      (!lineFilter || w.lineId === lineFilter) &&
      (!shiftFilter || w.shift === shiftFilter)
  );
  const headcount = eligible.length;
  if (headcount === 0) {
    return {
      headcount: 0,
      present: 0,
      absent: 0,
      late: 0,
      leave: 0,
      halfDay: 0,
      holiday: 0,
      presentPct: 0,
      absentPct: 0
    };
  }

  let present = 0;
  let absent = 0;
  let late = 0;
  let leave = 0;
  let halfDay = 0;
  let holiday = 0;

  for (const w of eligible) {
    const st: AttendanceStatus = dayMap?.[w.id] ?? (w.status === "on_leave" ? "leave" : "present");
    if (st === "present") present++;
    else if (st === "absent") absent++;
    else if (st === "late") late++;
    else if (st === "leave") leave++;
    else if (st === "half_day") halfDay++;
    else if (st === "holiday") holiday++;
  }

  const effectivePresent = present + late + halfDay * 0.5;
  const denom = Math.max(1, headcount - holiday);
  const presentPct = holiday === headcount ? 100 : Math.round((effectivePresent / denom) * 1000) / 10;
  const absentPct = holiday === headcount ? 0 : Math.round((absent / denom) * 1000) / 10;

  return {
    headcount,
    present,
    absent,
    late,
    leave,
    halfDay,
    holiday,
    presentPct,
    absentPct
  };
}

export type LineAbsenteeismRow = {
  lineId: WorkforceLineId;
  code: string;
  nameEn: string;
  nameBn: string;
  headcount: number;
  present: number;
  absent: number;
  late: number;
  leave: number;
  halfDay: number;
  absentPct: number;
  presentPct: number;
};

export function computeLineAbsenteeism(
  workers: readonly WorkerRecord[],
  dayMap: Record<string, AttendanceStatus> | undefined
): LineAbsenteeismRow[] {
  return WORKFORCE_LINES.map((line) => {
    const s = computeDayAttendanceSummary(workers, dayMap, line.id);
    return {
      lineId: line.id,
      code: line.code,
      nameEn: line.nameEn,
      nameBn: line.nameBn,
      headcount: s.headcount,
      present: s.present,
      absent: s.absent,
      late: s.late,
      leave: s.leave,
      halfDay: s.halfDay,
      absentPct: s.absentPct,
      presentPct: s.presentPct
    };
  });
}

export type AttendanceTrendPoint = {
  date: string;
  presentPct: number;
  absentCount: number;
  lateCount: number;
  isHoliday: boolean;
};

export function compute14DayAttendanceTrend(
  workers: readonly WorkerRecord[],
  attendanceByDate: Record<string, Record<string, AttendanceStatus>>,
  anchorDate: string
): AttendanceTrendPoint[] {
  const points: AttendanceTrendPoint[] = [];
  for (let d = 13; d >= 0; d--) {
    const date = addIsoDays(anchorDate, -d);
    const holiday = isFridayHoliday(date);
    const summary = computeDayAttendanceSummary(workers, attendanceByDate[date]);
    points.push({
      date,
      presentPct: summary.presentPct,
      absentCount: summary.absent,
      lateCount: summary.late,
      isHoliday: holiday
    });
  }
  return points;
}

export function computeWorkerAttendanceRatePct(
  workerId: string,
  attendanceByDate: Record<string, Record<string, AttendanceStatus>>,
  anchorDate: string,
  windowDays = 30
): number {
  let workingDays = 0;
  let attendedPoints = 0;
  for (let d = windowDays - 1; d >= 0; d--) {
    const date = addIsoDays(anchorDate, -d);
    if (isFridayHoliday(date)) continue;
    const st = attendanceByDate[date]?.[workerId];
    if (!st || st === "holiday") continue;
    workingDays++;
    if (st === "present" || st === "late") attendedPoints += 1;
    else if (st === "half_day") attendedPoints += 0.5;
    else if (st === "leave") attendedPoints += 1; // approved leave does not penalize attendance rate
  }
  if (workingDays === 0) return 100;
  return Math.round((attendedPoints / workingDays) * 1000) / 10;
}

export type LeaveBalanceItem = {
  type: LeaveType;
  entitled: number;
  used: number;
  remaining: number;
};

/**
 * Computes statutory demo leave balances per worker:
 * - casual: 10 days/yr
 * - sick: 14 days/yr
 * - annual (earned): 1 day per 18 days worked
 * - maternity: 16 weeks (112 days) for female workers
 * - festival: 11 festival holiday days/yr
 */
export function computeLeaveBalances(worker: WorkerRecord): Record<LeaveType, LeaveBalanceItem> {
  const annualEntitled = Math.floor(
    Math.max(0, worker.daysWorkedYtd) / WORKFORCE_POLICY.leave.annualAccrualDaysWorkedPerDay
  );
  const maternityEntitled =
    worker.gender === "female" ? WORKFORCE_POLICY.leave.maternityWeeksTotal * 7 : 0;

  const entitledMap: Record<LeaveType, number> = {
    casual: WORKFORCE_POLICY.leave.casualDaysPerYear,
    sick: WORKFORCE_POLICY.leave.sickDaysPerYear,
    annual: annualEntitled,
    maternity: maternityEntitled,
    festival: WORKFORCE_POLICY.leave.festivalHolidaysTotalDays
  };

  const result = {} as Record<LeaveType, LeaveBalanceItem>;
  for (const type of ["casual", "sick", "annual", "maternity", "festival"] as const) {
    const entitled = entitledMap[type];
    const used = Math.max(0, worker.leaveUsedDays?.[type] ?? 0);
    const remaining = Math.max(0, entitled - used);
    result[type] = { type, entitled, used, remaining };
  }
  return result;
}

export function validateLeaveRequest(
  worker: WorkerRecord,
  type: LeaveType,
  days: number
): { valid: boolean; entitled: number; used: number; remaining: number; reasonCode?: "invalid_days" | "ineligible_maternity" | "overdrawn" } {
  const balances = computeLeaveBalances(worker);
  const b = balances[type];
  if (!Number.isFinite(days) || days <= 0) {
    return { valid: false, entitled: b.entitled, used: b.used, remaining: b.remaining, reasonCode: "invalid_days" };
  }
  if (type === "maternity" && worker.gender !== "female") {
    return { valid: false, entitled: 0, used: 0, remaining: 0, reasonCode: "ineligible_maternity" };
  }
  if (days > b.remaining) {
    return { valid: false, entitled: b.entitled, used: b.used, remaining: b.remaining, reasonCode: "overdrawn" };
  }
  return { valid: true, entitled: b.entitled, used: b.used, remaining: b.remaining };
}

export function getWorkerCurrentWeeklyHours(worker: WorkerRecord): number {
  const hist = worker.weeklyHoursHistory;
  return hist[hist.length - 1] ?? 48;
}

export function getWorkerCurrentOvertimeHours(worker: WorkerRecord): number {
  const total = getWorkerCurrentWeeklyHours(worker);
  return Math.max(0, total - WORKFORCE_POLICY.overtime.standardWeeklyHours);
}

export function isOverWeeklyCap(weeklyHours: number): boolean {
  return weeklyHours > WORKFORCE_POLICY.overtime.weeklyTotalCapHours;
}

/**
 * Hours-only workload signal: true when a worker's total weekly hours
 * exceeded 60 h/week for 3 consecutive weeks.
 * NEVER uses or implies any wellbeing/mental-health score.
 */
export function hasConsecutiveWorkloadAlert(weeklyHoursHistory: readonly number[]): boolean {
  const n = WORKFORCE_POLICY.overtime.workloadAlertConsecutiveWeeks;
  if (!weeklyHoursHistory || weeklyHoursHistory.length < n) return false;
  const recent = weeklyHoursHistory.slice(-n);
  return recent.every((h) => h > WORKFORCE_POLICY.overtime.weeklyTotalCapHours);
}

export type WorkerBonusPreview = {
  workerId: string;
  grade: number;
  grossMonthlyBdt: number;
  basicMonthlyBdt: number;
  hourlyBasicBdt: number;
  overtimeHourlyRateBdt: number;
  overtimeHoursThisWeek: number;
  overtimePayThisWeekBdt: number;
  attendanceBonusBdt: number;
  efficiencyTier: "none" | "tier1" | "tier2";
  efficiencyBonusBdt: number;
  festivalBonusPreviewBdt: number;
  estimatedMonthlyTotalBdt: number;
};

export function computeWorkerBonusPreview(
  worker: WorkerRecord,
  attendanceRatePct: number
): WorkerBonusPreview {
  const wage = getGradeWage(worker.grade);
  const hourlyBasicBdt = getHourlyBasicBdt(worker.grade);
  const overtimeHourlyRateBdt = getOvertimeHourlyRateBdt(worker.grade);
  const overtimeHoursThisWeek = getWorkerCurrentOvertimeHours(worker);
  const overtimePayThisWeekBdt = Math.round(overtimeHoursThisWeek * overtimeHourlyRateBdt);

  const attendanceBonusBdt =
    attendanceRatePct >= 96 ? WORKFORCE_POLICY.bonuses.attendanceBonusBdt : 0;

  const avgEff = calcWorkerAvgEfficiencyPct(worker);
  const avgDhu = calcWorkerAvgDhuPct(worker);

  let efficiencyTier: "none" | "tier1" | "tier2" = "none";
  let efficiencyBonusBdt = 0;
  if (
    avgEff >= WORKFORCE_POLICY.bonuses.efficiencyTier2MinPct &&
    avgDhu <= WORKFORCE_POLICY.bonuses.efficiencyTier2MaxDhuPct
  ) {
    efficiencyTier = "tier2";
    efficiencyBonusBdt = WORKFORCE_POLICY.bonuses.efficiencyTier2BonusBdt;
  } else if (
    avgEff >= WORKFORCE_POLICY.bonuses.efficiencyTier1MinPct &&
    avgDhu <= WORKFORCE_POLICY.bonuses.efficiencyTier1MaxDhuPct
  ) {
    efficiencyTier = "tier1";
    efficiencyBonusBdt = WORKFORCE_POLICY.bonuses.efficiencyTier1BonusBdt;
  }

  const festivalBonusPreviewBdt = Math.round(
    (wage.basicBdt * WORKFORCE_POLICY.bonuses.festivalBonusPctOfBasic) / 100
  );

  const estimatedMonthlyTotalBdt =
    wage.grossMonthlyBdt +
    overtimePayThisWeekBdt * 4 +
    attendanceBonusBdt +
    efficiencyBonusBdt;

  return {
    workerId: worker.id,
    grade: worker.grade,
    grossMonthlyBdt: wage.grossMonthlyBdt,
    basicMonthlyBdt: wage.basicBdt,
    hourlyBasicBdt,
    overtimeHourlyRateBdt,
    overtimeHoursThisWeek,
    overtimePayThisWeekBdt,
    attendanceBonusBdt,
    efficiencyTier,
    efficiencyBonusBdt,
    festivalBonusPreviewBdt,
    estimatedMonthlyTotalBdt
  };
}

export type LinePulseAggregate = {
  lineId: WorkforceLineId;
  count: number;
  minRequired: number;
  /** True only when count >= minRequired (default k >= 5). */
  sufficient: boolean;
  avgQ1: number | null;
  avgQ2: number | null;
  avgQ3: number | null;
  overallAvg: number | null;
};

/**
 * Enforces the k >= 5 privacy threshold per line.
 * When a line has fewer than `minK` responses, averages are returned as `null`
 * so small-group responses can never be inferred.
 */
export function aggregatePulseByLine(
  responses: readonly PulseSurveyResponse[],
  minK: number = WORKFORCE_POLICY.wellbeing.minPulseResponsesPerLine
): LinePulseAggregate[] {
  return WORKFORCE_LINES.map((line) => {
    const items = responses.filter((r) => r.lineId === line.id);
    const count = items.length;
    if (count < minK) {
      return {
        lineId: line.id,
        count,
        minRequired: minK,
        sufficient: false,
        avgQ1: null,
        avgQ2: null,
        avgQ3: null,
        overallAvg: null
      };
    }
    const sumQ1 = items.reduce((acc, r) => acc + r.q1PaceManageable, 0);
    const sumQ2 = items.reduce((acc, r) => acc + r.q2ToolsAndCoolingOk, 0);
    const sumQ3 = items.reduce((acc, r) => acc + r.q3RespectfulSupport, 0);
    const avgQ1 = Math.round((sumQ1 / count) * 10) / 10;
    const avgQ2 = Math.round((sumQ2 / count) * 10) / 10;
    const avgQ3 = Math.round((sumQ3 / count) * 10) / 10;
    const overallAvg = Math.round(((avgQ1 + avgQ2 + avgQ3) / 3) * 10) / 10;
    return {
      lineId: line.id,
      count,
      minRequired: minK,
      sufficient: true,
      avgQ1,
      avgQ2,
      avgQ3,
      overallAvg
    };
  });
}

export type RuleBasedSuggestion = {
  id: string;
  titleEn: string;
  titleBn: string;
  rationaleEn: string;
  rationaleBn: string;
  impactEn: string;
  impactBn: string;
  kind: "move_helpers" | "cross_train" | "cap_overtime";
  fromLineId?: WorkforceLineId;
  toLineId: WorkforceLineId;
  workerIds: string[];
  skill?: MachineSkill;
};

/**
 * Deterministic rule-based suggestions derived from the current workforce state.
 * Explicitly labelled "Rule-based suggestion" in the UI — never claims AI/LLM.
 */
export function computeRuleBasedSuggestions(state: WorkforcePersistedData): RuleBasedSuggestion[] {
  const out: RuleBasedSuggestion[] = [];
  const dismissed = new Set(state.dismissedSuggestionIds);
  const applied = new Set(state.appliedSuggestionIds);

  // Rule 1: Line 5 has surplus cross-skilled helpers while Line 3 is short on helpers
  const l5Helpers = state.workers.filter(
    (w) =>
      w.status === "active" &&
      w.lineId === "line-5" &&
      w.role === "helper" &&
      (w.skills.snls >= 2 || w.skills.overlock >= 2)
  );
  const l3Helpers = state.workers.filter(
    (w) => w.status === "active" && w.lineId === "line-3" && w.role === "helper"
  );

  if (
    l5Helpers.length >= 4 &&
    l3Helpers.length < 3 &&
    !dismissed.has("sug-move-helpers-l5-l3") &&
    !applied.has("sug-move-helpers-l5-l3")
  ) {
    const picked = l5Helpers.slice(0, 2);
    out.push({
      id: "sug-move-helpers-l5-l3",
      titleEn: "Move 2 cross-skilled helpers from Line 5 to Line 3",
      titleBn: "লাইন ৫ থেকে ২ জন বহুমুখী হেল্পার লাইন ৩-এ স্থানান্তর করুন",
      rationaleEn: `Line 3 has only ${l3Helpers.length} helper (target 3) and the highest absenteeism today, while Line 5 has ${l5Helpers.length} helpers qualified on SNLS/Overlock (${picked.map((p) => p.id).join(", ")}).`,
      rationaleBn: `লাইন ৩-এ মাত্র ${l3Helpers.length} জন হেল্পার আছেন (লক্ষ্য ৩) এবং অনুপস্থিতি সবচেয়ে বেশি; অন্যদিকে লাইন ৫-এ এসএনএলএস/ওভারলক দক্ষ ${l5Helpers.length} জন হেল্পার রয়েছেন (${picked.map((p) => p.id).join(", ")})।`,
      impactEn: "Rebalances helper ratio to 3 on Line 3 and eases V-Neck bundle feeding.",
      impactBn: "লাইন ৩-এ হেল্পার সংখ্যা ৩-এ উন্নীত হবে এবং ভি-নেক বান্ডিল সরবরাহ স্বাভাবিক হবে।",
      kind: "move_helpers",
      fromLineId: "line-5",
      toLineId: "line-3",
      workerIds: picked.map((p) => p.id)
    });
  }

  // Rule 2: Line 3 buttonhole skill coverage gap
  const l3ButtonholeQualified = state.workers.filter(
    (w) => w.status === "active" && w.lineId === "line-3" && (w.skills.buttonhole ?? 0) >= 2
  );
  const l3Learners = state.workers.filter(
    (w) =>
      w.status === "active" &&
      w.lineId === "line-3" &&
      w.role === "sewing_operator" &&
      (w.skills.buttonhole ?? 0) < 2
  );
  if (
    l3ButtonholeQualified.length < 3 &&
    l3Learners.length >= 2 &&
    !dismissed.has("sug-cross-train-l3-buttonhole") &&
    !applied.has("sug-cross-train-l3-buttonhole")
  ) {
    const picked = l3Learners.slice(0, 2);
    out.push({
      id: "sug-cross-train-l3-buttonhole",
      titleEn: "Enrol 2 Line 3 operators in Buttonhole cross-training",
      titleBn: "লাইন ৩-এর ২ জন অপারেটরকে বাটনহোল ক্রস-ট্রেনিংয়ে যুক্ত করুন",
      rationaleEn: `Line 3 has only ${l3ButtonholeQualified.length} qualified buttonhole operator(s). Enrolling ${picked.map((p) => p.id).join(", ")} eliminates single-operator dependency.`,
      rationaleBn: `লাইন ৩-এ মাত্র ${l3ButtonholeQualified.length} জন দক্ষ বাটনহোল অপারেটর আছেন। ${picked.map((p) => p.id).join(", ")}-কে প্রশিক্ষণে যুক্ত করলে একক নির্ভরতা কমবে।`,
      impactEn: "Adds 2 backup buttonhole operators on Line 3.",
      impactBn: "লাইন ৩-এ ২ জন অতিরিক্ত বাটনহোল অপারেটর তৈরি হবে।",
      kind: "cross_train",
      toLineId: "line-3",
      workerIds: picked.map((p) => p.id),
      skill: "buttonhole"
    });
  }

  // Rule 3: Cap weekly overtime for workers on 3-week workload alert
  const stressed = state.workers.filter(
    (w) =>
      w.status === "active" &&
      w.lineId === "line-3" &&
      hasConsecutiveWorkloadAlert(w.weeklyHoursHistory)
  );
  if (
    stressed.length > 0 &&
    !dismissed.has("sug-cap-ot-l3") &&
    !applied.has("sug-cap-ot-l3")
  ) {
    out.push({
      id: "sug-cap-ot-l3",
      titleEn: `Cap weekly hours at 56 h for ${stressed.length} Line 3 workers on Workload alert`,
      titleBn: `ওয়ার্কলোড সতর্কতায় থাকা লাইন ৩-এর ${stressed.length} জন কর্মীর সাপ্তাহিক কর্মঘণ্টা ৫৬ ঘণ্টায় সীমিত করুন`,
      rationaleEn: `${stressed.length} workers (${stressed.slice(0, 3).map((w) => w.id).join(", ")}${stressed.length > 3 ? "…" : ""}) have exceeded 60 h/week for 3 consecutive weeks.`,
      rationaleBn: `${stressed.length} জন কর্মী টানা ৩ সপ্তাহ ৬০ ঘণ্টা/সপ্তাহের বেশি কাজ করেছেন।`,
      impactEn: "Clears 3-week >60 h workload alerts on Line 3.",
      impactBn: "লাইন ৩-এর টানা ৩ সপ্তাহের >৬০ ঘণ্টা ওয়ার্কলোড সতর্কতা দূর হবে।",
      kind: "cap_overtime",
      toLineId: "line-3",
      workerIds: stressed.map((w) => w.id)
    });
  }

  return out;
}

export type SkillCoverageCell = {
  lineId: WorkforceLineId;
  skill: MachineSkill;
  qualifiedCount: number; // proficiency >= 2
  trainerCount: number; // proficiency === 4
};

export function computeSkillCoverageMatrix(
  workers: readonly WorkerRecord[]
): Record<WorkforceLineId, Record<MachineSkill, SkillCoverageCell>> {
  const matrix = {} as Record<WorkforceLineId, Record<MachineSkill, SkillCoverageCell>>;
  for (const line of WORKFORCE_LINES) {
    matrix[line.id] = {} as Record<MachineSkill, SkillCoverageCell>;
    for (const sk of MACHINE_SKILLS) {
      const lineWorkers = workers.filter((w) => w.status !== "resigned" && w.lineId === line.id);
      const qualifiedCount = lineWorkers.filter((w) => (w.skills[sk] ?? 0) >= 2).length;
      const trainerCount = lineWorkers.filter((w) => (w.skills[sk] ?? 0) === 4).length;
      matrix[line.id][sk] = {
        lineId: line.id,
        skill: sk,
        qualifiedCount,
        trainerCount
      };
    }
  }
  return matrix;
}

export type CrossTrainingCandidate = {
  workerId: string;
  nameEn: string;
  nameBn: string;
  lineId: WorkforceLineId;
  currentRole: WorkerRecord["role"];
  grade: number;
  targetSkill: MachineSkill;
  currentProficiency: number;
  reasonEn: string;
  reasonBn: string;
};

export function computeCrossTrainingCandidates(
  workers: readonly WorkerRecord[]
): CrossTrainingCandidate[] {
  const candidates: CrossTrainingCandidate[] = [];
  for (const w of workers) {
    if (w.status !== "active") continue;
    if (w.lineId === "line-3" && w.role === "sewing_operator" && (w.skills.buttonhole ?? 0) < 2) {
      candidates.push({
        workerId: w.id,
        nameEn: w.nameEn,
        nameBn: w.nameBn,
        lineId: w.lineId,
        currentRole: w.role,
        grade: w.grade,
        targetSkill: "buttonhole",
        currentProficiency: w.skills.buttonhole ?? 0,
        reasonEn: "Line 3 has only 1 qualified buttonhole operator; strong SNLS base.",
        reasonBn: "লাইন ৩-এ মাত্র ১ জন বাটনহোল অপারেটর আছেন; এসএনএলএস ভিত্তি ভালো।"
      });
    } else if (w.lineId === "line-5" && w.role === "helper" && (w.skills.snls ?? 0) >= 2) {
      candidates.push({
        workerId: w.id,
        nameEn: w.nameEn,
        nameBn: w.nameBn,
        lineId: w.lineId,
        currentRole: w.role,
        grade: w.grade,
        targetSkill: "overlock",
        currentProficiency: w.skills.overlock ?? 1,
        reasonEn: "Cross-skilled helper ready for Grade 6 operator progression.",
        reasonBn: "গ্রেড ৬ অপারেটর পদে উন্নীত হওয়ার জন্য প্রস্তুত বহুমুখী হেল্পার।"
      });
    } else if ((w.skills.flatlock ?? 0) === 1 && w.role === "sewing_operator") {
      candidates.push({
        workerId: w.id,
        nameEn: w.nameEn,
        nameBn: w.nameBn,
        lineId: w.lineId,
        currentRole: w.role,
        grade: w.grade,
        targetSkill: "flatlock",
        currentProficiency: 1,
        reasonEn: "At learner stage (1/4) on Flatlock; 1 workshop away from qualification.",
        reasonBn: "ফ্ল্যাটলকে শিক্ষানবিশ পর্যায়ে (১/৪); ১টি কর্মশালাতেই দক্ষতা অর্জন সম্ভব।"
      });
    }
    if (candidates.length >= 10) break;
  }
  return candidates;
}

export function computeGrievanceSlaDays(
  createdAtIso: string,
  anchorDateIso: string
): { daysOpen: number; overdue: boolean } {
  const [y1, m1, d1] = createdAtIso.split("-").map(Number);
  const [y2, m2, d2] = anchorDateIso.split("-").map(Number);
  const t1 = Date.UTC(y1 || 2026, (m1 || 1) - 1, d1 || 1);
  const t2 = Date.UTC(y2 || 2026, (m2 || 1) - 1, d2 || 1);
  const daysOpen = Math.max(0, Math.round((t2 - t1) / 86_400_000));
  return {
    daysOpen,
    overdue: daysOpen > WORKFORCE_POLICY.wellbeing.grievanceSlaDays
  };
}

function escapeCsvCell(val: string | number): string {
  const str = String(val ?? "");
  if (/[",\n\r]/.test(str)) {
    return `"${str.replace(/"/g, '""')}"`;
  }
  return str;
}

/**
 * Builds a CSV string with Viewer-safe fields ONLY (never phone, wage, NID, leave reason, or wellbeing).
 */
export function buildViewerSafeCsv(workers: readonly WorkerRecord[]): string {
  const headers = ["id", "nameEn", "nameBn", "role", "lineId", "shift", "grade", "status", "joinDate"];
  const rows = workers.map((w) =>
    [w.id, w.nameEn, w.nameBn, w.role, w.lineId, w.shift, w.grade, w.status, w.joinDate]
      .map(escapeCsvCell)
      .join(",")
  );
  return [headers.join(","), ...rows].join("\n");
}
