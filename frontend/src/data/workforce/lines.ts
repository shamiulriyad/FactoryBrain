// Factory lines for the Workforce demo module.
// Kept in sync with the six plant lines used across BunonBrain so the demo
// feels like one plant without coupling to factory.store.ts.

export type WorkforceLineId = "line-1" | "line-2" | "line-3" | "line-4" | "line-5" | "line-6";

export type WorkforceLineInfo = {
  id: WorkforceLineId;
  code: string;
  nameEn: string;
  nameBn: string;
  garmentEn: string;
  garmentBn: string;
  targetHeadcount: number;
  targetHelperCount: number;
  targetEfficiencyPct: number;
};

export const WORKFORCE_LINES: readonly WorkforceLineInfo[] = [
  {
    id: "line-1",
    code: "L1",
    nameEn: "Line 1 — Polo Tee",
    nameBn: "লাইন ১ — পোলো টি",
    garmentEn: "Polo Tee",
    garmentBn: "পোলো টি",
    targetHeadcount: 20,
    targetHelperCount: 3,
    targetEfficiencyPct: 78
  },
  {
    id: "line-2",
    code: "L2",
    nameEn: "Line 2 — Crew Neck",
    nameBn: "লাইন ২ — ক্রু নেক",
    garmentEn: "Crew Neck",
    garmentBn: "ক্রু নেক",
    targetHeadcount: 20,
    targetHelperCount: 3,
    targetEfficiencyPct: 78
  },
  {
    id: "line-3",
    code: "L3",
    nameEn: "Line 3 — V-Neck",
    nameBn: "লাইন ৩ — ভি-নেক",
    garmentEn: "V-Neck",
    garmentBn: "ভি-নেক",
    targetHeadcount: 20,
    targetHelperCount: 3,
    targetEfficiencyPct: 78
  },
  {
    id: "line-4",
    code: "L4",
    nameEn: "Line 4 — Hoodie",
    nameBn: "লাইন ৪ — হুডি",
    garmentEn: "Hoodie",
    garmentBn: "হুডি",
    targetHeadcount: 18,
    targetHelperCount: 3,
    targetEfficiencyPct: 75
  },
  {
    id: "line-5",
    code: "L5",
    nameEn: "Line 5 — Tank Top",
    nameBn: "লাইন ৫ — ট্যাঙ্ক টপ",
    garmentEn: "Tank Top",
    garmentBn: "ট্যাঙ্ক টপ",
    targetHeadcount: 20,
    targetHelperCount: 3,
    targetEfficiencyPct: 76
  },
  {
    id: "line-6",
    code: "L6",
    nameEn: "Line 6 — Jacket",
    nameBn: "লাইন ৬ — জ্যাকেট",
    garmentEn: "Jacket",
    garmentBn: "জ্যাকেট",
    targetHeadcount: 18,
    targetHelperCount: 3,
    targetEfficiencyPct: 74
  }
] as const;

export function getWorkforceLine(lineId: string): WorkforceLineInfo | undefined {
  return WORKFORCE_LINES.find((l) => l.id === lineId);
}
