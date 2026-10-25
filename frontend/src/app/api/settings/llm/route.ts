// Server-side pass-through for /api/settings/llm (GET and POST).
//
// Forwards the caller's own bearer token to the .NET API, which requires a
// signed-in user for GET and the Admin role for POST. No shared secret is
// added here; see src/lib/adminProxy.ts.

import { NextRequest } from "next/server";
import { proxyAdminRequest } from "@/lib/adminProxy";

export const dynamic = "force-dynamic";
export const runtime  = "nodejs"; // Node fetch gives us the body stream

export async function GET(req: NextRequest) {
  return proxyAdminRequest(req, "/api/settings/llm");
}

export async function POST(req: NextRequest) {
  return proxyAdminRequest(req, "/api/settings/llm");
}
