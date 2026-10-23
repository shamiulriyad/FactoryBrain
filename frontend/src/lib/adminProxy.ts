// Server-only proxy for the three admin write routes.
//
// Why this exists:
//   POST /api/settings/llm, POST /api/rag/reindex, POST /api/rag/ingest are
//   guarded by X-Admin-Token on the .NET backend in any non-Development
//   environment. Plain Next.js rewrites (next.config.mjs) can't attach
//   secret headers from the browser, so each route here forwards the
//   request to the backend and adds X-Admin-Token from the server-only
//   ADMIN_API_TOKEN env var.
//
// Security invariants:
//   - ADMIN_API_TOKEN is read from process.env, NEVER from NEXT_PUBLIC_*,
//     and is never returned to the client.
//   - The token value never appears in any client bundle or .next/static
//     output (the env var is read only on the server).
//   - All proxied responses carry Cache-Control: no-store (mirroring the
//     NoStoreMiddleware contract on the .NET side) so the secret-bearing
//     status payload is never cached by intermediaries.
//
// The backend URL is read from DOTNET_API_URL (legacy) or
// NEXT_PUBLIC_API_URL, with the same http://localhost:5000 fallback the
// Next.js rewrite uses. The PUBLIC var is allowed here only for the URL —
// not the token — and is safe because it's already public via the rewrite.

import { NextRequest, NextResponse } from "next/server";

const ADMIN_TOKEN_ENV = "ADMIN_API_TOKEN";
const DEFAULT_BACKEND = "http://localhost:5000";

/** Resolves the .NET backend base URL. Server-only. */
export function getBackendBaseUrl(): string {
  // process.env is server-only in Next.js; the URL is also exposed via
  // NEXT_PUBLIC_API_URL for the browser rewrite, so reading it server-side
  // does not leak anything new.
  return (
    process.env.DOTNET_API_URL ||
    process.env.NEXT_PUBLIC_API_URL ||
    DEFAULT_BACKEND
  ).replace(/\/$/, "");
}

/** Reads the admin token from server env. Trims whitespace. Empty string = unset. */
export function getAdminToken(): string {
  return (process.env[ADMIN_TOKEN_ENV] ?? "").trim();
}

/**
 * Forward an incoming Next.js request to the given backend path. Adds the
 * X-Admin-Token header when one is configured server-side and applies
 * Cache-Control: no-store on the response (for both success and error
 * branches).
 *
 * Never throws; returns a NextResponse with a stable error envelope on
 * backend or network failures. The token value is never logged.
 */
export async function proxyAdminRequest(
  req: NextRequest,
  backendPath: string
): Promise<NextResponse> {
  const base   = getBackendBaseUrl();
  const token  = getAdminToken();
  const url    = `${base}${backendPath}`;

  // Read the body once so we can forward it (fetch requires the original
  // stream be consumed before retrying). For GET this will be empty.
  const init: RequestInit = {
    method: req.method,
    // Next.js fetch on Node forwards request body directly when we use
    // request.clone(), but easier to read the raw body ourselves for
    // POST/PUT/PATCH. Read once, then re-use.
    headers: forwardHeaders(req.headers, token),
  };
  if (req.method !== "GET" && req.method !== "HEAD") {
    init.body = await req.arrayBuffer();
  }

  try {
    const upstream = await fetch(url, init);
    const buf      = await upstream.arrayBuffer();
    const headers  = filterResponseHeaders(upstream.headers);
    // Always re-stamp Cache-Control: no-store on the proxied response.
    headers.set("Cache-Control", "no-store");
    return new NextResponse(new Uint8Array(buf), {
      status: upstream.status,
      statusText: upstream.statusText,
      headers,
    });
  } catch (err) {
    // Network / DNS / connection-refused — surface a 502 with the
    // canonical envelope, no key material.
    return noStoreJson(
      {
        error: "BackendUnreachable",
        details: "the .NET API is not reachable on the configured URL",
      },
      502
    );
  }
}

function forwardHeaders(
  incoming: Headers,
  token: string
): HeadersInit {
  const out = new Headers();
  // Forward content negotiation + body shape.
  const contentType = incoming.get("content-type");
  if (contentType) out.set("content-type", contentType);
  // Add the admin token when configured. A missing token is handled by
  // the backend (returns 403) — we never invent one on the client side.
  if (token.length > 0) out.set("X-Admin-Token", token);
  return out;
}

function filterResponseHeaders(upstream: Headers): Headers {
  const out = new Headers();
  // Pass through content-type so JSON stays JSON, and any explicit
  // Cache-Control the upstream set. We override the latter below.
  const contentType = upstream.get("content-type");
  if (contentType) out.set("content-type", contentType);
  return out;
}

function noStoreJson(body: unknown, status: number): NextResponse {
  return NextResponse.json(body, {
    status,
    headers: { "Cache-Control": "no-store" },
  });
}
