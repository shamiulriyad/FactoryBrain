// Camera AI: the browser's webcam, sampled frames analysed by the OpenAI
// vision API on the BunonBrain server (/api/monitoring/sessions). The OpenAI
// key stays on the server; this page only ever sends JPEG frames with the
// user's own session token. Results are AI estimates, never identities.

import { apiFetch } from "@/lib/auth";
import { ApiError, apiJson, errorFromResponse } from "@/lib/api";

export type PpeStatus = "present" | "missing" | "unclear" | "unknown";
export type Presence = "present" | "absent" | "unclear";

export type AiBox = { x: number; y: number; width: number; height: number };
export type AiPerson = { activity: string; helmet: PpeStatus; safetyVest: PpeStatus; box?: AiBox | null };
export type AiIncident = { status: "none" | "possible" | "unclear"; type?: string | null; description?: string | null };

export type CameraAiAnalysis = {
  at: string;
  latencyMs: number;
  frameWidth: number;
  frameHeight: number;
  personPresence: Presence;
  personCount?: number | null;
  sceneActivity: string;
  helmet: PpeStatus;
  safetyVest: PpeStatus;
  people: AiPerson[];
  incident: AiIncident;
  imageQuality: "good" | "poor" | "unusable";
  notes?: string | null;
  adjustments: string[];
  boxesApproximate: boolean;
};

export type CameraAiError = { code: string; message: string; at: string };
export type CameraAiEvent = { id?: string | null; eventType: string; summary?: string | null; occurredAt: string; stored: boolean; error?: string | null; alertId?: string | null };

export type CameraAiStats = {
  framesReceived: number;
  analysed: number;
  failed: number;
  rejected: number;
  lastLatencyMs?: number | null;
  avgLatencyMs?: number | null;
  inputTokens: number;
  outputTokens: number;
  estimatedCostUsd?: number | null;
};

export type CameraAiSession = {
  id: string;
  cameraId: string;
  cameraCode: string;
  cameraName: string;
  factoryId: string;
  status: "ACTIVE" | "OFFLINE" | "ENDED";
  isMine: boolean;
  startedByEmail: string;
  startedAt: string;
  expiresAt: string;
  endedAt?: string | null;
  endReason?: string | null;
  lastFrameAt?: string | null;
  intervalMs: number;
  model: string;
  confirmed: { personPresent: boolean; activity?: string | null; ppeViolations: string[]; detectionUnavailable: boolean; offline: boolean };
  stats: CameraAiStats;
  lastAnalysis?: CameraAiAnalysis | null;
  lastError?: CameraAiError | null;
  recentEvents: CameraAiEvent[];
};

export type CameraAiFrameResult = {
  outcome: "analysed" | "failed";
  analysis?: CameraAiAnalysis | null;
  error?: CameraAiError | null;
  events: CameraAiEvent[];
  session: CameraAiSession;
};

export type CameraAiOverview = {
  configured: boolean;
  reason?: string | null;
  provider: string;
  model: string;
  keySource?: string | null;
  limits: {
    defaultIntervalMs: number;
    minIntervalMs: number;
    maxIntervalMs: number;
    maxFrameBytes: number;
    maxSessions: number;
    maxSessionMinutes: number;
    offlineAfterSeconds: number;
    idleTimeoutSeconds: number;
    imageDetail: string;
    ppeRequired: string[];
    confirmPresent: number;
    confirmAbsent: number;
    confirmActivity: number;
    confirmPpe: number;
    failuresBeforeUnavailable: number;
  };
  usageToday: { calls: number; failedCalls: number; inputTokens: number; outputTokens: number; estimatedCostUsd?: number | null; dailyCallLimit: number; pricesConfigured: boolean };
  sessions: CameraAiSession[];
  disclaimer: string;
};

export const SESSIONS = "/api/monitoring/sessions";

export const getCameraAiOverview = (signal?: AbortSignal) => apiJson<CameraAiOverview>(SESSIONS, { signal });

export const startCameraAiSession = (cameraId: string, intervalMs?: number) =>
  apiJson<CameraAiSession>(SESSIONS, { method: "POST", body: { cameraId, intervalMs } });

export const stopCameraAiSession = (id: string, reason?: string) =>
  apiJson<CameraAiSession>(`${SESSIONS}/${id}/stop`, { method: "POST", body: { reason } });

/** Stop when the page is closing: keepalive lets the request outlive the tab. Failures are ignored (the server's idle timeout ends it anyway). */
export function stopCameraAiSessionOnUnload(id: string) {
  void apiFetch(`${SESSIONS}/${id}/stop`, {
    method: "POST",
    keepalive: true,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ reason: "The page was closed." })
  }).catch(() => undefined);
}

/** Send one JPEG frame. Throws ApiError (409 / 429 / 503 mean "skip this frame"). */
export async function sendCameraAiFrame(id: string, jpeg: Blob, signal?: AbortSignal, timeoutMs = 45_000): Promise<CameraAiFrameResult> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  const onAbort = () => ctrl.abort();
  signal?.addEventListener("abort", onAbort, { once: true });
  let res: Response;
  try {
    res = await apiFetch(`${SESSIONS}/${id}/frames`, { method: "POST", body: jpeg, headers: { "Content-Type": "image/jpeg" }, cache: "no-store", signal: ctrl.signal });
  } catch (e) {
    if (signal?.aborted) throw e;
    throw new ApiError({ status: 0, kind: ctrl.signal.aborted ? "timeout" : "network", message: ctrl.signal.aborted ? "The analysis took too long." : "Could not reach the server." });
  } finally {
    clearTimeout(timer);
    signal?.removeEventListener("abort", onAbort);
  }
  if (!res.ok) throw await errorFromResponse(res);
  return (await res.json()) as CameraAiFrameResult;
}

/** The API's stable error code (ProblemDetails extension "code"). */
export function problemCode(e: unknown): string | null {
  if (e instanceof ApiError && e.body && typeof e.body === "object" && "code" in e.body) {
    const c = (e.body as { code?: unknown }).code;
    return typeof c === "string" ? c : null;
  }
  return null;
}

// ─── Labels ───────────────────────────────────────────────────────────────

export const PPE_LABEL: Record<PpeStatus, string> = {
  present: "Worn",
  missing: "Not worn",
  unclear: "Unclear",
  unknown: "Not visible"
};

export const PPE_TONE: Record<PpeStatus, "success" | "danger" | "warning" | "neutral"> = {
  present: "success",
  missing: "danger",
  unclear: "warning",
  unknown: "neutral"
};

export const PRESENCE_LABEL: Record<Presence, string> = { present: "Person in view", absent: "Nobody in view", unclear: "Unclear" };

export const PPE_ITEM_LABEL: Record<string, string> = { HELMET: "Helmet", SAFETY_VEST: "Safety vest" };

export const INCIDENT_LABEL: Record<string, string> = {
  FALL: "Fall",
  PERSON_ON_GROUND: "Person on the ground",
  FIRE_OR_SMOKE: "Fire or smoke",
  BLOCKED_PATH: "Blocked path",
  UNSAFE_MACHINE_USE: "Unsafe machine use",
  OTHER: "Other"
};

export const FAILURE_LABEL: Record<string, string> = {
  not_configured: "AI not configured",
  unauthorized: "OpenAI rejected the key",
  quota_exceeded: "OpenAI quota used up",
  rate_limited: "OpenAI rate limit",
  timeout: "OpenAI timed out",
  unavailable: "OpenAI unavailable",
  refused: "Model declined the frame",
  invalid_output: "Answer did not match the contract",
  provider_error: "OpenAI refused the request"
};

export function formatUsd(v: number | null | undefined): string {
  if (v == null) return "—";
  return v < 0.01 ? `$${v.toFixed(4)}` : `$${v.toFixed(2)}`;
}
