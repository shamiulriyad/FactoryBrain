import type { HeroSettings } from "@/content/site/heroMedia";

const API = (process.env.NEXT_PUBLIC_API_URL || process.env.DOTNET_API_URL || "http://localhost:5000").replace(/\/$/, "");

/**
 * Server-side read of the hero settings saved under Administration → Website
 * (GET /api/site-settings/hero). Cached for 30 seconds. Returns null when
 * nothing is saved or the API can't be reached, so the page falls back to the
 * default photo instead of failing.
 */
export async function getHeroSettings(): Promise<Partial<HeroSettings> | null> {
  try {
    const res = await fetch(`${API}/api/site-settings/hero`, { next: { revalidate: 30 }, signal: AbortSignal.timeout(3000) });
    if (!res.ok) return null;
    const body = (await res.json()) as { configured: boolean; value?: Partial<HeroSettings> };
    return body.configured && body.value ? body.value : null;
  } catch {
    return null;
  }
}
