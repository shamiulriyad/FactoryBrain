// Sensors store — keeps the slice of recent sensor readings the agents see.
// Pattern mirrors ask.store.ts: Zustand state, persisted under a
// "bunonbrain:*" key, with a small mutator set and a reset hook for the
// demo-reset affordance.

"use client";

import { create } from "zustand";
import { persist as storage } from "@/lib/persist";
import { buildSensorReadings, type SensorReading } from "@/data/sensors";
import { SensorReadingListSchema } from "@/services/sensors.schemas";

const SENSORS_KEY = "bunonbrain:sensors";
/** How many readings we keep in memory + localStorage. */
const MAX_READINGS = 200;

function readValidated(): SensorReading[] {
  const raw = storage.get<unknown>(SENSORS_KEY, []);
  const parsed = SensorReadingListSchema.safeParse(raw);
  if (parsed.success) return parsed.data;
  // Drop corrupted state — keep the app booting.
  return [];
}

type SensorsState = {
  readings: SensorReading[];
  /** Push a single reading (capped + validated, persisted). */
  push: (r: SensorReading) => void;
  /** Push many readings at once (capped + validated, persisted). */
  pushMany: (rs: SensorReading[]) => void;
  markRead: (id: string) => void;
  reset: () => void;
};

export const useSensorsStore = create<SensorsState>((set, get) => ({
  readings: readValidated(),
  push: (r) => {
    const cur = get().readings.filter((x) => x.id !== r.id);
    const next = [r, ...cur].slice(0, MAX_READINGS);
    storage.set(SENSORS_KEY, next);
    set({ readings: next });
  },
  pushMany: (rs) => {
    if (!rs.length) return;
    const seen = new Set(get().readings.map((x) => x.id));
    const fresh = rs.filter((r) => !seen.has(r.id));
    if (!fresh.length) return;
    const next = [...fresh.reverse(), ...get().readings].slice(0, MAX_READINGS);
    storage.set(SENSORS_KEY, next);
    set({ readings: next });
  },
  markRead: (id) => {
    const next = get().readings.map((r) => (r.id === id ? { ...r } : r));
    storage.set(SENSORS_KEY, next);
    set({ readings: next });
  },
  reset: () => {
    // Small initial set the agents can reason over on first load.
    const fresh = buildSensorReadings(0xC0FFEE, 6);
    storage.set(SENSORS_KEY, fresh);
    set({ readings: fresh });
  }
}));

/** First-run helper: seeds a tiny initial set when no persisted state exists. */
export function ensureSensorsSeeded(): void {
  if (useSensorsStore.getState().readings.length === 0) {
    useSensorsStore.getState().reset();
  }
}
