// GET /api/line-board — current simulated line board.
//
// Returns rows derived from src/services/sensors.server (sim buffer) plus a
// small deterministic per-line seed. Response payload is Zod-strict validated
// before serialization so the contract is enforced at the API boundary.

import { buildLineBoard } from "@/services/lineBoard.server";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function GET() {
  const payload = buildLineBoard();
  return Response.json(payload, {
    status: 200,
    headers: { "Cache-Control": "no-store" }
  });
}
