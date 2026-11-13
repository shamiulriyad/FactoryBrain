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
// Permission checks in the UI (hiding menu items and buttons) are
// convenience only; the API enforces every permission and every factory /
// line scope on each request.

import { create } from "zustand";
import type { Permission, RoleId } from "@/lib/access";

export type FactoryRef = { id: string; code: string; name: string };

export type SessionUser = {
  id: string;
  email: string;
  fullName?: string | null;
  /** Highest-ranked role, for display. */
  role: RoleId | string;
  roles: (RoleId | string)[];
  /** Effective permissions; what the server enforces. */
  permissions: (Permission | string)[];
  /** Platform, Factory, Line or Self. */
  scope: "Platform" | "Factory" | "Line" | "Self" | string;
  factories: FactoryRef[];
  lineIds: string[];
  /** A temporary password must be changed before anything else works. */
  mustChangePassword: boolean;
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
  | { ok: false; reason: "invalid" | "deactivated" | "rate_limited" | "unavailable"; retryAfterSeconds?: number };

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
  if (res.status === 403) return { ok: false, reason: "deactivated" };
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

/** Create an account (no roles until an administrator grants them). On success the user is signed in, exactly like login(). */
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

/** True when the user holds at least one of the permissions (any-of, like the API's policies). */
export function can(user: SessionUser | null | undefined, ...anyOf: (Permission | string)[]): boolean {
  if (!user || user.mustChangePassword) return false;
  if (anyOf.length === 0) return true;
  return anyOf.some((p) => user.permissions?.includes(p));
}

/** Hook form of can(): re-renders when the session changes. */
export function useCan(...anyOf: (Permission | string)[]): boolean {
  const user = useAuthStore((s) => s.user);
  return can(user, ...anyOf);
}

/** Display name: full name, else the email. */
export function displayName(user: Pick<SessionUser, "email" | "fullName"> | null | undefined): string {
  return user?.fullName?.trim() || user?.email || "";
}
