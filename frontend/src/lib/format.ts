// Locale-aware formatting for Bangladesh SME context
// Uses Intl with bn-BD / en-US. BDT currency. Bangla numerals for bn.

export type Locale = "en" | "bn";

const banglaDigits = ["০", "১", "২", "৩", "৪", "৫", "৬", "৭", "৮", "৯"];

function toBanglaDigits(input: string | number) {
  return String(input).replace(/[0-9]/g, (d) => banglaDigits[Number(d)]);
}

export function formatBDT(value: number, locale: Locale = "en") {
  const formatted = new Intl.NumberFormat(locale === "bn" ? "bn-BD" : "en-US", {
    style: "currency",
    currency: "BDT",
    maximumFractionDigits: 0
  }).format(value);
  if (locale === "bn") return toBanglaDigits(formatted);
  return formatted;
}

export function formatNumber(value: number, locale: Locale = "en") {
  const formatted = new Intl.NumberFormat(locale === "bn" ? "bn-BD" : "en-US").format(value);
  if (locale === "bn") return toBanglaDigits(formatted);
  return formatted;
}

export function formatPercent(value: number, locale: Locale = "en", digits = 1) {
  const formatted = new Intl.NumberFormat(locale === "bn" ? "bn-BD" : "en-US", {
    style: "percent",
    minimumFractionDigits: digits,
    maximumFractionDigits: digits
  }).format(value);
  if (locale === "bn") return toBanglaDigits(formatted);
  return formatted;
}

export function formatDate(iso: string, locale: Locale = "en") {
  const d = new Date(iso);
  const formatted = new Intl.DateTimeFormat(locale === "bn" ? "bn-BD" : "en-US", {
    day: "numeric",
    month: "short",
    year: "numeric"
  }).format(d);
  if (locale === "bn") return toBanglaDigits(formatted);
  return formatted;
}

export function formatRelative(iso: string, locale: Locale = "en", now: Date = new Date()) {
  const then = new Date(iso).getTime();
  const diff = (now.getTime() - then) / 1000;
  const rtf = new Intl.RelativeTimeFormat(locale === "bn" ? "bn-BD" : "en-US", { numeric: "auto" });
  let v: number; let u: Intl.RelativeTimeFormatUnit;
  if (diff < 60) return locale === "bn" ? "এইমাত্র" : "just now";
  if (diff < 3600) { v = Math.round(diff / 60); u = "minute"; }
  else if (diff < 86400) { v = Math.round(diff / 3600); u = "hour"; }
  else if (diff < 604800) { v = Math.round(diff / 86400); u = "day"; }
  else { v = Math.round(diff / 604800); u = "week"; }
  const out = rtf.format(-v, u);
  return locale === "bn" ? toBanglaDigits(out) : out;
}

// Convert a number to Bangla numerals
export function bn(n: number | string) {
  return toBanglaDigits(n);
}
