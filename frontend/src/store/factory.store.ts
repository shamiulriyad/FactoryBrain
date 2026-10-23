"use client";

import { create } from "zustand";
import { makeRng } from "@/data/seed";
import { persist as storage } from "@/lib/persist";

// --- Factory floor entities -------------------------------------------------

export type FactoryLine = {
  id: string;
  name: string;
  nameBn: string;
  /** Number of machines attached. */
  machineCount: number;
  /** Current SAH efficiency (0..1). */
  efficiency: number;
  /** Target efficiency for the day (0..1). */
  targetEfficiency: number;
  status: "healthy" | "at_risk" | "down";
  process: string;
};

export type FactoryMachine = {
  id: string;
  lineId: string;
  name: string;
  type: "sewing" | "cutting" | "finishing";
  /** Duty cycle (0..1). */
  dutyCycle: number;
  /** Last vibration reading (mm/s, C-MAPSS-shaped). */
  vibration: number;
  /** Last temperature (C). */
  temperature: number;
  status: "healthy" | "at_risk" | "down";
  /** Index for C-MAPSS-shaped degradation. Higher = older / closer to failure. */
  wearIndex: number;
};

export type FactoryOrder = {
  id: string;
  buyerId: string;
  lineId: string;
  process: string;
  status: "queued" | "in_progress" | "finishing" | "ready";
  unitsTarget: number;
  unitsDone: number;
  dueDays: number;
  risk: "healthy" | "at_risk" | "down";
};

export type FactoryRisk = {
  id: string;
  title: string;
  titleBn: string;
  description: string;
  severity: "low" | "medium" | "high";
  /** When the risk was raised. */
  isoDate: string;
  /** The entity id the risk threatens. */
  targetId?: string;
};

export type SensorEvent = {
  id: string;
  kind: "bundle_scan" | "machine_reading" | "energy_meter";
  isoDate: string;
  /** Free-form structured payload. */
  payload: Record<string, any>;
};

// --- Store ------------------------------------------------------------------

type FactoryState = {
  lines: FactoryLine[];
  machines: FactoryMachine[];
  orders: FactoryOrder[];
  risks: FactoryRisk[];
  recentEvents: SensorEvent[];
  /** Bumps when a tick runs so React components can re-subscribe cheaply. */
  tick: number;
  /** Recompute derived metrics (efficiency, risk nodes, etc.). */
  recompute: () => void;
  /** Inject a synthetic sensor event. */
  ingest: (e: Omit<SensorEvent, "id" | "isoDate"> & { isoDate?: string }) => void;
  /** Reset the synthetic data (used for tests / reset-demo). */
  reset: () => void;
};

const FACTORY_KEY = "bunonbrain:factory";

// --- Initial seed (6 lines, 36 machines, 12 orders, 3 risks) --------------

function seedFactory(): Pick<FactoryState, "lines" | "machines" | "orders" | "risks"> {
  const rng = makeRng(0xFA47_0001);

  const lineNames = [
    { name: "Line 1 — Polo Tee", nameBn: "লাইন ১ — পোলো টি" },
    { name: "Line 2 — Crew Neck", nameBn: "লাইন ২ — ক্রু নেক" },
    { name: "Line 3 — V-Neck", nameBn: "লাইন ৩ — ভি-নেক" },
    { name: "Line 4 — Hoodie", nameBn: "লাইন ৪ — হুডি" },
    { name: "Line 5 — Tank Top", nameBn: "লাইন ৫ — ট্যাঙ্ক টপ" },
    { name: "Line 6 — Jacket", nameBn: "লাইন ৬ — জ্যাকেট" }
  ];

  const lines: FactoryLine[] = lineNames.map((ln, i) => {
    const efficiency = 0.62 + rng() * 0.28; // 62–90%
    const targetEfficiency = i === 2 ? 0.78 : 0.7; // line 3 under-target
    const status: FactoryLine["status"] =
      efficiency < targetEfficiency - 0.05
        ? "at_risk"
        : efficiency < 0.55
        ? "down"
        : "healthy";
    return {
      id: `line-${i + 1}`,
      name: ln.name,
      nameBn: ln.nameBn,
      machineCount: 6,
      efficiency: Math.round(efficiency * 1000) / 1000,
      targetEfficiency,
      status,
      process: ["Cutting", "Sewing", "QC", "Finishing"][i % 4]
    };
  });

  const machines: FactoryMachine[] = [];
  lines.forEach((line) => {
    for (let m = 0; m < line.machineCount; m++) {
      const wearIndex = Math.floor(rng() * 100);
      // C-MAPSS-shaped degradation: when wearIndex > 70, machine trending toward failure
      const baseVib = 1.5 + (wearIndex / 100) * 5.5; // 1.5 – 7 mm/s
      const baseTemp = 55 + (wearIndex / 100) * 25; // 55 – 80 C
      const dutyCycle = 0.55 + rng() * 0.4;
      let status: FactoryMachine["status"] = "healthy";
      if (wearIndex > 80) status = "down";
      else if (wearIndex > 60) status = "at_risk";

      machines.push({
        id: `m-${line.id.slice(5)}-${m + 1}`,
        lineId: line.id,
        name: `${line.name.split(" — ")[0]} #${m + 1}`,
        type: m < 2 ? "cutting" : m < 4 ? "sewing" : "finishing",
        dutyCycle: Math.round(dutyCycle * 1000) / 1000,
        vibration: Math.round(baseVib * 100) / 100,
        temperature: Math.round(baseTemp * 10) / 10,
        status,
        wearIndex
      });
    }
  });

  const orderCount = 12;
  const orders: FactoryOrder[] = [];
  for (let i = 0; i < orderCount; i++) {
    const line = lines[i % lines.length];
    const unitsTarget = 800 + Math.floor(rng() * 4000);
    const unitsDone = Math.floor(unitsTarget * (0.2 + rng() * 0.7));
    const dueDays = Math.floor(rng() * 14);
    const risk: FactoryOrder["risk"] = line.status === "down" ? "down" : line.status === "at_risk" ? "at_risk" : "healthy";
    orders.push({
      id: `po-${4471 + i}`,
      buyerId: `buyer-${(i % 6) + 1}`,
      lineId: line.id,
      process: line.process,
      status: unitsDone >= unitsTarget ? "ready" : "in_progress",
      unitsTarget,
      unitsDone,
      dueDays,
      risk
    });
  }

  const risks: FactoryRisk[] = [
    {
      id: "risk-line3-throughput",
      title: "Line 3 efficiency below target",
      titleBn: "লাইন ৩ দক্ষতা লক্ষ্যমাত্রার নিচে",
      description: "Line 3 has been averaging 12–18% below its 78% efficiency target for the last 4 hours.",
      severity: "high",
      isoDate: new Date(Date.now() - 25 * 60_000).toISOString(),
      targetId: "line-3"
    },
    {
      id: "risk-machine-2-5",
      title: "Sewing machine Line 2 #5 trending toward bearing wear",
      titleBn: "সেলাই মেশিন লাইন ২ #৫ বিয়ারিং ক্ষয়ের দিকে",
      description: "Vibration readings have crossed 5.2 mm/s and temperature is above 75°C for the last 90 minutes.",
      severity: "medium",
      isoDate: new Date(Date.now() - 90 * 60_000).toISOString(),
      targetId: "m-2-5"
    },
    {
      id: "risk-energy-spike",
      title: "Compressor energy spike on Line 4",
      titleBn: "লাইন ৪ এ কম্প্রেসর শক্তি স্পাইক",
      description: "Energy meter readings on the Line 4 compressor are 22% above the rolling 7-day average.",
      severity: "medium",
      isoDate: new Date(Date.now() - 3 * 60 * 60_000).toISOString(),
      targetId: "line-4"
    }
  ];

  return { lines, machines, orders, risks };
}

const initial = seedFactory();

export const useFactoryStore = create<FactoryState>((set, get) => ({
  ...initial,
  recentEvents: [],
  tick: 0,
  recompute: () => set((s) => ({ tick: s.tick + 1 })),
  ingest: (e) => {
    const ev: SensorEvent = {
      id: `ev-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`,
      isoDate: e.isoDate ?? new Date().toISOString(),
      kind: e.kind,
      payload: e.payload
    };
    set((s) => {
      const recentEvents = [ev, ...s.recentEvents].slice(0, 60);

      // Update derived state based on event type.
      let lines = s.lines;
      let machines = s.machines;
      let risks = s.risks;

      if (e.kind === "machine_reading" && e.payload.machineId) {
        const mid = e.payload.machineId as string;
        machines = s.machines.map((m) => {
          if (m.id !== mid) return m;
          const vibration = Number(e.payload.vibration ?? m.vibration);
          const temperature = Number(e.payload.temperature ?? m.temperature);
          const wearIndex = Math.min(100, m.wearIndex + 1);
          let status: FactoryMachine["status"] = m.status;
          if (wearIndex > 80 || vibration > 6 || temperature > 78) status = "down";
          else if (wearIndex > 60 || vibration > 4.5 || temperature > 70) status = "at_risk";
          else status = "healthy";
          return { ...m, vibration, temperature, wearIndex, status };
        });
      } else if (e.kind === "bundle_scan" && e.payload.lineId) {
        const lid = e.payload.lineId as string;
        lines = s.lines.map((l) => {
          if (l.id !== lid) return l;
          const eff = Number(e.payload.efficiency ?? l.efficiency);
          let status: FactoryLine["status"] = l.status;
          if (eff < l.targetEfficiency - 0.05) status = "at_risk";
          else if (eff < 0.55) status = "down";
          else status = "healthy";
          return { ...l, efficiency: eff, status };
        });
      } else if (e.kind === "energy_meter") {
        // No derived state change — energy reading logged for the morning brief.
      }

      // Persist a snapshot every 5 ticks.
      const snap = { lines, machines, orders: s.orders, risks };
      if (typeof window !== "undefined" && get().tick % 5 === 0) {
        try {
          storage.set(FACTORY_KEY, snap);
        } catch {}
      }

      return { lines, machines, recentEvents, risks, tick: s.tick + 1 };
    });
  },
  reset: () => {
    const fresh = seedFactory();
    set({ ...fresh, recentEvents: [], tick: 0 });
    try {
      storage.set(FACTORY_KEY, fresh);
    } catch {}
  }
}));

// --- Synth tick driver ------------------------------------------------------
// Pushes synthetic sensor events every few seconds so the brain re-renders.

let synthTimer: ReturnType<typeof setInterval> | null = null;

export function startSyntheticSensorStream(): () => void {
  if (typeof window === "undefined") return () => {};
  if (synthTimer) return () => synthTimer && clearInterval(synthTimer);
  const store = useFactoryStore.getState();
  const tick = () => {
    const state = useFactoryStore.getState();
    const lines = state.lines;
    const machines = state.machines;
    if (!lines.length || !machines.length) return;
    const rng = Math.random;

    // Randomly pick one event type per tick.
    const pick = rng();
    if (pick < 0.5) {
      // Bundle scan
      const line = lines[Math.floor(rng() * lines.length)];
      const drift = (rng() - 0.5) * 0.04;
      const eff = Math.max(0.4, Math.min(0.95, line.efficiency + drift));
      state.ingest({ kind: "bundle_scan", payload: { lineId: line.id, efficiency: eff, ts: Date.now() } });
    } else if (pick < 0.9) {
      // Machine reading
      const m = machines[Math.floor(rng() * machines.length)];
      const vib = Math.max(1.2, Math.min(7.5, m.vibration + (rng() - 0.5) * 0.4));
      const temp = Math.max(50, Math.min(85, m.temperature + (rng() - 0.5) * 1.5));
      state.ingest({ kind: "machine_reading", payload: { machineId: m.id, vibration: vib, temperature: temp } });
    } else {
      // Energy meter
      state.ingest({ kind: "energy_meter", payload: { kwh: 18 + rng() * 12, lineId: lines[Math.floor(rng() * lines.length)].id } });
    }
  };
  synthTimer = setInterval(tick, 4000);
  return () => {
    if (synthTimer) clearInterval(synthTimer);
    synthTimer = null;
  };
}
