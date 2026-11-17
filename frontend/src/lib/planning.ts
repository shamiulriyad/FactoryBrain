// Production planning & target management: types mirroring /api/planning and
// the order fields of /api/production/orders, labels, and the API calls.
// Dates without a time are factory-local operational dates (yyyy-MM-dd).
// Every figure that comes from counters says how it was measured (basis), and
// a missing figure stays missing: "no data" is never shown as zero.

import { apiJson } from "@/lib/api";
import { qs } from "@/lib/monitoring";
import type { ReportDocument } from "@/lib/analytics";
import type { OrderStatus } from "@/services/contracts";

export type Paged<T> = { items: T[]; page: number; pageSize: number; total: number };
export type Fact = { label: string; value: string };

// ── Context ─────────────────────────────────────────────────────────────────

export type PlanningShift = { id: string; code: string; name: string; start: string; end: string; overnight: boolean; productiveMinutes: number; weekdays: string[] };
export type PlanningLine = {
  id: string; name: string; isActive: boolean; workstations: number; floorId?: string | null; floorName?: string | null; departmentId?: string | null; departmentName?: string | null;
  efficiencyPct?: number | null; plannedOperators?: number | null; outputPoints: number; canManage: boolean;
};
export type PlanningCapabilities = {
  manageOrders: boolean; manageStyles: boolean; manageAllocations: boolean; manageTargets: boolean; managePlans: boolean; approvePlans: boolean; publishPlans: boolean;
  reviewExceptions: boolean; setup: boolean; exportReports: boolean; seesDrafts: boolean; lineScoped: boolean;
};
export type PlanningContext = {
  factoryId: string; factoryCode: string; factoryName: string; timeZone: string; today: string; shifts: PlanningShift[]; lines: PlanningLine[];
  floors: { id: string; code: string; name: string }[]; departments: { id: string; code: string; name: string }[];
  capabilities: PlanningCapabilities; limits: { outputPointsPerLine: number; behindTolerancePct: number; interruptionMinutes: number; requireSeparateApprover: boolean };
};
export type FactoryRef = { id: string; code: string; name: string };

export type CapacitySettings = { lineId: string; factoryId: string; efficiencyPct?: number | null; plannedOperators?: number | null; notes?: string | null; updatedAt?: string | null; updatedByEmail?: string | null; version: number };
export type OutputPoint = {
  id: string; factoryId: string; lineId: string; lineName: string; deviceId: string; deviceCode: string; deviceName: string; simulated: boolean; connectionStatus: string;
  countingMode?: string | null; note?: string | null; isActive: boolean; createdAt: string; createdByEmail: string;
};

// ── Orders ──────────────────────────────────────────────────────────────────

export const PRIORITIES = ["LOW", "NORMAL", "HIGH", "URGENT"] as const;
export type Priority = (typeof PRIORITIES)[number];
export type OrderTransitions = { current: OrderStatus; allowed: OrderStatus[]; history: { id: string; from?: string | null; to: string; reason?: string | null; changedAt: string; changedByEmail: string }[] };

// ── Allocations ─────────────────────────────────────────────────────────────

export type Allocation = {
  id: string; orderId: string; orderNumber: string; factoryId: string; lineId: string; lineName: string; quantity: number; plannedStart: string; plannedEnd: string;
  priority: Priority; status: "PLANNED" | "ACTIVE" | "COMPLETED" | "CANCELLED"; supervisorUserId?: string | null; supervisorEmail?: string | null; overAllocated: boolean;
  overrideReason?: string | null; overrideByEmail?: string | null; notes?: string | null; createdAt: string; createdByEmail: string; version: number;
};
export type AllocationDetail = { allocation: Allocation; history: { id: string; kind: string; previousValues?: string | null; newValues: string; reason?: string | null; changedAt: string; changedByEmail: string }[]; warnings: string[] };
export type AllocationSummary = { orderId: string; orderNumber: string; orderQuantity: number; allocated: number; remaining: number; excess: number; allocations: Allocation[] };

// ── Targets ─────────────────────────────────────────────────────────────────

export const TARGET_LEVELS = ["FACTORY", "DEPARTMENT", "FLOOR", "LINE", "SHIFT", "WORKSTATION", "MACHINE"] as const;
export type TargetLevel = (typeof TARGET_LEVELS)[number];
export const TARGET_PERIODS = ["DAILY", "SHIFT", "HOURLY"] as const;
export type TargetPeriod = (typeof TARGET_PERIODS)[number];
export type TargetPerformance = { date: string; actual?: number | null; achievementPct?: number | null; basis: string; note: string };
export type Target = {
  id: string; targetKey: string; revision: number; supersedesId?: string | null; level: TargetLevel; period: TargetPeriod; factoryId: string;
  departmentId?: string | null; departmentName?: string | null; floorId?: string | null; floorName?: string | null; lineId?: string | null; lineName?: string | null;
  shiftId?: string | null; shiftName?: string | null; workstationId?: string | null; workstationCode?: string | null; machineId?: string | null; machineCode?: string | null;
  orderId?: string | null; orderNumber?: string | null; allocationId?: string | null; quantity: number; effectiveFrom: string; effectiveTo?: string | null;
  approvalStatus: "NOT_REQUIRED" | "PENDING" | "APPROVED" | "REJECTED"; isSuperseded: boolean; inForceToday: boolean; notes?: string | null; changeReason?: string | null;
  createdAt: string; createdByEmail: string; decidedAt?: string | null; decidedByEmail?: string | null; decisionNote?: string | null; version: number; performance?: TargetPerformance | null;
};
export type TargetRevision = { id: string; targetId: string; revision: number; action: string; previousQuantity?: number | null; newQuantity: number; previousEffectiveFrom?: string | null; newEffectiveFrom: string; reason?: string | null; at: string; byEmail: string };
export type TargetDetail = { target: Target; versions: Target[]; revisions: TargetRevision[] };

// ── Capacity ────────────────────────────────────────────────────────────────

export type CapacityMissing = { code: string; message: string; howToFix: string };
export type CapacityCommitment = { allocationId: string; orderId: string; orderNumber: string; quantity: number; start: string; end: string; status: string; overlapDays: number; overlappingQuantity: number };
export type CapacityResult = {
  status: "COMPLETE" | "INCOMPLETE"; missing: CapacityMissing[]; availableMinutes: number; workingDays: number; capacityPieces?: number | null; plannedQuantity: number;
  committedElsewhere: number; utilizationPct?: number | null; shortagePieces?: number | null; canMeet?: boolean | null; conflicts: CapacityCommitment[];
  machines: { machineType: string; available: number; faulty: number; state: string }[]; assumptions: { label: string; value: string; source: string }[]; warnings: string[]; formula: string;
};
export type CapacityEstimate = { label: string; lineId: string; lineName: string; styleCode?: string | null; from: string; to: string; result: CapacityResult; generatedAt: string; isEstimate: boolean; disclaimer: string };
export type LineCapacityRow = {
  lineId: string; lineName: string; isActive: boolean; allocations: number; availableHours: number; demandMinutes?: number | null; capacityMinutes?: number | null; utilizationPct?: number | null;
  status: "COMPLETE" | "INCOMPLETE"; missing: CapacityMissing[]; estimates: CapacityEstimate[]; notes: string[];
};
export type CapacityOverview = { factoryId: string; from: string; to: string; lines: LineCapacityRow[]; warnings: string[]; disclaimer: string };

// ── Daily plans ─────────────────────────────────────────────────────────────

export type PlanStatus = "DRAFT" | "UNDER_REVIEW" | "APPROVED" | "PUBLISHED" | "REVISED" | "CANCELLED";
export type ItemState = "NOT_STARTED" | "ON_TRACK" | "AHEAD" | "BEHIND" | "COMPLETE" | "MISSED" | "NO_DATA";
export type PlanItemActual = {
  actual?: number | null; remaining?: number | null; achievementPct?: number | null; expectedByNow?: number | null; varianceToExpected?: number | null;
  state: ItemState; basis: "COUNTER" | "RECORDED" | "NONE"; verified: boolean; hourlyRate?: number | null; source: string; note: string;
};
export type PlanItem = {
  id: string; orderId: string; orderNumber: string; buyer?: string | null; styleCode?: string | null; allocationId?: string | null; lineId: string; lineName: string; shiftId: string; shiftName: string;
  targetQuantity: number; targetSource: "MANUAL" | "TARGET"; targetId?: string | null; hourlyTarget?: number | null; supervisorUserId?: string | null; notes?: string | null;
  actual?: PlanItemActual | null; warnings: string[];
};
export type PlanEvent = { id: string; action: string; details?: string | null; reason?: string | null; at: string; byEmail: string };
export type DailyPlanListItem = { id: string; reference: string; factoryId: string; planDate: string; revisionNo: number; status: PlanStatus; items: number; totalTarget: number; updatedAt: string; createdByEmail: string; publishedAt?: string | null; publishedByEmail?: string | null };
export type DailyPlan = {
  id: string; reference: string; factoryId: string; factoryName: string; planDate: string; revisionNo: number; status: PlanStatus; basedOnPlanId?: string | null; revisionReason?: string | null; notes?: string | null;
  submittedAt?: string | null; submittedByEmail?: string | null; approvedAt?: string | null; approvedByEmail?: string | null; publishedAt?: string | null; publishedByEmail?: string | null;
  closedAt?: string | null; closedReason?: string | null; createdAt: string; createdByEmail: string; updatedAt: string; version: number;
  items: PlanItem[]; events: PlanEvent[]; warnings: string[]; allowedActions: string[];
};

// ── Monitoring ──────────────────────────────────────────────────────────────

export type ForecastStatus = "COMPLETE" | "ON_TRACK" | "AT_RISK" | "OVERDUE" | "INSUFFICIENT_DATA";
export type MonitorOrder = {
  orderId: string; orderNumber: string; buyer?: string | null; styleCode?: string | null; status: string; priority: Priority; quantity: number; deliveryDeadline: string; daysToDeadline?: number | null;
  allocated: number; produced?: number | null; completionPct?: number | null; producedBasis: string; forecastStatus: ForecastStatus; projectedCompletion?: string | null; forecastNote: string;
};
export type MonitorItem = {
  itemId: string; orderId: string; orderNumber: string; lineId: string; lineName: string; floorId?: string | null; floorName?: string | null; departmentId?: string | null; departmentName?: string | null;
  shiftId: string; shiftName: string; target: number; actual: PlanItemActual; hourlyTarget?: number | null;
};
export type MonitorLine = {
  lineId: string; lineName: string; floorId?: string | null; floorName?: string | null; departmentId?: string | null; departmentName?: string | null; target: number; actual?: number | null;
  achievementPct?: number | null; basis: string; outputCounterConfigured: boolean; state: ItemState; itemIds: string[];
};
export type MonitorShift = { shiftId: string; shiftName: string; target: number; actual?: number | null; achievementPct?: number | null; state: ItemState; items: number };
export type MonitorSummary = {
  activeOrders: number; ordersDelayed: number; ordersDueSoon: number; targetToday: number; actualToday?: number | null; achievementPct?: number | null; remainingToday?: number | null;
  openExceptions: number; capacityWarnings: number; linesPlanned: number; linesWithoutOutputCounter: number;
};
export type Monitor = {
  factoryId: string; factoryName: string; date: string; timeZone: string; source: "REAL" | "SIMULATED"; generatedAt: string; summary: MonitorSummary; planStatus?: PlanStatus | null;
  planReference?: string | null; planRevision?: number | null; orders: MonitorOrder[]; upcomingDeadlines: MonitorOrder[]; items: MonitorItem[]; lines: MonitorLine[]; shifts: MonitorShift[];
  hourly: { hour: string; label: string; pieces: number }[]; exceptions: ProductionException[]; allocations: Allocation[]; warnings: string[]; definitions: string[];
};
export type OrderProgress = {
  order: MonitorOrder; allocations: Allocation[]; days: { date: string; target: number; actual?: number | null; basis: string; achievementPct?: number | null }[];
  forecast: { status: ForecastStatus; remaining: number; dailyRate?: number | null; observedDays: number; workingDaysNeeded?: number | null; workingDaysLeft: number; projectedCompletion?: string | null; missesDeadline?: boolean | null; basis: string; note: string };
  warnings: string[];
};

// ── Exceptions ──────────────────────────────────────────────────────────────

export type ProductionException = {
  id: string; factoryId: string; type: string; typeLabel: string; severity: "LOW" | "MEDIUM" | "HIGH"; status: "OPEN" | "ACKNOWLEDGED" | "RESOLVED"; orderId?: string | null; orderNumber?: string | null;
  allocationId?: string | null; planItemId?: string | null; lineId?: string | null; lineName?: string | null; shiftId?: string | null; productionDate?: string | null; title: string; reason: string;
  data: Fact[]; suggestedInvestigation: string; sourceType: string; detectedAt: string; lastDetectedAt: string; occurrences: number; acknowledgedAt?: string | null; acknowledgedByEmail?: string | null;
  resolvedAt?: string | null; resolvedByEmail?: string | null; resolutionNote?: string | null; autoResolved: boolean; version: number;
};
export type ExceptionEvaluation = { factoryId: string; source: string; checked: number; raised: number; refreshed: number; autoResolved: number; notes: string[]; at: string };

export type PlanningReportType = { type: string; title: string; description: string };

// ── Labels ──────────────────────────────────────────────────────────────────

export const ORDER_STATUS_TEXT: Record<OrderStatus, string> = { draft: "Draft", planned: "Planned", in_progress: "In progress", on_hold: "On hold", completed: "Completed", cancelled: "Cancelled" };
export const PLAN_STATUS_TEXT: Record<PlanStatus, string> = { DRAFT: "Draft", UNDER_REVIEW: "Under review", APPROVED: "Approved", PUBLISHED: "Published", REVISED: "Revised (replaced)", CANCELLED: "Cancelled" };
export const ITEM_STATE_TEXT: Record<ItemState, string> = {
  NOT_STARTED: "Not started", ON_TRACK: "On track", AHEAD: "Ahead", BEHIND: "Behind", COMPLETE: "Target met", MISSED: "Missed", NO_DATA: "No data"
};
export const BASIS_TEXT: Record<string, string> = { COUNTER: "Verified (counter)", RECORDED: "Recorded by people", NONE: "No data", MIXED: "Mixed sources" };
export const FORECAST_TEXT: Record<ForecastStatus, string> = { COMPLETE: "Complete", ON_TRACK: "On track", AT_RISK: "At risk", OVERDUE: "Overdue", INSUFFICIENT_DATA: "Not enough data" };
export const TARGET_LEVEL_TEXT: Record<TargetLevel, string> = { FACTORY: "Factory", DEPARTMENT: "Department", FLOOR: "Floor", LINE: "Line", SHIFT: "Shift", WORKSTATION: "Workstation", MACHINE: "Machine" };
export const TARGET_PERIOD_TEXT: Record<TargetPeriod, string> = { DAILY: "per day", SHIFT: "per shift", HOURLY: "per hour" };
export const ALLOCATION_STATUS_TEXT: Record<Allocation["status"], string> = { PLANNED: "Planned", ACTIVE: "Active", COMPLETED: "Completed", CANCELLED: "Cancelled" };

export const num = (v: number | null | undefined, digits = 0): string =>
  v == null ? "—" : v.toLocaleString("en-US", { maximumFractionDigits: digits });
export const pct = (v: number | null | undefined): string => (v == null ? "—" : `${v.toLocaleString("en-US", { maximumFractionDigits: 1 })} %`);

export function addDays(date: string, days: number): string {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

// ── API ─────────────────────────────────────────────────────────────────────

const J = (method: "POST" | "PUT" | "DELETE", body?: unknown) => ({ method, body });
const P = "/api/planning";

export const planningApi = {
  factories: () => apiJson<FactoryRef[]>(`${P}/factories`),
  context: (factoryId?: string) => `${P}/context${qs({ factoryId })}`,
  capacitySettings: (lineId: string) => apiJson<CapacitySettings>(`${P}/lines/${lineId}/capacity-settings`),
  saveCapacitySettings: (lineId: string, body: Record<string, unknown>) => apiJson<CapacitySettings>(`${P}/lines/${lineId}/capacity-settings`, J("PUT", body)),
  outputPoints: (factoryId: string) => `${P}/output-points${qs({ factoryId })}`,
  addOutputPoint: (body: { lineId: string; deviceId: string; note?: string }) => apiJson<OutputPoint>(`${P}/output-points`, J("POST", body)),
  removeOutputPoint: (id: string) => apiJson<OutputPoint>(`${P}/output-points/${id}`, J("DELETE")),

  orders: (p: { factoryId?: string; q?: string; status?: string; priority?: string; page?: number; pageSize?: number; sort?: string }) =>
    `/api/production/orders${qs({ ...p, includeRisk: false })}`,
  orderTransitions: (id: string) => `/api/production/orders/${id}/transitions`,
  changeOrderStatus: (id: string, body: { status: string; reason?: string; expectedVersion: number }) =>
    apiJson<import("@/services/contracts").ProductionOrder>(`/api/production/orders/${id}/status`, J("POST", body)),
  createOrder: (body: Record<string, unknown>) => apiJson<import("@/services/contracts").ProductionOrder>("/api/production/orders", J("POST", body)),
  updateOrder: (id: string, body: Record<string, unknown>) => apiJson<import("@/services/contracts").ProductionOrder>(`/api/production/orders/${id}`, J("PUT", body)),
  orderProgress: (id: string, source?: string) => `${P}/orders/${id}/progress${qs({ source: source === "SIMULATED" ? source : null })}`,
  orderAllocations: (id: string) => `${P}/orders/${id}/allocations`,

  styles: "/api/production/styles?includeInactive=true",
  createStyle: (body: Record<string, unknown>) => apiJson<import("@/services/contracts").ProductionStyle>("/api/production/styles", J("POST", body)),
  updateStyle: (id: string, body: Record<string, unknown>) => apiJson<import("@/services/contracts").ProductionStyle>(`/api/production/styles/${id}`, J("PUT", body)),

  allocations: (p: { factoryId?: string; orderId?: string; lineId?: string; status?: string; from?: string; to?: string; page?: number; pageSize?: number }) => `${P}/allocations${qs(p)}`,
  allocation: (id: string) => `${P}/allocations/${id}`,
  createAllocation: (body: Record<string, unknown>) => apiJson<AllocationDetail>(`${P}/allocations`, J("POST", body)),
  updateAllocation: (id: string, body: Record<string, unknown>) => apiJson<AllocationDetail>(`${P}/allocations/${id}`, J("PUT", body)),
  allocationStatus: (id: string, body: Record<string, unknown>) => apiJson<AllocationDetail>(`${P}/allocations/${id}/status`, J("POST", body)),
  allocationCapacity: (id: string) => `${P}/allocations/${id}/capacity`,

  targets: (p: { factoryId?: string; level?: string; lineId?: string; date?: string; includeHistory?: boolean; withPerformance?: boolean; approval?: string; page?: number; pageSize?: number }) =>
    `${P}/targets${qs(p)}`,
  target: (id: string) => `${P}/targets/${id}`,
  createTarget: (body: Record<string, unknown>) => apiJson<TargetDetail>(`${P}/targets`, J("POST", body)),
  reviseTarget: (id: string, body: Record<string, unknown>) => apiJson<TargetDetail>(`${P}/targets/${id}/revise`, J("POST", body)),
  decideTarget: (id: string, body: Record<string, unknown>) => apiJson<TargetDetail>(`${P}/targets/${id}/decision`, J("POST", body)),
  endTarget: (id: string, body: Record<string, unknown>) => apiJson<TargetDetail>(`${P}/targets/${id}/end`, J("POST", body)),

  capacity: (p: { lineId: string; styleId: string; from: string; to: string; quantity?: number; excludeAllocationId?: string }) => `${P}/capacity${qs(p)}`,
  capacityOverview: (p: { factoryId: string; from: string; to: string }) => `${P}/capacity/overview${qs(p)}`,

  plans: (p: { factoryId?: string; status?: string; from?: string; to?: string; page?: number; pageSize?: number }) => `${P}/daily-plans${qs(p)}`,
  plan: (id: string, source?: string) => `${P}/daily-plans/${id}${qs({ source: source === "SIMULATED" ? source : null })}`,
  planForDate: (factoryId: string, date: string) => `${P}/daily-plans/for-date${qs({ factoryId, date })}`,
  createPlan: (body: Record<string, unknown>) => apiJson<DailyPlan>(`${P}/daily-plans`, J("POST", body)),
  updatePlan: (id: string, body: Record<string, unknown>) => apiJson<DailyPlan>(`${P}/daily-plans/${id}`, J("PUT", body)),
  addPlanItem: (id: string, expectedVersion: number, body: Record<string, unknown>) => apiJson<DailyPlan>(`${P}/daily-plans/${id}/items${qs({ expectedVersion })}`, J("POST", body)),
  updatePlanItem: (id: string, itemId: string, expectedVersion: number, body: Record<string, unknown>) =>
    apiJson<DailyPlan>(`${P}/daily-plans/${id}/items/${itemId}${qs({ expectedVersion })}`, J("PUT", body)),
  removePlanItem: (id: string, itemId: string, expectedVersion: number) => apiJson<DailyPlan>(`${P}/daily-plans/${id}/items/${itemId}${qs({ expectedVersion })}`, J("DELETE")),
  planAction: (id: string, action: "submit" | "approve" | "return" | "publish" | "revise" | "cancel", body: { reason?: string; expectedVersion: number }) =>
    apiJson<DailyPlan>(`${P}/daily-plans/${id}/${action}`, J("POST", body)),

  monitor: (p: { factoryId?: string; date?: string; source?: string; departmentId?: string; floorId?: string; lineId?: string }) =>
    `${P}/monitor${qs({ ...p, source: p.source === "SIMULATED" ? p.source : null })}`,

  exceptions: (p: { factoryId?: string; status?: string; type?: string; severity?: string; lineId?: string; orderId?: string; page?: number; pageSize?: number }) => `${P}/exceptions${qs(p)}`,
  evaluateExceptions: (body: { factoryId?: string; source?: string }) => apiJson<ExceptionEvaluation>(`${P}/exceptions/evaluate`, J("POST", body)),
  acknowledgeException: (id: string, body: { note?: string; expectedVersion: number }) => apiJson<ProductionException>(`${P}/exceptions/${id}/acknowledge`, J("POST", body)),
  resolveException: (id: string, body: { note: string; expectedVersion: number }) => apiJson<ProductionException>(`${P}/exceptions/${id}/resolve`, J("POST", body)),

  reportTypes: `${P}/reports`,
  report: (type: string, p: { factoryId: string; from?: string; to?: string; source?: string; lineId?: string; orderId?: string }) =>
    `${P}/reports/${type}${qs({ ...p, source: p.source === "SIMULATED" ? p.source : null })}`,
  reportExport: (type: string, format: "csv" | "pdf", p: { factoryId: string; from?: string; to?: string; source?: string; lineId?: string; orderId?: string }) =>
    `${P}/reports/${type}/export${qs({ ...p, format, source: p.source === "SIMULATED" ? p.source : null })}`
};

export type { ReportDocument };
