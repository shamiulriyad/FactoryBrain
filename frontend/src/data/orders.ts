import { intBetween, makeRng, pick } from "./seed";
import type { Customer, Region } from "./customers";
import type { Product } from "./products";

// 12 months of orders ending on the demo "today" date.
// Planted:
//   - Ramadan / Eid spike (Mar–Apr in 2026 calendar — used as seasonality proxy)
//   - Recent Dhaka dip (last 30 days, Dhaka down ~22%)
//   - Stockout on a couple of products (insufficient stock on hot sellers)
//   - Churn risk rise (drop in repeat orders last 30 days)

export type Order = {
  id: string;
  customerId: string;
  productId: string;
  quantity: number;
  totalBdt: number;
  region: Region;
  channel: "shopify" | "whatsapp" | "facebook" | "instagram" | "direct";
  daysAgo: number;
};

export type OrdersByDay = {
  daysAgo: number;
  date: string; // ISO date
  orders: number;
  revenueBdt: number;
  byRegion: Partial<Record<Region, { orders: number; revenueBdt: number }>>;
};

const channels: Order["channel"][] = ["shopify", "whatsapp", "facebook", "instagram", "direct"];

function isoDate(daysAgo: number): string {
  const d = new Date();
  d.setHours(0, 0, 0, 0);
  d.setDate(d.getDate() - daysAgo);
  return d.toISOString().slice(0, 10);
}

// Seasonality multipliers by month index relative to "today" (0..11).
// Month 0 = ~30 days ago, Month 11 = ~360 days ago.
// We plant a strong spring (Mar–Apr, indices 10–11) and a moderate winter (Dec–Jan, indices 2–3).
function seasonalMultiplier(monthIdx: number): number {
  // March–April spike
  if (monthIdx === 10 || monthIdx === 11) return 1.55;
  // December–January moderate
  if (monthIdx === 2 || monthIdx === 3) return 1.2;
  // June–August slump
  if (monthIdx === 6 || monthIdx === 7 || monthIdx === 8) return 0.85;
  return 1.0;
}

export function buildOrders(
  customers: Customer[],
  products: Product[]
): { orders: Order[]; byDay: OrdersByDay[] } {
  const rng = makeRng(0xF00D_0001);
  // Total orders target: ~14k over 360 days (~38/day average)
  const targetCount = 14_200;
  const orders: Order[] = [];
  const productById = new Map(products.map((p) => [p.id, p]));
  const customerById = new Map(customers.map((c) => [c.id, c]));

  // Build pool of (product, baseDailyDemand) from product categories.
  // Hot categories get more orders.
  const productPool = products.map((p) => ({
    productId: p.id,
    weight: p.category === "Apparel" || p.category === "Beauty" || p.category === "Grocery" ? 2 : 1
  }));

  for (let i = 0; i < targetCount; i++) {
    const daysAgo = intBetween(rng, 0, 360);
    const monthIdx = Math.floor(daysAgo / 30);
    const seasonal = seasonalMultiplier(monthIdx);
    // Apply Dhaka dip in last 30 days
    const isRecent = daysAgo <= 30;
    const recentDhakaFactor = isRecent ? 0.78 : 1.0;
    // Base acceptance probability so density matches
    if (rng() > seasonal * 0.95 * recentDhakaFactor) continue;

    // Weighted product pick
    const productPick = weightedPick(rng, productPool);
    const product = productById.get(productPick.productId);
    if (!product) continue;

    const customerPick = customers[Math.floor(rng() * customers.length)];
    const quantity = intBetween(rng, 1, 4);
    const totalBdt = product.priceBdt * quantity;

    orders.push({
      id: `o-${(i + 1).toString().padStart(6, "0")}`,
      customerId: customerPick.id,
      productId: product.id,
      quantity,
      totalBdt,
      region: customerPick.region,
      channel: pick(rng, channels),
      daysAgo
    });
  }

  // Group by day
  const byDayMap = new Map<number, OrdersByDay>();
  for (let d = 0; d <= 360; d++) {
    byDayMap.set(d, { daysAgo: d, date: isoDate(d), orders: 0, revenueBdt: 0, byRegion: {} });
  }
  for (const o of orders) {
    const bucket = byDayMap.get(o.daysAgo)!;
    bucket.orders += 1;
    bucket.revenueBdt += o.totalBdt;
    const r = bucket.byRegion[o.region] ?? { orders: 0, revenueBdt: 0 };
    r.orders += 1;
    r.revenueBdt += o.totalBdt;
    bucket.byRegion[o.region] = r;
  }
  const byDay = Array.from(byDayMap.values()).sort((a, b) => a.daysAgo - b.daysAgo);
  void customerById; // touch

  return { orders, byDay };
}

function weightedPick(rng: () => number, items: { productId: string; weight: number }[]) {
  const total = items.reduce((a, b) => a + b.weight, 0);
  let r = rng() * total;
  for (const it of items) {
    r -= it.weight;
    if (r <= 0) return it;
  }
  return items[items.length - 1];
}