// Server-side pass-through for the three admin routes.
//
// POST /api/settings/llm, POST /api/rag/reindex and POST /api/rag/ingest
// (and GET /api/settings/llm) are mounted as Next.js route handlers, which
// take precedence over the generic /api/* rewrite in next.config.mjs. Each
// handler forwards the request to the .NET API unchanged in meaning.
//
// Security model:
//   - The .NET API makes the authorization decision. It requires a valid
//     JWT and, for these routes, the Admin role (AuthPolicies.Admin).
//   - This proxy forwards ONLY the caller's own `Authorization: Bearer …`
//     header plus the content type. It never adds a credential of its own.
//     (It used to attach a shared ADMIN_API_TOKEN to every request, which
//     made any visitor an admin; that path is removed on both sides.)
//   - Cookies are not forwarded: the refresh cookie is scoped to /api/auth
//     and has no business on these routes.
//   - All responses carry Cache-Control: no-store, mirroring the API's
//     NoStoreMiddleware, so settings status is never cached.
//
// The backend URL is read from DOTNET_API_URL (legacy) or
// NEXT_PUBLIC_API_URL, with the same http://localhost:5000 fallback the
// Next.js rewrite uses.

import { NextRequest, NextResponse } from "next/server";

const DEFAULT_BACKEND = "http://localhost:5000";
/** Upper bound for a forwarded Authorization header (a JWT is far shorter). */
const MAX_AUTHORIZATION_LENGTH = 8192;

/** Resolves the .NET backend base URL. Server-only. */
export function getBackendBaseUrl(): string {
  return (
    process.env.DOTNET_API_URL ||
    process.env.NEXT_PUBLIC_API_URL ||
    DEFAULT_BACKEND
  ).replace(/\/$/, "");
}

/**
 * Forward an incoming Next.js request to the given backend path with the
 * caller's bearer token (if any) and apply Cache-Control: no-store on the
 * response, for success and error branches alike.
 *
 * Never throws; returns a NextResponse with a stable error envelope on
 * network failures. Credentials are never logged.
 */
export async function proxyAdminRequest(
  req: NextRequest,
  backendPath: string
): Promise<NextResponse> {
  const url = `${getBackendBaseUrl()}${backendPath}`;

  const init: RequestInit = {
    method: req.method,
    headers: forwardHeaders(req.headers),
    cache: "no-store",
  };
  if (req.method !== "GET" && req.method !== "HEAD") {
    init.body = await req.arrayBuffer();
  }

  try {
    const upstream = await fetch(url, init);
    const buf      = await upstream.arrayBuffer();
    const headers  = filterResponseHeaders(upstream.headers);
    headers.set("Cache-Control", "no-store");
    return new NextResponse(new Uint8Array(buf), {
      status: upstream.status,
      statusText: upstream.statusText,
      headers,
    });
  } catch {
    // Network / DNS / connection-refused — surface a 502 with the
    // canonical envelope.
    return noStoreJson(
      {
        error: "BackendUnreachable",
        details: "the .NET API is not reachable on the configured URL",
      },
      502
    );
  }
}

function forwardHeaders(incoming: Headers): HeadersInit {
  const out = new Headers();
  const contentType = incoming.get("content-type");
  if (contentType) out.set("content-type", contentType);
  // Pass through the caller's own bearer token; the API validates it.
  const authorization = incoming.get("authorization");
  if (
    authorization &&
    authorization.length <= MAX_AUTHORIZATION_LENGTH &&
    /^Bearer\s+\S+$/i.test(authorization)
  ) {
    out.set("authorization", authorization);
  }
  return out;
}

function filterResponseHeaders(upstream: Headers): Headers {
  const out = new Headers();
  const contentType = upstream.get("content-type");
  if (contentType) out.set("content-type", contentType);
  // The API's bearer challenge, so clients can tell "sign in" from "forbidden".
  const challenge = upstream.get("www-authenticate");
  if (challenge) out.set("www-authenticate", challenge);
  return out;
}

function noStoreJson(body: unknown, status: number): NextResponse {
  return NextResponse.json(body, {
    status,
    headers: { "Cache-Control": "no-store" },
  });
}
