// GET /api/sensors/latest — latest SensorReading per (source, entityId)
// from the shared server ingest buffer (not the client Zustand store).

import { listLatestReadings } from "@/services/sensors.server";

export const runtime = "nodejs";

export async function GET() {
  const validated = listLatestReadings();

  return Response.json(
    {
      simulated: true,
      source: "Simulated — derived from server sensor ingest buffer",
      count: validated.length,
      readings: validated
    },
    {
      status: 200,
      headers: { "Cache-Control": "no-store" }
    }
  );
}
