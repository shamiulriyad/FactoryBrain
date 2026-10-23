// Factory Brain graph — derives from the factory store (lines / machines / orders)
// and from the existing supplier/buyer/policies/goals seed data, relabelled into
// the factory domain.

import type { Supplier } from "./suppliers";
import type { Order } from "./orders";
import type { Customer } from "./customers";
import { policies } from "./policies";

/** Lazily resolve the factory store so server bundle never touches zustand directly. */
function getFactory() {
  const mod = require("@/store/factory.store") as typeof import("@/store/factory.store");
  return mod.useFactoryStore.getState();
}

export type EntityKind =
  | "line"
  | "machine"
  | "order"
  | "buyer"
  | "supplier"
  | "process"
  | "target"
  | "risk"
  | "compliance";

export type BrainEntity = {
  id: string;
  kind: EntityKind;
  label: string;
  labelBn: string;
  weight: number;
  meta?: Record<string, string | number>;
  status?: "healthy" | "at_risk" | "down";
};

export type BrainEdge = {
  id: string;
  source: string;
  target: string;
  weight: number;
  strong?: boolean;
  label?: string;
  kind?:
    | "assigned_to"
    | "contains"
    | "follows"
    | "has"
    | "threatens"
    | "sourced_from"
    | "shipped_to";
};

/**
 * Build the factory graph. Reads from the factory store (lines / machines /
 * orders / risks) and from the legacy supplier / customer / policy / goals seed
 * — those legacy seeds are repurposed as fabric suppliers, buyers, and Higg
 * FEM-style compliance docs.
 */
export function buildGraph(
  _products: any[], // legacy seed — unused; kept for signature parity
  customers: Customer[],
  suppliers: Supplier[],
  orders: Order[],
  goalsList: Array<{ id: string; label: string; labelBn: string }>
): { nodes: BrainEntity[]; edges: BrainEdge[] } {
  const nodes: BrainEntity[] = [];
  const edges: BrainEdge[] = [];

  const factory = getFactory();

  // --- Lines ---
  for (const line of factory.lines) {
    nodes.push({
      id: line.id,
      kind: "line",
      label: line.name,
      labelBn: line.nameBn,
      weight: 0.7,
      meta: {
        process: line.process,
        efficiency: `${Math.round(line.efficiency * 100)}%`,
        target: `${Math.round(line.targetEfficiency * 100)}%`
      },
      status: line.status
    });
  }

  // --- Machines (every machine) ---
  for (const m of factory.machines) {
    nodes.push({
      id: m.id,
      kind: "machine",
      label: m.name,
      labelBn: m.name,
      weight: m.status === "down" ? 0.9 : m.status === "at_risk" ? 0.7 : 0.5,
      meta: {
        line: m.lineId,
        type: m.type,
        vibration: `${m.vibration} mm/s`,
        temperature: `${m.temperature}°C`,
        wear: `${m.wearIndex}/100`
      },
      status: m.status
    });
    edges.push({
      id: `e-${m.id}-${m.lineId}`,
      source: m.lineId,
      target: m.id,
      kind: "contains",
      label: "contains",
      weight: 0.6
    });
  }

  // --- Processes (Cutting / Sewing / QC / Finishing) ---
  const processes = Array.from(new Set(factory.lines.map((l) => l.process))).map(
    (p, i) => ({
      id: `proc-${p.toLowerCase().replace(/\s+/g, "-")}`,
      label: p,
      labelBn: ({ Cutting: "কাটিং", Sewing: "সেলাই", QC: "কিউসি", Finishing: "ফিনিশিং" } as Record<string, string>)[p] ?? p,
      weight: 0.5,
      kind: "process" as EntityKind
    })
  );
  for (const p of processes) {
    nodes.push(p);
  }

  // --- Targets (rebuilt from goals seed + throughput target per line) ---
  const throughputTargets = factory.lines.map((line) => ({
    id: `tgt-eff-${line.id}`,
    label: `${line.name.split(" — ")[0]} efficiency ≥ ${Math.round(line.targetEfficiency * 100)}%`,
    labelBn: `${line.nameBn.split(" — ")[1] ?? line.nameBn} দক্ষতা ≥ ${Math.round(line.targetEfficiency * 100)}%`,
    weight: 0.6,
    kind: "target" as EntityKind,
    meta: { lineId: line.id, metric: "efficiency", target: line.targetEfficiency }
  }));
  for (const t of throughputTargets) {
    nodes.push(t);
    edges.push({
      id: `e-${t.id}-${t.id.replace("tgt-eff-", "proc-")}`,
      source: factory.lines.find((l) => `tgt-eff-${l.id}` === t.id)!.process
        ? processes.find((p) => p.label === factory.lines.find((l) => `tgt-eff-${l.id}` === t.id)!.process)!.id
        : processes[0].id,
      target: t.id,
      kind: "has",
      label: "has target",
      weight: 0.5
    });
  }

  // Carry through any user goals as targets too (legacy seed).
  for (const g of goalsList.slice(0, 4)) {
    nodes.push({
      id: g.id,
      kind: "target",
      label: g.label,
      labelBn: g.labelBn,
      weight: 0.5
    });
  }

  // --- Orders (top 18 by units target, drawn from factory store, but legacy
  //     orders array also contributes as "buyer-shipped history" — we use the
  //     factory store's active POs because they have the right shape) ---
  const topOrders = [...factory.orders]
    .sort((a, b) => b.unitsTarget - a.unitsTarget)
    .slice(0, 18);
  for (const o of topOrders) {
    const line = factory.lines.find((l) => l.id === o.lineId);
    const buyer = customers.find((c) => c.id === o.buyerId) ?? customers[0];
    nodes.push({
      id: o.id,
      kind: "order",
      label: `${o.id} — ${buyer.name}`,
      labelBn: `${o.id} — ${buyer.name}`,
      weight: 0.65,
      meta: {
        line: line?.name ?? o.lineId,
        units: `${o.unitsDone.toLocaleString()} / ${o.unitsTarget.toLocaleString()}`,
        dueDays: o.dueDays
      },
      status: o.risk
    });
    edges.push({
      id: `e-${o.id}-${o.lineId}`,
      source: o.id,
      target: o.lineId,
      kind: "assigned_to",
      label: "assigned to",
      weight: 0.7,
      strong: o.risk !== "healthy"
    });
    edges.push({
      id: `e-${o.id}-${buyer.id}`,
      source: o.id,
      target: buyer.id,
      kind: "shipped_to",
      label: "shipped to",
      weight: 0.5
    });
  }

  // --- Buyers (top 12 by LTV — repurposed from customer seed) ---
  const topBuyers = [...customers].sort((a, b) => b.ltvBdt - a.ltvBdt).slice(0, 12);
  for (const b of topBuyers) {
    nodes.push({
      id: b.id,
      kind: "buyer",
      label: b.name,
      labelBn: b.name,
      weight: 0.55,
      meta: { region: b.region, ltv: b.ltvBdt }
    });
  }

  // --- Suppliers (fabric / trim — same supplier seed, relabeled) ---
  for (const s of suppliers) {
    nodes.push({
      id: s.id,
      kind: "supplier",
      label: s.name,
      labelBn: s.name,
      weight: 0.5,
      meta: { region: s.region, leadTimeDays: s.leadTimeDays, onTime: s.onTimeRate }
    });
  }

  // Source orders from suppliers — each top order has a sourced_from edge to
  // the round-robin supplier for variety.
  topOrders.forEach((o, i) => {
    const sup = suppliers[i % suppliers.length];
    edges.push({
      id: `e-${o.id}-${sup.id}`,
      source: o.id,
      target: sup.id,
      kind: "sourced_from",
      label: "sourced from",
      weight: 0.4
    });
  });

  // --- Processes follow lines ---
  for (const line of factory.lines) {
    const proc = processes.find((p) => p.label === line.process);
    if (proc) {
      edges.push({
        id: `e-${line.id}-${proc.id}`,
        source: line.id,
        target: proc.id,
        kind: "follows",
        label: "follows",
        weight: 0.55
      });
    }
  }

  // --- Risks ---
  for (const r of factory.risks) {
    nodes.push({
      id: r.id,
      kind: "risk",
      label: r.title,
      labelBn: r.titleBn,
      weight: 0.75,
      meta: { severity: r.severity },
      status: r.severity === "high" ? "down" : r.severity === "medium" ? "at_risk" : "healthy"
    });
    if (r.targetId) {
      edges.push({
        id: `e-${r.id}-${r.targetId}`,
        source: r.id,
        target: r.targetId,
        kind: "threatens",
        label: "threatens",
        weight: 0.8,
        strong: r.severity === "high"
      });
    }
  }

  // --- Compliance docs (from policy seed, relabelled) ---
  for (const p of policies) {
    nodes.push({
      id: p.id,
      kind: "compliance",
      label: p.title,
      labelBn: p.titleBn,
      weight: 0.5,
      meta: { type: p.type, effective: p.effectiveDate }
    });
  }
  // Link compliance docs to relevant risks/orders — demo wiring.
  edges.push({ id: "e-compliance-risk1", source: "policy:return-1", target: "risk-line3-throughput", kind: "threatens", label: "applies to", weight: 0.3 });
  edges.push({ id: "e-compliance-risk2", source: "policy:supplier-agreement-1", target: "risk-energy-spike", kind: "threatens", label: "applies to", weight: 0.3 });

  // Use the legacy `orders` parameter so the build graph signature stays the
  // same even though we no longer reference it directly (kept for future
  // extensions like shipment history vs active POs).
  void orders;

  return { nodes, edges };
}
