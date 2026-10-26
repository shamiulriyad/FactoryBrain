// Merchandising calls (/api/merchandising/**). Reads go through useResource
// with the path builders below; writes return the API's new state. Uploads
// are multipart with an Idempotency-Key generated once per submission, and the
// original file is fetched as a blob with the bearer token. The API validates
// every value and decides every permission; nothing here does.

import { apiFetch } from "@/lib/auth";
import { ApiError, apiJson, errorFromResponse } from "@/lib/api";
import { query } from "@/services/production.api";
import type {
  MerchConfirmRequest,
  MerchConflict,
  MerchDocumentType,
  MerchMilestone,
  MerchOrderDetail,
  MerchOrderWrite,
  MerchVersionDetail
} from "@/services/contracts";

const BASE = "/api/merchandising";

export type MerchOrderFilters = {
  q?: string; buyer?: string; status?: string; deliveryFrom?: string; deliveryTo?: string; attention?: string;
  page?: number; pageSize?: number; sort?: string;
};

export const merchPaths = {
  config: `${BASE}/config`,
  overview: `${BASE}/overview`,
  orders: (f: MerchOrderFilters) => `${BASE}/orders${query({ ...f, q: f.q?.trim(), buyer: f.buyer?.trim() })}`,
  order: (id: string) => `${BASE}/orders/${encodeURIComponent(id)}`,
  documents: (p: { q?: string; status?: string; type?: string; orderId?: string; page?: number; pageSize?: number }) => `${BASE}/documents${query(p)}`,
  document: (id: string) => `${BASE}/documents/${encodeURIComponent(id)}`,
  version: (id: string) => `${BASE}/versions/${encodeURIComponent(id)}`
};

export const createMerchOrder = (body: MerchOrderWrite) => apiJson<MerchOrderDetail>(`${BASE}/orders`, { method: "POST", body });
export const updateMerchOrder = (id: string, body: MerchOrderWrite) =>
  apiJson<MerchOrderDetail>(`${BASE}/orders/${encodeURIComponent(id)}`, { method: "PUT", body });

export type MilestoneBody = { name?: string; dueDate?: string; status?: string; completedOn?: string; notes?: string; expectedVersion?: number };
export const addMilestone = (orderId: string, body: MilestoneBody) =>
  apiJson<MerchMilestone>(`${BASE}/orders/${encodeURIComponent(orderId)}/milestones`, { method: "POST", body });
export const updateMilestone = (id: string, body: MilestoneBody) =>
  apiJson<MerchMilestone>(`${BASE}/milestones/${encodeURIComponent(id)}`, { method: "PUT", body });

export const resolveConflict = (id: string, body: { expectedVersion: number; chosenValue: string; note: string }) =>
  apiJson<MerchConflict>(`${BASE}/conflicts/${encodeURIComponent(id)}/resolve`, { method: "POST", body });

export const retryVersion = (id: string) => apiJson<MerchVersionDetail>(`${BASE}/versions/${encodeURIComponent(id)}/retry`, { method: "POST" });
export const confirmVersion = (id: string, body: MerchConfirmRequest) =>
  apiJson<MerchVersionDetail>(`${BASE}/versions/${encodeURIComponent(id)}/confirm`, { method: "POST", body });

/** Uploads can take longer than a JSON call on a slow factory connection. */
const UPLOAD_TIMEOUT_MS = 180_000;

async function upload(url: string, form: FormData, idempotencyKey: string): Promise<{ version: MerchVersionDetail; replayed: boolean }> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), UPLOAD_TIMEOUT_MS);
  let res: Response;
  try {
    res = await apiFetch(url, { method: "POST", body: form, cache: "no-store", signal: ctrl.signal, headers: { "Idempotency-Key": idempotencyKey } });
  } catch (e) {
    if ((e as Error).name === "AbortError")
      throw new ApiError({ status: 0, kind: "timeout", message: "The upload took too long. Check the connection and try again." });
    throw new ApiError({ status: 0, kind: "network", message: "Could not reach the server. Check your connection and try again." });
  } finally {
    clearTimeout(timer);
  }
  if (!res.ok) throw await errorFromResponse(res);
  return { version: (await res.json()) as MerchVersionDetail, replayed: res.headers.get("Idempotent-Replayed") === "true" };
}

/** POST /orders/{id}/documents: a new document (version 1). */
export function uploadDocument(orderId: string, input: { file: File; type: MerchDocumentType; title?: string }, idempotencyKey: string) {
  const form = new FormData();
  form.append("file", input.file, input.file.name);
  form.append("type", input.type);
  if (input.title?.trim()) form.append("title", input.title.trim());
  return upload(`${BASE}/orders/${encodeURIComponent(orderId)}/documents`, form, idempotencyKey);
}

/** POST /documents/{id}/versions: a replacement file; the previous version stays in the history. */
export function uploadVersion(documentId: string, input: { file: File; changeNote?: string }, idempotencyKey: string) {
  const form = new FormData();
  form.append("file", input.file, input.file.name);
  if (input.changeNote?.trim()) form.append("changeNote", input.changeNote.trim());
  return upload(`${BASE}/documents/${encodeURIComponent(documentId)}/versions`, form, idempotencyKey);
}

/** Download the original file (the bearer token can't ride on a plain link). */
export async function downloadOriginal(url: string, fileName: string): Promise<void> {
  const res = await apiFetch(url, { cache: "no-store" });
  if (!res.ok) throw await errorFromResponse(res);
  const objectUrl = URL.createObjectURL(await res.blob());
  try {
    const a = document.createElement("a");
    a.href = objectUrl;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    a.remove();
  } finally {
    setTimeout(() => URL.revokeObjectURL(objectUrl), 10_000);
  }
}

export function newIdempotencyKey(): string {
  return typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `k-${Date.now()}-${Math.random().toString(36).slice(2)}`;
}
