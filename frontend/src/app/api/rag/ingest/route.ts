// Server-only proxy for POST /api/rag/ingest.
//
// The backend requires X-Admin-Token in Production. The frontend ingest
// form posts to /api/rag/ingest; this route forwards to the .NET backend
// and attaches the token from ADMIN_API_TOKEN.

import { NextRequest } from "next/server";
import { proxyAdminRequest } from "@/lib/adminProxy";

export const dynamic = "force-dynamic";
export const runtime  = "nodejs";

export async function POST(req: NextRequest) {
  return proxyAdminRequest(req, "/api/rag/ingest");
}
