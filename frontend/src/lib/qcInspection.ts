// Client-side rules for the QC inspection workflow: upload pre-checks, labels,
// overlay geometry and the review draft. Pure functions with no app imports,
// so scripts/smoke.mjs can import this file directly (Node strips the types).
// The API re-checks everything; these checks only give faster feedback.

import type {
  QcBox,
  QcDecision,
  QcFinding,
  QcInspectionDetail,
  QcOutcome,
  QcReviewRequest,
  QcSeverity,
  QcStage,
  QcStatus
} from "../services/contracts";

export const QC_IMAGE_TYPES = ["image/jpeg", "image/png", "image/webp"];
const EXTENSIONS: Record<string, string> = { jpg: "image/jpeg", jpeg: "image/jpeg", png: "image/png", webp: "image/webp" };

export type FileLike = { name: string; type: string; size: number };

/** Why a picked file can't be uploaded, or null when it may be sent. */
export function validateImageFile(file: FileLike | null | undefined, maxBytes: number): string | null {
  if (!file) return "Choose an image to inspect.";
  if (file.size <= 0) return "The file is empty.";
  const ext = file.name.toLowerCase().split(".").pop() ?? "";
  const type = file.type || EXTENSIONS[ext] || "";
  if (!QC_IMAGE_TYPES.includes(type)) return "Only JPEG, PNG and WebP images are accepted.";
  if (file.size > maxBytes) return `The image is ${formatBytes(file.size)}; the limit is ${formatBytes(maxBytes)}.`;
  return null;
}

/** The image type from the first bytes (what the server checks), or null for anything else. */
export function sniffImageType(head: Uint8Array): string | null {
  const b = head;
  if (b.length >= 8 && b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47) return "image/png";
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return "image/jpeg";
  if (b.length >= 12 && b[0] === 0x52 && b[1] === 0x49 && b[2] === 0x46 && b[3] === 0x46 && b[8] === 0x57 && b[9] === 0x45 && b[10] === 0x42 && b[11] === 0x50)
    return "image/webp";
  return null;
}

export function formatBytes(n: number): string {
  if (n >= 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1).replace(/\.0$/, "")} MB`;
  if (n >= 1024) return `${Math.round(n / 1024)} KB`;
  return `${n} B`;
}

/** Line ids and order numbers: the same characters the API accepts. */
export const SAFE_TOKEN = /^[A-Za-z0-9][A-Za-z0-9 _./#-]*$/;

export const STAGE_LABEL: Record<QcStage, string> = {
  fabric: "Fabric inspection",
  cutting: "Cutting",
  inline: "Inline (sewing)",
  end_of_line: "End of line",
  final: "Final / pre-shipment"
};

export const STATUS_LABEL: Record<QcStatus, string> = {
  pending: "Queued",
  processing: "Analysing",
  completed: "Awaiting review",
  failed: "Analysis failed",
  reviewed: "Reviewed"
};

export type Tone = "neutral" | "accent" | "info" | "success" | "warning" | "danger";

export const STATUS_TONE: Record<QcStatus, Tone> = {
  pending: "info",
  processing: "info",
  completed: "warning",
  failed: "danger",
  reviewed: "success"
};

export const OUTCOME_LABEL: Record<QcOutcome, string> = { pass: "Pass", rework: "Rework", reject: "Reject" };
export const OUTCOME_TONE: Record<QcOutcome, Tone> = { pass: "success", rework: "warning", reject: "danger" };
export const SEVERITY_TONE: Record<QcSeverity, Tone> = { minor: "neutral", major: "warning", critical: "danger" };

export function isInProgress(status: QcStatus): boolean {
  return status === "pending" || status === "processing";
}

export function canReview(status: QcStatus): boolean {
  return status === "completed" || status === "failed" || status === "reviewed";
}

export function formatConfidence(c: number | null | undefined): string {
  return typeof c === "number" && Number.isFinite(c) ? `${Math.round(c * 100)}%` : "—";
}

export function formatPct(v: number | null | undefined, digits = 1): string {
  return typeof v === "number" && Number.isFinite(v) ? `${v.toFixed(digits)}%` : "—";
}

const clamp01 = (v: number) => Math.min(1, Math.max(0, v));

/** Absolute-position percentages for a normalised box over the image. */
export function boxStyle(box: QcBox): { left: string; top: string; width: string; height: string } {
  const x = clamp01(box.x);
  const y = clamp01(box.y);
  const w = Math.min(clamp01(box.width), 1 - x);
  const h = Math.min(clamp01(box.height), 1 - y);
  const pct = (v: number) => `${(v * 100).toFixed(3)}%`;
  return { left: pct(x), top: pct(y), width: pct(w), height: pct(h) };
}

/** SVG points for a polygon in a 0–100 viewBox. */
export function polygonPoints(points: number[][]): string {
  return points.map(([x, y]) => `${(clamp01(x) * 100).toFixed(3)},${(clamp01(y) * 100).toFixed(3)}`).join(" ");
}

/** The label shown for a finding: the effective category, else the provider's raw label. */
export function findingLabel(f: QcFinding): string {
  return f.effectiveCategoryName ?? f.categoryName ?? (f.rawLabel ? `Unmapped: ${f.rawLabel}` : "Unmapped");
}

// ── Review draft ────────────────────────────────────────────────────────────

export type DraftDecision = {
  decision: Exclude<QcDecision, "unreviewed">;
  correctedCategoryCode?: string;
  correctedSeverity?: QcSeverity;
  note?: string;
};

export type DraftAddition = { key: string; categoryCode: string; severity?: QcSeverity; box?: QcBox; note?: string };

export type ReviewDraft = {
  outcome: QcOutcome | "";
  notes: string;
  decisions: Record<string, DraftDecision>;
  additions: DraftAddition[];
  requestFollowUp: boolean;
  followUpNote: string;
};

type DetailForReview = Pick<QcInspectionDetail, "findings" | "version" | "review" | "status">;

/** Start from what is stored: earlier decisions (on a re-review) or nothing decided yet. */
export function initialDraft(d: DetailForReview): ReviewDraft {
  const decisions: Record<string, DraftDecision> = {};
  for (const f of d.findings) {
    if (f.decision === "unreviewed") continue;
    decisions[f.id] = {
      decision: f.decision,
      correctedCategoryCode: f.correctedCategoryCode ?? undefined,
      correctedSeverity: f.correctedSeverity ?? undefined,
      note: f.reviewerNote ?? undefined
    };
  }
  return { outcome: d.review?.outcome ?? "", notes: d.review?.notes ?? "", decisions, additions: [], requestFollowUp: false, followUpNote: "" };
}

/** A finding counts as a verified defect after this draft is applied. */
export function draftVerifiedCount(d: DetailForReview, draft: ReviewDraft): number {
  let n = draft.additions.length;
  for (const f of d.findings) {
    const decision = draft.decisions[f.id]?.decision ?? f.decision;
    if (f.source === "inspector" ? decision !== "rejected" : decision === "accepted" || decision === "corrected") n++;
  }
  return n;
}

/** Problems that would make the API refuse the review, in the order a reviewer should fix them. */
export function validateReviewDraft(d: DetailForReview, draft: ReviewDraft): string[] {
  const errors: string[] = [];
  const undecided = d.findings.filter((f) => f.source === "model" && f.decision === "unreviewed" && !draft.decisions[f.id]);
  if (undecided.length) errors.push(`Accept, correct or reject every prediction (${undecided.length} left).`);
  for (const f of d.findings) {
    const dd = draft.decisions[f.id];
    if (!dd) continue;
    if (dd.decision === "corrected" && !dd.correctedCategoryCode && !dd.correctedSeverity)
      errors.push(`Choose the corrected category or severity for "${findingLabel(f)}".`);
    if (dd.decision !== "rejected" && !f.categoryCode && !dd.correctedCategoryCode)
      errors.push(`"${findingLabel(f)}" matches no category: correct it to a category or reject it.`);
  }
  if (draft.additions.some((a) => !a.categoryCode)) errors.push("Choose a category for every defect you add.");
  if (!draft.outcome) errors.push("Choose the outcome: Pass, Rework or Reject.");
  else if (draft.outcome !== "pass" && draftVerifiedCount(d, draft) === 0 && !draft.notes.trim())
    errors.push("Explain a Rework or Reject outcome when no defect is recorded.");
  return errors;
}

export function buildReviewRequest(d: DetailForReview, draft: ReviewDraft): QcReviewRequest {
  const clean = (s?: string) => (s && s.trim() ? s.trim() : undefined);
  return {
    expectedVersion: d.version,
    outcome: draft.outcome as QcOutcome,
    notes: clean(draft.notes),
    decisions: Object.entries(draft.decisions).map(([findingId, dd]) => ({
      findingId,
      decision: dd.decision,
      ...(dd.decision === "corrected"
        ? { correctedCategoryCode: dd.correctedCategoryCode || undefined, correctedSeverity: dd.correctedSeverity || undefined }
        : {}),
      note: clean(dd.note)
    })),
    additions: draft.additions.map((a) => ({ categoryCode: a.categoryCode, severity: a.severity, box: a.box, note: clean(a.note) })),
    requestFollowUp: draft.requestFollowUp || undefined,
    followUpNote: draft.requestFollowUp ? clean(draft.followUpNote) : undefined
  };
}
