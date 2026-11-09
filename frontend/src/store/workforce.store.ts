"use client";

import { create } from "zustand";
import { persist as storage } from "@/lib/persist";
import { WORKFORCE_LINES, type WorkforceLineId } from "@/data/workforce/lines";
import { buildWorkforceSeed, getDefaultAnchorDate } from "@/data/workforce/seed";
import {
  MACHINE_SKILLS,
  WorkforcePersistedSchema,
  type AttendanceStatus,
  type GrievanceCategory,
  type GrievanceStatus,
  type LeaveType,
  type MachineSkill,
  type PaymentChannel,
  type ReferralStatus,
  type SkillProficiency,
  type WorkerRecord,
  type WorkerRole,
  type WorkerShift,
  type WorkerStatus,
  type WorkforceAuditEntry,
  type WorkforcePersistedData
} from "@/data/workforce/types";
import type { SkillGrade } from "@/data/workforce/policy";
import { validateLeaveRequest, type RuleBasedSuggestion } from "@/lib/workforce";

export const WORKFORCE_STORAGE_KEY = "bunonbrain:workforce:v1";

function loadValidatedOrSeed(): WorkforcePersistedData {
  if (typeof window === "undefined") {
    return buildWorkforceSeed("2026-10-25");
  }
  const raw = storage.get<unknown>(WORKFORCE_STORAGE_KEY, null);
  if (raw) {
    const parsed = WorkforcePersistedSchema.safeParse(raw);
    if (parsed.success) {
      return parsed.data as WorkforcePersistedData;
    }
  }
  const fresh = buildWorkforceSeed(getDefaultAnchorDate());
  storage.set(WORKFORCE_STORAGE_KEY, fresh);
  return fresh;
}

function makeAudit(
  actorEmail: string,
  actionEn: string,
  actionBn: string,
  targetId?: string
): WorkforceAuditEntry {
  return {
    id: `AUD-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`,
    isoDate: new Date().toISOString(),
    actorEmail: actorEmail || "operator@bunonbrain.demo",
    actionEn,
    actionBn,
    targetId
  };
}

export type AddWorkerInput = {
  nameEn: string;
  nameBn: string;
  gender: "female" | "male";
  age: number;
  role: WorkerRole;
  lineId: WorkforceLineId;
  shift: WorkerShift;
  grade: SkillGrade;
  status: WorkerStatus;
  joinDate: string;
  homeDistrictEn: string;
  homeDistrictBn: string;
  residenceAreaEn: string;
  residenceAreaBn: string;
  maskedPhone: string;
  nidOnFile: boolean;
  paymentChannel: PaymentChannel;
  firstAidTrained: boolean;
  dayCareEligible: boolean;
  primarySkill?: MachineSkill;
};

type WorkforceStoreState = WorkforcePersistedData & {
  hydrated: boolean;
  rehydrate: () => void;
  resetDemoData: (actorEmail: string) => void;
  toggleRamadanHours: (actorEmail: string) => { undo: () => void };
  setWorkerAttendance: (
    date: string,
    workerId: string,
    status: AttendanceStatus,
    actorEmail: string
  ) => { undo: () => void };
  markLinePresent: (
    date: string,
    lineId: WorkforceLineId,
    shift: string,
    actorEmail: string
  ) => { count: number; undo: () => void };
  markLineCoverForAbsent: (
    date: string,
    lineId: WorkforceLineId,
    actorEmail: string
  ) => { undo: () => void };
  decideLeaveRequest: (
    requestId: string,
    decision: "approved" | "rejected",
    note: string | undefined,
    actorEmail: string
  ) => { undo: () => void };
  createLeaveRequest: (
    input: {
      workerId: string;
      type: LeaveType;
      startDate: string;
      endDate: string;
      days: number;
      reasonEn: string;
      reasonBn: string;
    },
    actorEmail: string
  ) => { ok: true; id: string; undo: () => void } | { ok: false; reasonCode: string };
  addWorker: (input: AddWorkerInput, actorEmail: string) => { id: string; undo: () => void };
  updateWorker: (
    workerId: string,
    patch: Partial<
      Pick<
        WorkerRecord,
        | "nameEn"
        | "nameBn"
        | "age"
        | "gender"
        | "role"
        | "lineId"
        | "shift"
        | "grade"
        | "status"
        | "homeDistrictEn"
        | "homeDistrictBn"
        | "residenceAreaEn"
        | "residenceAreaBn"
        | "paymentChannel"
        | "firstAidTrained"
        | "dayCareEligible"
        | "nidOnFile"
      >
    >,
    actorEmail: string
  ) => { undo: () => void };
  updateWorkerSkill: (
    workerId: string,
    skill: MachineSkill,
    proficiency: SkillProficiency,
    actorEmail: string
  ) => { undo: () => void };
  adjustWorkerWeeklyHours: (
    workerId: string,
    newCurrentWeekHours: number,
    actorEmail: string
  ) => { undo: () => void };
  acknowledgeWorkloadAlert: (workerId: string, actorEmail: string) => { undo: () => void };
  applySuggestion: (suggestion: RuleBasedSuggestion, actorEmail: string) => { undo: () => void };
  dismissSuggestion: (suggestionId: string, actorEmail: string) => { undo: () => void };
  createTrainingBatch: (
    input: {
      titleEn: string;
      titleBn: string;
      skill: MachineSkill;
      lineId: WorkforceLineId;
      instructorEn: string;
      instructorBn: string;
      scheduledDate: string;
      enrolledWorkerIds: string[];
    },
    actorEmail: string
  ) => { id: string; undo: () => void };
  enrollInTrainingBatch: (
    batchId: string,
    workerId: string,
    actorEmail: string
  ) => { undo: () => void };
  toggleTrainingAttendance: (
    batchId: string,
    workerId: string,
    actorEmail: string
  ) => { undo: () => void };
  markTrainingCompleted: (
    batchId: string,
    workerId: string,
    actorEmail: string
  ) => { undo: () => void };
  addWorkerNote: (
    workerId: string,
    bodyEn: string,
    bodyBn: string,
    actorEmail: string
  ) => { id: string; undo: () => void };
  createGrievance: (
    input: {
      category: GrievanceCategory;
      anonymous: boolean;
      workerId?: string;
      lineId: WorkforceLineId;
      subjectEn: string;
      subjectBn: string;
      detailEn: string;
      detailBn: string;
    },
    actorEmail: string
  ) => { id: string; ticketNo: string; undo: () => void };
  updateGrievanceStatus: (
    grievanceId: string,
    status: GrievanceStatus,
    resolutionNote: string | undefined,
    actorEmail: string
  ) => { undo: () => void };
  submitPulseSurvey: (input: {
    lineId: WorkforceLineId;
    q1PaceManageable: 1 | 2 | 3 | 4 | 5;
    q2ToolsAndCoolingOk: 1 | 2 | 3 | 4 | 5;
    q3RespectfulSupport: 1 | 2 | 3 | 4 | 5;
  }) => { id: string; undo: () => void };
  createCounsellorReferral: (
    input: {
      lineId: WorkforceLineId;
      counsellorNameEn: string;
      counsellorNameBn: string;
      scheduledDate?: string;
    },
    actorEmail: string
  ) => { id: string; caseCode: string; undo: () => void };
  updateCounsellorReferralStatus: (
    referralId: string,
    status: ReferralStatus,
    scheduledDate: string | undefined,
    actorEmail: string
  ) => { undo: () => void };
  verifySafetyItem: (itemId: string, actorEmail: string) => { undo: () => void };
};

function toPersistedSnapshot(state: WorkforcePersistedData): WorkforcePersistedData {
  return {
    schemaVersion: 1,
    anchorDate: state.anchorDate,
    ramadanHours: state.ramadanHours,
    hasLocalEdits: state.hasLocalEdits,
    workers: state.workers,
    attendanceByDate: state.attendanceByDate,
    attendanceEditedKeys: state.attendanceEditedKeys,
    coveredLineDates: state.coveredLineDates,
    dismissedSuggestionIds: state.dismissedSuggestionIds,
    appliedSuggestionIds: state.appliedSuggestionIds,
    acknowledgedWorkloadWorkerIds: state.acknowledgedWorkloadWorkerIds,
    leaveRequests: state.leaveRequests,
    trainingBatches: state.trainingBatches,
    workerNotes: state.workerNotes,
    grievances: state.grievances,
    wellbeing: state.wellbeing,
    auditTrail: state.auditTrail
  };
}

const initialSeed = buildWorkforceSeed("2026-10-25");

export const useWorkforceStore = create<WorkforceStoreState>((set, get) => {
  function commit(next: WorkforcePersistedData) {
    const snap = toPersistedSnapshot(next);
    storage.set(WORKFORCE_STORAGE_KEY, snap);
    set({ ...snap, hydrated: true });
  }

  return {
    ...initialSeed,
    hydrated: false,

    rehydrate: () => {
      if (get().hydrated) return;
      const loaded = loadValidatedOrSeed();
      set({ ...loaded, hydrated: true });
    },

    resetDemoData: (actorEmail) => {
      const fresh = buildWorkforceSeed(getDefaultAnchorDate());
      const entry = makeAudit(
        actorEmail,
        "Reset workforce demo data to initial seed",
        "কর্মীবাহিনী ডেমো ডেটা প্রারম্ভিক অবস্থায় রিসেট করা হয়েছে"
      );
      const next: WorkforcePersistedData = {
        ...fresh,
        hasLocalEdits: false,
        auditTrail: [entry, ...fresh.auditTrail]
      };
      commit(next);
    },

    toggleRamadanHours: (actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const nextVal = !prev.ramadanHours;
      const entry = makeAudit(
        actorEmail,
        nextVal ? "Enabled Ramadan shift hours display" : "Restored regular shift hours display",
        nextVal ? "রমজান শিফট সময়সূচি চালু করা হয়েছে" : "সাধারণ শিফট সময়সূচি পুনরুদ্ধার করা হয়েছে"
      );
      commit({
        ...prev,
        ramadanHours: nextVal,
        hasLocalEdits: true,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    setWorkerAttendance: (date, workerId, status, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const dayMap = { ...(prev.attendanceByDate[date] ?? {}) };
      dayMap[workerId] = status;
      const key = `${date}::${workerId}`;
      const entry = makeAudit(
        actorEmail,
        `Marked ${workerId} as ${status} on ${date}`,
        `${date} তারিখে ${workerId}-কে ${status} হিসেবে চিহ্নিত করা হয়েছে`,
        workerId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        attendanceByDate: { ...prev.attendanceByDate, [date]: dayMap },
        attendanceEditedKeys: { ...prev.attendanceEditedKeys, [key]: true },
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    markLinePresent: (date, lineId, shift, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const dayMap = { ...(prev.attendanceByDate[date] ?? {}) };
      const editedKeys = { ...prev.attendanceEditedKeys };
      let count = 0;
      for (const w of prev.workers) {
        if (w.status === "resigned") continue;
        if (w.lineId !== lineId) continue;
        if (shift && w.shift !== shift) continue;
        if (dayMap[w.id] !== "present") {
          dayMap[w.id] = "present";
          editedKeys[`${date}::${w.id}`] = true;
          count++;
        }
      }
      const lineInfo = WORKFORCE_LINES.find((l) => l.id === lineId);
      const entry = makeAudit(
        actorEmail,
        `Marked all workers on ${lineInfo?.code ?? lineId} present for ${date} (${count} updated)`,
        `${date} তারিখে ${lineInfo?.nameBn ?? lineId}-এর সবাইকে উপস্থিত চিহ্নিত করা হয়েছে (${count} জন)`,
        lineId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        attendanceByDate: { ...prev.attendanceByDate, [date]: dayMap },
        attendanceEditedKeys: editedKeys,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        count,
        undo: () => commit(prev)
      };
    },

    markLineCoverForAbsent: (date, lineId, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const key = `${date}::${lineId}`;
      const entry = makeAudit(
        actorEmail,
        `Assigned float/helper cover for absent stations on ${lineId.toUpperCase()} (${date})`,
        `${lineId.toUpperCase()}-এ অনুপস্থিত স্টেশনের জন্য ফ্লোট/হেল্পার কভার নির্ধারণ করা হয়েছে (${date})`,
        lineId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        coveredLineDates: { ...prev.coveredLineDates, [key]: true },
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    decideLeaveRequest: (requestId, decision, note, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const target = prev.leaveRequests.find((r) => r.id === requestId);
      if (!target) return { undo: () => {} };

      const decidedAt = prev.anchorDate;
      const leaveRequests = prev.leaveRequests.map((r) =>
        r.id === requestId
          ? {
              ...r,
              status: decision,
              decidedBy: actorEmail,
              decidedAt,
              decisionNote: note?.trim() ? note.trim() : r.decisionNote,
              updatedByLocal: true
            }
          : r
      );

      // When approved, update worker's leaveUsedDays and status if active
      const workers = prev.workers.map((w) => {
        if (w.id !== target.workerId) return w;
        if (decision === "approved" && target.status !== "approved") {
          const curUsed = w.leaveUsedDays[target.type] ?? 0;
          return {
            ...w,
            leaveUsedDays: {
              ...w.leaveUsedDays,
              [target.type]: curUsed + target.days
            },
            updatedByLocal: true
          };
        }
        return w;
      });

      const entry = makeAudit(
        actorEmail,
        `${decision === "approved" ? "Approved" : "Rejected"} ${target.type} leave request ${requestId} for ${target.workerId} (${target.days}d)`,
        `${target.workerId}-এর ${target.days} দিনের ছুটি আবেদন (${requestId}) ${decision === "approved" ? "অনুমোদন" : "প্রত্যাখ্যান"} করা হয়েছে`,
        requestId
      );

      commit({
        ...prev,
        hasLocalEdits: true,
        leaveRequests,
        workers,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    createLeaveRequest: (input, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const worker = prev.workers.find((w) => w.id === input.workerId);
      if (!worker) return { ok: false, reasonCode: "worker_not_found" };

      const check = validateLeaveRequest(worker, input.type, input.days);
      if (!check.valid) {
        return { ok: false, reasonCode: check.reasonCode ?? "overdrawn" };
      }

      const id = `LR-${1000 + prev.leaveRequests.length + 1}`;
      const newReq = {
        id,
        workerId: input.workerId,
        type: input.type,
        startDate: input.startDate,
        endDate: input.endDate,
        days: input.days,
        reasonEn: input.reasonEn.trim(),
        reasonBn: input.reasonBn.trim() || input.reasonEn.trim(),
        status: "pending" as const,
        createdAt: prev.anchorDate,
        updatedByLocal: true
      };
      const entry = makeAudit(
        actorEmail,
        `Logged ${input.type} leave request ${id} for ${input.workerId} (${input.days}d)`,
        `${input.workerId}-এর জন্য ${input.days} দিনের ছুটির আবেদন (${id}) তৈরি করা হয়েছে`,
        id
      );

      commit({
        ...prev,
        hasLocalEdits: true,
        leaveRequests: [newReq, ...prev.leaveRequests],
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        ok: true,
        id,
        undo: () => commit(prev)
      };
    },

    addWorker: (input, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const maxNum = prev.workers.reduce((acc, w) => {
        const n = Number(w.id.replace(/\D/g, ""));
        return Number.isFinite(n) && n > acc ? n : acc;
      }, 120);
      const id = `W-${String(maxNum + 1).padStart(4, "0")}`;

      const skills: Record<MachineSkill, SkillProficiency> = {
        snls: 0,
        overlock: 0,
        flatlock: 0,
        buttonhole: 0,
        button_attach: 0,
        bartack: 0,
        iron_press: 0
      };
      if (input.primarySkill) {
        skills[input.primarySkill] = 2;
      } else if (input.role === "sewing_operator") {
        skills.snls = 2;
      }

      const lineInfo = WORKFORCE_LINES.find((l) => l.id === input.lineId);
      const targetEff = lineInfo?.targetEfficiencyPct ?? 76;
      const templateWeeks = prev.workers[0]?.performanceWeeks ?? [];
      const performanceWeeks = templateWeeks.map((tw) => ({
        weekStart: tw.weekStart,
        pieces: 185,
        smv: 11.5,
        minutesWorked: 2880,
        dhuPct: 1.6,
        targetEfficiencyPct: targetEff
      }));

      const newWorker: WorkerRecord = {
        id,
        nameEn: input.nameEn.trim(),
        nameBn: input.nameBn.trim() || input.nameEn.trim(),
        gender: input.gender,
        age: input.age,
        role: input.role,
        lineId: input.lineId,
        shift: input.shift,
        grade: input.grade,
        status: input.status,
        joinDate: input.joinDate,
        homeDistrictEn: input.homeDistrictEn,
        homeDistrictBn: input.homeDistrictBn || input.homeDistrictEn,
        residenceAreaEn: input.residenceAreaEn,
        residenceAreaBn: input.residenceAreaBn || input.residenceAreaEn,
        maskedPhone: input.maskedPhone || "017XX-XXXXXX",
        nidOnFile: input.nidOnFile,
        paymentChannel: input.paymentChannel,
        firstAidTrained: input.firstAidTrained,
        dayCareEligible: input.dayCareEligible,
        skills,
        weeklyHoursHistory: [48, 48, 50, 50],
        performanceWeeks,
        daysWorkedYtd: 90,
        leaveUsedDays: { casual: 0, sick: 0, annual: 0, maternity: 0, festival: 0 },
        updatedByLocal: true
      };

      const dayMap = { ...(prev.attendanceByDate[prev.anchorDate] ?? {}), [id]: "present" as const };
      const entry = makeAudit(
        actorEmail,
        `Added worker ${id} (${newWorker.nameEn}) to ${input.lineId.toUpperCase()}`,
        `${input.lineId.toUpperCase()}-এ নতুন কর্মী ${id} (${newWorker.nameBn}) যুক্ত করা হয়েছে`,
        id
      );

      commit({
        ...prev,
        hasLocalEdits: true,
        workers: [newWorker, ...prev.workers],
        attendanceByDate: { ...prev.attendanceByDate, [prev.anchorDate]: dayMap },
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        id,
        undo: () => commit(prev)
      };
    },

    updateWorker: (workerId, patch, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const workers = prev.workers.map((w) =>
        w.id === workerId ? { ...w, ...patch, updatedByLocal: true } : w
      );
      const entry = makeAudit(
        actorEmail,
        `Updated worker profile ${workerId}`,
        `কর্মী প্রোফাইল ${workerId} হালনাগাদ করা হয়েছে`,
        workerId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        workers,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    updateWorkerSkill: (workerId, skill, proficiency, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const workers = prev.workers.map((w) =>
        w.id === workerId
          ? {
              ...w,
              skills: { ...w.skills, [skill]: proficiency },
              updatedByLocal: true
            }
          : w
      );
      const entry = makeAudit(
        actorEmail,
        `Updated ${workerId} skill ${skill.toUpperCase()} to proficiency ${proficiency}/4`,
        `${workerId}-এর ${skill.toUpperCase()} দক্ষতা ${proficiency}/৪-এ উন্নীত করা হয়েছে`,
        workerId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        workers,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    adjustWorkerWeeklyHours: (workerId, newCurrentWeekHours, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const clamped = Math.max(0, Math.min(84, Math.round(newCurrentWeekHours)));
      const workers = prev.workers.map((w) => {
        if (w.id !== workerId) return w;
        const [w1, w2, w3] = w.weeklyHoursHistory;
        return {
          ...w,
          weeklyHoursHistory: [w1, w2, w3, clamped] as [number, number, number, number],
          updatedByLocal: true
        };
      });
      const entry = makeAudit(
        actorEmail,
        `Updated current weekly hours for ${workerId} to ${clamped} h`,
        `${workerId}-এর চলতি সপ্তাহের কর্মঘণ্টা ${clamped} ঘণ্টায় নির্ধারণ করা হয়েছে`,
        workerId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        workers,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    acknowledgeWorkloadAlert: (workerId, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const workers = prev.workers.map((w) => {
        if (w.id !== workerId) return w;
        const [w1, w2, w3] = w.weeklyHoursHistory;
        return {
          ...w,
          weeklyHoursHistory: [w1, w2, w3, 54] as [number, number, number, number],
          updatedByLocal: true
        };
      });
      const acked = prev.acknowledgedWorkloadWorkerIds.includes(workerId)
        ? prev.acknowledgedWorkloadWorkerIds
        : [...prev.acknowledgedWorkloadWorkerIds, workerId];
      const entry = makeAudit(
        actorEmail,
        `Capped weekly hours at 54 h for ${workerId} (cleared Workload alert)`,
        `${workerId}-এর সাপ্তাহিক কর্মঘণ্টা ৫৪ ঘণ্টায় সীমিত করা হয়েছে (ওয়ার্কলোড সতর্কতা নিষ্পত্তি)`,
        workerId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        workers,
        acknowledgedWorkloadWorkerIds: acked,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    applySuggestion: (suggestion, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      let workers = prev.workers;
      let trainingBatches = prev.trainingBatches;

      if (suggestion.kind === "move_helpers") {
        const moveSet = new Set(suggestion.workerIds);
        workers = prev.workers.map((w) =>
          moveSet.has(w.id)
            ? { ...w, lineId: suggestion.toLineId, updatedByLocal: true }
            : w
        );
      } else if (suggestion.kind === "cross_train" && suggestion.skill) {
        const targetBatch = prev.trainingBatches.find(
          (b) => b.lineId === suggestion.toLineId && b.skill === suggestion.skill
        );
        if (targetBatch) {
          trainingBatches = prev.trainingBatches.map((b) =>
            b.id === targetBatch.id
              ? {
                  ...b,
                  enrolledWorkerIds: Array.from(new Set([...b.enrolledWorkerIds, ...suggestion.workerIds])),
                  updatedByLocal: true
                }
              : b
          );
        }
        const trainSet = new Set(suggestion.workerIds);
        const sk = suggestion.skill;
        workers = prev.workers.map((w) =>
          trainSet.has(w.id)
            ? {
                ...w,
                skills: { ...w.skills, [sk]: Math.max(2, w.skills[sk] ?? 0) as SkillProficiency },
                updatedByLocal: true
              }
            : w
        );
      } else if (suggestion.kind === "cap_overtime") {
        const capSet = new Set(suggestion.workerIds);
        workers = prev.workers.map((w) => {
          if (!capSet.has(w.id)) return w;
          const [w1, w2, w3] = w.weeklyHoursHistory;
          return {
            ...w,
            weeklyHoursHistory: [w1, w2, w3, 56] as [number, number, number, number],
            updatedByLocal: true
          };
        });
      }

      const entry = makeAudit(
        actorEmail,
        `Applied rule-based suggestion: ${suggestion.titleEn}`,
        `নিয়ম-ভিত্তিক পরামর্শ প্রয়োগ করা হয়েছে: ${suggestion.titleBn}`,
        suggestion.id
      );

      commit({
        ...prev,
        hasLocalEdits: true,
        workers,
        trainingBatches,
        appliedSuggestionIds: Array.from(new Set([...prev.appliedSuggestionIds, suggestion.id])),
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    dismissSuggestion: (suggestionId, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const entry = makeAudit(
        actorEmail,
        `Dismissed suggestion ${suggestionId}`,
        `পরামর্শ (${suggestionId}) লুকানো হয়েছে`,
        suggestionId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        dismissedSuggestionIds: Array.from(new Set([...prev.dismissedSuggestionIds, suggestionId])),
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    createTrainingBatch: (input, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const id = `TB-${200 + prev.trainingBatches.length + 1}`;
      const newBatch = {
        id,
        titleEn: input.titleEn.trim(),
        titleBn: input.titleBn.trim() || input.titleEn.trim(),
        skill: input.skill,
        lineId: input.lineId,
        instructorEn: input.instructorEn.trim(),
        instructorBn: input.instructorBn.trim() || input.instructorEn.trim(),
        scheduledDate: input.scheduledDate,
        enrolledWorkerIds: input.enrolledWorkerIds,
        attendedWorkerIds: [],
        completedWorkerIds: [],
        updatedByLocal: true
      };
      const entry = makeAudit(
        actorEmail,
        `Created training batch ${id} (${newBatch.titleEn})`,
        `প্রশিক্ষণ ব্যাচ ${id} (${newBatch.titleBn}) তৈরি করা হয়েছে`,
        id
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        trainingBatches: [newBatch, ...prev.trainingBatches],
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        id,
        undo: () => commit(prev)
      };
    },

    enrollInTrainingBatch: (batchId, workerId, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const trainingBatches = prev.trainingBatches.map((b) =>
        b.id === batchId
          ? {
              ...b,
              enrolledWorkerIds: Array.from(new Set([...b.enrolledWorkerIds, workerId])),
              updatedByLocal: true
            }
          : b
      );
      const entry = makeAudit(
        actorEmail,
        `Enrolled ${workerId} in training batch ${batchId}`,
        `${workerId}-কে প্রশিক্ষণ ব্যাচ ${batchId}-এ নথিভুক্ত করা হয়েছে`,
        batchId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        trainingBatches,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    toggleTrainingAttendance: (batchId, workerId, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const trainingBatches = prev.trainingBatches.map((b) => {
        if (b.id !== batchId) return b;
        const has = b.attendedWorkerIds.includes(workerId);
        return {
          ...b,
          attendedWorkerIds: has
            ? b.attendedWorkerIds.filter((id) => id !== workerId)
            : [...b.attendedWorkerIds, workerId],
          updatedByLocal: true
        };
      });
      const entry = makeAudit(
        actorEmail,
        `Updated training attendance for ${workerId} in ${batchId}`,
        `${batchId}-এ ${workerId}-এর প্রশিক্ষণ উপস্থিতি হালনাগাদ করা হয়েছে`,
        batchId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        trainingBatches,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    markTrainingCompleted: (batchId, workerId, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const batch = prev.trainingBatches.find((b) => b.id === batchId);
      if (!batch) return { undo: () => {} };

      const trainingBatches = prev.trainingBatches.map((b) =>
        b.id === batchId
          ? {
              ...b,
              enrolledWorkerIds: Array.from(new Set([...b.enrolledWorkerIds, workerId])),
              attendedWorkerIds: Array.from(new Set([...b.attendedWorkerIds, workerId])),
              completedWorkerIds: Array.from(new Set([...b.completedWorkerIds, workerId])),
              updatedByLocal: true
            }
          : b
      );

      // Completing a training batch upgrades the worker's proficiency on that machine skill to at least 2 (qualified)
      const workers = prev.workers.map((w) => {
        if (w.id !== workerId) return w;
        const cur = w.skills[batch.skill] ?? 0;
        const nextProf = Math.min(4, Math.max(2, cur + 1)) as SkillProficiency;
        return {
          ...w,
          skills: { ...w.skills, [batch.skill]: nextProf },
          updatedByLocal: true
        };
      });

      const entry = makeAudit(
        actorEmail,
        `Marked ${workerId} completed on training ${batchId} (${batch.skill.toUpperCase()} qualified)`,
        `${workerId}-এর প্রশিক্ষণ ${batchId} সম্পন্ন হিসেবে চিহ্নিত হয়েছে (${batch.skill.toUpperCase()} দক্ষ)`,
        batchId
      );

      commit({
        ...prev,
        hasLocalEdits: true,
        trainingBatches,
        workers,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    addWorkerNote: (workerId, bodyEn, bodyBn, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const id = `WN-${300 + prev.workerNotes.length + 1}`;
      const newNote = {
        id,
        workerId,
        date: prev.anchorDate,
        authorEmail: actorEmail || "operator@bunonbrain.demo",
        bodyEn: bodyEn.trim(),
        bodyBn: bodyBn.trim() || bodyEn.trim(),
        updatedByLocal: true
      };
      const entry = makeAudit(
        actorEmail,
        `Added operational note on ${workerId}`,
        `${workerId}-এর প্রোফাইলে অপারেশনাল নোট যুক্ত করা হয়েছে`,
        workerId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        workerNotes: [newNote, ...prev.workerNotes],
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        id,
        undo: () => commit(prev)
      };
    },

    createGrievance: (input, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const num = 100 + prev.grievances.length + 1;
      const ticketNo = `GRV-${num}`;
      const id = ticketNo;
      const newTicket = {
        id,
        ticketNo,
        category: input.category,
        anonymous: input.anonymous,
        workerId: input.anonymous ? undefined : input.workerId,
        lineId: input.lineId,
        subjectEn: input.subjectEn.trim(),
        subjectBn: input.subjectBn.trim() || input.subjectEn.trim(),
        detailEn: input.detailEn.trim(),
        detailBn: input.detailBn.trim() || input.detailEn.trim(),
        status: "new" as const,
        createdAt: prev.anchorDate,
        updatedAt: prev.anchorDate,
        updatedByLocal: true
      };
      const entry = makeAudit(
        input.anonymous ? "anonymous-box" : actorEmail,
        `Submitted grievance ${ticketNo} (${input.category})`,
        `অভিযোগ ${ticketNo} (${input.category}) জমা দেওয়া হয়েছে`,
        ticketNo
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        grievances: [newTicket, ...prev.grievances],
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        id,
        ticketNo,
        undo: () => commit(prev)
      };
    },

    updateGrievanceStatus: (grievanceId, status, resolutionNote, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const grievances = prev.grievances.map((g) =>
        g.id === grievanceId
          ? {
              ...g,
              status,
              updatedAt: prev.anchorDate,
              resolutionNote: resolutionNote?.trim() ? resolutionNote.trim() : g.resolutionNote,
              updatedByLocal: true
            }
          : g
      );
      const entry = makeAudit(
        actorEmail,
        `Updated grievance ${grievanceId} status to ${status}`,
        `অভিযোগ ${grievanceId}-এর অবস্থা ${status}-এ পরিবর্তন করা হয়েছে`,
        grievanceId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        grievances,
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    submitPulseSurvey: (input) => {
      const prev = toPersistedSnapshot(get());
      const id = `PLS-${prev.wellbeing.pulseResponses.length + 1}-${Date.now().toString(36).slice(-3)}`;
      const newResp = {
        id,
        lineId: input.lineId,
        weekIso: prev.anchorDate,
        q1PaceManageable: input.q1PaceManageable,
        q2ToolsAndCoolingOk: input.q2ToolsAndCoolingOk,
        q3RespectfulSupport: input.q3RespectfulSupport,
        submittedAt: prev.anchorDate,
        createdByLocal: true
      };
      // Anonymous submission: never records an actor email or workerId!
      commit({
        ...prev,
        hasLocalEdits: true,
        wellbeing: {
          ...prev.wellbeing,
          pulseResponses: [newResp, ...prev.wellbeing.pulseResponses]
        }
      });
      return {
        id,
        undo: () => commit(prev)
      };
    },

    createCounsellorReferral: (input, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const num = 200 + prev.wellbeing.counsellorReferrals.length + 1;
      const caseCode = `REF-${num}`;
      const newRef = {
        id: caseCode,
        caseCode,
        lineId: input.lineId,
        counsellorNameEn: input.counsellorNameEn,
        counsellorNameBn: input.counsellorNameBn || input.counsellorNameEn,
        requestedDate: prev.anchorDate,
        scheduledDate: input.scheduledDate,
        status: input.scheduledDate ? ("scheduled" as const) : ("requested" as const),
        updatedByLocal: true
      };
      const entry = makeAudit(
        actorEmail,
        `Logged voluntary counsellor referral ${caseCode} (metadata only)`,
        `ঐচ্ছিক কাউন্সেলর রেফারেল ${caseCode} নথিভুক্ত হয়েছে (শুধু মেটাডেটা)`,
        caseCode
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        wellbeing: {
          ...prev.wellbeing,
          counsellorReferrals: [newRef, ...prev.wellbeing.counsellorReferrals]
        },
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        id: caseCode,
        caseCode,
        undo: () => commit(prev)
      };
    },

    updateCounsellorReferralStatus: (referralId, status, scheduledDate, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const counsellorReferrals = prev.wellbeing.counsellorReferrals.map((r) =>
        r.id === referralId
          ? {
              ...r,
              status,
              scheduledDate: scheduledDate || r.scheduledDate,
              updatedByLocal: true
            }
          : r
      );
      const entry = makeAudit(
        actorEmail,
        `Updated referral ${referralId} status to ${status}`,
        `রেফারেল ${referralId}-এর অবস্থা ${status}-এ হালনাগাদ করা হয়েছে`,
        referralId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        wellbeing: {
          ...prev.wellbeing,
          counsellorReferrals
        },
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    },

    verifySafetyItem: (itemId, actorEmail) => {
      const prev = toPersistedSnapshot(get());
      const safetyChecklist = prev.wellbeing.safetyChecklist.map((s) =>
        s.id === itemId
          ? {
              ...s,
              status: "verified" as const,
              lastCheckedDate: prev.anchorDate,
              updatedByLocal: true
            }
          : s
      );
      const entry = makeAudit(
        actorEmail,
        `Marked safety checklist item ${itemId} as verified`,
        `নিরাপত্তা চেকলিস্ট আইটেম ${itemId} যাচাইকৃত হিসেবে চিহ্নিত করা হয়েছে`,
        itemId
      );
      commit({
        ...prev,
        hasLocalEdits: true,
        wellbeing: {
          ...prev.wellbeing,
          safetyChecklist
        },
        auditTrail: [entry, ...prev.auditTrail].slice(0, 60)
      });
      return {
        undo: () => commit(prev)
      };
    }
  };
});
