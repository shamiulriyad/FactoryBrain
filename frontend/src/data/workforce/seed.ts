import { makeRng, intBetween, pick } from "@/data/seed";
import type { WorkforceLineId } from "./lines";
import type { SkillGrade } from "./policy";
import {
  MACHINE_SKILLS,
  type AttendanceStatus,
  type CounsellorReferral,
  type GrievanceTicket,
  type LeaveRequest,
  type LeaveType,
  type MachineSkill,
  type PaymentChannel,
  type PulseSurveyResponse,
  type SafetyChecklistItem,
  type SkillProficiency,
  type TrainingBatch,
  type WorkerNote,
  type WorkerRecord,
  type WorkerRole,
  type WorkerShift,
  type WorkerWeeklyPerformance,
  type WorkforcePersistedData
} from "./types";

const FEMALE_NAMES: ReadonlyArray<{ en: string; bn: string }> = [
  { en: "Rahima Khatun", bn: "রহিমা খাতুন" },
  { en: "Fatema Akter", bn: "ফাতেমা আক্তার" },
  { en: "Nasrin Sultana", bn: "নাসরিন সুলতানা" },
  { en: "Salma Begum", bn: "সালমা বেগম" },
  { en: "Rina Parvin", bn: "রিনা পারভীন" },
  { en: "Rozina Akter", bn: "রোজিনা আক্তার" },
  { en: "Taslima Begum", bn: "তাসলিমা বেগম" },
  { en: "Sharmin Akter", bn: "শারমিন আক্তার" },
  { en: "Moushumi Khatun", bn: "মৌসুমী খাতুন" },
  { en: "Jahanara Begum", bn: "জাহানারা বেগম" },
  { en: "Parul Akter", bn: "পারুল আক্তার" },
  { en: "Shirin Sultana", bn: "শিরিন সুলতানা" },
  { en: "Amina Khatun", bn: "আমিনা খাতুন" },
  { en: "Kulsum Begum", bn: "কুলসুম বেগম" },
  { en: "Halima Akter", bn: "হালিমা আক্তার" },
  { en: "Sufia Khatun", bn: "সুফিয়া খাতুন" },
  { en: "Beauty Akter", bn: "বিউটি আক্তার" },
  { en: "Lipa Rani", bn: "লিপা রানী" },
  { en: "Sumi Akter", bn: "সুমি আক্তার" },
  { en: "Roksana Parvin", bn: "রোকসানা পারভীন" },
  { en: "Monwara Begum", bn: "মনোয়ারা বেগম" },
  { en: "Nargis Akter", bn: "নার্গিস আক্তার" },
  { en: "Jesmin Sultana", bn: "জেসমিন সুলতানা" },
  { en: "Asma Khatun", bn: "আসমা খাতুন" },
  { en: "Rekha Rani", bn: "রেখা রানী" },
  { en: "Bilkis Begum", bn: "বিলকিস বেগম" },
  { en: "Shahnaz Parvin", bn: "শাহনাজ পারভীন" },
  { en: "Farida Yasmin", bn: "ফরিদা ইয়াসমিন" },
  { en: "Tania Akter", bn: "তানিয়া আক্তার" },
  { en: "Mita Khatun", bn: "মিতা খাতুন" }
];

const MALE_NAMES: ReadonlyArray<{ en: string; bn: string }> = [
  { en: "Md. Sumon Mia", bn: "মো. সুমন মিয়া" },
  { en: "Jahidul Islam", bn: "জাহিদুল ইসলাম" },
  { en: "Kamrul Hasan", bn: "কামরুল হাসান" },
  { en: "Md. Liton Sheikh", bn: "মো. লিটন শেখ" },
  { en: "Alamin Hossain", bn: "আল-আমিন হোসেন" },
  { en: "Rafiqul Islam", bn: "রফিকুল ইসলাম" },
  { en: "Md. Sohel Rana", bn: "মো. সোহেল রানা" },
  { en: "Mizanur Rahman", bn: "মিজানুর রহমান" },
  { en: "Rubel Mia", bn: "রুবেল মিয়া" },
  { en: "Faruk Ahmed", bn: "ফারুক আহমেদ" },
  { en: "Shahin Alam", bn: "শাহীন আলম" },
  { en: "Anwar Hossain", bn: "আনোয়ার হোসেন" },
  { en: "Babul Chandra", bn: "বাবুল চন্দ্র" },
  { en: "Monirul Islam", bn: "মনিরুল ইসলাম" },
  { en: "Habibur Rahman", bn: "হাবিবুর রহমান" }
];

const HOME_DISTRICTS: ReadonlyArray<{ en: string; bn: string }> = [
  { en: "Gazipur", bn: "গাজীপুর" },
  { en: "Bhola", bn: "ভোলা" },
  { en: "Barishal", bn: "বরিশাল" },
  { en: "Kurigram", bn: "কুড়িগ্রাম" },
  { en: "Mymensingh", bn: "ময়মনসিংহ" },
  { en: "Rangpur", bn: "রংপুর" },
  { en: "Noakhali", bn: "নোয়াখালী" },
  { en: "Jamalpur", bn: "জামালপুর" }
];

const RESIDENCE_AREAS: ReadonlyArray<{ en: string; bn: string }> = [
  { en: "Ashulia", bn: "আশুলিয়া" },
  { en: "Savar", bn: "সাভার" },
  { en: "Tongi", bn: "টঙ্গী" },
  { en: "Narayanganj", bn: "নারায়ণগঞ্জ" },
  { en: "Gazipur Sadar", bn: "গাজীপুর সদর" }
];

const MASKED_PHONES = [
  "017XX-XXXXXX",
  "018XX-XXXXXX",
  "019XX-XXXXXX",
  "016XX-XXXXXX",
  "013XX-XXXXXX"
] as const;

const PAYMENT_OPTS: readonly PaymentChannel[] = ["bKash", "Nagad", "Bank"];

/** Parse YYYY-MM-DD as UTC midnight and add `deltaDays`, returning YYYY-MM-DD. */
export function addIsoDays(dateIso: string, deltaDays: number): string {
  const [y, m, d] = dateIso.split("-").map(Number);
  const dt = new Date(Date.UTC(y || 2026, (m || 1) - 1, d || 1));
  dt.setUTCDate(dt.getUTCDate() + deltaDays);
  return dt.toISOString().slice(0, 10);
}

/** True when `dateIso` (YYYY-MM-DD) falls on Friday (weekly RMG holiday). */
export function isFridayHoliday(dateIso: string): boolean {
  const [y, m, d] = dateIso.split("-").map(Number);
  const dt = new Date(Date.UTC(y || 2026, (m || 1) - 1, d || 1));
  return dt.getUTCDay() === 5;
}

function capFirst(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

const LINE_ALLOCATION: ReadonlyArray<{ lineId: WorkforceLineId; count: number }> = [
  { lineId: "line-1", count: 20 },
  { lineId: "line-2", count: 20 },
  { lineId: "line-3", count: 20 }, // weak spot: high absenteeism, low efficiency, 1 helper
  { lineId: "line-4", count: 18 },
  { lineId: "line-5", count: 24 }, // over-staffed with 6 cross-skilled helpers
  { lineId: "line-6", count: 18 }
];

function roleAndGradeForSlot(
  lineId: WorkforceLineId,
  slotInLine: number,
  rng: () => number
): { role: WorkerRole; grade: SkillGrade } {
  if (slotInLine === 0) return { role: "line_chief", grade: 1 };
  if (slotInLine === 1) return { role: "line_supervisor", grade: 2 };
  if (slotInLine === 2) return { role: "quality_inspector", grade: 3 };
  if (slotInLine === 3) return { role: "mechanic", grade: 3 };

  // Line 3 has only 1 helper (slot 4) — creating the helper shortage storyline
  if (lineId === "line-3") {
    if (slotInLine === 4) return { role: "helper", grade: 7 };
    if (slotInLine === 5) return { role: "cutter", grade: 4 };
    if (slotInLine === 6) return { role: "iron_man", grade: 6 };
    if (slotInLine === 7) return { role: "finishing_packing", grade: 6 };
    const opGrade = ([4, 5, 5, 6] as const)[slotInLine % 4];
    return { role: "sewing_operator", grade: opGrade };
  }

  // Line 5 has 6 helpers (slots 4..9), cross-skilled so 2 can move to Line 3
  if (lineId === "line-5") {
    if (slotInLine >= 4 && slotInLine <= 9) return { role: "helper", grade: 7 };
    if (slotInLine === 10) return { role: "cutter", grade: 4 };
    if (slotInLine === 11) return { role: "iron_man", grade: 6 };
    if (slotInLine === 12) return { role: "finishing_packing", grade: 6 };
    const opGrade = ([3, 4, 5, 6] as const)[slotInLine % 4];
    return { role: "sewing_operator", grade: opGrade };
  }

  // Other lines have 3 helpers (slots 4..6)
  if (slotInLine >= 4 && slotInLine <= 6) return { role: "helper", grade: 7 };
  if (slotInLine === 7) return { role: "cutter", grade: 4 };
  if (slotInLine === 8) return { role: "iron_man", grade: 6 };
  if (slotInLine === 9) return { role: "finishing_packing", grade: 6 };
  const g = ([3, 4, 5, 5, 6] as const)[Math.floor(rng() * 5)];
  return { role: "sewing_operator", grade: g };
}

function buildSkillMap(
  lineId: WorkforceLineId,
  role: WorkerRole,
  slotInLine: number,
  rng: () => number
): Record<MachineSkill, SkillProficiency> {
  const map: Record<MachineSkill, SkillProficiency> = {
    snls: 0,
    overlock: 0,
    flatlock: 0,
    buttonhole: 0,
    button_attach: 0,
    bartack: 0,
    iron_press: 0
  };

  if (role === "iron_man" || role === "finishing_packing") {
    map.iron_press = 3;
    map.button_attach = rng() > 0.5 ? 2 : 1;
    return map;
  }

  if (role === "helper") {
    // Line 5 helpers are cross-trained in SNLS and Overlock (proficiency 2-3)
    if (lineId === "line-5") {
      map.snls = slotInLine % 2 === 0 ? 3 : 2;
      map.overlock = 2;
      map.button_attach = 2;
      map.iron_press = 1;
      return map;
    }
    map.snls = rng() > 0.45 ? 1 : 0;
    map.iron_press = 2;
    map.button_attach = 1;
    return map;
  }

  if (role === "sewing_operator" || role === "line_supervisor" || role === "line_chief") {
    map.snls = role === "line_chief" ? 4 : (intBetween(rng, 2, 4) as SkillProficiency);
    map.overlock = intBetween(rng, 1, 3) as SkillProficiency;
    map.flatlock = rng() > 0.35 ? (intBetween(rng, 2, 3) as SkillProficiency) : 1;
    // Line 3 has a buttonhole / bartack bottleneck in skill coverage
    if (lineId === "line-3") {
      map.buttonhole = slotInLine === 8 ? 2 : 0;
      map.bartack = slotInLine === 9 ? 2 : 0;
    } else {
      map.buttonhole = rng() > 0.55 ? (intBetween(rng, 2, 3) as SkillProficiency) : 0;
      map.bartack = rng() > 0.55 ? (intBetween(rng, 2, 3) as SkillProficiency) : 0;
    }
    map.button_attach = rng() > 0.4 ? (intBetween(rng, 2, 3) as SkillProficiency) : 1;
    return map;
  }

  if (role === "mechanic") {
    for (const k of MACHINE_SKILLS) map[k] = 3;
    return map;
  }

  if (role === "quality_inspector" || role === "cutter") {
    map.snls = 2;
    map.overlock = 2;
    return map;
  }

  return map;
}

export function getDefaultAnchorDate(): string {
  if (typeof window === "undefined") return "2026-10-25";
  const now = new Date();
  const y = now.getFullYear();
  const m = String(now.getMonth() + 1).padStart(2, "0");
  const d = String(now.getDate()).padStart(2, "0");
  return `${y}-${m}-${d}`;
}

export function buildWorkforceSeed(anchorDate: string = getDefaultAnchorDate()): WorkforcePersistedData {
  const rng = makeRng(0xB090_4210);
  const workers: WorkerRecord[] = [];

  let workerSeq = 0;
  for (const alloc of LINE_ALLOCATION) {
    for (let slot = 0; slot < alloc.count; slot++) {
      workerSeq++;
      const id = `W-${String(workerSeq).padStart(4, "0")}`;
      const { role, grade } = roleAndGradeForSlot(alloc.lineId, slot, rng);

      // ~75% women across the plant (mechanics/iron_man skew male, sewing/helpers skew female)
      const isFemale =
        role === "mechanic" || role === "iron_man"
          ? rng() < 0.25
          : rng() < 0.8;
      const gender: "female" | "male" = isFemale ? "female" : "male";

      const nameEntry = isFemale
        ? FEMALE_NAMES[(workerSeq + slot) % FEMALE_NAMES.length]
        : MALE_NAMES[(workerSeq + slot) % MALE_NAMES.length];
      // Add suffix when name repeats so workers are easy to distinguish
      const nameEn = capFirst(nameEntry.en);
      const nameBn = nameEntry.bn;

      const age = intBetween(rng, 19, 43);
      const district = pick(rng, HOME_DISTRICTS);
      const residence = pick(rng, RESIDENCE_AREAS);
      const maskedPhone = pick(rng, MASKED_PHONES);
      const paymentChannel: PaymentChannel =
        workerSeq % 5 === 0 ? "Bank" : workerSeq % 2 === 0 ? "bKash" : "Nagad";
      const shift: WorkerShift = slot < Math.ceil(alloc.count * 0.75) ? "A" : "B";

      // Join date 4 months to 6 years before anchorDate
      const tenureDays = intBetween(rng, 120, 2100);
      const joinDate = addIsoDays(anchorDate, -tenureDays);

      // Status: almost all active, a few on_leave, 2 resigned
      let status: WorkerRecord["status"] = "active";
      if (workerSeq === 47 || workerSeq === 104) status = "resigned";
      else if (workerSeq === 14 || workerSeq === 52 || workerSeq === 89) status = "on_leave";

      // Weekly hours (last 4 weeks, oldest -> current week)
      // Line 3 has 5 workers with > 60h/week for 3 consecutive weeks (workload alert)
      let weeklyHoursHistory: [number, number, number, number];
      if (alloc.lineId === "line-3" && slot >= 8 && slot <= 12) {
        weeklyHoursHistory = [
          58 + (slot % 3),
          62 + (slot % 4),
          63 + (slot % 3),
          64 + (slot % 3)
        ];
      } else if (alloc.lineId === "line-3" && slot >= 13 && slot <= 15) {
        // Current week > 60h cap warning, but not 3 consecutive weeks
        weeklyHoursHistory = [52, 56, 58, 62];
      } else if (alloc.lineId === "line-2" && slot === 10) {
        // One worker on Line 2 also has a 3-week workload alert
        weeklyHoursHistory = [61, 62, 63, 62];
      } else {
        const baseH = alloc.lineId === "line-3" ? 56 : alloc.lineId === "line-5" ? 49 : 52;
        weeklyHoursHistory = [
          baseH + intBetween(rng, -2, 3),
          baseH + intBetween(rng, -2, 3),
          baseH + intBetween(rng, -2, 3),
          baseH + intBetween(rng, -2, 4)
        ];
      }

      // 8 weeks of performance records
      const targetEfficiencyPct =
        alloc.lineId === "line-4" ? 75 : alloc.lineId === "line-5" ? 76 : alloc.lineId === "line-6" ? 74 : 78;
      const smv =
        alloc.lineId === "line-1"
          ? 12.5
          : alloc.lineId === "line-2"
          ? 10.0
          : alloc.lineId === "line-3"
          ? 11.2
          : alloc.lineId === "line-4"
          ? 18.4
          : alloc.lineId === "line-5"
          ? 8.5
          : 22.0;

      const performanceWeeks: WorkerWeeklyPerformance[] = [];
      for (let w = 7; w >= 0; w--) {
        const weekStart = addIsoDays(anchorDate, -w * 7);
        const minutesWorked = 2880; // 48h standard week in minutes
        const baseEff =
          alloc.lineId === "line-3"
            ? 0.61 + rng() * 0.12 // Line 3 lags behind target (61-73%)
            : alloc.lineId === "line-5"
            ? 0.78 + rng() * 0.11
            : 0.73 + rng() * 0.14;
        const pieces = Math.max(40, Math.round((minutesWorked * baseEff) / smv));
        const dhuPct =
          alloc.lineId === "line-3"
            ? Math.round((2.4 + rng() * 1.8) * 10) / 10
            : Math.round((1.1 + rng() * 1.3) * 10) / 10;
        performanceWeeks.push({
          weekStart,
          pieces,
          smv,
          minutesWorked,
          dhuPct,
          targetEfficiencyPct
        });
      }

      const daysWorkedYtd = intBetween(rng, 144, 234);
      const leaveUsedDays: Record<LeaveType, number> = {
        casual: intBetween(rng, 1, 5),
        sick: intBetween(rng, 0, 4),
        annual: intBetween(rng, 0, 4),
        maternity: status === "on_leave" && isFemale && workerSeq === 52 ? 56 : 0,
        festival: 6
      };

      workers.push({
        id,
        nameEn,
        nameBn,
        gender,
        age,
        role,
        lineId: alloc.lineId,
        shift,
        grade,
        status,
        joinDate,
        homeDistrictEn: district.en,
        homeDistrictBn: district.bn,
        residenceAreaEn: residence.en,
        residenceAreaBn: residence.bn,
        maskedPhone,
        nidOnFile: workerSeq % 17 !== 0,
        paymentChannel,
        firstAidTrained: slot === 1 || slot === 2 || workerSeq % 9 === 0,
        dayCareEligible: isFemale && age >= 20 && age <= 34 && workerSeq % 3 === 0,
        skills: buildSkillMap(alloc.lineId, role, slot, rng),
        weeklyHoursHistory,
        performanceWeeks,
        daysWorkedYtd,
        leaveUsedDays
      });
    }
  }

  // 60 days of attendance history ending on anchorDate
  const attendanceByDate: Record<string, Record<string, AttendanceStatus>> = {};
  for (let d = 59; d >= 0; d--) {
    const dateIso = addIsoDays(anchorDate, -d);
    const dayMap: Record<string, AttendanceStatus> = {};
    const friday = isFridayHoliday(dateIso);

    for (let idx = 0; idx < workers.length; idx++) {
      const w = workers[idx];
      if (w.status === "resigned" && d < 10) {
        dayMap[w.id] = "absent";
        continue;
      }
      if (friday) {
        dayMap[w.id] = "holiday";
        continue;
      }
      if (w.status === "on_leave" && d < 7) {
        dayMap[w.id] = "leave";
        continue;
      }

      // Deterministic daily attendance with Line 3 having higher absenteeism
      const dayRng = makeRng((0xabc000 + d * 997 + idx * 31) >>> 0);
      const roll = dayRng();

      if (d === 0) {
        // Explicit deterministic setup on anchorDate ("today") so KPIs tell a clear story
        if (w.lineId === "line-3") {
          if (idx % 5 === 0) dayMap[w.id] = "absent";
          else if (idx % 7 === 0) dayMap[w.id] = "late";
          else if (idx % 13 === 0) dayMap[w.id] = "leave";
          else dayMap[w.id] = "present";
        } else if (w.lineId === "line-5") {
          if (idx % 19 === 0) dayMap[w.id] = "late";
          else dayMap[w.id] = "present";
        } else {
          if (idx % 16 === 0) dayMap[w.id] = "absent";
          else if (idx % 11 === 0) dayMap[w.id] = "late";
          else if (idx % 23 === 0) dayMap[w.id] = "half_day";
          else dayMap[w.id] = "present";
        }
      } else {
        const absentThresh = w.lineId === "line-3" ? 0.14 : w.lineId === "line-5" ? 0.03 : 0.06;
        const lateThresh = absentThresh + (w.lineId === "line-3" ? 0.08 : 0.04);
        const leaveThresh = lateThresh + 0.03;
        const halfDayThresh = leaveThresh + 0.015;
        if (roll < absentThresh) dayMap[w.id] = "absent";
        else if (roll < lateThresh) dayMap[w.id] = "late";
        else if (roll < leaveThresh) dayMap[w.id] = "leave";
        else if (roll < halfDayThresh) dayMap[w.id] = "half_day";
        else dayMap[w.id] = "present";
      }
    }
    attendanceByDate[dateIso] = dayMap;
  }

  const leaveRequests: LeaveRequest[] = [
    {
      id: "LR-1001",
      workerId: "W-0045", // Line 3
      type: "sick",
      startDate: addIsoDays(anchorDate, 1),
      endDate: addIsoDays(anchorDate, 3),
      days: 3,
      reasonEn: "Fever and clinic visit in Savar",
      reasonBn: "জ্বর এবং সাভারে ক্লিনিকে ডাক্তার দেখানো",
      status: "pending",
      createdAt: addIsoDays(anchorDate, -1)
    },
    {
      id: "LR-1002",
      workerId: "W-0006", // Line 1
      type: "casual",
      startDate: addIsoDays(anchorDate, 2),
      endDate: addIsoDays(anchorDate, 3),
      days: 2,
      reasonEn: "Family land registration in Gazipur",
      reasonBn: "গাজীপুরে পারিবারিক জমি রেজিস্ট্রেশন",
      status: "pending",
      createdAt: addIsoDays(anchorDate, -1)
    },
    {
      id: "LR-1003",
      workerId: "W-0028", // Line 2
      type: "annual",
      startDate: addIsoDays(anchorDate, 4),
      endDate: addIsoDays(anchorDate, 7),
      days: 4,
      reasonEn: "Visiting parents in Barishal",
      reasonBn: "বরিশালে বাবা-মায়ের সাথে দেখা করতে যাওয়া",
      status: "pending",
      createdAt: addIsoDays(anchorDate, -2)
    },
    {
      id: "LR-1004",
      workerId: "W-0068", // Line 4
      type: "casual",
      startDate: addIsoDays(anchorDate, 1),
      endDate: addIsoDays(anchorDate, 2),
      days: 2,
      reasonEn: "Child school admission interview",
      reasonBn: "সন্তানের স্কুলে ভর্তি সাক্ষাৎকার",
      status: "pending",
      createdAt: addIsoDays(anchorDate, 0)
    },
    {
      id: "LR-1005",
      workerId: "W-0052", // Line 3 maternity
      type: "maternity",
      startDate: addIsoDays(anchorDate, -14),
      endDate: addIsoDays(anchorDate, 98),
      days: 56,
      reasonEn: "Statutory 16-week maternity leave (pre + post delivery)",
      reasonBn: "আইনানুগ ১৬ সপ্তাহের মাতৃত্বকালীন ছুটি (প্রসব-পূর্ব ও পরবর্তী)",
      status: "approved",
      decidedBy: "operator@bunonbrain.demo",
      decidedAt: addIsoDays(anchorDate, -16),
      decisionNote: "Verified clinic certificate; maternity benefit scheduled.",
      createdAt: addIsoDays(anchorDate, -20)
    },
    {
      id: "LR-1006",
      workerId: "W-0083", // Line 5
      type: "casual",
      startDate: addIsoDays(anchorDate, -6),
      endDate: addIsoDays(anchorDate, -5),
      days: 2,
      reasonEn: "Sister's wedding ceremony",
      reasonBn: "বোনের বিবাহ অনুষ্ঠান",
      status: "approved",
      decidedBy: "operator@bunonbrain.demo",
      decidedAt: addIsoDays(anchorDate, -8),
      decisionNote: "Line 5 has helper cover available.",
      createdAt: addIsoDays(anchorDate, -9)
    },
    {
      id: "LR-1007",
      workerId: "W-0049", // Line 3
      type: "casual",
      startDate: addIsoDays(anchorDate, -4),
      endDate: addIsoDays(anchorDate, -2),
      days: 3,
      reasonEn: "Personal trip during peak V-Neck shipment week",
      reasonBn: "ভি-নেক শিপমেন্ট সপ্তাহে ব্যক্তিগত সফর",
      status: "rejected",
      decidedBy: "operator@bunonbrain.demo",
      decidedAt: addIsoDays(anchorDate, -5),
      decisionNote: "Rescheduled to next week due to Line 3 bottleneck.",
      createdAt: addIsoDays(anchorDate, -6)
    }
  ];

  const trainingBatches: TrainingBatch[] = [
    {
      id: "TB-201",
      titleEn: "Line 5 → Line 3 Overlock & Buttonhole Cross-Training",
      titleBn: "লাইন ৫ → লাইন ৩ ওভারলক ও বাটনহোল ক্রস-ট্রেনিং",
      skill: "buttonhole",
      lineId: "line-3",
      instructorEn: "Master Mechanic Faruk Ahmed",
      instructorBn: "মাস্টার মেকানিক ফারুক আহমেদ",
      scheduledDate: addIsoDays(anchorDate, 2),
      enrolledWorkerIds: ["W-0045", "W-0046", "W-0083", "W-0084"],
      attendedWorkerIds: ["W-0045", "W-0083"],
      completedWorkerIds: ["W-0083"]
    },
    {
      id: "TB-202",
      titleEn: "Flatlock Seam Tension & Zero-Skip Workshop",
      titleBn: "ফ্ল্যাটলক সিম টেনশন ও জিরো-স্কিপ কর্মশালা",
      skill: "flatlock",
      lineId: "line-2",
      instructorEn: "Line Chief Rahima Khatun",
      instructorBn: "লাইন চিফ রহিমা খাতুন",
      scheduledDate: addIsoDays(anchorDate, -5),
      enrolledWorkerIds: ["W-0025", "W-0026", "W-0027"],
      attendedWorkerIds: ["W-0025", "W-0026", "W-0027"],
      completedWorkerIds: ["W-0025", "W-0026"]
    },
    {
      id: "TB-203",
      titleEn: "Bartack Pocket Reinforcement Qualification",
      titleBn: "বারট্যাক পকেট রিইনফোর্সমেন্ট দক্ষতা প্রশিক্ষণ",
      skill: "bartack",
      lineId: "line-6",
      instructorEn: "QC Lead Nasrin Sultana",
      instructorBn: "কিউসি লিড নাসরিন সুলতানা",
      scheduledDate: addIsoDays(anchorDate, 5),
      enrolledWorkerIds: ["W-0105", "W-0106", "W-0107"],
      attendedWorkerIds: [],
      completedWorkerIds: []
    }
  ];

  const workerNotes: WorkerNote[] = [
    {
      id: "WN-301",
      workerId: "W-0001",
      date: addIsoDays(anchorDate, -10),
      authorEmail: "operator@bunonbrain.demo",
      bodyEn: "Led Line 1 changeover from crew neck to polo placket 25 minutes ahead of target.",
      bodyBn: "নির্ধারিত সময়ের ২৫ মিনিট আগেই লাইন ১-এর পোলো প্ল্যাকেট চেঞ্জওভার সম্পন্ন করেছেন।"
    },
    {
      id: "WN-302",
      workerId: "W-0045",
      date: addIsoDays(anchorDate, -4),
      authorEmail: "operator@bunonbrain.demo",
      bodyEn: "Requested transfer to shift A after completing buttonhole module.",
      bodyBn: "বাটনহোল মডিউল শেষ করার পর শিফট এ-তে বদলির অনুরোধ করেছেন।"
    },
    {
      id: "WN-303",
      workerId: "W-0083",
      date: addIsoDays(anchorDate, -2),
      authorEmail: "operator@bunonbrain.demo",
      bodyEn: "Qualified on both SNLS and Overlock; ready to cover Line 3 helper/operator gap.",
      bodyBn: "এসএনএলএস এবং ওভারলক উভয় মেশিনে দক্ষ; লাইন ৩-এর ঘাটতি পূরণে প্রস্তুত।"
    }
  ];

  const grievances: GrievanceTicket[] = [
    {
      id: "GRV-101",
      ticketNo: "GRV-101",
      category: "overtime",
      anonymous: false,
      workerId: "W-0049",
      lineId: "line-3",
      subjectEn: "Consecutive late-evening overtime on Line 3 V-Neck order",
      subjectBn: "লাইন ৩ ভি-নেক অর্ডারে টানা সন্ধ্যা-পরবর্তী ওভারটাইম",
      detailEn: "Operators on Line 3 have worked above 60 hours/week for three weeks due to helper shortage.",
      detailBn: "হেল্পার ঘাটতির কারণে লাইন ৩-এর অপারেটররা টানা তিন সপ্তাহ ৬০ ঘণ্টার বেশি কাজ করছেন।",
      status: "new",
      createdAt: addIsoDays(anchorDate, -3),
      updatedAt: addIsoDays(anchorDate, -3)
    },
    {
      id: "GRV-102",
      ticketNo: "GRV-102",
      category: "safety",
      anonymous: true,
      lineId: "line-3",
      subjectEn: "Exhaust fan #2 near sewing bay making loud rattling noise",
      subjectBn: "সেলাই বে-র কাছে এক্সজস্ট ফ্যান #২ থেকে জোরে শব্দ হচ্ছে",
      detailEn: "Ventilation drops in the afternoon near machines 8–12 on Line 3.",
      detailBn: "দুপুরের পর লাইন ৩-এর ৮–১২ নম্বর মেশিনের কাছে বাতাস চলাচল কমে যায়।",
      status: "investigating",
      createdAt: addIsoDays(anchorDate, -6),
      updatedAt: addIsoDays(anchorDate, -2)
    },
    {
      id: "GRV-103",
      ticketNo: "GRV-103",
      category: "wages",
      anonymous: false,
      workerId: "W-0012",
      lineId: "line-1",
      subjectEn: "bKash overtime payout slip missing 2 hours from last Saturday",
      subjectBn: "গত শনিবারের বিকাশ ওভারটাইম স্লিপে ২ ঘণ্টা কম এসেছে",
      detailEn: "Timecard shows 10 hours on Saturday; slip recorded 8 hours.",
      detailBn: "টাইমকার্ডে শনিবার ১০ ঘণ্টা থাকলেও স্লিপে ৮ ঘণ্টা লেখা হয়েছে।",
      status: "acknowledged",
      createdAt: addIsoDays(anchorDate, -4),
      updatedAt: addIsoDays(anchorDate, -1)
    },
    {
      id: "GRV-104",
      ticketNo: "GRV-104",
      category: "behaviour",
      anonymous: true,
      lineId: "line-4",
      subjectEn: "Harsh verbal tone during bundle target check",
      subjectBn: "বান্ডিল লক্ষ্যমাত্রা যাচাইয়ের সময় রূঢ় আচরণ",
      detailEn: "Anonymous submission requesting welfare officer mediation on afternoon target calls.",
      detailBn: "বিকেলের টার্গেট কলের সময় ওয়েলফেয়ার অফিসারের মধ্যস্থতা চেয়ে গোপন অভিযোগ।",
      status: "new",
      createdAt: addIsoDays(anchorDate, -2),
      updatedAt: addIsoDays(anchorDate, -2)
    },
    {
      id: "GRV-105",
      ticketNo: "GRV-105",
      category: "harassment",
      anonymous: true,
      lineId: "line-2",
      subjectEn: "Request for Anti-Harassment Committee orientation on Shift B",
      subjectBn: "শিফট বি-তে হয়রানি প্রতিরোধ কমিটির ওরিয়েন্টেশন অনুরোধ",
      detailEn: "New operators asked for confidential drop-box instructions in Bangla.",
      detailBn: "নতুন অপারেটররা বাংলায় গোপন অভিযোগ বক্সের নির্দেশিকা চেয়েছেন।",
      status: "resolved",
      createdAt: addIsoDays(anchorDate, -14),
      updatedAt: addIsoDays(anchorDate, -7),
      resolutionNote: "Welfare officer & Harassment Complaints Committee held Shift B briefing and posted hotline notices."
    }
  ];

  // Anonymous pulse survey responses:
  // Lines 1..5 have >= 6 responses (above k=5 threshold).
  // Line 6 intentionally has 3 responses (< 5) so the privacy threshold guard is visible immediately.
  const pulseResponses: PulseSurveyResponse[] = [];
  const lineResponseCounts: Record<WorkforceLineId, number> = {
    "line-1": 8,
    "line-2": 7,
    "line-3": 9,
    "line-4": 6,
    "line-5": 8,
    "line-6": 3 // < 5 -> triggers "Not enough responses to show safely"
  };

  let pulseSeq = 0;
  for (const [lid, count] of Object.entries(lineResponseCounts) as Array<[WorkforceLineId, number]>) {
    for (let i = 0; i < count; i++) {
      pulseSeq++;
      const isLine3 = lid === "line-3";
      pulseResponses.push({
        id: `PLS-${pulseSeq}`,
        lineId: lid,
        weekIso: anchorDate,
        q1PaceManageable: isLine3 ? (((i % 3) + 2) as 2 | 3 | 4) : (((i % 2) + 4) as 4 | 5),
        q2ToolsAndCoolingOk: isLine3 ? (((i % 3) + 2) as 2 | 3 | 4) : (((i % 3) + 3) as 3 | 4 | 5),
        q3RespectfulSupport: (((i % 2) + 4) as 4 | 5),
        submittedAt: addIsoDays(anchorDate, -(i % 5))
      });
    }
  }

  const counsellorReferrals: CounsellorReferral[] = [
    {
      id: "REF-201",
      caseCode: "REF-201",
      lineId: "line-3",
      counsellorNameEn: "Farhana Rahman (Welfare Counsellor)",
      counsellorNameBn: "ফারহানা রহমান (ওয়েলফেয়ার কাউন্সেলর)",
      requestedDate: addIsoDays(anchorDate, -2),
      status: "requested"
    },
    {
      id: "REF-202",
      caseCode: "REF-202",
      lineId: "line-2",
      counsellorNameEn: "Dr. Mahbub Alam (Visiting Counsellor)",
      counsellorNameBn: "ডা. মাহবুব আলম (ভিজিটিং কাউন্সেলর)",
      requestedDate: addIsoDays(anchorDate, -5),
      scheduledDate: addIsoDays(anchorDate, 1),
      status: "scheduled"
    },
    {
      id: "REF-203",
      caseCode: "REF-203",
      lineId: "line-5",
      counsellorNameEn: "Farhana Rahman (Welfare Counsellor)",
      counsellorNameBn: "ফারহানা রহমান (ওয়েলফেয়ার কাউন্সেলর)",
      requestedDate: addIsoDays(anchorDate, -12),
      scheduledDate: addIsoDays(anchorDate, -8),
      status: "closed"
    }
  ];

  const safetyChecklist: SafetyChecklistItem[] = [
    {
      id: "SAF-1",
      key: "fire_drill",
      titleEn: "Monthly Fire & Evacuation Drill",
      titleBn: "মাসিক অগ্নি ও জরুরি বহির্গমন মহড়া",
      detailEn: "All 6 lines evacuated to assembly point in 3m 10s (demo checklist item).",
      detailBn: "সব ৬টি লাইন ৩ মিনিট ১০ সেকেন্ডে অ্যাসেম্বলি পয়েন্টে পৌঁছেছে (ডেমো চেকলিস্ট)।",
      status: "verified",
      lastCheckedDate: addIsoDays(anchorDate, -11),
      metricEn: "Last drill: 11 days ago · Next due in 19 days",
      metricBn: "সর্বশেষ মহড়া: ১১ দিন আগে · পরবর্তী ১৯ দিন পর"
    },
    {
      id: "SAF-2",
      key: "first_aid",
      titleEn: "Floor First-Aid Responders on Shift",
      titleBn: "শিফটে ফ্লোর প্রাথমিক চিকিৎসা প্রশিক্ষণপ্রাপ্ত কর্মী",
      detailEn: "Trained first-aid responders present across Lines 1–6 (illustrative compliance cue).",
      detailBn: "লাইন ১–৬ জুড়ে প্রাথমিক চিকিৎসা প্রশিক্ষণপ্রাপ্ত কর্মী উপস্থিত (ডেমো কমপ্লায়েন্স সূচক)।",
      status: "verified",
      lastCheckedDate: addIsoDays(anchorDate, -3),
      metricEn: "24 trained responders across 6 lines",
      metricBn: "৬টি লাইনে ২৪ জন প্রশিক্ষিত কর্মী"
    },
    {
      id: "SAF-3",
      key: "day_care",
      titleEn: "Child Day-Care & Lactation Corner",
      titleBn: "শিশু দিবাযত্ন ও মাতৃদুগ্ধ কর্নার",
      detailEn: "Ground-floor room staffed by 2 attendants; eligibility tracked in worker profiles.",
      detailBn: "নিচতলায় ২ জন সেবিকাসহ কক্ষ চালু; কর্মীদের প্রোফাইলে যোগ্যতা চিহ্নিত।",
      status: "verified",
      lastCheckedDate: addIsoDays(anchorDate, -5),
      metricEn: "26 eligible mothers · 14 enrolled children",
      metricBn: "২৬ জন যোগ্য মা · ১৪ জন নিবন্ধিত শিশু"
    },
    {
      id: "SAF-4",
      key: "harassment_committee",
      titleEn: "Anti-Harassment & Grievance Committee",
      titleBn: "হয়রানি প্রতিরোধ ও অভিযোগ কমিটি",
      detailEn: "5-member committee (3 women, 2 men) reviews confidential box every Tuesday.",
      detailBn: "৫ সদস্যের কমিটি (৩ জন নারী, ২ জন পুরুষ) প্রতি মঙ্গলবার গোপন বক্স পর্যালোচনা করে।",
      status: "due_soon",
      lastCheckedDate: addIsoDays(anchorDate, -6),
      metricEn: "Weekly review due tomorrow",
      metricBn: "সাপ্তাহিক পর্যালোচনা আগামীকাল"
    }
  ];

  return {
    schemaVersion: 1,
    anchorDate,
    ramadanHours: false,
    hasLocalEdits: false,
    workers,
    attendanceByDate,
    attendanceEditedKeys: {},
    coveredLineDates: {},
    dismissedSuggestionIds: [],
    appliedSuggestionIds: [],
    acknowledgedWorkloadWorkerIds: [],
    leaveRequests,
    trainingBatches,
    workerNotes,
    grievances,
    wellbeing: {
      pulseResponses,
      counsellorReferrals,
      safetyChecklist
    },
    auditTrail: [
      {
        id: "AUD-1",
        isoDate: new Date(Date.UTC(2026, 9, 25, 8, 15)).toISOString(),
        actorEmail: "system-seed@bunonbrain.demo",
        actionEn: "Initialized demo workforce dataset (120 workers across 6 lines)",
        actionBn: "ডেমো কর্মীবাহিনী ডেটাসেট লোড হয়েছে (৬টি লাইনে ১২০ জন কর্মী)"
      }
    ]
  };
}
