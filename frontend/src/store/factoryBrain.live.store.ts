// In-memory store for the live simulated tick stream pulled from
// /api/sensors/ingest. Mirrors the pattern of `useSensorsStore`
// (Zustand `create`) but does not persist: this buffer is meant to be
// refreshed on every request so the Overview KPIs visibly move over
// time without polluting localStorage.

"use client";

import { create } from "zustand";
import type { SensorReading } from "@/data/sensors";

export type LiveLineSummary = {
  id: string;
  efficiency: number; // 0..1
  uptime: number; // 0..1
  energyKwh: number;
};

export type LiveMachineSummary = {
  id: string;
  vibration: number; // mm/s
  temperature: number; // °C
  dutyCycle: number; // 0..1
  status: "healthy" | "at_risk" | "down";
};

export type TickSnapshot = {
  /** Server-reported sim tick after this call advanced. */
  tick: number;
  /** Lines/efficiency rolled up by the route. */
  lines: LiveLineSummary[];
  /** Per-machine summary used by the Overview. */
  machines: LiveMachineSummary[];
  /** Newest readings from this tick (already newest-first). */
  readings: SensorReading[];
  /** ISO timestamp the snapshot was applied. */
  appliedAt: string;
};

type State = {
  snapshot: TickSnapshot | null;
  /** True while a POST is in-flight. */
  fetching: boolean;
  /** Last error message from the route, if any. */
  error: string | null;
  /** Manually mark a snapshot. Used by both the polling effect and the manual button. */
  setSnapshot: (s: TickSnapshot) => void;
  setFetching: (v: boolean) => void;
  setError: (e: string | null) => void;
};

export const useFactoryBrainLiveStore = create<State>((set) => ({
  snapshot: null,
  fetching: false,
  error: null,
  setSnapshot: (s) => set({ snapshot: s, error: null }),
  setFetching: (v) => set({ fetching: v }),
  setError: (e) => set({ error: e })
}));
