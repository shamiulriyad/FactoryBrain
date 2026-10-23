// POST /api/vision/analyze
// ---------------------------------------------------------------------------
// Deterministic demo mapping for the Vision Repair page. Mirrors the staged
// sample names already used on /app/vision. No real VLM in this build — the
// anomaly + repair steps are sourced from the same En/Bn templates that
// `src/data/vision.ts` exposes for the client store.
//
// Side effects (all go through `src/services/run.persistence.ts`):
//   - inserts a typed `VisionResult`
//   - persists an `Insight` and elevates it into the pending-approval queue
//     (risk tier: medium)
//   - pushes a `FloorAlert` on the whatsapp_sim channel bound to the insight
//   - logs an `ActivityEvent`
//
// Errors:
//   400 invalid_json / invalid_body   — body fails Zod validation
//   400 unknown_sample_file          — sampleFile is not a known staged name

import { NextRequest } from "next/server";
import { z } from "zod";
import {
  buildVisionResults,
  type Anomaly
} from "@/data/vision";
import { VisionResultSchema } from "@/services/sensors.schemas";
import { persistAgentRun } from "@/services/run.persistence";
import type { VisionResultT } from "@/services/sensors.schemas";

export const runtime = "nodejs";

/** The four staged sample names surfaced by `/app/vision`. */
const SAMPLE_FILES = [
  "defect-1-stitch-skip.jpg",
  "defect-2-buttonhole.jpg",
  "defect-3-seam-pucker.jpg",
  "defect-4-fabric-stain.jpg"
] as const;

const BodySchema = z.object({
  sampleFile: z.enum(SAMPLE_FILES)
});

/** Pick a stable `VisionResult` for a given staged sample name. Uses the same
 *  seed as the client store so that running an analyze produces a row whose
 *  En/Bn labels line up with what /app/vision shows in its preview tiles. */
function mapStagedSample(sampleFile: string): Anomaly {
  const seed = 0xC0FFEE_71;
  const produced = buildVisionResults(seed, 32);
  // Find the first produced row whose sampleFile matches; otherwise the
  // first one (deterministic fallback if the seed only outputs variants).
  const found = produced.find((p) => p.sampleFile === sampleFile);
  if (found) return { ...found };
  const fallback = produced[0];
  return { ...fallback, sampleFile };
}

export async function POST(req: NextRequest) {
  let body: unknown;
  try {
    body = await req.json();
  } catch {
    return Response.json({ error: "invalid_json" }, { status: 400 });
  }

  const parsed = BodySchema.safeParse(body);
  if (!parsed.success) {
    return Response.json(
      {
        error: parsed.error.issues[0]?.message?.includes("Invalid enum value")
          ? "unknown_sample_file"
          : "invalid_body",
        allowed: SAMPLE_FILES,
        issues: parsed.error.issues
      },
      { status: 400 }
    );
  }

  const { sampleFile } = parsed.data;
  const anomaly = mapStagedSample(sampleFile);

  // Assemble the structured VisionResult. Schema-validate before returning so
  // any drift in `data/vision.ts` surfaces here first.
  const result: VisionResultT = VisionResultSchema.parse({
    id: `vis-${Date.now().toString(36)}`,
    sampleFile: anomaly.sampleFile,
    anomalyLabelEn: anomaly.anomalyLabelEn,
    anomalyLabelBn: anomaly.anomalyLabelBn,
    repairStepsEn: anomaly.repairStepsEn,
    repairStepsBn: anomaly.repairStepsBn,
    confidence: anomaly.confidence,
    machineId: anomaly.machineId,
    createdAt: new Date().toISOString()
  });

  // Build an Insight draft that summarises the detection + repair plan. Risk
  // tier is forced to "medium" so the existing `shouldRequireApproval`
  // heuristic (risk != low) flips the insight into the pending-approval
  // queue. The agent id is reused for the activity log; it doesn't have to
  // exist in the agent roster — it's just an attribution id.
  const draft = {
    title: `${result.anomalyLabelEn} (${result.sampleFile})`,
    titleBn: `${result.anomalyLabelBn} (${result.sampleFile})`,
    finding: `${result.anomalyLabelEn}. Model confidence ${(result.confidence * 100).toFixed(0)}%. Sample: ${result.sampleFile}${result.machineId ? `, attributed to ${result.machineId}` : ""}.`,
    findingBn: `${result.anomalyLabelBn}। মডেল আত্মবিশ্বাস ${(result.confidence * 100).toFixed(0)}%। নমুনা: ${result.sampleFile}${result.machineId ? `, ${result.machineId} এর জন্য বরাদ্দ` : ""}।`,
    recommendation: {
      title: "Apply repair plan",
      titleBn: "মেরামতি পরিকল্পনা প্রয়োগ করুন",
      action: result.repairStepsEn.join(" → "),
      actionBn: result.repairStepsBn.join(" → "),
      riskTier: "medium" as const,
      targetStage: "pending_approval" as const
    },
    confidence: result.confidence
  };

  // Reuse the existing persistence helper so the route doesn't fork the
  // side-effect logic. Source is "demo" to mirror the agent-run demo path.
  const persisted = persistAgentRun("vision-repair-agent", "Vision Repair", draft, {
    source: "demo"
  });

  return Response.json(
    {
      simulated: true,
      source: "Simulated — demo VLM mapping (no real model call)",
      result,
      insight: persisted.insight,
      approvalPending: persisted.approvalPending,
      approvalReason: persisted.approvalReason,
      floorAlertId: persisted.floorAlertId,
      activityEventId: persisted.activityEventId
    },
    {
      status: 200,
      headers: { "Cache-Control": "no-store" }
    }
  );
}

export async function GET() {
  return Response.json(
    {
      simulated: true,
      source: "Simulated — no VLM backend wired",
      allowedSampleFiles: SAMPLE_FILES,
      method: "POST"
    },
    { headers: { "Cache-Control": "no-store" } }
  );
}
