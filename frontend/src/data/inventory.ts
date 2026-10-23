import { intBetween, makeRng } from "./seed";
import type { Product } from "./products";
import type { Order } from "./orders";

export type InventoryRecord = {
  productId: string;
  stock: number;
  reorderPoint: number;
  reorderQty: number;
  daysUntilStockout: number; // computed from recent daily demand
  recentDailyDemand: number; // units/day over last 30 days
  atRisk: boolean;
};

// Stock snapshot is generated so that ~6 products are heading for stockout in <14 days,
// including two planted critical ones for the demo narrative.

export const plantedCriticalSkus = [
  "APP-0042", // Coral Apparel Pro — festival favourite
  "GRO-0117"  // Harvest Grocery Max — mango season
];

export function buildInventory(products: Product[], orders: Order[]): InventoryRecord[] {
  const rng = makeRng(0x1A2B_0001);
  // Compute recent daily demand per product (last 30 days)
  const demand = new Map<string, number>();
  for (const o of orders) {
    if (o.daysAgo <= 30) demand.set(o.productId, (demand.get(o.productId) ?? 0) + o.quantity);
  }
  // Average per day over 30 days
  for (const [k, v] of demand) demand.set(k, v / 30);

  return products.map((p) => {
    const recent = demand.get(p.id) ?? 0;
    let stock: number;
    const isPlanted = plantedCriticalSkus.includes(p.sku);
    if (isPlanted) {
      // Plant critical stockouts: 6–10 days of stock left at recent demand
      stock = Math.max(2, Math.round(recent * intBetween(rng, 6, 10)));
    } else if (recent > 0.4) {
      // Other active SKUs: 30–90 days of stock
      stock = Math.round(recent * intBetween(rng, 30, 90));
    } else {
      // Slow movers: 5–30 days of stock
      stock = intBetween(rng, 5, 30);
    }
    const reorderPoint = Math.max(2, Math.round(recent * 14));
    const reorderQty = Math.max(5, Math.round(recent * 30));
    const daily = Math.max(recent, 0.1);
    const daysUntilStockout = Math.max(0, Math.floor(stock / daily) - p.leadTimeDays);
    const atRisk = daysUntilStockout < 14;
    return {
      productId: p.id,
      stock,
      reorderPoint,
      reorderQty,
      daysUntilStockout,
      recentDailyDemand: recent,
      atRisk
    };
  });
}