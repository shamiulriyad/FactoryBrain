// Sensor readings — simulated inputs feeding the agents.
// Three sources in the prototype:
//   - "rfid":      bundle-scan events from RFID gates
//   - "telemetry": machine telemetry (vibration, temp, duty cycle, energy)
//   - "energy":    floor-level energy meter aggregates
// Real prototype: every reading has a simTick so the brain can reason
// about sequence rather than wall clock alone.

import { intBetween, makeRng, pick } from "./seed";

export type SensorSource = "rfid" | "telemetry" | "energy";

export type SensorReading = {
  id: string;
  source: SensorSource;
  entityId: string; // machineId, lineId, bundleId, "meter:floor"
  metric: string; // e.g. "vibration_rms", "temperature_c", "kwh", "scans_per_min"
  value: number;
  unit: string; // "mm/s", "°C", "kWh", "scans/min"
  ts: string; // ISO timestamp
  simTick: number; // monotonically increasing sim tick
};

const METRIC_PROFILES: Record<SensorSource, Array<{ metric: string; unit: string; lo: number; hi: number }>> = {
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

const ENTITY_POOL: Record<SensorSource, string[]> = {
  rfid: ["gate-A1", "gate-A2", "gate-B1", "gate-C1"],
  telemetry: ["M-101", "M-102", "M-103", "M-201", "M-202", "M-301", "M-302"],
  energy: ["meter:floor-1", "meter:floor-2", "meter:floor-3"]
};

export function buildSensorReadings(seed = 0xC0FFEE, ticks = 24): SensorReading[] {
  const rng = makeRng(seed);
  const start = Date.now() - ticks * 60_000; // one reading per "minute" of sim time
  const out: SensorReading[] = [];
  let id = 1;

  for (let t = 0; t < ticks; t++) {
    const ts = new Date(start + t * 60_000).toISOString();
    (Object.keys(METRIC_PROFILES) as SensorSource[]).forEach((source) => {
      const profiles = METRIC_PROFILES[source];
      const entities = ENTITY_POOL[source];
      // 1–2 readings per source per tick so the dataset looks dense but not uniform.
      const count = intBetween(rng, 1, 2);
      for (let i = 0; i < count; i++) {
        const profile = pick(rng, profiles);
        const entityId = pick(rng, entities);
        const value = Number((rng() * (profile.hi - profile.lo) + profile.lo).toFixed(2));
        out.push({
          id: `sensor-${id++}`,
          source,
          entityId,
          metric: profile.metric,
          value,
          unit: profile.unit,
          ts,
          simTick: t
        });
      }
    });
  }

  return out;
}
