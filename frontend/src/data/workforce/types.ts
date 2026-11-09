import { z } from "zod";
import type { WorkforceLineId } from "./lines";
import type { SkillGrade } from "./policy";

export const WORKER_ROLES = [
  "sewing_operator",
  "helper",
  "line_supervisor",
  "line_chief",
  "quality_inspector",
  "cutter",
  "iron_man",
  "finishing_packing",
  "mechanic"
] as const;

export type WorkerRole = (typeof WORKER_ROLES)[number];

export const MACHINE_SKILLS = [
  "snls",
  "overlock",
  "flatlock",
  "buttonhole",
  "button_attach",
  "bartack",
  "iron_press"
] as const;

export type MachineSkill = (typeof MACHINE_SKILLS)[number];

export type SkillProficiency = 0 | 1 | 2 | 3 | 4; // 0 = untrained, 1 = learner, 2 = qualified, 3 = proficient, 4 = trainer

export const WORKER_STATUSES = ["active", "on_leave", "resigned"] as const;
export type WorkerStatus = (typeof WORKER_STATUSES)[number];

export const WORKER_SHIFTS = ["A", "B"] as const;
export type WorkerShift = (typeof WORKER_SHIFTS)[number];

export const PAYMENT_CHANNELS = ["bKash", "Nagad", "Bank"] as const;
export type PaymentChannel = (typeof PAYMENT_CHANNELS)[number];

export const ATTENDANCE_STATUSES = ["present", "absent", "late", "leave", "half_day", "holiday"] as const;
export type AttendanceStatus = (typeof ATTENDANCE_STATUSES)[number];

export const LEAVE_TYPES = ["casual", "sick", "annual", "maternity", "festival"] as const;
export type LeaveType = (typeof LEAVE_TYPES)[number];

export const LEAVE_REQUEST_STATUSES = ["pending", "approved", "rejected"] as const;
export type LeaveRequestStatus = (typeof LEAVE_REQUEST_STATUSES)[number];

export const GRIEVANCE_CATEGORIES = ["wages", "overtime", "safety", "harassment", "behaviour", "other"] as const;
export type GrievanceCategory = (typeof GRIEVANCE_CATEGORIES)[number];

export const GRIEVANCE_STATUSES = ["new", "acknowledged", "investigating", "resolved"] as const;
export type GrievanceStatus = (typeof GRIEVANCE_STATUSES)[number];

export const REFERRAL_STATUSES = ["requested", "scheduled", "closed"] as const;
export type ReferralStatus = (typeof REFERRAL_STATUSES)[number];

export type WorkerWeeklyPerformance = {
  weekStart: string; // YYYY-MM-DD (Saturday start of 6-day RMG week)
  pieces: number;
  smv: number;
  minutesWorked: number;
  dhuPct: number;
  targetEfficiencyPct: number;
};

export type WorkerRecord = {
  id: string; // e.g. W-0001
  nameEn: string;
  nameBn: string;
  gender: "female" | "male";
  age: number;
  role: WorkerRole;
  lineId: WorkforceLineId;
  shift: WorkerShift;
  grade: SkillGrade;
  status: WorkerStatus;
  joinDate: string; // YYYY-MM-DD
  homeDistrictEn: string;
  homeDistrictBn: string;
  residenceAreaEn: string;
  residenceAreaBn: string;
  maskedPhone: string; // e.g. 017XX-XXXXXX (never unmasked)
  nidOnFile: boolean; // boolean only, never a real NID number
  paymentChannel: PaymentChannel;
  firstAidTrained: boolean;
  dayCareEligible: boolean;
  skills: Record<MachineSkill, SkillProficiency>;
  /** Last 4 weeks of total weekly hours worked (oldest to current week). */
  weeklyHoursHistory: [number, number, number, number];
  /** 8 weeks of production performance (oldest to latest). */
  performanceWeeks: WorkerWeeklyPerformance[];
  /** Days worked this calendar year (drives annual leave accrual 1 day / 18 days). */
  daysWorkedYtd: number;
  /** Used leave days this year by type (approved requests add to this). */
  leaveUsedDays: Record<LeaveType, number>;
  updatedByLocal?: boolean;
};

export type LeaveRequest = {
  id: string;
  workerId: string;
  type: LeaveType;
  startDate: string; // YYYY-MM-DD
  endDate: string; // YYYY-MM-DD
  days: number;
  reasonEn: string;
  reasonBn: string;
  status: LeaveRequestStatus;
  decidedBy?: string;
  decidedAt?: string;
  decisionNote?: string;
  createdAt: string;
  updatedByLocal?: boolean;
};

export type TrainingBatch = {
  id: string;
  titleEn: string;
  titleBn: string;
  skill: MachineSkill;
  lineId: WorkforceLineId;
  instructorEn: string;
  instructorBn: string;
  scheduledDate: string; // YYYY-MM-DD
  enrolledWorkerIds: string[];
  attendedWorkerIds: string[];
  completedWorkerIds: string[];
  updatedByLocal?: boolean;
};

export type WorkerNote = {
  id: string;
  workerId: string;
  date: string; // YYYY-MM-DD
  authorEmail: string;
  bodyEn: string;
  bodyBn: string;
  updatedByLocal?: boolean;
};

export type GrievanceTicket = {
  id: string;
  ticketNo: string; // e.g. GRV-101
  category: GrievanceCategory;
  anonymous: boolean;
  /** Omitted when anonymous === true. */
  workerId?: string;
  lineId: WorkforceLineId;
  subjectEn: string;
  subjectBn: string;
  detailEn: string;
  detailBn: string;
  status: GrievanceStatus;
  createdAt: string; // YYYY-MM-DD
  updatedAt: string; // YYYY-MM-DD
  resolutionNote?: string;
  updatedByLocal?: boolean;
};

/**
 * Anonymous pulse survey response.
 * NON-NEGOTIABLE PRIVACY RULE: never stores a workerId or any personal identifier.
 */
export type PulseSurveyResponse = {
  id: string;
  lineId: WorkforceLineId;
  weekIso: string; // YYYY-MM-DD
  q1PaceManageable: 1 | 2 | 3 | 4 | 5;
  q2ToolsAndCoolingOk: 1 | 2 | 3 | 4 | 5;
  q3RespectfulSupport: 1 | 2 | 3 | 4 | 5;
  submittedAt: string;
  createdByLocal?: boolean;
};

/**
 * Voluntary, worker-initiated counsellor referral.
 * NON-NEGOTIABLE PRIVACY RULE: stores case metadata ONLY (status, date, counsellor name).
 * Never stores a reason, notes, diagnosis, or mental-health score.
 */
export type CounsellorReferral = {
  id: string;
  caseCode: string; // e.g. REF-201 (no worker name or reason stored)
  lineId: WorkforceLineId;
  counsellorNameEn: string;
  counsellorNameBn: string;
  requestedDate: string; // YYYY-MM-DD
  scheduledDate?: string; // YYYY-MM-DD
  status: ReferralStatus;
  updatedByLocal?: boolean;
};

export type SafetyChecklistItem = {
  id: string;
  key: "fire_drill" | "first_aid" | "day_care" | "harassment_committee";
  titleEn: string;
  titleBn: string;
  detailEn: string;
  detailBn: string;
  status: "verified" | "due_soon";
  lastCheckedDate: string; // YYYY-MM-DD
  metricEn: string;
  metricBn: string;
  updatedByLocal?: boolean;
};

/**
 * Separate slice for Wellbeing data so it is structurally isolated from
 * performance, bonus, discipline, or worker records.
 */
export type WellbeingSlice = {
  pulseResponses: PulseSurveyResponse[];
  counsellorReferrals: CounsellorReferral[];
  safetyChecklist: SafetyChecklistItem[];
};

export type WorkforceAuditEntry = {
  id: string;
  isoDate: string;
  actorEmail: string;
  actionEn: string;
  actionBn: string;
  targetId?: string;
};

export type WorkforcePersistedData = {
  schemaVersion: 1;
  anchorDate: string; // YYYY-MM-DD used when seed was built
  ramadanHours: boolean;
  hasLocalEdits: boolean;
  workers: WorkerRecord[];
  /** Map of date (YYYY-MM-DD) -> workerId -> AttendanceStatus */
  attendanceByDate: Record<string, Record<string, AttendanceStatus>>;
  /** Map of `${date}::${workerId}` -> true when edited locally */
  attendanceEditedKeys: Record<string, boolean>;
  /** Lines marked as covered for absent workers on a given date (`${date}::${lineId}`) */
  coveredLineDates: Record<string, boolean>;
  /** Dismissed rule-based suggestion IDs */
  dismissedSuggestionIds: string[];
  /** Applied rule-based suggestion IDs */
  appliedSuggestionIds: string[];
  /** Acknowledged workload alert worker IDs */
  acknowledgedWorkloadWorkerIds: string[];
  leaveRequests: LeaveRequest[];
  trainingBatches: TrainingBatch[];
  workerNotes: WorkerNote[];
  grievances: GrievanceTicket[];
  wellbeing: WellbeingSlice;
  auditTrail: WorkforceAuditEntry[];
};

// Zod schema to validate localStorage payloads on read (corrupt/old data falls back to seed).
export const WorkforcePersistedSchema = z.object({
  schemaVersion: z.literal(1),
  anchorDate: z.string().regex(/^\d{4}-\d{2}-\d{2}$/),
  ramadanHours: z.boolean(),
  hasLocalEdits: z.boolean(),
  workers: z.array(
    z.object({
      id: z.string(),
      nameEn: z.string(),
      nameBn: z.string(),
      gender: z.enum(["female", "male"]),
      age: z.number(),
      role: z.enum(WORKER_ROLES),
      lineId: z.enum(["line-1", "line-2", "line-3", "line-4", "line-5", "line-6"]),
      shift: z.enum(WORKER_SHIFTS),
      grade: z.union([
        z.literal(1),
        z.literal(2),
        z.literal(3),
        z.literal(4),
        z.literal(5),
        z.literal(6),
        z.literal(7)
      ]),
      status: z.enum(WORKER_STATUSES),
      joinDate: z.string(),
      homeDistrictEn: z.string(),
      homeDistrictBn: z.string(),
      residenceAreaEn: z.string(),
      residenceAreaBn: z.string(),
      maskedPhone: z.string(),
      nidOnFile: z.boolean(),
      paymentChannel: z.enum(PAYMENT_CHANNELS),
      firstAidTrained: z.boolean(),
      dayCareEligible: z.boolean(),
      skills: z.record(z.string(), z.number()),
      weeklyHoursHistory: z.tuple([z.number(), z.number(), z.number(), z.number()]),
      performanceWeeks: z.array(
        z.object({
          weekStart: z.string(),
          pieces: z.number(),
          smv: z.number(),
          minutesWorked: z.number(),
          dhuPct: z.number(),
          targetEfficiencyPct: z.number()
        })
      ),
      daysWorkedYtd: z.number(),
      leaveUsedDays: z.record(z.string(), z.number()),
      updatedByLocal: z.boolean().optional()
    })
  ).min(1),
  attendanceByDate: z.record(z.string(), z.record(z.string(), z.enum(ATTENDANCE_STATUSES))),
  attendanceEditedKeys: z.record(z.string(), z.boolean()),
  coveredLineDates: z.record(z.string(), z.boolean()),
  dismissedSuggestionIds: z.array(z.string()),
  appliedSuggestionIds: z.array(z.string()),
  acknowledgedWorkloadWorkerIds: z.array(z.string()),
  leaveRequests: z.array(
    z.object({
      id: z.string(),
      workerId: z.string(),
      type: z.enum(LEAVE_TYPES),
      startDate: z.string(),
      endDate: z.string(),
      days: z.number(),
      reasonEn: z.string(),
      reasonBn: z.string(),
      status: z.enum(LEAVE_REQUEST_STATUSES),
      decidedBy: z.string().optional(),
      decidedAt: z.string().optional(),
      decisionNote: z.string().optional(),
      createdAt: z.string(),
      updatedByLocal: z.boolean().optional()
    })
  ),
  trainingBatches: z.array(
    z.object({
      id: z.string(),
      titleEn: z.string(),
      titleBn: z.string(),
      skill: z.enum(MACHINE_SKILLS),
      lineId: z.enum(["line-1", "line-2", "line-3", "line-4", "line-5", "line-6"]),
      instructorEn: z.string(),
      instructorBn: z.string(),
      scheduledDate: z.string(),
      enrolledWorkerIds: z.array(z.string()),
      attendedWorkerIds: z.array(z.string()),
      completedWorkerIds: z.array(z.string()),
      updatedByLocal: z.boolean().optional()
    })
  ),
  workerNotes: z.array(
    z.object({
      id: z.string(),
      workerId: z.string(),
      date: z.string(),
      authorEmail: z.string(),
      bodyEn: z.string(),
      bodyBn: z.string(),
      updatedByLocal: z.boolean().optional()
    })
  ),
  grievances: z.array(
    z.object({
      id: z.string(),
      ticketNo: z.string(),
      category: z.enum(GRIEVANCE_CATEGORIES),
      anonymous: z.boolean(),
      workerId: z.string().optional(),
      lineId: z.enum(["line-1", "line-2", "line-3", "line-4", "line-5", "line-6"]),
      subjectEn: z.string(),
      subjectBn: z.string(),
      detailEn: z.string(),
      detailBn: z.string(),
      status: z.enum(GRIEVANCE_STATUSES),
      createdAt: z.string(),
      updatedAt: z.string(),
      resolutionNote: z.string().optional(),
      updatedByLocal: z.boolean().optional()
    })
  ),
  wellbeing: z.object({
    pulseResponses: z.array(
      z.object({
        id: z.string(),
        lineId: z.enum(["line-1", "line-2", "line-3", "line-4", "line-5", "line-6"]),
        weekIso: z.string(),
        q1PaceManageable: z.union([z.literal(1), z.literal(2), z.literal(3), z.literal(4), z.literal(5)]),
        q2ToolsAndCoolingOk: z.union([z.literal(1), z.literal(2), z.literal(3), z.literal(4), z.literal(5)]),
        q3RespectfulSupport: z.union([z.literal(1), z.literal(2), z.literal(3), z.literal(4), z.literal(5)]),
        submittedAt: z.string(),
        createdByLocal: z.boolean().optional()
      })
    ),
    counsellorReferrals: z.array(
      z.object({
        id: z.string(),
        caseCode: z.string(),
        lineId: z.enum(["line-1", "line-2", "line-3", "line-4", "line-5", "line-6"]),
        counsellorNameEn: z.string(),
        counsellorNameBn: z.string(),
        requestedDate: z.string(),
        scheduledDate: z.string().optional(),
        status: z.enum(REFERRAL_STATUSES),
        updatedByLocal: z.boolean().optional()
      })
    ),
    safetyChecklist: z.array(
      z.object({
        id: z.string(),
        key: z.enum(["fire_drill", "first_aid", "day_care", "harassment_committee"]),
        titleEn: z.string(),
        titleBn: z.string(),
        detailEn: z.string(),
        detailBn: z.string(),
        status: z.enum(["verified", "due_soon"]),
        lastCheckedDate: z.string(),
        metricEn: z.string(),
        metricBn: z.string(),
        updatedByLocal: z.boolean().optional()
      })
    )
  }),
  auditTrail: z.array(
    z.object({
      id: z.string(),
      isoDate: z.string(),
      actorEmail: z.string(),
      actionEn: z.string(),
      actionBn: z.string(),
      targetId: z.string().optional()
    })
  )
});
