// QC inspection calls. JSON goes through apiJson; the image upload is
// multipart/form-data and the image itself is fetched as a blob, both with
// apiFetch so the bearer token is attached (an <img src> could not carry it).

import { apiFetch } from "@/lib/auth";
import { ApiError, apiJson, errorFromResponse } from "@/lib/api";
import type {
  QcCategory,
  QcInspectionDetail,
  QcInspectionPage,
  QcOutcome,
  QcReviewRequest,
  QcSeverity,
  QcStage,
  QcStatus,
  QcSummary
} from "@/services/contracts";

export type InspectionFilters = {
  q?: string;
  status?: QcStatus | "";
  stage?: QcStage | "";
  lineId?: string;
  outcome?: QcOutcome | "";
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
};

function query(params: Record<string, string | number | undefined | null>): string {
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === "") continue;
    sp.set(k, String(v));
  }
  const s = sp.toString();
  return s ? `?${s}` : "";
}

export function inspectionsPath(f: InspectionFilters): string {
  return `/api/qc/inspections${query({ ...f, q: f.q?.trim() })}`;
}

export function summaryPath(p: { days?: number; lineId?: string; stage?: string; productType?: string }): string {
  return `/api/qc/summary${query(p)}`;
}

export const fetchInspection = (id: string, signal?: AbortSignal) =>
  apiJson<QcInspectionDetail>(`/api/qc/inspections/${encodeURIComponent(id)}`, { signal });

export const fetchInspections = (f: InspectionFilters, signal?: AbortSignal) => apiJson<QcInspectionPage>(inspectionsPath(f), { signal });

export const reviewInspection = (id: string, body: QcReviewRequest) =>
  apiJson<QcInspectionDetail>(`/api/qc/inspections/${encodeURIComponent(id)}/review`, { method: "POST", body });

export const retryInspection = (id: string) =>
  apiJson<QcInspectionDetail>(`/api/qc/inspections/${encodeURIComponent(id)}/retry`, { method: "POST" });

export const fetchSummary = (p: Parameters<typeof summaryPath>[0]) => apiJson<QcSummary>(summaryPath(p));

export const fetchCategories = (includeInactive = false) =>
  apiJson<QcCategory[]>(`/api/qc/categories${includeInactive ? "?includeInactive=true" : ""}`);

export const createCategory = (body: { code: string; name: string; nameBn?: string; description?: string; defaultSeverity?: QcSeverity }) =>
  apiJson<QcCategory>("/api/qc/categories", { method: "POST", body });

export const updateCategory = (
  code: string,
  body: { expectedVersion: number; name?: string; description?: string; defaultSeverity?: QcSeverity; isActive?: boolean; sortOrder?: number }
) => apiJson<QcCategory>(`/api/qc/categories/${encodeURIComponent(code)}`, { method: "PUT", body });

export type NewInspection = {
  image: File;
  productType: string;
  stage: QcStage;
  lineId?: string;
  productionOrder?: string;
  notes?: string;
  categoryCodes: string[];
};

/** Uploads can take longer than a JSON call on a slow factory connection. */
const UPLOAD_TIMEOUT_MS = 120_000;

/**
 * POST /api/qc/inspections. `idempotencyKey` is generated once per form
 * submission, so a retried or double-clicked submit returns the same
 * inspection instead of creating a second one.
 */
export async function createInspection(input: NewInspection, idempotencyKey: string): Promise<{ inspection: QcInspectionDetail; replayed: boolean }> {
  const form = new FormData();
  form.append("image", input.image, input.image.name);
  form.append("productType", input.productType.trim());
  form.append("stage", input.stage);
  if (input.lineId?.trim()) form.append("lineId", input.lineId.trim());
  if (input.productionOrder?.trim()) form.append("productionOrder", input.productionOrder.trim());
  if (input.notes?.trim()) form.append("notes", input.notes.trim());
  for (const c of input.categoryCodes) form.append("categoryCodes", c);

  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), UPLOAD_TIMEOUT_MS);
  let res: Response;
  try {
    res = await apiFetch("/api/qc/inspections", {
      method: "POST",
      body: form,
      cache: "no-store",
      signal: ctrl.signal,
      headers: { "Idempotency-Key": idempotencyKey }
    });
  } catch (e) {
    if ((e as Error).name === "AbortError")
      throw new ApiError({ status: 0, kind: "timeout", message: "The upload took too long. Check the connection and try again." });
    throw new ApiError({ status: 0, kind: "network", message: "Could not reach the server. Check your connection and try again." });
  } finally {
    clearTimeout(timer);
  }
  if (!res.ok) throw await errorFromResponse(res);
  return { inspection: (await res.json()) as QcInspectionDetail, replayed: res.headers.get("Idempotent-Replayed") === "true" };
}

/** The stored image as an object URL (revoke it when done). */
export async function fetchInspectionImage(url: string, signal?: AbortSignal): Promise<string> {
  const res = await apiFetch(url, { cache: "no-store", signal });
  if (!res.ok) throw await errorFromResponse(res);
  return URL.createObjectURL(await res.blob());
}
