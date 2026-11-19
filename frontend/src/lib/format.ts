// Number, currency and date formatting. The interface is English only;
// the locale parameter is kept so existing callers don't change.

export type Locale = "en";

export function formatBDT(value: number, _locale: Locale = "en") {
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "BDT",
    maximumFractionDigits: 0
  }).format(value);
}

export function formatNumber(value: number, _locale: Locale = "en") {
  return new Intl.NumberFormat("en-US").format(value);
}

export function formatPercent(value: number, _locale: Locale = "en", digits = 1) {
  return new Intl.NumberFormat("en-US", {
    style: "percent",
    minimumFractionDigits: digits,
    maximumFractionDigits: digits
  }).format(value);
}

export function formatDate(iso: string, _locale: Locale = "en") {
  return new Intl.DateTimeFormat("en-US", {
    day: "numeric",
    month: "short",
    year: "numeric"
  }).format(new Date(iso));
}

/** Date + time, e.g. "25 Oct 2026, 14:05" (local time zone). */
export function formatDateTime(iso: string | null | undefined, _locale: Locale = "en") {
  if (!iso) return "—";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "—";
  return new Intl.DateTimeFormat("en-GB", {
    day: "numeric",
    month: "short",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit"
  }).format(d);
}

export function formatRelative(iso: string, _locale: Locale = "en", now: Date = new Date()) {
  const then = new Date(iso).getTime();
  const diff = (now.getTime() - then) / 1000;
  const rtf = new Intl.RelativeTimeFormat("en-US", { numeric: "auto" });
  let v: number; let u: Intl.RelativeTimeFormatUnit;
  if (diff < 60) return "just now";
  if (diff < 3600) { v = Math.round(diff / 60); u = "minute"; }
  else if (diff < 86400) { v = Math.round(diff / 3600); u = "hour"; }
  else if (diff < 604800) { v = Math.round(diff / 86400); u = "day"; }
  else { v = Math.round(diff / 604800); u = "week"; }
  return rtf.format(-v, u);
}
