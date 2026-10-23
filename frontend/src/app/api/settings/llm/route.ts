// Server-only proxy for /api/settings/llm.
//
// The settings page lives at /app/app/settings/page.tsx and posts the
// user's provider / API key / model to /api/settings/llm. In Production the
// backend demands X-Admin-Token, which the browser cannot supply — so this
// route forwards the request to the .NET backend and attaches the token
// from the server-only ADMIN_API_TOKEN env var.
//
// Both GET and POST are routed here so the page's "save" call AND the
// subsequent status reload pass through the same proxy (the GET route
// stays open on the backend, so adding X-Admin-Token is harmless).

import { NextRequest } from "next/server";
import { proxyAdminRequest } from "@/lib/adminProxy";

export const dynamic = "force-dynamic";
export const runtime  = "nodejs"; // Node fetch gives us the env + body stream

export async function GET(req: NextRequest) {
  return proxyAdminRequest(req, "/api/settings/llm");
}

export async function POST(req: NextRequest) {
  return proxyAdminRequest(req, "/api/settings/llm");
}
