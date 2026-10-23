// Analytics: derived metrics computed from seed.
// All UI numbers come from here — no hardcoded "৳1.24M" anywhere.

import { mean } from "@/lib/stats";
import type { OrdersByDay } from "./orders";
import type { Customer, Region } from "./customers";
import type { Order } from "./orders";
import type { InventoryRecord } from "./inventory";

export type HealthSnapshot = {
  revenue30: number;
  revenuePrev30: number;
  revenueTrend: number[]; // 30 daily points
  activeCustomers: number;
  activeCustomersPrev: number;
  inventoryAtRisk: number;
  inventoryAtRiskPrev: number;
  topCategories: Array<{ category: string; revenue: number; share: number }>;
  byRegion30: Array<{ region: Region; revenue: number; orders: number }>;
  // Planted findings
  dhakaDip: { region: Region; pct: number };
  stockoutRiskCount: number;
  churnRiskCount: number;
  churnRisePct: number;
};

export function computeHealth(
  orders: Order[],
  byDay: OrdersByDay[],
  customers: Customer[],
  inventory: InventoryRecord[]
): HealthSnapshot {
  const last30 = byDay.slice(-30);
  const prev30 = byDay.slice(-60, -30);
  const revenue30 = last30.reduce((a, b) => a + b.revenueBdt, 0);
  const revenuePrev30 = prev30.reduce((a, b) => a + b.revenueBdt, 0);

  const activeSet = new Set<string>();
  const prevActiveSet = new Set<string>();
  for (const o of orders) {
    if (o.daysAgo <= 30) activeSet.add(o.customerId);
    else if (o.daysAgo <= 60) prevActiveSet.add(o.customerId);
  }

  // Region rollup for last 30 days
  const byRegion = new Map<Region, { revenue: number; orders: number }>();
  for (const d of last30) {
    for (const [r, v] of Object.entries(d.byRegion) as [Region, { orders: number; revenueBdt: number }][]) {
      const cur = byRegion.get(r) ?? { revenue: 0, orders: 0 };
      cur.revenue += v.revenueBdt;
      cur.orders += v.orders;
      byRegion.set(r, cur);
    }
  }
  const byRegion30 = Array.from(byRegion.entries())
    .map(([region, v]) => ({ region, ...v }))
    .sort((a, b) => b.revenue - a.revenue);

  // Dhaka dip detection: compare Dhaka revenue share last 30 vs previous 30
  const dhaka30 = byRegion.get("Dhaka")?.revenue ?? 0;
  const dhakaPrev = (() => {
    let total = 0;
    for (const d of prev30) total += d.byRegion.Dhaka?.revenueBdt ?? 0;
    return total;
  })();
  const dhakaPct = dhakaPrev > 0 ? (dhaka30 - dhakaPrev) / dhakaPrev : 0;

  // Categories rollup
  // We don't have category on orders — derive via product map (passed via caller in real code).
  // Use a placeholder top-categories calculation here using the orders totals.
  void customers;
  void inventory;

  const inventoryAtRisk = inventory.filter((i) => i.atRisk).length;
  const inventoryAtRiskPrev = inventory.filter((i) => i.recentDailyDemand > 0 && i.daysUntilStockout < 21 && i.daysUntilStockout >= 14).length;

  // Churn rise: compare share of repeat buyers who ordered in last 30 vs previous 30
  const recent30Buyers = new Set(orders.filter((o) => o.daysAgo <= 30).map((o) => o.customerId));
  const prev30Buyers = new Set(orders.filter((o) => o.daysAgo > 30 && o.daysAgo <= 60).map((o) => o.customerId));
  const recentRepeats = customers.filter((c) => recent30Buyers.has(c.id) && c.repeatBuyer).length;
  const recentTotal = recent30Buyers.size || 1;
  const prevRepeats = customers.filter((c) => prev30Buyers.has(c.id) && c.repeatBuyer).length;
  const prevTotal = prev30Buyers.size || 1;
  const recentRepeatRate = recentRepeats / recentTotal;
  const prevRepeatRate = prevRepeats / prevTotal;
  const churnRisePct = prevRepeatRate > 0 ? (recentRepeatRate - prevRepeatRate) / prevRepeatRate : 0;

  const churnRiskCount = customers.filter((c) => c.churnRisk >= 0.7).length;

  return {
    revenue30,
    revenuePrev30,
    revenueTrend: last30.map((d) => d.revenueBdt),
    activeCustomers: activeSet.size,
    activeCustomersPrev: prevActiveSet.size,
    inventoryAtRisk,
    inventoryAtRiskPrev,
    topCategories: [], // populated by caller with product map
    byRegion30,
    dhakaDip: { region: "Dhaka", pct: dhakaPct },
    stockoutRiskCount: inventoryAtRisk,
    churnRiskCount,
    churnRisePct
  };
}

// Trend line for charts: 30 daily values normalised to a 0..1 envelope
export function sparklineSeries(values: number[]) {
  if (!values.length) return [];
  const max = Math.max(...values);
  const min = Math.min(...values);
  const range = Math.max(1, max - min);
  return values.map((v) => (v - min) / range);
}

export function deltaPct(curr: number, prev: number) {
  if (prev <= 0) return 0;
  return (curr - prev) / prev;
}

export function movingAverageSeries(values: number[], _window = 7) {
  // Simple box average over `_window`. Returns the same length so callers
  // don't need to special-case the head.
  if (values.length === 0) return values;
  const w = Math.max(1, Math.min(_window, values.length));
  let sum = 0;
  for (let i = 0; i < w; i++) sum += values[i];
  const out = new Array<number>(values.length);
  out[0] = sum / w;
  for (let i = 1; i < values.length; i++) {
    sum += values[i] - values[Math.max(0, i - w)];
    out[i] = sum / Math.min(w, i + 1);
  }
  return out;
}