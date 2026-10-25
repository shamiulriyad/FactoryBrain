// Client-side session for the BunonBrain API.
//
// The .NET API decides every access question; this module only carries the
// signed-in user's credentials to it.
//
//   - Access token: returned in the login/refresh JSON body, kept in module
//     memory only (never localStorage / sessionStorage), sent as
//     `Authorization: Bearer …` by apiFetch.
//   - Refresh token: HttpOnly `fb_refresh` cookie scoped to /api/auth, set by
//     the API. JavaScript never sees it; POST /api/auth/refresh rotates it and
//     returns a fresh access token. A page reload therefore costs one refresh
//     call, not a new login.
//
// Role checks in the UI (e.g. hiding admin controls) are convenience only;
// the API enforces them with its Operator / Admin policies.

import { create } from "zustand";

export type SessionRole = "Admin" | "Operator" | "Viewer";

export type SessionUser = {
  id: string;
  email: string;
  role: SessionRole | string;
};

type AuthState = {
  /** "unknown" until the first refresh attempt settles. */
  status: "unknown" | "authenticated" | "anonymous";
  user: SessionUser | null;
  /**
   * Why an authenticated session ended: "expired" when the API refused the
   * session (refresh failed or a final 401), "signed_out" after logout().
   * Lets the login page say "your session expired" instead of nothing.
   */
  endedReason: "expired" | "signed_out" | null;
};

export const useAuthStore = create<AuthState>(() => ({ status: "unknown", user: null, endedReason: null }));

type AuthResponse = { accessToken: string; expiresIn: number; user: SessionUser };

let accessToken: string | null = null;
let expiresAtMs = 0;
let refreshInFlight: Promise<boolean> | null = null;

/** Refresh this many ms before the access token actually expires. */
const EXPIRY_MARGIN_MS = 30_000;

function applySession(body: AuthResponse) {
  accessToken = body.accessToken;
  expiresAtMs = Date.now() + Math.max(0, body.expiresIn) * 1000;
  useAuthStore.setState({ status: "authenticated", user: body.user, endedReason: null });
}

function clearSession(reason: "expired" | "signed_out" = "expired") {
  const wasSignedIn = useAuthStore.getState().status === "authenticated";
  accessToken = null;
  expiresAtMs = 0;
  // A second clear (e.g. refresh failed, then the retried call 401s) keeps the first reason.
  useAuthStore.setState({ status: "anonymous", user: null, endedReason: wasSignedIn ? reason : useAuthStore.getState().endedReason });
}

export type LoginResult =
  | { ok: true; user: SessionUser }
  | { ok: false; reason: "invalid" | "rate_limited" | "unavailable"; retryAfterSeconds?: number };

export async function login(email: string, password: string): Promise<LoginResult> {
  let res: Response;
  try {
    res = await fetch("/api/auth/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "same-origin",
      cache: "no-store",
      body: JSON.stringify({ email, password })
    });
  } catch {
    return { ok: false, reason: "unavailable" };
  }
  if (res.ok) {
    const body = (await res.json()) as AuthResponse;
    applySession(body);
    return { ok: true, user: body.user };
  }
  if (res.status === 429) {
    const retry = Number(res.headers.get("Retry-After"));
    return { ok: false, reason: "rate_limited", retryAfterSeconds: Number.isFinite(retry) ? retry : undefined };
  }
  if (res.status === 400 || res.status === 401) return { ok: false, reason: "invalid" };
  return { ok: false, reason: "unavailable" };
}

export type AuthOptions = { selfRegistration: boolean; passwordReset: boolean; passwordMinLength: number };

/** What the sign-in page may offer (public, no secrets). Falls back to "sign-in only" when unreachable. */
export async function fetchAuthOptions(): Promise<AuthOptions> {
  try {
    const res = await fetch("/api/auth/options", { cache: "no-store" });
    if (res.ok) return (await res.json()) as AuthOptions;
  } catch {
    /* fall through */
  }
  return { selfRegistration: false, passwordReset: false, passwordMinLength: 10 };
}

export type RegisterResult =
  | { ok: true; user: SessionUser }
  | { ok: false; reason: "taken" | "disabled" | "invalid" | "rate_limited" | "unavailable"; message?: string; retryAfterSeconds?: number };

/** Create an account (always a Viewer). On success the user is signed in, exactly like login(). */
export async function register(email: string, password: string): Promise<RegisterResult> {
  let res: Response;
  try {
    res = await fetch("/api/auth/register", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "same-origin",
      cache: "no-store",
      body: JSON.stringify({ email, password })
    });
  } catch {
    return { ok: false, reason: "unavailable" };
  }
  if (res.status === 201 || res.ok) {
    const body = (await res.json()) as AuthResponse;
    applySession(body);
    return { ok: true, user: body.user };
  }
  if (res.status === 409) return { ok: false, reason: "taken" };
  if (res.status === 403) return { ok: false, reason: "disabled" };
  if (res.status === 429) {
    const retry = Number(res.headers.get("Retry-After"));
    return { ok: false, reason: "rate_limited", retryAfterSeconds: Number.isFinite(retry) ? retry : undefined };
  }
  if (res.status === 400) {
    const body = (await res.json().catch(() => ({}))) as { errors?: Record<string, string[]> };
    const first = body.errors ? Object.values(body.errors).flat()[0] : undefined;
    return { ok: false, reason: "invalid", message: first };
  }
  return { ok: false, reason: "unavailable" };
}

/** Ask for a reset link. The server answers the same whether or not the email has an account. */
export async function requestPasswordReset(email: string): Promise<"sent" | "rate_limited" | "invalid" | "unavailable"> {
  try {
    const res = await fetch("/api/auth/forgot-password", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      cache: "no-store",
      body: JSON.stringify({ email })
    });
    if (res.status === 202 || res.ok) return "sent";
    if (res.status === 429) return "rate_limited";
    if (res.status === 400) return "invalid";
    return "unavailable";
  } catch {
    return "unavailable";
  }
}

/** Set a new password with the token from the emailed link. */
export async function resetPassword(token: string, password: string): Promise<"reset" | "invalid_link" | "weak" | "rate_limited" | "unavailable"> {
  try {
    const res = await fetch("/api/auth/reset-password", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      cache: "no-store",
      body: JSON.stringify({ token, password })
    });
    if (res.ok) return "reset";
    if (res.status === 429) return "rate_limited";
    if (res.status === 400) {
      const body = (await res.json().catch(() => ({}))) as { errors?: Record<string, unknown> };
      // A validation error on the password field means "too weak"; anything else is a bad link.
      return body.errors && Object.keys(body.errors).some((k) => k.toLowerCase() === "password") ? "weak" : "invalid_link";
    }
    return "unavailable";
  } catch {
    return "unavailable";
  }
}

/**
 * Exchange the refresh cookie for a new access token. Concurrent callers
 * share one request, because the API rotates the cookie on every refresh
 * and a second parallel refresh would present an already-rotated value.
 */
export function refreshSession(): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = (async () => {
    try {
      const res = await fetch("/api/auth/refresh", {
        method: "POST",
        credentials: "same-origin",
        cache: "no-store"
      });
      if (!res.ok) {
        clearSession();
        return false;
      }
      applySession((await res.json()) as AuthResponse);
      return true;
    } catch {
      // Network failure: keep whatever state we had; the next call retries.
      if (!accessToken) useAuthStore.setState({ status: "anonymous", user: null });
      return false;
    } finally {
      refreshInFlight = null;
    }
  })();
  return refreshInFlight;
}

export async function logout(): Promise<void> {
  try {
    await fetch("/api/auth/logout", { method: "POST", credentials: "same-origin", cache: "no-store" });
  } finally {
    clearSession("signed_out");
  }
}

function withAuth(init: RequestInit): RequestInit {
  const headers = new Headers(init.headers);
  if (accessToken) headers.set("Authorization", `Bearer ${accessToken}`);
  return { ...init, headers, credentials: "same-origin" };
}

/**
 * fetch() for authenticated API calls. Adds the bearer token, refreshes it
 * shortly before expiry, and retries once after a 401. Request bodies must
 * be replayable (strings / JSON), which every caller in this app uses.
 * A final 401 marks the session anonymous so the app shell can send the
 * user to /login.
 */
export async function apiFetch(input: string, init: RequestInit = {}): Promise<Response> {
  if (!accessToken || Date.now() > expiresAtMs - EXPIRY_MARGIN_MS) {
    await refreshSession();
  }
  let res = await fetch(input, withAuth(init));
  if (res.status === 401) {
    if (await refreshSession()) {
      res = await fetch(input, withAuth(init));
    }
    if (res.status === 401) clearSession();
  }
  return res;
}

/** True when the signed-in user's role may perform operational writes. */
export function canOperate(user: SessionUser | null): boolean {
  return user?.role === "Admin" || user?.role === "Operator";
}

/** True when the signed-in user's role may change configuration. */
export function isAdmin(user: SessionUser | null): boolean {
  return user?.role === "Admin";
}

/** Minimum role a page or action needs, mirroring the API's policies. */
export type RequiredRole = "Viewer" | "Operator" | "Admin";

export function hasRole(user: SessionUser | null, required: RequiredRole): boolean {
  if (!user) return false;
  if (required === "Admin") return isAdmin(user);
  if (required === "Operator") return canOperate(user);
  return true;
}
