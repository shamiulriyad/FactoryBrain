// POST /api/qc/flag — persist a user-flagged QC defect observation.
//
// The user picks a top operation/line from the QC Defects panel and asks
// the system to remember the issue. We turn it into a real Insight
// (suggested stage) via insightService.upsertCustom(), log it as a
// ActivityItem ("flagged"), and return the new insight id so the panel
// can deep-link the user into /app/insights?focus=….
//
// This is the QC-side mirror of the existing persistence helpers used by
// agents/run and Ask→Save; it intentionally bypasses an LLM (no QA
// capture, deterministic dataset) and stays inside the dataset.

import { NextRequest } from "next/server";
import { z } from "zod";
import { OPERATIONS } from "@/data/qc.defects";
import { LINES } from "@/services/sensors.server";
import { insightService } from "@/services/insight.service";
import { activityService } from "@/services/activity.service";
import type { RiskTier } from "@/services/types";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

const BodySchema = z
  .object({
    operation: z.enum(OPERATIONS as unknown as readonly [string, ...string[]]),
    lineId: z.enum(LINES as unknown as readonly [string, ...string[]]),
    defectRatePct: z.number().min(0).max(100).optional(),
    reworkRatePct: z.number().min(0).max(100).optional(),
    note: z.string().min(1).max(280).optional()
  })
  .strict();

const FlagResponseSchema = z
  .object({
    insightId: z.string().min(1),
    flaggedAt: z.string().min(1)
  })
  .strict();

function pickRisk(defectRatePct: number | undefined, reworkRatePct: number | undefined): RiskTier {
  const d = defectRatePct ?? 0;
  const r = reworkRatePct ?? 0;
  // Severity mapping — both are non-negative, capped at 100. The highest
  // of the two drives the suggested risk tier so the Insights page can
  // route the right approver. Thresholds: high ≥ 6, medium ≥ 3, low < 3.
  const worst = Math.max(d, r);
  if (worst >= 6) return "high";
  if (worst >= 3) return "medium";
  return "low";
}

function buildIdempotentId(op: string, lineId: string): string {
  // Stable per-cell id so a repeated flag doesn't pile up identical rows;
  // the user can still add new flags if they want — the activity log keeps
  // the chronology. Hash is just string concat (no crypto) for stability.
  const stamp = `${op}-${lineId}`.replace(/[^a-z0-9-]/gi, "-").toLowerCase();
  return `qc-flag-${stamp}`;
}

export async function POST(req: NextRequest) {
  let raw: unknown;
  try {
    raw = await req.json();
  } catch {
    return Response.json(
      { error: "invalid_json", message: "Body must be valid JSON" },
      { status: 400 }
    );
  }

  const parsed = BodySchema.safeParse(raw);
  if (!parsed.success) {
    return Response.json(
      { error: "invalid_body", issues: parsed.error.issues },
      { status: 400 }
    );
  }

  const { operation, lineId, defectRatePct, reworkRatePct, note } = parsed.data;
  const risk = pickRisk(defectRatePct, reworkRatePct);
  const opTitle = operation.replace(/_/g, " ");
  const lineTitle = lineId.replace(/-/g, " ").replace(/\b\w/g, (c) => c.toUpperCase());
  const id = buildIdempotentId(operation, lineId);

  const defStr = defectRatePct != null ? `${defectRatePct.toFixed(2)}%` : "—";
  const reworkStr = reworkRatePct != null ? `${reworkRatePct.toFixed(2)}%` : "—";

  insightService.upsertCustom({
    id,
    agentId: "qc-flag",
    agentLabel: "QC Flag",
    title: `Flagged ${opTitle} on ${lineTitle}`,
    titleBn: `${lineTitle}-এ ${opTitle} ফ্ল্যাগ করা হয়েছে`,
    finding:
      `User flagged ${opTitle} on ${lineTitle}. Defect rate ${defStr}, ` +
      `rework rate ${reworkStr}. ${note ? "Note: " + note : "No additional note."}`,
    findingBn:
      `ইউজার ${lineTitle}-এ ${opTitle} ফ্ল্যাগ করেছেন। ত্রুটির হার ${defStr}, ` +
      `রি-ওয়ার্ক হার ${reworkStr}। ${note ? "নোট: " + note : "অতিরিক্ত নোট নেই।"}`,
    recommendation: {
      title: `Review ${opTitle} on ${lineTitle}`,
      titleBn: `${lineTitle}-এ ${opTitle} পর্যালোচনা করুন`,
      action: `Open the ${opTitle} trend for ${lineTitle}, validate the rate against the line-board SAH, and create an approval if the trend needs intervention.`,
      actionBn: `${lineTitle}-এর ${opTitle} প্রবণতা দেখুন, লাইন-বোর্ডের লক্ষ্যমাত্রার সাথে যাচাই করুন এবং প্রবণতা হস্তক্ষেপের প্রয়োজন হলে একটি অনুমোদন তৈরি করুন।`,
      riskTier: risk,
      targetStage: "suggested"
    },
    confidence: 0.55
  });

  const iso = new Date().toISOString();
  activityService.push({
    actor: "user",
    actorLabel: "You",
    verb: "flagged",
    verbBn: "ফ্ল্যাগ করেছেন",
    target: `${opTitle} (${lineTitle})`,
    targetBn: `${opTitle} (${lineTitle})`,
    outcome: "flagged",
    isoDate: iso
  });

  const payload = FlagResponseSchema.parse({ insightId: id, flaggedAt: iso });

  return Response.json(payload, {
    status: 200,
    headers: { "Cache-Control": "no-store" }
  });
}