// POST /api/line-board/refresh — advance sim one tick then return the board.
//
// Optional alias for GET /api/line-board. The POST form lets clients trigger
// a fresh sensor tick before re-reading the board (e.g. after a "Simulate
// tick" click) without making two round-trips.
//
// Body is optional. When present, accepts { tick?: number } to advance the
// sim by an absolute target tick (mirrors /api/sensors/ingest semantics).

import { NextRequest } from "next/server";
import { z } from "zod";
import { advanceSim } from "@/services/sensors.server";
import { buildLineBoard } from "@/services/lineBoard.server";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

const BodySchema = z.object({
  tick: z.number().int().nonnegative().optional()
});

export async function POST(req: NextRequest) {
  // Body is optional — clients may POST with no body to "tick once".
  let raw: unknown = undefined;
  try {
    raw = await req.json();
  } catch {
    raw = undefined;
  }

  let tickOverride: number | undefined;
  if (raw !== undefined && raw !== null) {
    const parsed = BodySchema.safeParse(raw);
    if (!parsed.success) {
      return Response.json(
        { error: "invalid_body", issues: parsed.error.issues },
        { status: 400 }
      );
    }
    tickOverride = parsed.data.tick;
  }

  advanceSim(tickOverride);
  const payload = buildLineBoard();

  return Response.json(payload, {
    status: 200,
    headers: { "Cache-Control": "no-store" }
  });
}