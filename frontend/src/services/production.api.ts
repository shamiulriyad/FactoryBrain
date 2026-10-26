// Production planning calls (/api/production/**). Reads go through
// useResource with the path builders below; writes return the API's new
// state so the page can show it without guessing. The API calculates every
// metric and enforces every permission; nothing here filters or computes.

import { apiJson } from "@/lib/api";
import type {
  BalanceAnalysis,
  LineLayout,
  Observation,
  OutputEvent,
  OutputImportResult,
  OutputImportRow,
  OutputWrite,
  PlanActionResponse,
  PlanDetail,
  PlanWrite,
  ProductionLine,
  ProductionOrder,
  ProductionShift,
  ProductionStyle,
  ProposalActionResponse
} from "@/services/contracts";

const BASE = "/api/production";

export function query(params: Record<string, string | number | boolean | undefined | null>): string {
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === "" || v === false) continue;
    sp.set(k, String(v));
  }
  const s = sp.toString();
  return s ? `?${s}` : "";
}

export const productionPaths = {
  config: `${BASE}/config`,
  lines: (includeInactive = false) => `${BASE}/lines${query({ includeInactive })}`,
  shifts: (includeInactive = false) => `${BASE}/shifts${query({ includeInactive })}`,
  styles: (includeInactive = false) => `${BASE}/styles${query({ includeInactive })}`,
  summary: (p: { from?: string; to?: string; lineId?: string; orderId?: string; shift?: string }) => `${BASE}/summary${query(p)}`,
  lineDetail: (id: string, p: { date?: string; shift?: string }) => `${BASE}/lines/${encodeURIComponent(id)}/detail${query(p)}`,
  orders: (p: { q?: string; status?: string; styleId?: string; page?: number; pageSize?: number; sort?: string; includeRisk?: boolean }) =>
    `${BASE}/orders${query({ ...p, includeRisk: p.includeRisk === false ? "false" : undefined })}`,
  order: (id: string) => `${BASE}/orders/${encodeURIComponent(id)}`,
  plans: (p: { status?: string; lineId?: string; orderId?: string; from?: string; to?: string; page?: number; pageSize?: number }) => `${BASE}/plans${query(p)}`,
  plan: (id: string) => `${BASE}/plans/${encodeURIComponent(id)}`,
  output: (p: {
    from?: string; to?: string; lineId?: string; orderId?: string; shift?: string; stage?: string; source?: string;
    includeSuperseded?: boolean; page?: number; pageSize?: number; sort?: string;
  }) => `${BASE}/output${query(p)}`,
  observations: (p: { lineId?: string; styleId?: string; limit?: number }) => `${BASE}/observations${query(p)}`,
  proposals: (p: { lineId?: string; styleId?: string; status?: string; page?: number; pageSize?: number }) => `${BASE}/balance/proposals${query(p)}`,
  proposal: (id: string) => `${BASE}/balance/proposals/${encodeURIComponent(id)}`
};

// ── Setup (Admin) ───────────────────────────────────────────────────────────

export const createLine = (body: { id: string; name: string; workstations: number }) =>
  apiJson<ProductionLine>(`${BASE}/lines`, { method: "POST", body });
export const updateLine = (id: string, body: { name?: string; workstations?: number; isActive?: boolean; expectedVersion: number }) =>
  apiJson<ProductionLine>(`${BASE}/lines/${encodeURIComponent(id)}`, { method: "PUT", body });

export type ShiftBody = { code?: string; name?: string; start?: string; end?: string; breakStart?: string; breakMinutes?: number; weekdays?: string[]; isActive?: boolean; expectedVersion?: number };
export const createShift = (body: ShiftBody) => apiJson<ProductionShift>(`${BASE}/shifts`, { method: "POST", body });
export const updateShift = (code: string, body: ShiftBody) => apiJson<ProductionShift>(`${BASE}/shifts/${encodeURIComponent(code)}`, { method: "PUT", body });

export type StyleOperationBody = { id?: string; sequence: number; name: string; smv: number; machineType?: string; predecessors: number[] };
export type StyleBody = {
  code?: string; name?: string; productType?: string; approvedSmv?: number | null; clearApprovedSmv?: boolean; isActive?: boolean;
  operations?: StyleOperationBody[]; expectedVersion?: number;
};
export const createStyle = (body: StyleBody) => apiJson<ProductionStyle>(`${BASE}/styles`, { method: "POST", body });
export const updateStyle = (id: string, body: StyleBody) => apiJson<ProductionStyle>(`${BASE}/styles/${encodeURIComponent(id)}`, { method: "PUT", body });

// ── Orders and plans (Operator) ─────────────────────────────────────────────

export type OrderBody = { orderNumber?: string; styleId?: string; buyer?: string; quantity?: number; dueDate?: string; status?: string; notes?: string; expectedVersion?: number };
export const createOrder = (body: OrderBody) => apiJson<ProductionOrder>(`${BASE}/orders`, { method: "POST", body });
export const updateOrder = (id: string, body: OrderBody) => apiJson<ProductionOrder>(`${BASE}/orders/${encodeURIComponent(id)}`, { method: "PUT", body });

export const createPlan = (body: PlanWrite) => apiJson<PlanDetail>(`${BASE}/plans`, { method: "POST", body });
export const updatePlan = (id: string, body: PlanWrite) => apiJson<PlanActionResponse>(`${BASE}/plans/${encodeURIComponent(id)}`, { method: "PUT", body });
export const planAction = (id: string, action: "submit" | "withdraw" | "activate" | "complete" | "cancel", body: { reason?: string; expectedVersion?: number } = {}) =>
  apiJson<PlanActionResponse>(`${BASE}/plans/${encodeURIComponent(id)}/${action}`, { method: "POST", body });
export const resolvePlanChange = (id: string, changeId: string, action: "resolve" | "withdraw" = "resolve") =>
  apiJson<PlanActionResponse>(`${BASE}/plans/${encodeURIComponent(id)}/changes/${encodeURIComponent(changeId)}/${action}`, { method: "POST" });

// ── Output (Operator) ───────────────────────────────────────────────────────

export const recordOutput = (body: OutputWrite) => apiJson<OutputEvent>(`${BASE}/output`, { method: "POST", body });
export const correctOutput = (id: string, body: Partial<OutputWrite> & { reason: string }) =>
  apiJson<OutputEvent>(`${BASE}/output/${encodeURIComponent(id)}/correct`, { method: "POST", body });
export const importOutput = (fileName: string, rows: OutputImportRow[]) =>
  apiJson<OutputImportResult>(`${BASE}/output/import`, { method: "POST", body: { fileName, rows }, timeoutMs: 120_000 });

// ── Time studies, layouts, balancing (Operator) ─────────────────────────────

export const recordObservation = (body: {
  lineId: string; styleId: string; operationId: string; observedAt?: string; cycleSeconds: number; sampleCount: number; operatorsAssigned: number;
  queueBefore?: number | null; notes?: string;
}) => apiJson<Observation>(`${BASE}/observations`, { method: "POST", body });
export const recordLayout = (body: { lineId: string; styleId: string; stations: { stationNo: number; operationIds: string[] }[] }) =>
  apiJson<LineLayout>(`${BASE}/layouts/current`, { method: "PUT", body });
export const analyzeBalance = (body: { lineId: string; styleId: string; workstations?: number; targetOutputPerHour?: number }) =>
  apiJson<BalanceAnalysis>(`${BASE}/balance/analyze`, { method: "POST", body });
export const submitProposal = (id: string, body: { reason?: string; expectedVersion?: number } = {}) =>
  apiJson<ProposalActionResponse>(`${BASE}/balance/proposals/${encodeURIComponent(id)}/submit`, { method: "POST", body });
export const applyProposal = (id: string) =>
  apiJson<ProposalActionResponse>(`${BASE}/balance/proposals/${encodeURIComponent(id)}/apply`, { method: "POST" });
