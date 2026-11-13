/**
 * Client for POST /api/inquiries (BunonBrain.Api InquiriesController). Every
 * website form uses it. Success is reported only when the API confirms it
 * stored the message (HTTP 202); nothing is ever faked.
 */

export type InquiryKind = "demo" | "consultation" | "contact" | "support" | "problem" | "feature" | "feedback" | "partnership";

export interface InquiryPayload {
  kind: InquiryKind;
  name: string;
  email?: string;
  phone?: string;
  company?: string;
  jobTitle?: string;
  subject?: string;
  message: string;
  details?: Record<string, string>;
  sourcePath?: string;
  /** Honeypot; must stay empty. */
  website?: string;
}

export type InquiryResult =
  | { ok: true }
  | { ok: false; message: string; fieldErrors: Record<string, string> };

const NETWORK_ERROR = "We couldn't reach our servers. Check your connection and try again.";

export async function submitInquiry(payload: InquiryPayload): Promise<InquiryResult> {
  let res: Response;
  try {
    res = await fetch("/api/inquiries", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
      credentials: "omit",
      cache: "no-store",
    });
  } catch {
    return { ok: false, message: NETWORK_ERROR, fieldErrors: {} };
  }
  if (res.status === 202) return { ok: true };
  if (res.status === 429) {
    return { ok: false, message: "Too many messages from this connection. Please wait a minute and try again.", fieldErrors: {} };
  }
  if (res.status === 400) {
    const body = (await res.json().catch(() => null)) as { errors?: Record<string, string[]> } | null;
    const fieldErrors: Record<string, string> = {};
    for (const [k, v] of Object.entries(body?.errors ?? {})) {
      const key = k.charAt(0).toLowerCase() + k.slice(1);
      if (v?.[0]) fieldErrors[key] = v[0];
    }
    return { ok: false, message: "Some fields need attention.", fieldErrors };
  }
  return { ok: false, message: "Something went wrong on our side and your message was not saved. Please try again.", fieldErrors: {} };
}
