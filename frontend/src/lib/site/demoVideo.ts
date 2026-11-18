const API = (process.env.NEXT_PUBLIC_API_URL || process.env.DOTNET_API_URL || "http://localhost:5000").replace(/\/$/, "");

export interface DemoVideo {
  /** YouTube video id (11 characters). */
  videoId: string;
  title?: string | null;
}

/**
 * Server-side read of the demo video saved under Administration → Website
 * (GET /api/site-settings/demo-video). Cached for 30 seconds. Null when none
 * is set or the API can't be reached, so the homepage shows its placeholder.
 */
export async function getDemoVideo(): Promise<DemoVideo | null> {
  try {
    const res = await fetch(`${API}/api/site-settings/demo-video`, { next: { revalidate: 30 }, signal: AbortSignal.timeout(3000) });
    if (!res.ok) return null;
    const body = (await res.json()) as { configured: boolean; value?: DemoVideo };
    return body.configured && body.value && /^[A-Za-z0-9_-]{11}$/.test(body.value.videoId) ? body.value : null;
  } catch {
    return null;
  }
}
