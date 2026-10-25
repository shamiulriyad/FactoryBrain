// Typed JSON calls to the BunonBrain API on top of apiFetch (lib/auth.ts).
//
// Every call either resolves with the parsed body or throws an ApiError that
// carries what the UI needs to explain the failure: the HTTP status, the
// API's stable error code (`error` / ProblemDetails `title`), a readable
// message, per-field validation errors and, for 429, the Retry-After delay.
// Nothing here swallows an error or turns a failure into an empty success.

import { apiFetch } from "@/lib/auth";

/** Requests that take longer than this are aborted and reported as a timeout. */
export const DEFAULT_TIMEOUT_MS = 30_000;

export type ApiErrorKind =
  | "network"
  | "timeout"
  | "validation"     // 400 / 422
  | "unauthorized"   // 401 (session expired or missing)
  | "forbidden"      // 403
  | "not_found"      // 404
  | "conflict"       // 409
  | "precondition"   // 428
  | "rate_limited"   // 429
  | "unavailable"    // 502 / 503 / 504
  | "server";        // other 5xx and anything unexpected

export class ApiError extends Error {
  readonly status: number;
  readonly kind: ApiErrorKind;
  /** Stable machine code from the body (`error`, else ProblemDetails `title`). */
  readonly code: string | null;
  /** Field → messages, from ValidationProblemDetails or `{ errors: { field: "msg" } }`. */
  readonly fieldErrors: Record<string, string[]>;
  readonly retryAfterSeconds: number | null;
  readonly traceId: string | null;
  /** The parsed error body, for callers that need extra fields (e.g. a reindex `plan`). */
  readonly body: unknown;

  constructor(init: {
    status: number;
    kind: ApiErrorKind;
    message: string;
    code?: string | null;
    fieldErrors?: Record<string, string[]>;
    retryAfterSeconds?: number | null;
    traceId?: string | null;
    body?: unknown;
  }) {
    super(init.message);
    this.name = "ApiError";
    this.status = init.status;
    this.kind = init.kind;
    this.code = init.code ?? null;
    this.fieldErrors = init.fieldErrors ?? {};
    this.retryAfterSeconds = init.retryAfterSeconds ?? null;
    this.traceId = init.traceId ?? null;
    this.body = init.body;
  }
}

export function isApiError(e: unknown): e is ApiError {
  return e instanceof ApiError;
}

function kindFor(status: number): ApiErrorKind {
  if (status === 400 || status === 422) return "validation";
  if (status === 401) return "unauthorized";
  if (status === 403) return "forbidden";
  if (status === 404) return "not_found";
  if (status === 409) return "conflict";
  if (status === 428) return "precondition";
  if (status === 429) return "rate_limited";
  if (status === 502 || status === 503 || status === 504) return "unavailable";
  return "server";
}

const GENERIC: Record<ApiErrorKind, string> = {
  network: "Could not reach the server. Check your connection and try again.",
  timeout: "The server took too long to answer. Try again.",
  validation: "Some of the values were not accepted.",
  unauthorized: "Your session has expired. Sign in again.",
  forbidden: "Your role can't do this. Ask an administrator.",
  not_found: "That item no longer exists.",
  conflict: "Someone else changed this first. The latest state has been reloaded.",
  precondition: "This needs an explicit confirmation first.",
  rate_limited: "Too many requests. Wait a moment and try again.",
  unavailable: "A service the API depends on is unavailable right now. Try again shortly.",
  server: "The server could not complete the request."
};

function normaliseFieldErrors(raw: unknown): Record<string, string[]> {
  if (!raw || typeof raw !== "object") return {};
  const out: Record<string, string[]> = {};
  for (const [k, v] of Object.entries(raw as Record<string, unknown>)) {
    const key = k.charAt(0).toLowerCase() + k.slice(1);
    if (Array.isArray(v)) out[key] = v.map(String);
    else if (typeof v === "string") out[key] = [v];
  }
  return out;
}

/** Build an ApiError from a non-2xx Response. Reads the body once. */
export async function errorFromResponse(res: Response): Promise<ApiError> {
  const kind = kindFor(res.status);
  let body: any = null;
  const text = await res.text().catch(() => "");
  if (text) {
    try {
      body = JSON.parse(text);
    } catch {
      body = null;
    }
  }
  const fieldErrors = normaliseFieldErrors(body?.errors);
  const firstField = Object.values(fieldErrors).flat()[0];
  const code: string | null = typeof body?.error === "string" ? body.error : typeof body?.title === "string" ? body.title : null;
  const detail: string | null =
    typeof body?.detail === "string" ? body.detail : typeof body?.details === "string" ? body.details : null;
  const retryHeader = Number(res.headers.get("Retry-After"));
  const retryAfterSeconds = Number.isFinite(retryHeader) && retryHeader > 0 ? retryHeader : null;

  let message = detail ?? firstField ?? GENERIC[kind];
  if (kind === "rate_limited" && retryAfterSeconds && !detail) {
    message = `Too many requests. Try again in ${retryAfterSeconds} seconds.`;
  }
  return new ApiError({
    status: res.status,
    kind,
    message,
    code,
    fieldErrors,
    retryAfterSeconds,
    traceId: typeof body?.traceId === "string" ? body.traceId : null,
    body
  });
}

export type ApiRequestOptions = {
  method?: "GET" | "POST" | "PATCH" | "PUT" | "DELETE";
  /** JSON-serialised as the request body. */
  body?: unknown;
  signal?: AbortSignal;
  timeoutMs?: number;
};

/**
 * Call the API and parse the JSON response. Throws ApiError on any non-2xx
 * status, network failure or timeout. A caller-supplied AbortSignal still
 * rejects with the native AbortError so callers can ignore their own aborts.
 */
export async function apiJson<T>(path: string, opts: ApiRequestOptions = {}): Promise<T> {
  const { method = "GET", body, signal, timeoutMs = DEFAULT_TIMEOUT_MS } = opts;
  const ctrl = new AbortController();
  let timedOut = false;
  const timer = setTimeout(() => {
    timedOut = true;
    ctrl.abort();
  }, timeoutMs);
  const onOuterAbort = () => ctrl.abort();
  if (signal) {
    if (signal.aborted) ctrl.abort();
    else signal.addEventListener("abort", onOuterAbort, { once: true });
  }

  let res: Response;
  try {
    res = await apiFetch(path, {
      method,
      cache: "no-store",
      signal: ctrl.signal,
      headers: body !== undefined ? { "Content-Type": "application/json" } : undefined,
      body: body !== undefined ? JSON.stringify(body) : undefined
    });
  } catch (e) {
    if (timedOut) throw new ApiError({ status: 0, kind: "timeout", message: GENERIC.timeout });
    if ((e as Error).name === "AbortError") throw e;
    throw new ApiError({ status: 0, kind: "network", message: GENERIC.network });
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener("abort", onOuterAbort);
  }

  if (!res.ok) throw await errorFromResponse(res);
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  if (!text) return undefined as T;
  try {
    return JSON.parse(text) as T;
  } catch {
    throw new ApiError({ status: res.status, kind: "server", message: "The server sent a response this page could not read." });
  }
}

/** A readable one-line message for any thrown value. */
export function errorMessage(e: unknown): string {
  if (isApiError(e)) return e.message;
  if (e instanceof Error && e.message) return e.message;
  return GENERIC.server;
}

/** True for a caller's own AbortController.abort() (not a timeout). */
export function isAbort(e: unknown): boolean {
  return e instanceof Error && e.name === "AbortError";
}
