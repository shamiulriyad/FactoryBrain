// Line-board server module.
// Derives a small, deterministic line-board from the simulated sensor buffer
// (src/services/sensors.server) plus a fixed per-line seed. Used by:
//
//   GET  /api/line-board          → current board
//   POST /api/line-board/refresh  → advance sim one tick, then return board
//
// All numbers are derived — no live PLC / Modbus / MQTT traffic. The
// `meta.simulated` flag makes that honesty explicit in the API payload.

import { z } from "zod";
import { LINES, getServerSimState, type LineSummary } from "@/services/sensors.server";
import { makeRng } from "@/data/seed";

// --- Types + Zod -----------------------------------------------------------

export const BottleneckEnum = z.enum(["green", "amber", "red"]);
export type Bottleneck = z.infer<typeof BottleneckEnum>;

export const LineBoardRowSchema = z.object({
  lineId: z.string().min(1),
  name: z.string().min(1),
  /** SAH efficiency 0–100, integer percentage. */
  efficiencyPct: z.number().min(0).max(100),
  /** SAH target — fixed per line. Integer percentage. */
  sahTarget: z.number().min(0).max(100).int(),
  /** SAH actual — efficiencyPct expressed against target. Integer percentage. */
  sahActual: z.number().min(0).max(100).int(),
  /** Work-in-progress bundles on this line. Integer ≥ 0. */
  wipBundles: z.number().int().nonnegative(),
  bottleneck: BottleneckEnum,
  /** Non-productive time in minutes for the current shift. Integer ≥ 0. */
  nptMinutes: z.number().int().nonnegative(),
  /** ISO timestamp of last derived update. */
  updatedAt: z.string().min(1)
});
export type LineBoardRow = z.infer<typeof LineBoardRowSchema>;

export const LineBoardResponseSchema = z.object({
  rows: z.array(LineBoardRowSchema).length(LINES.length),
  meta: z.object({
    simulated: z.literal(true),
    source: z.string().min(1),
    tick: z.number().int().nonnegative(),
    /** ISO timestamp for the snapshot. */
    updatedAt: z.string().min(1),
    /** Per-line derivation recipe, surfaced for callers to make sense of NPT/WIP. */
    notes: z.string().min(1)
  })
});
export type LineBoardResponse = z.infer<typeof LineBoardResponseSchema>;

// --- Deterministic seed (low cost, no live traffic) -----------------------

type LineSeed = {
  /** Pretty line name. Mirrors factory.store style. */
  name: string;
  /** SAH target — fixed per line so callers can diff actual vs target. */
  sahTarget: number;
  /** Workload baseline (bundles) for WIP computation. */
  baseWip: number;
  /** NPT baseline (minutes) for non-productive time. */
  baseNpt: number;
};

/**
 * Per-line seed. Order is aligned with src/services/sensors.server.LINES:
 *   line-1 .. line-6
 * Stable across deploys, small, and cheap to compute.
 */
const LINE_SEEDS: Record<string, LineSeed> = {
  "line-1": { name: "Line 1 — Polo Tee",   sahTarget: 70, baseWip: 180, baseNpt: 24 },
  "line-2": { name: "Line 2 — Crew Neck",  sahTarget: 70, baseWip: 165, baseNpt: 18 },
  "line-3": { name: "Line 3 — V-Neck",     sahTarget: 78, baseWip: 140, baseNpt: 36 },
  "line-4": { name: "Line 4 — Hoodie",     sahTarget: 70, baseWip: 210, baseNpt: 22 },
  "line-5": { name: "Line 5 — Tank Top",   sahTarget: 70, baseWip: 120, baseNpt: 14 },
  "line-6": { name: "Line 6 — Jacket",     sahTarget: 70, baseWip: 260, baseNpt: 30 }
};

function clampInt(n: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, Math.round(n)));
}

function classifyBottleneck(efficiencyPct: number, target: number): Bottleneck {
  // Red   : ≥10 pp below target (or below 50% absolute)
  // Amber : ≥5 pp below target
  // Green : at or above target
  if (efficiencyPct < 50) return "red";
  if (target - efficiencyPct >= 10) return "red";
  if (target - efficiencyPct >= 5) return "amber";
  return "green";
}

function deriveRow(line: LineSummary, tick: number): LineBoardRow {
  const seed = LINE_SEEDS[line.id] ?? {
    name: line.id,
    sahTarget: 70,
    baseWip: 150,
    baseNpt: 20
  };

  const efficiencyPct = clampInt(line.efficiency * 100, 0, 100);
  const sahActual = efficiencyPct; // percent of target met
  const bottleneck = classifyBottleneck(efficiencyPct, seed.sahTarget);

  // WIP drifts slightly with the sim tick so refresh() changes the board.
  // Deterministic from (lineId, tick) — no Math.random.
  const rng = makeRng(0xBEEF ^ hashTick(line.id, tick));
  const wipDelta = Math.floor(rng() * 21) - 10; // -10..+10
  const wipBundles = Math.max(0, seed.baseWip + wipDelta);

  // NPT grows when efficiency is below target (worst case for red).
  const gap = Math.max(0, seed.sahTarget - efficiencyPct);
  const nptMinutes = Math.max(0, seed.baseNpt + gap * 2 + Math.floor(rng() * 6));

  return {
    lineId: line.id,
    name: seed.name,
    efficiencyPct,
    sahTarget: seed.sahTarget,
    sahActual,
    wipBundles,
    bottleneck,
    nptMinutes,
    updatedAt: new Date().toISOString()
  };
}

function hashTick(lineId: string, tick: number): number {
  // FNV-1a 32-bit; small but stable.
  let h = 0x811c9dc5;
  const s = `${lineId}::${tick}`;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

// --- Public builder --------------------------------------------------------

export const LINE_BOARD_SIMULATED_LABEL =
  "Simulated — derived from src/services/sensors.server.ts (no live PLC / Modbus / MQTT traffic)";

export function buildLineBoard(): LineBoardResponse {
  const state = getServerSimState();
  const rows = state.lines.map((l) => deriveRow(l, state.tick));
  // Stable order by lineId so callers can render deterministically.
  rows.sort((a, b) => a.lineId.localeCompare(b.lineId));

  const payload: LineBoardResponse = {
    rows,
    meta: {
      simulated: true,
      source: LINE_BOARD_SIMULATED_LABEL,
      tick: state.tick,
      updatedAt: new Date().toISOString(),
      notes: "Deterministic per-line seed + latest server sim state. NPT grows with efficiency gap; WIP drifts on tick."
    }
  };

  return LineBoardResponseSchema.parse(payload);
}
