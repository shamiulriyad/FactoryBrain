// Morning-brief server module.
//
// Deterministic, demo-stable assembly of the morning brief. Composes:
//   - line-board rows + meta  (src/services/lineBoard.server)
//   - suggested insights       (src/services/insight.service, stage "suggested")
//   - pending approvals        (src/services/approval.service)
//
// No LLM is required for the demo path. The response shape leaves room for
// an LLM-driven upgrade later — see the FUTURE_LLM_HOOK comment below.
//
// Used by:
//   GET /api/brief/morning
//
// The brief is intentionally pure: no `Math.random()`, no live network.
// Two consecutive calls within the same render frame return identical JSON.
//
// FUTURE_LLM_HOOK: when LLM_API_KEY is configured (see /api/settings/llm),
// buildBullets() could be swapped for an LLM call gated on the env var.
// Until then, deterministic insight-derived bullets are returned.

import { z } from "zod";
import { buildLineBoard, type LineBoardRow, type Bottleneck } from "@/services/lineBoard.server";
import { insightService } from "@/services/insight.service";
import { approvalService } from "@/services/approval.service";

// --- Public constants ------------------------------------------------------

export const BRIEF_SIMULATED_LABEL =
  "Simulated — deterministic assembly from line-board + suggested insights + pending approvals (no live traffic, no LLM)";

// --- Schema ----------------------------------------------------------------

export const BriefResponseSchema = z
  .object({
    /** YYYY-MM-DD local date the brief was assembled. */
    date: z.string().regex(/^\d{4}-\d{2}-\d{2}$/),
    /** Parallel English bullet headlines (length === bulletsBn.length). */
    bulletsEn: z.array(z.string().min(1)),
    /** Parallel Bangla bullet headlines. */
    bulletsBn: z.array(z.string().min(1)),
    /** First red-bottleneck line; falls back to first amber; omitted if all green. */
    topBottleneckLineId: z.string().min(1).optional(),
    /** Count of suggested insights with riskTier medium/high. */
    riskCount: z.number().int().nonnegative(),
    /** Count of insights in stage pending_approval. */
    pendingApprovals: z.number().int().nonnegative(),
    /** Honesty / provenance metadata. Always emitted for the demo path. */
    meta: z.object({
      simulated: z.literal(true),
      source: z.string().min(1),
      tick: z.number().int().nonnegative(),
      generatedAt: z.string().min(1),
      notes: z.string().min(1)
    })
  })
  .strict();

export type BriefResponse = z.infer<typeof BriefResponseSchema>;

// --- Helpers ---------------------------------------------------------------

const BOTTLENECK_LABEL_EN: Record<Bottleneck, string> = {
  green: "on target",
  amber: "watch",
  red: "critical"
};

const BOTTLENECK_LABEL_BN: Record<Bottleneck, string> = {
  green: "লক্ষ্যে",
  amber: "নজরে",
  red: "সংকটাপন্ন"
};

function todayDateString(): string {
  // YYYY-MM-DD in the runtime's local time zone. Stable across the day.
  return new Date().toISOString().slice(0, 10);
}

function pickTopBottleneck(rows: LineBoardRow[]): { lineId: string; bottleneck: Bottleneck } | null {
  // Rows come pre-sorted by lineId.localeCompare() from buildLineBoard(),
  // so the first match is the stable, deterministic choice.
  const red = rows.find((r) => r.bottleneck === "red");
  if (red) return { lineId: red.lineId, bottleneck: "red" };
  const amber = rows.find((r) => r.bottleneck === "amber");
  if (amber) return { lineId: amber.lineId, bottleneck: "amber" };
  return null;
}

function headerBulletEn(row: LineBoardRow | undefined, top: { lineId: string; bottleneck: Bottleneck } | null): string {
  if (!row) return "All lines are running on target.";
  const bn = top?.bottleneck ?? row.bottleneck;
  return `Line ${row.lineId} efficiency ${row.efficiencyPct}% vs target ${row.sahTarget}% (${BOTTLENECK_LABEL_EN[bn]}).`;
}

function headerBulletBn(row: LineBoardRow | undefined, top: { lineId: string; bottleneck: Bottleneck } | null): string {
  if (!row) return "সব লাইন লক্ষ্যমাত্রায় চলছে।";
  const bn = top?.bottleneck ?? row.bottleneck;
  return `লাইন ${row.lineId} দক্ষতা ${row.efficiencyPct}% বনাম লক্ষ্য ${row.sahTarget}% (${BOTTLENECK_LABEL_BN[bn]})।`;
}

const FALLBACK_NO_RECS_EN = "No new recommendations.";
const FALLBACK_NO_RECS_BN = "নতুন কোনো সুপারিশ নেই।";

function buildBullets(
  topBottleneckRow: LineBoardRow | undefined,
  top: { lineId: string; bottleneck: Bottleneck } | null,
  suggested: Array<{ title: string; titleBn: string }>
): { en: string[]; bn: string[] } {
  const en: string[] = [headerBulletEn(topBottleneckRow, top)];
  const bn: string[] = [headerBulletBn(topBottleneckRow, top)];

  if (suggested.length === 0) {
    en.push(FALLBACK_NO_RECS_EN);
    bn.push(FALLBACK_NO_RECS_BN);
    return { en, bn };
  }

  // Take up to 2 insight headlines (deterministic order: feed() is pinned-first).
  const tail = suggested.slice(0, 2);
  for (const s of tail) {
    en.push(s.title);
    bn.push(s.titleBn);
  }
  return { en, bn };
}

// --- Public builder --------------------------------------------------------

export function buildMorningBrief(): BriefResponse {
  const board = buildLineBoard();
  const top = pickTopBottleneck(board.rows);
  const topRow = top ? board.rows.find((r) => r.lineId === top.lineId) : undefined;

  // Suggested insights, sorted pinned-first by insightService.feed().
  const suggestedAll = insightService.feed({ stage: "suggested" });
  const suggested = suggestedAll.slice(0, 3).map((i) => ({ title: i.title, titleBn: i.titleBn }));

  const pendingApprovals = approvalService.pending().length;
  const riskCount = suggestedAll.filter(
    (i) => i.recommendation.riskTier === "medium" || i.recommendation.riskTier === "high"
  ).length;

  const bullets = buildBullets(topRow, top, suggested);

  const payload: BriefResponse = {
    date: todayDateString(),
    bulletsEn: bullets.en,
    bulletsBn: bullets.bn,
    ...(top ? { topBottleneckLineId: top.lineId } : {}),
    riskCount,
    pendingApprovals,
    meta: {
      simulated: true,
      source: BRIEF_SIMULATED_LABEL,
      tick: board.meta.tick,
      generatedAt: new Date().toISOString(),
      notes: "Deterministic assembly. Same sim tick + insights snapshot → identical response. No LLM required."
    }
  };

  return BriefResponseSchema.parse(payload);
}
