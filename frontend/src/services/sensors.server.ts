// Shared in-memory simulated sensor buffer for API routes.
// POST /api/sensors/ingest writes here; GET /api/sensors/latest reads here.
// Kept off the client Zustand store ("use client") so server routes stay valid.

import { makeRng } from "@/data/seed";
import type { SensorReading, SensorSource } from "@/data/sensors";
import { SensorReadingListSchema } from "@/services/sensors.schemas";

export const LINES = ["line-1", "line-2", "line-3", "line-4", "line-5", "line-6"];
export const MACHINES = [
  "m-1-1", "m-1-2", "m-1-3", "m-1-4", "m-1-5", "m-1-6",
  "m-2-1", "m-2-2", "m-2-3", "m-2-4", "m-2-5", "m-2-6",
  "m-3-1", "m-3-2", "m-3-3", "m-3-4", "m-3-5", "m-3-6",
  "m-4-1", "m-4-2", "m-4-3", "m-4-4", "m-4-5", "m-4-6",
  "m-5-1", "m-5-2", "m-5-3", "m-5-4", "m-5-5", "m-5-6",
  "m-6-1", "m-6-2", "m-6-3", "m-6-4", "m-6-5", "m-6-6"
];
export const METERS = ["meter:floor-1", "meter:floor-2", "meter:floor-3"];

type Profile = { metric: string; unit: string; lo: number; hi: number };
const METRIC_PROFILES: Record<SensorSource, Profile[]> = {
  rfid: [
    { metric: "scans_per_min", unit: "scans/min", lo: 8, hi: 22 },
    { metric: "miss_rate", unit: "%", lo: 0, hi: 6 }
  ],
  telemetry: [
    { metric: "vibration_rms", unit: "mm/s", lo: 0.8, hi: 4.5 },
    { metric: "temperature_c", unit: "°C", lo: 45, hi: 82 },
    { metric: "duty_cycle", unit: "%", lo: 55, hi: 95 }
  ],
  energy: [
    { metric: "kwh", unit: "kWh", lo: 42, hi: 78 },
    { metric: "peak_demand", unit: "kW", lo: 110, hi: 165 }
  ]
};

export type LineSummary = {
  id: string;
  efficiency: number;
  uptime: number;
  energyKwh: number;
};
export type MachineSummary = {
  id: string;
  vibration: number;
  temperature: number;
  dutyCycle: number;
  status: "healthy" | "at_risk" | "down";
};
export type ServerSimState = {
  tick: number;
  lines: LineSummary[];
  machines: MachineSummary[];
  readings: SensorReading[];
};

declare global {
  // eslint-disable-next-line no-var
  var __factoryBrainSimState: ServerSimState | undefined;
}

function clamp(n: number, lo: number, hi: number) {
  return Math.max(lo, Math.min(hi, n));
}

function pickOne<T>(arr: T[], rng: () => number): T {
  return arr[Math.floor(rng() * arr.length)];
}

function seedServerSim(): ServerSimState {
  const rng = makeRng(0xFA47_0001);
  const lines: LineSummary[] = LINES.map((id) => ({
    id,
    efficiency: Math.round((0.62 + rng() * 0.28) * 1000) / 1000,
    uptime: Math.round((0.86 + rng() * 0.13) * 1000) / 1000,
    energyKwh: 0
  }));
  const machines: MachineSummary[] = MACHINES.map((id) => {
    const wearIndex = Math.floor(rng() * 100);
    const vibration = 1.5 + (wearIndex / 100) * 5.5;
    const temperature = 55 + (wearIndex / 100) * 25;
    const status: MachineSummary["status"] =
      wearIndex > 80 ? "down" : wearIndex > 60 ? "at_risk" : "healthy";
    return {
      id,
      vibration: Math.round(vibration * 100) / 100,
      temperature: Math.round(temperature * 10) / 10,
      dutyCycle: Math.round((0.55 + rng() * 0.4) * 1000) / 1000,
      status
    };
  });
  return { tick: 0, lines, machines, readings: [] };
}

export function getServerSimState(): ServerSimState {
  if (!globalThis.__factoryBrainSimState) {
    globalThis.__factoryBrainSimState = seedServerSim();
  }
  return globalThis.__factoryBrainSimState;
}

export function emitSensorTick(state: ServerSimState): SensorReading[] {
  const tick = state.tick + 1;
  const rng = makeRng(0xA11CE ^ tick);
  const ts = new Date(Date.now() - (200 - tick) * 60_000).toISOString();
  const batch: SensorReading[] = [];
  let id = state.readings.length + 1;

  (Object.keys(METRIC_PROFILES) as SensorSource[]).forEach((source) => {
    const profiles = METRIC_PROFILES[source];
    const profile = pickOne(profiles, rng);
    const entityId =
      source === "rfid" ? `gate-${pickOne(["A1", "A2", "B1", "C1"], rng)}` :
      source === "telemetry" ? pickOne(MACHINES, rng) :
      pickOne(METERS, rng);
    const value = Number((rng() * (profile.hi - profile.lo) + profile.lo).toFixed(2));
    const reading: SensorReading = {
      id: `srv-sensor-${id++}`,
      source,
      entityId,
      metric: profile.metric,
      value,
      unit: profile.unit,
      ts,
      simTick: tick
    };
    batch.push(reading);

    if (source === "rfid") {
      const line = state.lines[Math.floor(rng() * state.lines.length)];
      const drift = (rng() - 0.5) * 0.04;
      line.efficiency = clamp(line.efficiency + drift, 0.4, 0.95);
      line.uptime = clamp(line.uptime + (rng() - 0.5) * 0.01, 0.7, 1);
    } else if (source === "telemetry") {
      const m = state.machines.find((x) => x.id === entityId);
      if (m) {
        if (profile.metric === "vibration_rms") m.vibration = clamp(value, 0.5, 8);
        else if (profile.metric === "temperature_c") m.temperature = clamp(value, 40, 90);
        else if (profile.metric === "duty_cycle") m.dutyCycle = clamp(value / 100, 0.2, 1);
        m.status =
          m.vibration > 6 || m.temperature > 78 ? "down" :
          m.vibration > 4.5 || m.temperature > 70 ? "at_risk" :
          "healthy";
      }
    } else if (source === "energy") {
      const kwh = value;
      state.lines.forEach((l) => {
        l.energyKwh = Math.round((l.energyKwh + kwh / state.lines.length) * 100) / 100;
      });
    }
  });

  state.tick = tick;
  state.readings = [...batch.reverse(), ...state.readings].slice(0, 200);
  return batch;
}

export function advanceSim(tick?: number): ServerSimState {
  const state = getServerSimState();
  if (typeof tick === "number") {
    const next = Math.max(state.tick + 1, tick);
    while (state.tick < next) emitSensorTick(state);
  } else {
    emitSensorTick(state);
  }
  // Validate buffer shape stays schema-clean.
  SensorReadingListSchema.parse(state.readings.slice(0, 50));
  return state;
}

export function listLatestReadings(): SensorReading[] {
  const raw = getServerSimState().readings;
  const sorted = [...raw].sort((a, b) => {
    if (b.simTick !== a.simTick) return b.simTick - a.simTick;
    return b.ts.localeCompare(a.ts);
  });
  const seen = new Set<string>();
  const out: SensorReading[] = [];
  for (const r of sorted) {
    const key = `${r.source}::${r.entityId}`;
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(r);
  }
  const sourceOrder: Record<SensorSource, number> = { rfid: 0, telemetry: 1, energy: 2 };
  out.sort((a, b) => {
    const so = sourceOrder[a.source] - sourceOrder[b.source];
    if (so !== 0) return so;
    return a.entityId.localeCompare(b.entityId);
  });
  return SensorReadingListSchema.parse(out);
}
