// GET /api/brief/morning — deterministic, demo-stable morning brief.
//
// Assembled server-side from:
//   - src/services/lineBoard.server.buildLineBoard()
//   - src/services/insight.service.feed({ stage: "suggested" })
//   - src/services/approval.service.pending()
//
// No LLM is required; the shape leaves room for an LLM upgrade later.

import { buildMorningBrief } from "@/services/brief.server";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function GET() {
  const payload = buildMorningBrief();
  return Response.json(payload, {
    status: 200,
    headers: { "Cache-Control": "no-store" }
  });
}
