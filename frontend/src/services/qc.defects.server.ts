// QC defects server module.
//
// Assembles Bosch-shaped defect / rework statistics from the small static
// seed in src/data/qc.defects.ts. All numbers are deterministic for a given
// calendar day. No live QA capture, no LLM, no external data file.
//
// Used by:
//   GET /api/qc/defects
//
// The response shape is UI-friendly so a future card can plug in without
// schema churn.

import { z } from "zod";
import {
  OPERATIONS,
  recentWeekStarts,
  generateOperationLineWeeks,
  type Operation
} from "@/data/qc.defects";
import { LINES } from "@/services/sensors.server";

// --- Public constants ------------------------------------------------------

export const QC_SIMULATED_LABEL =
  "Simulated — Bosch-shaped defect/rework seed in src/data/qc.defects.ts (no live QA capture, no external data file)";

export const QC_DEFECT_SOURCE =
  "Demo seed · Bosch-shaped taxonomy (cutting / sewing / buttonhole / top_stitch / qc_inspection / finishing)";

// --- Schemas ----------------------------------------------------------------

const WeekBucketSchema = z
  .object({
    weekStart: z.string().regex(/^\d{4}-\d{2}-\d{2}$/),
    inspected: z.number().int().nonnegative(),
    defects: z.number().int().nonnegative(),
    major: z.number().int().nonnegative(),
    minor: z.number().int().nonnegative(),
    rework: z.number().int().nonnegative()
  })
  .strict();

const OperationTotalsSchema = z
  .object({
    inspected: z.number().int().nonnegative(),
    defects: z.number().int().nonnegative(),
    major: z.number().int().nonnegative(),
    minor: z.number().int().nonnegative(),
    rework: z.number().int().nonnegative()
  })
  .strict();

export const QcOperationStatsSchema = z
  .object({
    operation: z.enum(OPERATIONS as unknown as readonly [string, ...string[]]),
    lineId: z.enum(LINES as unknown as readonly [string, ...string[]]),
    weeks: z.array(WeekBucketSchema).length(8),
    totals: OperationTotalsSchema
  })
  .strict();

export const QcTopOperationSchema = z
  .object({
    operation: z.enum(OPERATIONS as unknown as readonly [string, ...string[]]),
    lineId: z.enum(LINES as unknown as readonly [string, ...string[]]),
    defectRatePct: z.number().min(0).max(100),
    reworkRatePct: z.number().min(0).max(100)
  })
  .strict();

export const QcResponseSchema = z
  .object({
    operations: z.array(QcOperationStatsSchema),
    topByDefectRate: z.array(QcTopOperationSchema).length(5),
    topByReworkRate: z.array(QcTopOperationSchema).length(5),
    meta: z
      .object({
        simulated: z.literal(true),
        source: z.string().min(1),
        defectSource: z.string().min(1),
        generatedAt: z.string().min(1),
        notes: z.string().min(1)
      })
      .strict()
  })
  .strict();

export type QcOperationStats = z.infer<typeof QcOperationStatsSchema>;
export type QcTopOperation = z.infer<typeof QcTopOperationSchema>;
export type QcResponse = z.infer<typeof QcResponseSchema>;

// --- Helpers ----------------------------------------------------------------

function ratePct(part: number, whole: number): number {
  if (whole <= 0) return 0;
  // Clamp to 0..100; round to 2 decimals to keep responses compact.
  const v = (part / whole) * 100;
  return Math.max(0, Math.min(100, Math.round(v * 100) / 100));
}

function topNBy<T>(arr: T[], n: number, score: (t: T) => number): T[] {
  // Deterministic sort by score desc, then by stable stringified identity.
  return [...arr]
    .sort((a, b) => {
      const sb = score(b);
      const sa = score(a);
      if (sb !== sa) return sb - sa;
      return JSON.stringify(a).localeCompare(JSON.stringify(b));
    })
    .slice(0, n);
}

// --- Public builder ---------------------------------------------------------

export function buildQcDefects(): QcResponse {
  const weekStarts = recentWeekStarts(8);

  // Build one operation×line cell per (op, line).
  const operations: QcOperationStats[] = [];
  for (const op of OPERATIONS) {
    for (const lineId of LINES) {
      const cell = generateOperationLineWeeks(op as Operation, lineId, weekStarts);
      operations.push({
        operation: op as Operation,
        lineId: lineId as (typeof LINES)[number],
        weeks: cell.weeks,
        totals: cell.totals
      });
    }
  }

  // Top-5 by defect rate / rework rate across the whole grid.
  const candidates: QcTopOperation[] = operations.map((o) => ({
    operation: o.operation,
    lineId: o.lineId,
    defectRatePct: ratePct(o.totals.defects, o.totals.inspected),
    reworkRatePct: ratePct(o.totals.rework, o.totals.inspected)
  }));

  const topByDefectRate = topNBy(candidates, 5, (c) => c.defectRatePct);
  const topByReworkRate = topNBy(candidates, 5, (c) => c.reworkRatePct);

  const payload: QcResponse = {
    operations,
    topByDefectRate,
    topByReworkRate,
    meta: {
      simulated: true,
      source: QC_SIMULATED_LABEL,
      defectSource: QC_DEFECT_SOURCE,
      generatedAt: new Date().toISOString(),
      notes: "Deterministic per-cell PRNG seed; same calendar day → identical payload (except generatedAt)."
    }
  };

  return QcResponseSchema.parse(payload);
}