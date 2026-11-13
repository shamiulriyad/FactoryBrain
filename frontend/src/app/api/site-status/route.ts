import { NextResponse } from "next/server";

/**
 * Public status probe for /status. Calls the API's own /health endpoint
 * (which checks the database) from the server and reports what it saw.
 * Mounted under app/api, so it is served by Next.js rather than proxied.
 */
export const dynamic = "force-dynamic";

const API = process.env.NEXT_PUBLIC_API_URL || process.env.DOTNET_API_URL || "http://localhost:5000";

export async function GET() {
  const started = Date.now();
  let api: "operational" | "degraded" | "unreachable" = "unreachable";
  let httpStatus: number | null = null;
  try {
    const res = await fetch(`${API.replace(/\/$/, "")}/health`, { cache: "no-store", signal: AbortSignal.timeout(6000) });
    httpStatus = res.status;
    const body = (await res.text()).trim();
    api = res.ok && /healthy/i.test(body) ? "operational" : "degraded";
  } catch {
    api = "unreachable";
  }
  return NextResponse.json(
    { website: "operational", api, httpStatus, latencyMs: Date.now() - started, checkedAt: new Date().toISOString() },
    { headers: { "Cache-Control": "no-store" } },
  );
}
