// POST /api/sensors/ingest — advance simulated sensor stream by one tick.

import { NextRequest } from "next/server";
import { z } from "zod";
import { SensorReadingListSchema } from "@/services/sensors.schemas";
import { advanceSim } from "@/services/sensors.server";

export const runtime = "nodejs";

const BodySchema = z.object({
  tick: z.number().int().nonnegative().optional()
});

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
      { error: "invalid_body", issues: parsed.error.issues },
      { status: 400 }
    );
  }

  const state = advanceSim(parsed.data.tick);
  const readings = SensorReadingListSchema.parse(state.readings.slice(0, 50));

  return Response.json(
    {
      simulated: true,
      source: "Simulated — no live PLC / Modbus / MQTT traffic",
      tick: state.tick,
      readings: readings.slice(0, 50),
      lines: state.lines.map((l) => ({
        id: l.id,
        efficiency: l.efficiency,
        uptime: l.uptime,
        energyKwh: l.energyKwh
      })),
      machines: state.machines.map((m) => ({
        id: m.id,
        vibration: m.vibration,
        temperature: m.temperature,
        dutyCycle: m.dutyCycle,
        status: m.status
      }))
    },
    {
      status: 200,
      headers: { "Cache-Control": "no-store" }
    }
  );
}

export async function GET() {
  const { getServerSimState } = await import("@/services/sensors.server");
  const state = getServerSimState();
  return Response.json(
    {
      simulated: true,
      source: "Simulated — no live PLC / Modbus / MQTT traffic",
      tick: state.tick,
      readingsCount: state.readings.length,
      linesCount: state.lines.length,
      machinesCount: state.machines.length
    },
    { headers: { "Cache-Control": "no-store" } }
  );
}
