// Display helpers for the production pages: labels, badge tones, number
// formatting, plant-local time conversion and CSV parsing for imports.
// No production metric is calculated here: every figure comes from the API.
// Pure functions with no app imports (types only).

import type {
  BottleneckStatus,
  OrderStatus,
  OutputImportRow,
  OutputSource,
  PlanStatus,
  ProductionStage,
  ProposalStatus,
  RiskLevel,
  WipBuffer
} from "../services/contracts";

type Tone = "neutral" | "accent" | "info" | "success" | "warning" | "danger";

export const STAGE_LABEL: Record<ProductionStage, string> = {
  cutting: "Cutting",
  sewing_input: "Sewing input (loading)",
  sewing_output: "Sewing output",
  finishing: "Finishing",
  packing: "Packing"
};

export const PLAN_STATUS_LABEL: Record<PlanStatus, string> = {
  draft: "Draft",
  pending_approval: "Waiting for approval",
  active: "Active",
  completed: "Completed",
  cancelled: "Cancelled"
};

export const PLAN_STATUS_TONE: Record<PlanStatus, Tone> = {
  draft: "neutral",
  pending_approval: "warning",
  active: "success",
  completed: "info",
  cancelled: "neutral"
};

export const ORDER_STATUS_LABEL: Record<OrderStatus, string> = { open: "Open", closed: "Closed", cancelled: "Cancelled" };

export const RISK_LABEL: Record<RiskLevel, string> = {
  complete: "Complete",
  low: "Low risk",
  medium: "Medium risk",
  high: "High risk",
  insufficient_data: "Not enough data",
  not_applicable: "—"
};

export const RISK_TONE: Record<RiskLevel, Tone> = {
  complete: "info",
  low: "success",
  medium: "warning",
  high: "danger",
  insufficient_data: "neutral",
  not_applicable: "neutral"
};

export const PROPOSAL_STATUS_LABEL: Record<ProposalStatus, string> = {
  proposed: "Proposed",
  pending_approval: "Waiting for approval",
  applied: "Applied",
  rejected: "Rejected",
  superseded: "Superseded"
};

export const PROPOSAL_STATUS_TONE: Record<ProposalStatus, Tone> = {
  proposed: "accent",
  pending_approval: "warning",
  applied: "success",
  rejected: "danger",
  superseded: "neutral"
};

export const SOURCE_LABEL: Record<OutputSource, string> = { manual: "Manual entry", import: "File import", integration: "Integration" };

export const BOTTLENECK_LABEL: Record<BottleneckStatus, string> = {
  observed: "Measured (time studies)",
  standard_only: "Standard minutes only",
  insufficient_data: "Not enough data"
};

export const WIP_STATE_LABEL: Record<WipBuffer["state"], string> = {
  ok: "",
  missing_to: "nothing recorded downstream yet",
  inconsistent: "inconsistent: more out than in — records are missing upstream",
  partial: "some orders' records are incomplete"
};

export const APPROVAL_STATUS_LABEL: Record<string, string> = {
  pending_approval: "Waiting for a decision",
  approved: "Approved",
  rejected: "Rejected"
};

/** A number with a fixed number of decimals, or "—" when the API had no value. */
export function fmt(n: number | null | undefined, digits = 0): string {
  if (n === null || n === undefined || Number.isNaN(n)) return "—";
  return n.toLocaleString("en-US", { minimumFractionDigits: digits, maximumFractionDigits: digits });
}

export function fmtPct(n: number | null | undefined, digits = 1): string {
  return n === null || n === undefined ? "—" : `${fmt(n, digits)} %`;
}

export function fmtSigned(n: number): string {
  return n > 0 ? `+${fmt(n)}` : fmt(n);
}

export function fmtHours(minutes: number | null | undefined): string {
  return minutes === null || minutes === undefined ? "—" : `${fmt(minutes / 60, 1)} h`;
}

// ── Plant-local time ────────────────────────────────────────────────────────
// The API reports the plant's current UTC offset (e.g. "UTC+06:00"). Times the
// user types are plant-local and are sent with that explicit offset, so the
// API stores the exact instant; it returns the production date and shift it
// derived, which the form shows back.

/** "UTC+06:00" → 360. */
export function offsetMinutes(utcOffset: string | null | undefined): number {
  const m = /^UTC([+-])(\d{2}):(\d{2})$/.exec(utcOffset ?? "");
  if (!m) return 0;
  const v = Number(m[2]) * 60 + Number(m[3]);
  return m[1] === "-" ? -v : v;
}

/** "+06:00" for an ISO timestamp. */
export function isoOffset(minutes: number): string {
  const sign = minutes < 0 ? "-" : "+";
  const abs = Math.abs(minutes);
  return `${sign}${String(Math.floor(abs / 60)).padStart(2, "0")}:${String(abs % 60).padStart(2, "0")}`;
}

/** Plant-local date + "HH:mm" → ISO string with the plant offset. */
export function plantIso(date: string, time: string, offsetMin: number): string {
  return `${date}T${time}:00${isoOffset(offsetMin)}`;
}

/** Today's plant-local date (yyyy-MM-dd). */
export function plantToday(offsetMin: number, now: Date = new Date()): string {
  return new Date(now.getTime() + offsetMin * 60_000).toISOString().slice(0, 10);
}

export function addDays(date: string, days: number): string {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

/** An instant shown in plant-local time, "yyyy-MM-dd HH:mm". */
export function plantDateTime(iso: string | null | undefined, offsetMin: number): string {
  if (!iso) return "—";
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return "—";
  return new Date(t + offsetMin * 60_000).toISOString().slice(0, 16).replace("T", " ");
}

export function plantTime(iso: string, offsetMin: number): string {
  return plantDateTime(iso, offsetMin).slice(11);
}

// ── CSV import ──────────────────────────────────────────────────────────────

export const IMPORT_COLUMNS = [
  "source_ref", "line_id", "order_number", "stage", "interval_start", "interval_end", "quantity",
  "rework_quantity", "reject_quantity", "operators_present", "downtime_minutes", "downtime_reason", "notes"
] as const;

export const IMPORT_TEMPLATE =
  IMPORT_COLUMNS.join(",") + "\n" +
  "MES-0001,line-1,PO-1001,sewing_output,2026-10-26 08:00,2026-10-26 09:00,52,3,1,20,0,,\n";

/** RFC 4180-style CSV: quoted fields, doubled quotes, CRLF or LF. */
export function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let field = "";
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') { field += '"'; i++; }
      else if (c === '"') quoted = false;
      else field += c;
    } else if (c === '"') quoted = true;
    else if (c === ",") { row.push(field); field = ""; }
    else if (c === "\n" || c === "\r") {
      if (c === "\r" && text[i + 1] === "\n") i++;
      row.push(field); field = "";
      if (row.some((f) => f.trim() !== "")) rows.push(row);
      row = [];
    } else field += c;
  }
  row.push(field);
  if (row.some((f) => f.trim() !== "")) rows.push(row);
  return rows;
}

/**
 * Header-mapped CSV rows → import rows. Times may be ISO 8601 with an offset
 * or "yyyy-MM-dd HH:mm" in plant-local time. Values are passed through as
 * typed; the API validates every row and reports what is wrong.
 */
export function csvToImportRows(text: string, offsetMin: number): { rows: OutputImportRow[]; problems: string[] } {
  const table = parseCsv(text);
  if (table.length === 0) return { rows: [], problems: ["The file is empty."] };
  const header = table[0].map((h) => h.trim().toLowerCase());
  const missing = ["line_id", "order_number", "stage", "interval_start", "interval_end", "quantity"].filter((c) => !header.includes(c));
  if (missing.length > 0) return { rows: [], problems: [`Missing column${missing.length === 1 ? "" : "s"}: ${missing.join(", ")}.`] };
  const col = (r: string[], name: string) => {
    const i = header.indexOf(name);
    const v = i < 0 ? "" : (r[i] ?? "").trim();
    return v === "" ? undefined : v;
  };
  const num = (v: string | undefined) => (v === undefined ? undefined : Number(v));
  const time = (v: string | undefined) => {
    if (v === undefined) return undefined;
    const local = /^(\d{4}-\d{2}-\d{2})[ T](\d{2}:\d{2})$/.exec(v);
    return local ? plantIso(local[1], local[2], offsetMin) : v;
  };
  const rows = table.slice(1).map((r) => ({
    sourceRef: col(r, "source_ref"),
    lineId: col(r, "line_id"),
    orderNumber: col(r, "order_number"),
    stage: col(r, "stage"),
    intervalStart: time(col(r, "interval_start")),
    intervalEnd: time(col(r, "interval_end")),
    quantity: num(col(r, "quantity")),
    reworkQuantity: num(col(r, "rework_quantity")),
    rejectQuantity: num(col(r, "reject_quantity")),
    operatorsPresent: num(col(r, "operators_present")),
    downtimeMinutes: num(col(r, "downtime_minutes")),
    downtimeReason: col(r, "downtime_reason"),
    notes: col(r, "notes")
  }));
  const problems = rows.flatMap((r, i) =>
    [r.quantity, r.reworkQuantity, r.rejectQuantity, r.operatorsPresent, r.downtimeMinutes].some((n) => n !== undefined && Number.isNaN(n))
      ? [`Row ${i + 1}: a number column holds text.`]
      : []
  );
  return { rows, problems };
}
