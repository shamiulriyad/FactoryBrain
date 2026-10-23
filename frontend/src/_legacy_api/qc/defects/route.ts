// GET /api/qc/defects — Bosch-shaped defect / rework statistics.
//
// Deterministic, day-stable. No live QA capture, no LLM. The seed file is
// src/data/qc.defects.ts; the assembly is src/services/qc.defects.server.ts.

import { buildQcDefects } from "@/services/qc.defects.server";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function GET() {
  const payload = buildQcDefects();
  return Response.json(payload, {
    status: 200,
    headers: { "Cache-Control": "no-store" }
  });
}
