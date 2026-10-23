// GET /api/floor-alerts
// ---------------------------------------------------------------------------
// Lists FloorAlerts newest first. Reads the same buffer the agent-run
// route writes to (`src/services/floorAlerts.server.ts`) and validates the
// response with the Zod schema from `src/services/sensors.schemas.ts`.
//
// Empty array is fine — alerts are produced by agent activity, not seeds.

import { listFloorAlerts } from "@/services/floorAlerts.server";
import { FloorAlertListSchema } from "@/services/sensors.schemas";

export const runtime = "nodejs";

export async function GET() {
  const rows = listFloorAlerts();
  // Newest first by `createdAt` (ISO timestamp). Fall back to original order
  // when the timestamps tie, which keeps the list deterministic.
  const sorted = [...rows].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
  const validated = FloorAlertListSchema.parse(sorted);

  return Response.json(
    {
      simulated: true,
      source: "Simulated — derived from src/store/floorAlerts.store.ts",
      count: validated.length,
      alerts: validated
    },
    {
      status: 200,
      headers: { "Cache-Control": "no-store" }
    }
  );
}
