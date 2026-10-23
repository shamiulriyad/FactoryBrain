// PATCH /api/floor-alerts/:id
// ---------------------------------------------------------------------------
// Mark a single FloorAlert as read (or unread). Mirrors the client store's
// `markRead(...)` mutator but routes through the shared server-side buffer
// (`src/services/floorAlerts.server.ts`). Validates input with Zod; 404 when
// the id is not found; 400 when the body fails validation.

import { NextRequest } from "next/server";
import { z } from "zod";
import { markFloorAlertRead } from "@/services/floorAlerts.server";
import { FloorAlertSchema } from "@/services/sensors.schemas";

export const runtime = "nodejs";

const PatchBodySchema = z
  .object({
    read: z.boolean()
  })
  .strict();

export async function PATCH(
  req: NextRequest,
  ctx: { params: Promise<{ id: string }> }
) {
  const { id } = await ctx.params;
  if (!id) {
    return Response.json({ error: "missing_id" }, { status: 400 });
  }

  let body: unknown;
  try {
    body = await req.json();
  } catch {
    return Response.json({ error: "invalid_json" }, { status: 400 });
  }

  const parsed = PatchBodySchema.safeParse(body);
  if (!parsed.success) {
    return Response.json(
      { error: "invalid_body", issues: parsed.error.issues },
      { status: 400 }
    );
  }

  const updated = markFloorAlertRead(id, parsed.data.read);
  if (!updated) {
    return Response.json({ error: "alert_not_found", id }, { status: 404 });
  }

  // Validate the persisted row through the shared schema before returning.
  const safe = FloorAlertSchema.parse(updated);

  return Response.json(
    {
      ok: true,
      alert: safe
    },
    {
      status: 200,
      headers: { "Cache-Control": "no-store" }
    }
  );
}
