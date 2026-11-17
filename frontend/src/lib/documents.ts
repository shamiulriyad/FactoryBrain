// Factory documentation automation: types mirroring /api/documents, /api/inventory
// and /api/notifications, labels, and the API calls. Every figure a document shows
// comes from the server (generated from records or calculated); the browser never
// computes totals, AQL or balances itself.

import { apiJson, errorFromResponse, ApiError } from "@/lib/api";
import { apiFetch } from "@/lib/auth";

export type DocStatus = "DRAFT" | "SUBMITTED" | "UNDER_REVIEW" | "APPROVED" | "REJECTED" | "ARCHIVED";
export type ApprovalLevel = "NONE" | "SINGLE" | "REVIEW_AND_APPROVE";
export type Risk = "LOW" | "NORMAL" | "HIGH";

export type DocField = {
  key: string; label: string; kind: "text" | "textarea" | "number" | "integer" | "date" | "select" | "boolean" | "table";
  required: boolean; computed: boolean; options?: string[] | null; columns?: DocField[] | null; help?: string | null; unit?: string | null; lookup?: string | null;
};

export type DocType = {
  code: string; name: string; category: string; categoryName: string; description: string; risk: Risk; defaultApproval: ApprovalLevel; approvalLevel: ApprovalLevel;
  period: "NONE" | "DAY" | "RANGE"; generated: boolean; lineOption: boolean; numberField?: string | null; autoGenerate: boolean; expectedDaily: boolean; decisionDueHours: number;
  fields: DocField[]; parameters: DocField[]; canCreate: boolean;
  /** Scheduled generation (used when autoGenerate is on and DOCUMENT_AUTOMATION_ENABLED=true on the server). */
  scheduleFrequency: ScheduleFrequency; scheduleScope: ScheduleScope; weekStartDay: number; scheduleFrequencies?: ScheduleFrequency[] | null;
};

export type ScheduleFrequency = "DAILY" | "WEEKLY" | "MONTHLY";
export type ScheduleScope = "FACTORY" | "EACH_LINE";

export type ScheduleRun = {
  id: string; typeCode: string; typeName: string; periodFrom: string; periodTo: string; lineKey: string; status: "CREATED" | "EXISTS" | "NO_DATA" | "FAILED";
  attempts: number; errorCode?: string | null; errorMessage?: string | null; documentId?: string | null; documentReference?: string | null; firstAttemptAt: string; lastAttemptAt: string;
};
export type ScheduleStatus = { automationEnabled: boolean; runs: ScheduleRun[] };

export type DocCatalog = {
  factoryId: string; factoryName: string; today: string; types: DocType[]; categoryList: { code: string; name: string }[];
  can: { create: boolean; review: boolean; approve: boolean; approveHigh: boolean; control: boolean; monitor: boolean; categories: string[]; lineScoped: boolean };
  lines: { id: string; name: string }[]; ai: { configured: boolean; provider?: string | null; model?: string | null; reason?: string | null };
};

export type DocListItem = {
  id: string; factoryId: string; reference: string; typeCode: string; typeName: string; category: string; title: string; status: DocStatus; risk: Risk; approvalLevel: ApprovalLevel;
  source: "FORM" | "GENERATED" | "UPLOAD" | "TEMPLATE"; periodFrom?: string | null; periodTo?: string | null; lineId?: string | null; currentVersion: number;
  missingCount: number; errorCount: number; warningCount: number; createdAt: string; createdByEmail: string; updatedAt: string; submittedAt?: string | null; decidedAt?: string | null; decidedByEmail?: string | null;
};

export type Paged<T> = { items: T[]; page: number; pageSize: number; total: number };

export type DocIssue = { field: string; code: string; message: string };
export type DocChecks = { missing: DocIssue[]; errors: DocIssue[]; warnings: DocIssue[] };

export type DocActions = {
  edit: boolean; submit: boolean; startReview: boolean; verify: boolean; approve: boolean; reject: boolean; revise: boolean; archive: boolean; upload: boolean;
  regenerate: boolean; useAi: boolean; whyNotSubmit?: string | null; whyNotApprove?: string | null;
};

export type DocFile = {
  id: string; fileName: string; format: string; contentType: string; sizeBytes: number; sha256: string; extractionStatus: "NONE" | "COMPLETED" | "FAILED";
  extractionMethod?: string | null; extractionModel?: string | null; extractedAt?: string | null; extractionAttempts: number; failureCode?: string | null; failureMessage?: string | null;
  suggestedTypeCode?: string | null;
  suggestions?: { values: Record<string, unknown>; methods: Record<string, string>; confidence: Record<string, number>; rejected: string[] } | null;
  createdAt: string; createdByEmail: string;
};

export type DocDetail = {
  document: DocListItem; type: DocType; data: Record<string, unknown>; sources: Record<string, string>; checks: DocChecks; actions: DocActions;
  versions: { number: number; createdAt: string; createdByEmail: string; changeNote?: string | null; missingCount: number; errorCount: number; warningCount: number; contentSha256: string }[];
  history: { id: number; action: string; fromStatus?: string | null; toStatus?: string | null; versionNumber?: number | null; note?: string | null; occurredAt: string; actorEmail: string }[];
  files: DocFile[];
  submittedByEmail?: string | null; reviewStartedByEmail?: string | null; reviewStartedAt?: string | null; verifiedByEmail?: string | null; verifiedAt?: string | null;
  decisionNote?: string | null; archivedAt?: string | null; archivedByEmail?: string | null; stockPosted: boolean;
  aiSummary?: string | null; aiSummaryAt?: string | null; aiSummaryModel?: string | null; lineName?: string | null;
};

export type DocMonitor = {
  factoryId: string; today: string; drafts: number; pending: number; overdue: number; rejected: number; missingToday: number; approvedThisWeek: number;
  items: { documentId?: string | null; reference?: string | null; typeCode: string; typeName: string; category: string; kind: string; status?: string | null; period?: string | null; lineId?: string | null; since?: string | null; message: string }[];
  byCategory: { category: string; pending: number; drafts: number; missing: number }[];
};

export type DocUsage = { calls: number; failures: number; inputTokens: number; outputTokens: number; byPurpose: { purpose: string; calls: number; failures: number }[]; provider?: string | null; model?: string | null };

export type InventoryItem = {
  id: string; factoryId: string; code: string; name: string; category: string; unit: string; reorderLevel?: number | null; isActive: boolean;
  received: number; issued: number; balance: number; belowReorder: boolean; updatedAt: string; rollsInStock?: number; onRolls?: number;
};
export type RollStatus = "IN_STOCK" | "PARTLY_ISSUED" | "ISSUED";
export type InventoryRoll = {
  id: string; itemId: string; itemCode: string; itemName: string; rollNumber: string; lotNumber?: string | null; supplier?: string | null; color?: string | null;
  shadeGroup?: string | null; widthCm?: number | null; warehouse?: string | null; location?: string | null; unit: string; received: number; issued: number; remaining: number;
  status: RollStatus; receivedOn: string; grnDocumentId: string; grnReference: string;
};
export const ROLL_STATUS_TEXT: Record<RollStatus, string> = { IN_STOCK: "In stock", PARTLY_ISSUED: "Partly issued", ISSUED: "Issued" };
export type StockMovement = {
  id: number; itemId: string; itemCode: string; itemName: string; kind: "RECEIPT" | "ISSUE"; quantity: number; unitCost?: number | null; movementDate: string;
  documentId: string; documentReference: string; counterparty?: string | null; recordedAt: string; recordedByEmail: string;
};

export type AppNotification = { id: string; kind: string; title: string; body: string; link?: string | null; entityType?: string | null; entityId?: string | null; createdAt: string; readAt?: string | null };

export const ITEM_CATEGORIES = ["FABRIC", "ACCESSORIES", "TRIMS", "PACKING", "CHEMICALS", "OTHER"] as const;

export const STATUS_TEXT: Record<DocStatus, string> = {
  DRAFT: "Draft", SUBMITTED: "Submitted", UNDER_REVIEW: "Under review", APPROVED: "Approved", REJECTED: "Rejected", ARCHIVED: "Archived"
};
export const APPROVAL_TEXT: Record<ApprovalLevel, string> = { NONE: "Filed (no approval)", SINGLE: "One approver", REVIEW_AND_APPROVE: "Review, then approval" };
export const SOURCE_TEXT: Record<DocListItem["source"], string> = { FORM: "Form", GENERATED: "Generated from records", UPLOAD: "Uploaded", TEMPLATE: "From template" };

export const ACTION_TEXT: Record<string, string> = {
  created: "Created", saved: "Saved a new version", revised_after_rejection: "Corrected after rejection", regenerated: "Refreshed from records",
  generated_automatically: "Generated automatically", submitted: "Submitted", filed: "Filed (no approval needed)", review_started: "Review started",
  verified: "Verified", approved: "Approved", rejected: "Rejected", reopened_for_revision: "Reopened for revision", archived: "Archived",
  file_attached: "File attached", file_read: "File read", ai_summary: "AI summary"
};

/** Where a value came from, in words. */
export function sourceText(s?: string | null): string | null {
  if (!s) return null;
  if (s === "records") return "From records";
  if (s === "user") return "Entered";
  if (s.startsWith("template:")) return `Copied from ${s.slice(9)}`;
  if (s.startsWith("merch_order:")) return `From buyer order ${s.slice(12)}`;
  if (s.startsWith("worker:")) return `From worker ${s.slice(7)}`;
  if (s.startsWith("machine:")) return `From machine ${s.slice(8)}`;
  if (s.startsWith("rules")) return "Read from file (rules)";
  if (s.startsWith("ai_ocr")) return "AI read (image) — accepted";
  if (s.startsWith("rules+ai") || s.startsWith("ai")) return "AI suggestion — accepted";
  return s;
}

const qs = (o: Record<string, string | number | boolean | null | undefined>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(o)) if (v !== null && v !== undefined && v !== "") p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : "";
};

async function send(url: string, init: RequestInit, timeoutMs = 120_000): Promise<Response> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const res = await apiFetch(url, { cache: "no-store", ...init, signal: ctrl.signal });
    if (!res.ok) throw await errorFromResponse(res);
    return res;
  } catch (e) {
    if (e instanceof ApiError) throw e;
    if ((e as Error).name === "AbortError") throw new ApiError({ status: 0, kind: "timeout", message: "The request took too long. Try again." });
    throw new ApiError({ status: 0, kind: "network", message: "Could not reach the server. Check your connection and try again." });
  } finally {
    clearTimeout(timer);
  }
}

async function downloadFrom(url: string, fallback: string) {
  const res = await send(url, { method: "GET" });
  const blob = await res.blob();
  const disposition = res.headers.get("Content-Disposition") ?? "";
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)?.[1] ?? fallback;
  const href = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = href;
  a.download = decodeURIComponent(name);
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(href), 1000);
}

export const docsApi = {
  catalog: (factoryId?: string) => `/api/documents/catalog${qs({ factoryId })}`,
  list: (q: { factoryId?: string; q?: string; category?: string; type?: string; status?: string; from?: string; to?: string; view?: string; includeArchived?: boolean; page?: number; pageSize?: number }) =>
    `/api/documents${qs(q)}`,
  detail: (id: string) => `/api/documents/${id}`,
  monitor: (factoryId?: string) => `/api/documents/monitor${qs({ factoryId })}`,
  usage: (factoryId?: string) => `/api/documents/ai-usage${qs({ factoryId })}`,
  scheduleRuns: (factoryId?: string, days = 30) => `/api/documents/schedule-runs${qs({ factoryId, days })}`,

  create: (body: { factoryId?: string; typeCode: string; mode?: string; periodFrom?: string | null; periodTo?: string | null; lineId?: string | null; title?: string | null;
    data?: Record<string, unknown> | null; parameters?: Record<string, unknown> | null; prefillKey?: string | null }) =>
    apiJson<DocDetail>("/api/documents", { method: "POST", body, timeoutMs: 120_000 }),
  save: (id: string, body: { data: Record<string, unknown>; title?: string | null; changeNote?: string | null; expectedVersion?: number; acceptedAiFields?: string[]; aiFileId?: string | null }) =>
    apiJson<DocDetail>(`/api/documents/${id}`, { method: "PUT", body }),
  regenerate: (id: string, note?: string) => apiJson<DocDetail>(`/api/documents/${id}/regenerate`, { method: "POST", body: { note: note ?? null }, timeoutMs: 120_000 }),
  act: (id: string, action: "submit" | "start-review" | "verify" | "approve" | "reject" | "revise" | "archive", body: { note?: string | null; expectedVersion?: number }) =>
    apiJson<DocDetail>(`/api/documents/${id}/${action}`, { method: "POST", body }),
  saveSettings: (factoryId: string, code: string, body: { approvalLevel: ApprovalLevel; autoGenerate: boolean; expectedDaily: boolean; decisionDueHours: number;
    scheduleFrequency?: ScheduleFrequency; scheduleScope?: ScheduleScope; weekStartDay?: number }) =>
    apiJson<DocType>(`/api/documents/types/${encodeURIComponent(code)}/settings${qs({ factoryId })}`, { method: "PUT", body }),
  lookup: (factoryId: string, lookup: string, key: string) =>
    apiJson<{ lookup: string; key: string; found: boolean; values?: Record<string, unknown> | null; message?: string | null }>(`/api/documents/lookup${qs({ factoryId, lookup, key })}`),
  version: (id: string, n: number) => apiJson<{ number: number; data: Record<string, unknown>; changeNote?: string | null; createdAt: string; createdByEmail: string }>(`/api/documents/${id}/versions/${n}`),
  summary: (id: string) => apiJson<DocDetail>(`/api/documents/${id}/summary`, { method: "POST", timeoutMs: 120_000 }),
  draftText: (id: string, field: string) => apiJson<{ field: string; text: string; aiGenerated: boolean }>(`/api/documents/${id}/draft-text`, { method: "POST", body: { field }, timeoutMs: 120_000 }),
  extract: (id: string, fileId: string, ai: boolean) => apiJson<DocFile>(`/api/documents/${id}/files/${fileId}/extract${qs({ ai })}`, { method: "POST", timeoutMs: 180_000 }),
  async upload(id: string, file: File): Promise<DocFile> {
    const form = new FormData();
    form.append("file", file, file.name);
    const res = await send(`/api/documents/${id}/files`, { method: "POST", body: form });
    return (await res.json()) as DocFile;
  },
  async classify(factoryId: string, file: File, ai: boolean) {
    const form = new FormData();
    form.append("file", file, file.name);
    const res = await send(`/api/documents/classify${qs({ factoryId, ai })}`, { method: "POST", body: form });
    return (await res.json()) as { typeCode?: string | null; method: string; confidence?: number | null; candidates: { code: string; score: number }[]; reason?: string | null };
  },
  download: (id: string, format: "pdf" | "xlsx" | "docx", version?: number) =>
    downloadFrom(`/api/documents/${id}/export${qs({ format, version })}`, `document.${format}`),
  openFile: (id: string, fileId: string, name: string) => downloadFrom(`/api/documents/${id}/files/${fileId}`, name)
};

export const inventoryApi = {
  items: (q: { factoryId?: string; q?: string; category?: string; includeInactive?: boolean }) => `/api/inventory/items${qs(q)}`,
  movements: (q: { factoryId?: string; itemId?: string; from?: string; to?: string; page?: number; pageSize?: number }) => `/api/inventory/movements${qs(q)}`,
  rolls: (q: { factoryId?: string; itemId?: string; status?: string; q?: string; page?: number; pageSize?: number }) => `/api/inventory/rolls${qs(q)}`,
  create: (body: { factoryId?: string; code: string; name: string; category: string; unit: string; reorderLevel?: number | null }) =>
    apiJson<InventoryItem>("/api/inventory/items", { method: "POST", body }),
  update: (id: string, body: { code: string; name: string; category: string; unit: string; reorderLevel?: number | null; isActive?: boolean }) =>
    apiJson<InventoryItem>(`/api/inventory/items/${id}`, { method: "PUT", body })
};

export const notificationsApi = {
  list: (unread = false) => `/api/notifications${qs({ unread: unread || undefined, limit: 20 })}`,
  markRead: (ids?: string[]) => apiJson<{ marked: number }>("/api/notifications/read", { method: "POST", body: { ids: ids ?? null } })
};
