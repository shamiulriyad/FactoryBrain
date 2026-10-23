import type { BusinessProfile } from "@/store/business.store";

// Goals the Business Brain tracks. The first 3 align with the onboarding profile defaults.
export type BusinessGoal = {
  id: string;
  label: string;
  labelBn: string;
  metric: "revenue" | "repeat_rate" | "stockout_days" | "csat" | "marketing_spend";
  target?: number;
};

export const goals: BusinessGoal[] = [
  { id: "goal-1", label: "Grow repeat purchases", labelBn: "পুনরায় কেনাকাটা বাড়ান", metric: "repeat_rate", target: 0.45 },
  { id: "goal-2", label: "Reduce stockouts", labelBn: "স্টকআউট কমান", metric: "stockout_days", target: 2 },
  { id: "goal-3", label: "Cut slow-moving inventory", labelBn: "ধীরে চলমান পণ্য কমান", metric: "revenue" },
  { id: "goal-4", label: "Improve customer response time", labelBn: "ক্রেতা প্রতিক্রিয়ার সময় উন্নত করুন", metric: "csat", target: 0.85 },
  { id: "goal-5", label: "Cut marketing waste", labelBn: "মার্কেটিং অপচয় কমান", metric: "marketing_spend" },
  { id: "goal-6", label: "Expand to new channels", labelBn: "নতুন চ্যানেলে প্রসারিত হন", metric: "revenue" },
  { id: "goal-7", label: "Improve cashflow visibility", labelBn: "ক্যাশফ্লো দৃশ্যতা উন্নত করুন", metric: "revenue" }
];

export function goalsForProfile(profile: BusinessProfile): BusinessGoal[] {
  const order = new Map<string, number>();
  profile.goals.forEach((label, i) => order.set(label, i));
  return [...goals].sort((a, b) => {
    const ai = order.has(a.label) ? order.get(a.label)! : 99;
    const bi = order.has(b.label) ? order.get(b.label)! : 99;
    return ai - bi;
  });
}