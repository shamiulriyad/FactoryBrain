// Server-side pass-through for POST /api/rag/reindex.
//
// Forwards the caller's own bearer token; the .NET API requires the Admin
// role. No shared secret is added here; see src/lib/adminProxy.ts.

import { NextRequest } from "next/server";
import { proxyAdminRequest } from "@/lib/adminProxy";

export const dynamic = "force-dynamic";
export const runtime  = "nodejs";

export async function POST(req: NextRequest) {
  return proxyAdminRequest(req, "/api/rag/reindex");
}
