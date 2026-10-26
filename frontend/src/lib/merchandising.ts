// Display helpers and the review draft for the merchandising pages. Nothing
// here validates business values or decides access: the API does both, and
// these helpers only shape what the reviewer chose into the request.

import type { BadgeTone } from "@/components/ui/Badge";
import type {
  MerchAttentionItem,
  MerchConfirmRequest,
  MerchDecision,
  MerchExtractedField,
  MerchOrderStatus,
  MerchRequiredDocument,
  MerchVersionDetail,
  MerchVersionStatus
} from "@/services/contracts";

export { formatBytes } from "@/lib/qcInspection";

export const VERSION_STATUS_LABEL: Record<MerchVersionStatus, string> = {
  pending: "Queued",
  processing: "Reading",
  needs_review: "Needs review",
  confirmed: "Confirmed",
  failed: "Failed"
};

export const VERSION_STATUS_TONE: Record<MerchVersionStatus, BadgeTone> = {
  pending: "neutral",
  processing: "info",
  needs_review: "warning",
  confirmed: "success",
  failed: "danger"
};

export const ORDER_STATUS_LABEL: Record<MerchOrderStatus, string> = { active: "Active", shipped: "Shipped", closed: "Closed", cancelled: "Cancelled" };
export const ORDER_STATUS_TONE: Record<MerchOrderStatus, BadgeTone> = { active: "info", shipped: "success", closed: "neutral", cancelled: "danger" };

export const PRIORITY_TONE: Record<MerchAttentionItem["priority"], BadgeTone> = { high: "danger", medium: "warning", low: "neutral" };

export const REQUIRED_STATE_LABEL: Record<MerchRequiredDocument["state"], string> = {
  missing: "Missing", processing: "Processing", needs_review: "Needs review", failed: "Failed", confirmed: "Confirmed"
};
export const REQUIRED_STATE_TONE: Record<MerchRequiredDocument["state"], BadgeTone> = {
  missing: "danger", processing: "info", needs_review: "warning", failed: "danger", confirmed: "success"
};

export const SOURCE_LABEL: Record<string, string> = {
  model: "Language model",
  rules: "Label parser",
  reviewer: "Entered by reviewer",
  manual: "Typed on the order",
  document: "Confirmed from a document",
  conflict_resolution: "Chosen when resolving a conflict"
};

export const DECISION_LABEL: Record<MerchDecision, string> = {
  unreviewed: "Not decided", accepted: "Accepted", corrected: "Corrected", rejected: "Rejected", unresolved: "Unresolved"
};

/** Plain-language explanation of a failure or "not extracted" code. */
export function describeCode(code: string | undefined): string {
  switch (code) {
    case "provider_not_configured": return "Automatic extraction is not configured; enter the values from the document text.";
    case "ocr_required": return "No text layer (scan or image). OCR is not configured on this server, so the text can't be read automatically.";
    case "malformed_document": return "The file is damaged or could not be read.";
    case "encrypted_document": return "The PDF is password-protected.";
    case "too_many_pages": return "The document has more pages than the server reads.";
    case "no_text": return "The document contains no text.";
    case "provider_timeout": return "The extraction model did not answer in time.";
    case "provider_rate_limited": return "The extraction model is rate-limiting requests.";
    case "provider_unauthorized": return "The extraction model rejected the configured key.";
    case "provider_error": return "The extraction model returned an error.";
    case "invalid_output": return "The extraction model's answer was not in the required format.";
    case "processing_abandoned": return "Processing did not finish after several attempts.";
    case "file_missing": return "The stored file could not be found.";
    default: return code ?? "";
  }
}

export function isInProgress(s: MerchVersionStatus): boolean {
  return s === "pending" || s === "processing";
}

export function days(n: number | undefined | null): string {
  if (n == null) return "";
  if (n === 0) return "today";
  return n > 0 ? `in ${n} day${n === 1 ? "" : "s"}` : `${-n} day${n === -1 ? "" : "s"} ago`;
}

export function fmtQty(n: number | undefined | null, unit?: string): string {
  if (n == null) return "—";
  return `${n.toLocaleString("en-US")}${unit ? ` ${unit}` : ""}`;
}

// ── Review draft ────────────────────────────────────────────────────────────

export type FieldDraft = { decision?: Exclude<MerchDecision, "unreviewed">; correctedValue: string; note: string; apply: boolean };
export type AdditionDraft = { key: string; fieldKey: string; value: string; decision: "accepted" | "unresolved"; apply: boolean };
export type ReviewDraft = { fields: Record<string, FieldDraft>; additions: AdditionDraft[]; notes: string };

export const BREAKDOWN = "size_color_breakdown";

/** The value as the API takes it: breakdown lines become JSON, everything else is trimmed text. */
function wire(key: string, value: string): string {
  return key === BREAKDOWN ? textToBreakdownJson(value) : value.trim();
}

export function hasBlockingIssue(f: MerchExtractedField): boolean {
  return f.issues.some((i) => i.blocking);
}

/**
 * Every field starts undecided: a person chooses for each one. A field is
 * pre-ticked to be written to the order only when the order has no value
 * yet, or its value came from an earlier version of this same document, and
 * no open conflict involves it.
 */
export function initialDraft(v: MerchVersionDetail): ReviewDraft {
  const conflicted = new Set(v.conflicts.filter((c) => c.status === "open").map((c) => c.fieldKey));
  const fields: Record<string, FieldDraft> = {};
  for (const f of v.fields) {
    const current = v.orderValues.find((o) => o.key === f.key);
    const sameDocument = current?.source?.kind === "document" && current.source.documentId === v.documentId;
    const start = f.key === BREAKDOWN ? breakdownToText(f.normalizedValue ?? f.rawValue) : f.normalizedValue ?? f.rawValue ?? "";
    fields[f.id] = { correctedValue: start, note: "", apply: !conflicted.has(f.key) && (!current?.value || sameDocument) };
  }
  return { fields, additions: [], notes: "" };
}

export function acceptAllValid(v: MerchVersionDetail, d: ReviewDraft): ReviewDraft {
  const fields = { ...d.fields };
  for (const f of v.fields) {
    if (!fields[f.id].decision && !hasBlockingIssue(f) && f.normalizedValue) fields[f.id] = { ...fields[f.id], decision: "accepted" };
  }
  return { ...d, fields };
}

/** Problems the reviewer can fix before sending (the API re-checks everything). */
export function draftProblems(v: MerchVersionDetail, d: ReviewDraft): string[] {
  const out: string[] = [];
  const undecided = v.fields.filter((f) => !d.fields[f.id]?.decision);
  if (undecided.length) out.push(`Decide every value (${undecided.map((f) => f.label).join(", ")} left).`);
  for (const f of v.fields) {
    const fd = d.fields[f.id];
    if (fd?.decision === "accepted" && (hasBlockingIssue(f) || !f.normalizedValue)) out.push(`${f.label} can't be accepted as read; correct it or mark it unresolved.`);
    if (fd?.decision === "corrected" && !fd.correctedValue.trim()) out.push(`${f.label}: enter the corrected value.`);
  }
  for (const a of d.additions) {
    if (!a.fieldKey) out.push("Choose the field for each added value.");
    else if (a.decision === "accepted" && !a.value.trim()) out.push("Enter a value for each added field, or mark it unresolved.");
  }
  const keys = d.additions.map((a) => a.fieldKey).filter(Boolean);
  if (new Set(keys).size !== keys.length) out.push("Each field can be added once.");
  return out;
}

export function buildConfirmRequest(v: MerchVersionDetail, d: ReviewDraft): MerchConfirmRequest {
  const apply: string[] = [];
  const decisions = v.fields.map((f) => {
    const fd = d.fields[f.id];
    if (fd.apply && (fd.decision === "accepted" || fd.decision === "corrected")) apply.push(f.key);
    return {
      fieldId: f.id,
      decision: fd.decision!,
      correctedValue: fd.decision === "corrected" ? wire(f.key, fd.correctedValue) : undefined,
      note: fd.note.trim() || undefined
    };
  });
  const additions = d.additions.map((a) => {
    if (a.apply && a.decision === "accepted") apply.push(a.fieldKey);
    return { fieldKey: a.fieldKey, value: a.decision === "accepted" ? wire(a.fieldKey, a.value) : undefined, decision: a.decision };
  });
  return { expectedVersion: v.version, decisions, additions, applyToOrder: apply, notes: d.notes.trim() || undefined };
}

// ── Breakdown editing ───────────────────────────────────────────────────────

/** Breakdown JSON as editable lines "Colour, Size, Quantity". */
export function breakdownToText(json: string | undefined): string {
  if (!json) return "";
  try {
    const rows = JSON.parse(json) as { color?: string; colour?: string; size?: string; quantity?: number | string }[];
    return rows.map((r) => `${r.color ?? r.colour ?? ""}, ${r.size ?? ""}, ${r.quantity ?? ""}`).join("\n");
  } catch {
    return json;
  }
}

/** Lines "Colour, Size, Quantity" (comma, tab or semicolon) as breakdown JSON for the API to validate. */
export function textToBreakdownJson(text: string): string {
  const rows = text.split(/\r?\n/).map((l) => l.trim()).filter(Boolean).map((l) => {
    const [color = "", size = "", quantity = ""] = l.split(/\s*[,;\t]\s*/);
    return { color, size, quantity: quantity.replace(/[\s,]/g, "") };
  });
  return JSON.stringify(rows);
}

export function parseBreakdown(json: string | undefined): { color: string; size: string; quantity: number }[] | null {
  if (!json) return null;
  try {
    const rows = JSON.parse(json);
    return Array.isArray(rows) ? rows.map((r) => ({ color: String(r.color ?? r.colour ?? ""), size: String(r.size ?? ""), quantity: Number(r.quantity) })) : null;
  } catch {
    return null;
  }
}

// ── Evidence highlighting ───────────────────────────────────────────────────

/**
 * Split `text` into [before, match, after] around the first occurrence of
 * `snippet`, ignoring case and runs of whitespace (as the API does when it
 * checks evidence). Null when it doesn't occur.
 */
export function locate(text: string, snippet: string | undefined): [string, string, string] | null {
  if (!snippet) return null;
  const words = snippet.trim().split(/\s+/).map((w) => w.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"));
  if (words.length === 0 || !words[0]) return null;
  const re = new RegExp(words.join("\\s+"), "i");
  const m = re.exec(text);
  if (!m) return null;
  return [text.slice(0, m.index), m[0], text.slice(m.index + m[0].length)];
}
