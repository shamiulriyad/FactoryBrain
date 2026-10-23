// QC defects seed — Bosch-shaped defect/rework taxonomy for the demo.
//
// Small, fully-deterministic dataset. No real Bosch/ZWEM file is read or
// embedded — the values are typed-shaped placeholders. Every number is
// derived from a stable PRNG seed combined with the (operation × line × week)
// cell coords, so two calls in the same day return identical output.
//
// Vocabulary is aligned with src/data/vision.ts defect classes
// (stitch_skip, buttonhole_misalign, seam_pucker, fabric_stain, …) so
// future UI can cross-link.

import { makeRng } from "@/data/seed";
import { LINES } from "@/services/sensors.server";

// --- Operation taxonomy ----------------------------------------------------

/** Operations that contribute to the defect/rework aggregate. */
export const OPERATIONS = [
  "cutting",
  "sewing",
  "buttonhole",
  "top_stitch",
  "qc_inspection",
  "finishing"
] as const;
export type Operation = (typeof OPERATIONS)[number];

// --- Week-start helper -----------------------------------------------------

/**
 * Returns the last `count` Monday ISO dates (YYYY-MM-DD), most recent first.
 * The most recent entry is the Monday of the current week (or the most recent
 * past Monday). Stable across the same calendar week.
 */
export function recentWeekStarts(count = 8, now: Date = new Date()): string[] {
  const out: string[] = [];
  // Anchor: Monday of the week containing `now`.
  const d = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const day = d.getDay(); // 0=Sun..6=Sat
  const offsetToMonday = (day + 6) % 7; // 0 if Monday, 1 if Tuesday, …
  d.setDate(d.getDate() - offsetToMonday);
  for (let i = 0; i < count; i++) {
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, "0");
    const dd = String(d.getDate()).padStart(2, "0");
    out.push(`${y}-${m}-${dd}`);
    d.setDate(d.getDate() - 7);
  }
  return out;
}

// --- Per-cell generator ----------------------------------------------------

/**
 * Generate deterministic weekly stats for a single (operation, line) pair.
 * Numbers are clamped so `defects ≤ inspected` and `major+minor ≤ defects`,
 * `rework ≤ defects * 0.4`.
 */
export function generateOperationLineWeeks(
  operation: Operation,
  lineId: string,
  weekStarts: readonly string[]
): {
  weeks: Array<{
    weekStart: string;
    inspected: number;
    defects: number;
    major: number;
    minor: number;
    rework: number;
  }>;
  totals: { inspected: number; defects: number; major: number; minor: number; rework: number };
} {
  // Per-cell seed mixes operation + line identity so different cells diverge.
  const opIdx = OPERATIONS.indexOf(operation);
  const lineIdx = LINES.indexOf(lineId as (typeof LINES)[number]);
  const seed = 0xDEFE_0001 ^ (opIdx * 0x9E37) ^ (lineIdx * 0x7F4A);
  const rng = makeRng(seed >>> 0);

  const weeks = weekStarts.map((weekStart, weekIdx) => {
    // Production volume drifts slightly week-over-week (deterministic).
    const baseInspected = 1400 + (weekIdx % 4) * 40 + Math.floor(rng() * 200);
    const inspected = baseInspected - ((weekIdx * 17) % 60);

    // Operation-level defect rate varies by op (some ops run cleaner).
    const opDefectRate = 0.012 + (opIdx * 0.0037) + (lineIdx * 0.0021);
    const drift = (rng() - 0.5) * 0.012;
    const defectRate = Math.max(0.005, opDefectRate + drift);
    const defects = Math.min(inspected, Math.round(inspected * defectRate));

    // 70/30 major vs minor split, plus noise.
    const majorMinorNoise = rng() * 0.15 - 0.075;
    const majorShare = Math.max(0.05, Math.min(0.95, 0.30 + majorMinorNoise));
    const major = Math.max(0, Math.round(defects * majorShare));
    const minor = Math.max(0, defects - major);

    // Rework rate: ~25% of defects with noise.
    const reworkShare = Math.max(0.0, Math.min(0.5, 0.25 + (rng() - 0.5) * 0.1));
    const rework = Math.min(defects, Math.round(defects * reworkShare));

    return { weekStart, inspected, defects, major, minor, rework };
  });

  const totals = weeks.reduce(
    (acc, w) => ({
      inspected: acc.inspected + w.inspected,
      defects: acc.defects + w.defects,
      major: acc.major + w.major,
      minor: acc.minor + w.minor,
      rework: acc.rework + w.rework
    }),
    { inspected: 0, defects: 0, major: 0, minor: 0, rework: 0 }
  );

  return { weeks, totals };
}

// --- Public types ----------------------------------------------------------

export type OperationLineWeek = ReturnType<typeof generateOperationLineWeeks>["weeks"][number];
export type OperationLineTotals = ReturnType<typeof generateOperationLineWeeks>["totals"];
