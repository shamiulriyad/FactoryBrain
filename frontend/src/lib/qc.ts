// Shared QC helpers — client-safe presentation utilities used by both the
// Overview one-liner (QcSummary) and the dedicated /app/qc page. Thresholds
// here are the single source of truth; if the risk bands change, every
// rate-based pill/text color updates with them.

export const QC_RATE_HIGH_THRESHOLD = 6;
export const QC_RATE_MEDIUM_THRESHOLD = 3;

export function qcOperationLabel(op: string): string {
  return op.replace(/_/g, " ").replace(/\b\w/g, (c) => c.toUpperCase());
}

/** Maps a defect/rework % to its text-color class — uses the design tokens
 *  the rest of the app already speaks. */
export function qcRateColor(rate: number): string {
  if (rate >= QC_RATE_HIGH_THRESHOLD) return "text-[var(--risk-high)]";
  if (rate >= QC_RATE_MEDIUM_THRESHOLD) return "text-[var(--risk-medium)]";
  return "text-[var(--risk-low)]";
}