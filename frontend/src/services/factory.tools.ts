// Typed "MCP-style" tool surface for the three factory agents.
// Each tool returns a deterministic JSON-serialisable payload so the Ask page
// can stream them into the answer, and so every tool call shows up in the
// Approvals/Activity views.

import type { FactoryLine, FactoryMachine } from "@/store/factory.store";
import { dataset } from "@/services/dataset";
import { getManualCorpus } from "@/data/manuals";
import type { SensorReading } from "@/data/sensors";
import type { DocSourceT, ManualDocT } from "@/services/sensors.schemas";

/** Lazily resolve factory store so server bundle never touches zustand directly. */
function state() {
  const mod = require("@/store/factory.store") as typeof import("@/store/factory.store");
  return mod.useFactoryStore.getState();
}

/** Lazily resolve the sensors store. Same SSR-safety pattern as `state()` —
 *  the sensors store is client-only, so we load it on demand. Returns an
 *  empty array on the server so the route still produces a payload. */
function sensorsReadings(): SensorReading[] {
  try {
    const mod = require("@/store/sensors.store") as typeof import("@/store/sensors.store");
    return mod.useSensorsStore.getState().readings;
  } catch {
    return [];
  }
}

export type LineStatusTool = {
  line: FactoryLine;
  bottleneck: string;
  ordersAtRisk: number;
};

export type MachineHealthTool = {
  machine: FactoryMachine;
  rulEstimate: number; // remaining useful life in days (C-MAPSS-shaped)
  reason: string;
};

export type EnergyUsageTool = {
  lineId: string;
  windowHours: number;
  totalKwh: number;
  baselineKwh: number;
  deltaPct: number;
};

/** Deterministic compressor duty recommendation. Driven by the latest
 *  energy `SensorReading`s + the per-line totals from `get_energy_usage`.
 *  No ML / RL — just a transparent rule (avg deltaPct + peak/median ratio).
 *  `simulated: true` is set explicitly so consumers can label it as such. */
export type EnergyDutyTool = {
  /** "all" represents the floor-wide aggregate. Per-line when scoped. */
  lineId: string;
  /** Current compressor duty cycle (0..100). */
  currentDutyPct: number;
  /** Recommended compressor duty cycle (0..100). */
  recommendedDutyPct: number;
  /** Expected kWh saved over the rolling window if recommendation applied. */
  expectedKwhSaved: number;
  rationaleEn: string;
  rationaleBn: string;
  /** 0..1 — driving score. Higher = more urgent. */
  score: number;
  /** Provenance: how many sensor readings + line totals the call consulted. */
  basedOn: {
    sensorReadings: number;
    lineCount: number;
    window: "1h" | "24h" | "7d";
  };
  /** Always `true` — explicit label so callers can show "Simulated / not RL". */
  simulated: true;
};

export type ManualSearchTool = {
  query: string;
  hits: Array<{
    id: string;
    title: string;
    /** English or Bangla excerpt (locale inferred from the query shape). */
    snippet: string;
    /** "manual" or "sensor_log". */
    source: DocSourceT;
    /** 0..1 ranking score, used by Ask/Manager agent for ordering. */
    score: number;
    /** Any tags that contributed to the ranking. */
    matchedTags?: string[];
  }>;
};

type SearchHit = ManualSearchTool["hits"][number];

export const factoryTools = {
  get_line_status(lineId?: string): LineStatusTool[] {
    const s = state();
    const lines = lineId ? s.lines.filter((l) => l.id === lineId) : s.lines;
    return lines.map((line) => {
      const ordersAtRisk = s.orders.filter(
        (o) => o.lineId === line.id && o.risk !== "healthy"
      ).length;
      const bottleneck = line.status === "down"
        ? "Line stopped — last stop cause: power fault"
        : line.efficiency < line.targetEfficiency - 0.05
        ? `${line.process} operation below SAH target by ${Math.round((line.targetEfficiency - line.efficiency) * 100)}%`
        : `${line.process} on target`;
      return { line, bottleneck, ordersAtRisk };
    });
  },

  get_machine_health(machineId?: string): MachineHealthTool[] {
    const s = state();
    const machines = machineId
      ? s.machines.filter((m) => m.id === machineId)
      : s.machines.filter((m) => m.status !== "healthy");
    return machines.map((m) => {
      // C-MAPSS-shaped RUL estimate (very rough, just for the demo).
      const rul = Math.max(0, Math.round((100 - m.wearIndex) * 1.6));
      const reason = m.vibration > 5.5
        ? `Vibration ${m.vibration} mm/s exceeds threshold`
        : m.temperature > 75
        ? `Bearing temp ${m.temperature}°C trending up`
        : m.wearIndex > 60
        ? `Wear index ${m.wearIndex}/100 over baseline`
        : "Within baseline";
      return { machine: m, rulEstimate: rul, reason };
    });
  },

  get_energy_usage(timeframe: "1h" | "24h" | "7d" = "1h"): EnergyUsageTool[] {
    const s = state();
    const windowHours = timeframe === "1h" ? 1 : timeframe === "24h" ? 24 : 24 * 7;
    // Pull the latest energy reading per (source, entityId) once, so the
    // tool stays deterministic without touching `Math.random()`.
    const latestEnergyByLine = latestEnergyByLineId(sensorsReadings());
    return s.lines.map((line) => {
      const baseline = 24 * windowHours; // ~24 kWh baseline per hour per line
      const reading = latestEnergyByLine.get(line.id);
      // If we have a current energy reading, derive the per-window total from
      // it; otherwise fall back to the per-line baseline.
      const perHour = reading ? reading : baseline / windowHours;
      const total = Math.max(0, Math.round(perHour * windowHours * 100) / 100);
      const baselineKwh = Math.round(baseline * 100) / 100;
      const deltaPct = Math.round(((total - baselineKwh) / baselineKwh) * 1000) / 10;
      return { lineId: line.id, windowHours, totalKwh: total, baselineKwh, deltaPct };
    });
  },

  /** Deterministic compressor duty recommendation.
   *
   *  Inputs (deterministic):
   *    - Latest energy SensorReadings from the sensors store
   *    - Per-line totals from `get_energy_usage(timeframe)`
   *
   *  Score (0..1) = clamp( (avgDeltaPct / 30) * 0.5 + (peakFactor - 1) * 0.5, 0, 1 )
   *    where peakFactor = peak kWh / median kWh across energy readings.
   *
   *  Mapping:
   *    - score > 0.6  → "shift-load to off-peak; cap compressor duty at 70%"
   *    - 0.3 ≤ s ≤ 0.6 → "trim duty 10% during dryer cycles"
   *    - score < 0.3  → "duty nominal; no change"
   *
   *  No ML / RL — explicit rule, `simulated: true` so consumers can label
   *  the surface as a deterministic demo. */
  recommend_energy_duty(opts?: { lineId?: string; timeframe?: "1h" | "24h" | "7d" }): EnergyDutyTool {
    const timeframe = opts?.timeframe ?? "1h";
    const usageRows = factoryTools.get_energy_usage(timeframe);
    const scoped = opts?.lineId
      ? usageRows.filter((r) => r.lineId === opts.lineId)
      : usageRows;
    const s = state();

    // Floor-wide aggregate energy kWh over the window. Used for both the
    // expected savings estimate and the floor-wide ("all") suggestion.
    const totalKwh = scoped.reduce((acc, r) => acc + r.totalKwh, 0);
    const baselineKwh = scoped.reduce((acc, r) => acc + r.baselineKwh, 0);
    const avgDeltaPct =
      scoped.length > 0
        ? scoped.reduce((acc, r) => acc + r.deltaPct, 0) / scoped.length
        : 0;

    // Peak vs median ratio from the latest energy SensorReadings.
    const readings = sensorsReadings().filter((r) => r.source === "energy" && r.metric === "kwh");
    const kwhValues = readings.map((r) => r.value).sort((a, b) => a - b);
    const peakFactor =
      kwhValues.length > 0 ? peakVsMedian(kwhValues) : 1;

    // Deterministic score in [0, 1].
    const raw = (Math.max(0, avgDeltaPct) / 30) * 0.5 + Math.max(0, peakFactor - 1) * 0.5;
    const score = Math.min(1, Math.round(raw * 100) / 100);

    // Map score to a duty suggestion. Current duty is the rolling average
    // of the latest per-line duty cycles from the live store — when no
    // machines are present (server path), fall back to 85%.
    const linesToUse = opts?.lineId ? s.lines.filter((l) => l.id === opts.lineId) : s.lines;
    const dutyRollup = avgDuty(linesToUse.flatMap((l) => s.machines.filter((m) => m.lineId === l.id)));
    const currentDutyPct = Math.round((dutyRollup > 0 ? dutyRollup : 0.85) * 100);

    const { recommendedDutyPct, rationaleEn, rationaleBn } = mapDuty(score, currentDutyPct);

    // Expected kWh saved = (current - recommended) / current * total kWh.
    const expectedKwhSaved =
      currentDutyPct > 0 && recommendedDutyPct < currentDutyPct
        ? Math.round(((currentDutyPct - recommendedDutyPct) / currentDutyPct) * totalKwh * 100) / 100
        : 0;

    return {
      lineId: opts?.lineId ?? "all",
      currentDutyPct,
      recommendedDutyPct,
      expectedKwhSaved,
      rationaleEn,
      rationaleBn,
      score,
      basedOn: {
        sensorReadings: readings.length,
        lineCount: scoped.length,
        window: timeframe
      },
      simulated: true
    };
  },

  search_manual(query: string, opts?: { limit?: number; locale?: "en" | "bn" }): ManualSearchTool {
    const q = query.trim().toLowerCase();
    const locale: "en" | "bn" = opts?.locale ?? "en";
    const limit = opts?.limit ?? 5;
    if (!q) return { query, hits: [] };

    // Tokenise once. Bengali words don't have whitespace inside the seeded
    // bodies, but we keep the regex split generic for forward-compat.
    const tokens = q.split(/\s+/).filter((t) => t.length >= 2);

    // Walk the canonical corpus + still consult legacy `dataset.policies` for
    // backward compatibility. Manual hits dominate; policies surface only when
    // they're truly relevant.
    const manuals = getManualCorpus();
    type ScoredManual = SearchHit & { _raw: ManualDocT };
    const scored: ScoredManual[] = manuals.map((doc) => {
      const titleEn = doc.titleEn.toLowerCase();
      const titleBn = doc.titleBn.toLowerCase();
      const bodyEn = doc.bodyEn.toLowerCase();
      const bodyBn = doc.bodyBn.toLowerCase();

      let score = 0;
      const matched: string[] = [];

      for (const t of tokens) {
        if (!t) continue;
        if (titleEn.includes(t) || titleBn.includes(t)) {
          score += 3;
        }
        if (bodyEn.includes(t) || bodyBn.includes(t)) {
          score += 1;
          // Bangla-prefixed body hits get a small extra boost so the demo
          // surfaces them consistently when the user wrote in Bangla.
          if (bodyBn.includes(t)) score += 0.5;
        }
        if (doc.tags.some((tag) => tag === t || tag.includes(t))) {
          score += 2;
          matched.push(t);
        }
      }

      // Pick the best locale's snippet for the hit. Falls back to English.
      const useBn = locale === "bn" || titleBn.includes(q);
      const snippet = (useBn ? doc.bodyBn : doc.bodyEn).slice(0, 160);

      return {
        id: doc.id,
        title: useBn ? doc.titleBn : doc.titleEn,
        snippet,
        source: doc.source,
        score: Math.round(score * 100) / 100,
        matchedTags: matched.length ? matched : undefined,
        _raw: doc
      };
    });

    // Strip the internal `_raw` projection from manual hits before they
    // touch the public surface.
    const cleanManuals: SearchHit[] = scored.map(({ _raw, ...rest }) => {
      void _raw;
      return rest;
    });

    // Fallback: surface a couple of legacy policies so the tool never returns
    // empty for queries that mention "policy" / "compliance".
    const policyFallback: SearchHit[] = dataset.policies
      .filter((p) => tokens.some((t) => p.title.toLowerCase().includes(t) || p.body.toLowerCase().includes(t)))
      .slice(0, 2)
      .map((p) => ({
        id: p.id,
        title: p.title,
        snippet: p.body.slice(0, 140),
        source: "manual" as DocSourceT,
        score: 0.1,
        matchedTags: undefined
      }));

    const merged = [...cleanManuals, ...policyFallback]
      .filter((h) => h.score > 0)
      .sort((a, b) => b.score - a.score)
      .slice(0, limit);

    return { query, hits: merged };
  }
};

// --- helpers ---------------------------------------------------------------

/** Map latest `energy` SensorReadings to a per-lineId kWh value. Picks the
 *  newest reading for each entityId and assigns it to the line the entity
 *  is associated with — entity ids are line-shaped (`meter:floor-N` maps to
 *  `line-N`). When a meter can't be mapped, the result is empty. */
function latestEnergyByLineId(readings: SensorReading[]): Map<string, number> {
  const byEntity = new Map<string, SensorReading>();
  for (const r of readings) {
    if (r.source !== "energy") continue;
    const prev = byEntity.get(r.entityId);
    if (!prev || r.ts > prev.ts) byEntity.set(r.entityId, r);
  }
  const out = new Map<string, number>();
  for (const [entityId, reading] of byEntity) {
    const lineId = entityIdToLineId(entityId);
    if (lineId) out.set(lineId, reading.value);
  }
  return out;
}

/** `meter:floor-N` → `line-N`. Falls back to `entityId` if it already looks
 *  like a line id. */
function entityIdToLineId(entityId: string): string | null {
  const m = entityId.match(/^meter:floor-(\d+)$/);
  if (m) return `line-${m[1]}`;
  if (/^line-\d+$/.test(entityId)) return entityId;
  return null;
}

/** Peak / median ratio. Pure deterministic math — no random sampling. */
function peakVsMedian(sortedAsc: number[]): number {
  if (sortedAsc.length === 0) return 1;
  const mid = Math.floor(sortedAsc.length / 2);
  const median = sortedAsc.length % 2 === 0
    ? (sortedAsc[mid - 1] + sortedAsc[mid]) / 2
    : sortedAsc[mid];
  const peak = sortedAsc[sortedAsc.length - 1];
  if (median <= 0) return 1;
  return peak / median;
}

/** Average duty cycle across the given machines (0..1). Returns 0 when no
 *  machines are supplied so callers can decide on a fallback. */
function avgDuty(machines: FactoryMachine[]): number {
  if (machines.length === 0) return 0;
  const sum = machines.reduce((acc, m) => acc + m.dutyCycle, 0);
  return sum / machines.length;
}

/** Map score → recommended duty + a one-line rationale (En/Bn). Clamped
 *  so the recommendation is always within a safe operating window
 *  (50–95%). */
function mapDuty(score: number, currentDutyPct: number): {
  recommendedDutyPct: number;
  rationaleEn: string;
  rationaleBn: string;
} {
  if (score > 0.6) {
    return {
      recommendedDutyPct: Math.min(currentDutyPct, 70),
      rationaleEn:
        "Score is high — shift compressor load to off-peak and cap duty at 70% for the next shift.",
      rationaleBn:
        "স্কোর উচ্চ — পরবর্তী শিফটে কম্প্রেসর লোড অফ-পিকে স্থানান্তর করুন এবং ডিউটি ৭০% এ ক্যাপ করুন।"
    };
  }
  if (score >= 0.3) {
    return {
      recommendedDutyPct: Math.max(50, currentDutyPct - 10),
      rationaleEn:
        "Score is mid — trim compressor duty ~10% during dryer cycles for the next 90 minutes.",
      rationaleBn:
        "স্কোর মাঝারি — পরবর্তী ৯০ মিনিটে ড্রায়ার সাইকেলের সময় কম্প্রেসর ডিউটি ~১০% কমান।"
    };
  }
  return {
    recommendedDutyPct: Math.max(50, currentDutyPct),
    rationaleEn: "Score is low — compressor duty nominal; no change required.",
    rationaleBn: "স্কোর কম — কম্প্রেসর ডিউটি স্বাভাবিক; কোনো পরিবর্তন প্রয়োজন নেই।"
  };
}
