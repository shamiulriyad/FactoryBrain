import { dataset } from "./dataset";
import type { EvidenceRefPublic } from "./types";

export type EvidenceRows = {
  domain: EvidenceRefPublic["domain"];
  columns: string[];
  rows: Array<Record<string, any>>;
};

export const evidenceService = {
  forInsight(id: string): EvidenceRefPublic[] {
    return dataset.insights.find((i) => i.id === id)?.evidence ?? [];
  },
  rows(ref: EvidenceRefPublic, page = 0, pageSize = 12): EvidenceRows {
    const offset = page * pageSize;
    const filterRegion = ref.filter?.region as string | undefined;
    const filterDays = ref.filter?.window as string | undefined;
    const daysAgoMax = filterDays === "30d" ? 30 : filterDays === "7d" ? 7 : 365;

    switch (ref.domain) {
      case "orders": {
        const filtered = dataset.orders.filter((o) => {
          if (filterRegion && o.region !== filterRegion) return false;
          if (o.daysAgo > daysAgoMax) return false;
          if (ref.filter?.channel && o.channel !== ref.filter.channel) return false;
          return true;
        });
        return {
          domain: "orders",
          columns: ["id", "date", "productId", "customerId", "region", "channel", "qty", "total"],
          rows: filtered.slice(offset, offset + pageSize).map((o) => ({
            id: o.id,
            date: new Date(Date.now() - o.daysAgo * 86400000).toISOString().slice(0, 10),
            productId: o.productId,
            customerId: o.customerId,
            region: o.region,
            channel: o.channel,
            qty: o.quantity,
            total: `৳${o.totalBdt.toLocaleString()}`
          }))
        };
      }
      case "inventory": {
        const ids = new Set(ref.previewIds ?? []);
        const filtered = dataset.inventory.filter((i) =>
          ids.size === 0 ? true : ids.has(i.productId)
        );
        return {
          domain: "inventory",
          columns: ["productId", "stock", "dailyDemand", "daysUntilStockout", "reorderPoint", "atRisk"],
          rows: filtered.slice(offset, offset + pageSize).map((i) => ({
            productId: i.productId,
            stock: i.stock,
            dailyDemand: Number(i.recentDailyDemand.toFixed(2)),
            daysUntilStockout: i.daysUntilStockout,
            reorderPoint: i.reorderPoint,
            atRisk: i.atRisk
          }))
        };
      }
      case "customers": {
        const filtered = dataset.customers.filter((c) => (ref.filter?.churnRisk ? c.churnRisk >= 0.7 : true));
        return {
          domain: "customers",
          columns: ["id", "name", "region", "totalOrders", "ltvBdt", "churnRisk", "lastOrderDays"],
          rows: filtered.slice(offset, offset + pageSize).map((c) => ({
            id: c.id,
            name: c.name,
            region: c.region,
            totalOrders: c.totalOrders,
            ltvBdt: c.ltvBdt,
            churnRisk: Number(c.churnRisk.toFixed(2)),
            lastOrderDays: c.lastOrderDays
          }))
        };
      }
      case "products": {
        return {
          domain: "products",
          columns: ["id", "sku", "name", "category", "priceBdt", "costBdt"],
          rows: dataset.products.slice(offset, offset + pageSize).map((p) => ({
            id: p.id,
            sku: p.sku,
            name: p.name,
            category: p.category,
            priceBdt: p.priceBdt,
            costBdt: p.costBdt
          }))
        };
      }
      case "conversations": {
        return {
          domain: "conversations",
          columns: ["id", "customerId", "channel", "daysAgo", "theme", "messages"],
          rows: dataset.conversations.slice(offset, offset + pageSize).map((c) => ({
            id: c.id,
            customerId: c.customerId,
            channel: c.channel,
            daysAgo: c.daysAgo,
            theme: c.theme,
            messages: c.messages.length
          }))
        };
      }
      case "policies": {
        return {
          domain: "policies",
          columns: ["id", "title", "type", "effectiveDate"],
          rows: dataset.policies.slice(offset, offset + pageSize).map((p) => ({
            id: p.id,
            title: p.title,
            type: p.type,
            effectiveDate: p.effectiveDate
          }))
        };
      }
      case "suppliers": {
        return {
          domain: "suppliers",
          columns: ["id", "name", "region", "leadTimeDays", "onTimeRate"],
          rows: dataset.suppliers.slice(offset, offset + pageSize).map((s) => ({
            id: s.id,
            name: s.name,
            region: s.region,
            leadTimeDays: s.leadTimeDays,
            onTimeRate: s.onTimeRate
          }))
        };
      }
      case "manuals": {
        const { getManualCorpus } = require("@/data/manuals") as typeof import("@/data/manuals");
        const docs = getManualCorpus();
        const ids = new Set(ref.previewIds ?? []);
        const filtered = ids.size === 0 ? docs : docs.filter((d) => ids.has(d.id));
        return {
          domain: "manuals",
          columns: ["id", "titleEn", "source", "tags"],
          rows: filtered.slice(offset, offset + pageSize).map((d) => ({
            id: d.id,
            titleEn: d.titleEn,
            source: d.source,
            tags: d.tags.join(", ")
          }))
        };
      }
    }
    // Exhaustive — every domain above returns. Keep the line below for
    // `noImplicitReturns` so TS still type-checks when a domain is added
    // without a corresponding case.
    return { domain: ref.domain, columns: [], rows: [] };
  }
};