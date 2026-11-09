// Illustrative demo values modelled on the Bangladesh Labour Act 2006 and the RMG minimum-wage structure. Verify against current law before any real use.

export type SkillGrade = 1 | 2 | 3 | 4 | 5 | 6 | 7;

export type GradeWageStructure = {
  grade: SkillGrade;
  labelEn: string;
  labelBn: string;
  basicBdt: number;
  houseRentBdt: number;
  medicalBdt: number;
  transportBdt: number;
  foodBdt: number;
  grossMonthlyBdt: number;
};

export type FestivalHoliday = {
  id: string;
  nameEn: string;
  nameBn: string;
  days: number;
  noteEn: string;
  noteBn: string;
};

export const WORKFORCE_POLICY = {
  disclaimerEn:
    "Illustrative demo values modelled on the Bangladesh Labour Act 2006 and the RMG minimum-wage structure. Verify against current law before any real use.",
  disclaimerBn:
    "বাংলাদেশ শ্রম আইন ২০০৬ এবং আরএমজি ন্যূনতম মজুরি কাঠামোর আদলে তৈরি দৃষ্টান্তমূলক ডেমো মান। যেকোনো বাস্তব ব্যবহারের আগে প্রচলিত আইন যাচাই করুন।",

  shifts: {
    regular: {
      start: "08:00",
      end: "17:00",
      lunchWindow: "13:00–14:00",
      lunchMinutes: 60,
      netDailyHours: 8,
      labelEn: "Regular shift (08:00–17:00, 1h lunch)",
      labelBn: "সাধারণ শিফট (০৮:০০–১৭:০০, ১ ঘণ্টা বিরতি)"
    },
    ramadan: {
      start: "08:00",
      end: "15:30",
      lunchWindow: "12:30–13:00",
      lunchMinutes: 30,
      netDailyHours: 7,
      labelEn: "Ramadan hours (08:00–15:30, 30m break)",
      labelBn: "রমজান সময়সূচি (০৮:০০–১৫:৩০, ৩০ মিনিট বিরতি)"
    },
    weeklyHolidayDayOfWeek: 5, // Friday (0 = Sunday .. 5 = Friday)
    weeklyHolidayNameEn: "Friday",
    weeklyHolidayNameBn: "শুক্রবার",
    workingDaysPerWeek: 6,
    standardMonthlyHours: 208 // 26 days * 8 hours
  },

  leave: {
    casualDaysPerYear: 10,
    sickDaysPerYear: 14,
    annualAccrualDaysWorkedPerDay: 18, // 1 day earned per 18 days worked
    maternityWeeksTotal: 16,
    maternityWeeksPre: 8,
    maternityWeeksPost: 8,
    festivalHolidaysTotalDays: 11,
    festivalHolidays: [
      {
        id: "fh-eid-fitr",
        nameEn: "Eid-ul-Fitr",
        nameBn: "ঈদুল ফিতর",
        days: 3,
        noteEn: "3 festival days (lunar calendar)",
        noteBn: "৩ দিন উৎসব ছুটি (চাঁদ দেখার ওপর নির্ভরশীল)"
      },
      {
        id: "fh-eid-adha",
        nameEn: "Eid-ul-Adha",
        nameBn: "ঈদুল আযহা",
        days: 3,
        noteEn: "3 festival days (lunar calendar)",
        noteBn: "৩ দিন উৎসব ছুটি (চাঁদ দেখার ওপর নির্ভরশীল)"
      },
      {
        id: "fh-durga-puja",
        nameEn: "Durga Puja",
        nameBn: "দুর্গাপূজা",
        days: 1,
        noteEn: "Dashami festival holiday",
        noteBn: "বিজয়া দশমী উৎসব ছুটি"
      },
      {
        id: "fh-pohela-boishakh",
        nameEn: "Pohela Boishakh (14 Apr)",
        nameBn: "পহেলা বৈশাখ (১৪ এপ্রিল)",
        days: 1,
        noteEn: "Bangla New Year",
        noteBn: "বাংলা নববর্ষ"
      },
      {
        id: "fh-21-feb",
        nameEn: "Shaheed & Intl Mother Language Day (21 Feb)",
        nameBn: "শহীদ ও আন্তর্জাতিক মাতৃভাষা দিবস (২১ ফেব্রুয়ারি)",
        days: 1,
        noteEn: "National holiday",
        noteBn: "জাতীয় ছুটি"
      },
      {
        id: "fh-26-mar",
        nameEn: "Independence Day (26 Mar)",
        nameBn: "স্বাধীনতা দিবস (২৬ মার্চ)",
        days: 1,
        noteEn: "National holiday",
        noteBn: "জাতীয় ছুটি"
      },
      {
        id: "fh-16-dec",
        nameEn: "Victory Day (16 Dec)",
        nameBn: "বিজয় দিবস (১৬ ডিসেম্বর)",
        days: 1,
        noteEn: "National holiday",
        noteBn: "জাতীয় ছুটি"
      }
    ] as readonly FestivalHoliday[]
  },

  overtime: {
    standardWeeklyHours: 48,
    weeklyTotalCapHours: 60,
    workloadAlertConsecutiveWeeks: 3,
    overtimeRateMultiplier: 2.0 // 2 x hourly basic
  },

  bonuses: {
    attendanceBonusBdt: 850,
    efficiencyTier1MinPct: 78,
    efficiencyTier1MaxDhuPct: 2.2,
    efficiencyTier1BonusBdt: 600,
    efficiencyTier2MinPct: 85,
    efficiencyTier2MaxDhuPct: 1.5,
    efficiencyTier2BonusBdt: 1200,
    festivalBonusPctOfBasic: 100 // 100% of basic per Eid (illustrative)
  },

  wellbeing: {
    minPulseResponsesPerLine: 5,
    grievanceSlaDays: 7
  },

  grades: {
    7: {
      grade: 7,
      labelEn: "Grade 7 — Entry / Helper",
      labelBn: "গ্রেড ৭ — এন্ট্রি / হেল্পার",
      basicBdt: 6700,
      houseRentBdt: 3350,
      medicalBdt: 750,
      transportBdt: 450,
      foodBdt: 1250,
      grossMonthlyBdt: 12500
    },
    6: {
      grade: 6,
      labelEn: "Grade 6 — Junior Operator",
      labelBn: "গ্রেড ৬ — জুনিয়র অপারেটর",
      basicBdt: 7100,
      houseRentBdt: 3550,
      medicalBdt: 750,
      transportBdt: 450,
      foodBdt: 1250,
      grossMonthlyBdt: 13100
    },
    5: {
      grade: 5,
      labelEn: "Grade 5 — Operator",
      labelBn: "গ্রেড ৫ — অপারেটর",
      basicBdt: 7700,
      houseRentBdt: 3850,
      medicalBdt: 750,
      transportBdt: 450,
      foodBdt: 1250,
      grossMonthlyBdt: 14000
    },
    4: {
      grade: 4,
      labelEn: "Grade 4 — Senior Operator",
      labelBn: "গ্রেড ৪ — সিনিয়র অপারেটর",
      basicBdt: 8300,
      houseRentBdt: 4150,
      medicalBdt: 750,
      transportBdt: 450,
      foodBdt: 1250,
      grossMonthlyBdt: 14900
    },
    3: {
      grade: 3,
      labelEn: "Grade 3 — Multi-Machine / QC / Mechanic",
      labelBn: "গ্রেড ৩ — মাল্টি-মেশিন / কিউসি / মেকানিক",
      basicBdt: 9000,
      houseRentBdt: 4500,
      medicalBdt: 750,
      transportBdt: 450,
      foodBdt: 1250,
      grossMonthlyBdt: 15950
    },
    2: {
      grade: 2,
      labelEn: "Grade 2 — Line Supervisor",
      labelBn: "গ্রেড ২ — লাইন সুপারভাইজার",
      basicBdt: 10000,
      houseRentBdt: 5000,
      medicalBdt: 750,
      transportBdt: 450,
      foodBdt: 1250,
      grossMonthlyBdt: 17450
    },
    1: {
      grade: 1,
      labelEn: "Grade 1 — Line Chief / Master Mechanic",
      labelBn: "গ্রেড ১ — লাইন চিফ / মাস্টার মেকানিক",
      basicBdt: 11100,
      houseRentBdt: 5550,
      medicalBdt: 750,
      transportBdt: 450,
      foodBdt: 1250,
      grossMonthlyBdt: 19100
    }
  } as Record<SkillGrade, GradeWageStructure>
} as const;

export function getGradeWage(grade: SkillGrade): GradeWageStructure {
  return WORKFORCE_POLICY.grades[grade] ?? WORKFORCE_POLICY.grades[7];
}

export function getHourlyBasicBdt(grade: SkillGrade): number {
  const w = getGradeWage(grade);
  return Math.round((w.basicBdt / WORKFORCE_POLICY.shifts.standardMonthlyHours) * 100) / 100;
}

export function getOvertimeHourlyRateBdt(grade: SkillGrade): number {
  return Math.round(getHourlyBasicBdt(grade) * WORKFORCE_POLICY.overtime.overtimeRateMultiplier * 100) / 100;
}
