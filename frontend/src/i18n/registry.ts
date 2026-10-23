import { en } from "./en";
import { bn } from "./bn";
import type { Dict } from "./en";

export type Locale = "en" | "bn";

const dicts: Record<Locale, Dict> = { en, bn };

export function t(locale: Locale, path: string, params?: Record<string, string | number>): string {
  const segments = path.split(".");
  let value: any = dicts[locale];
  for (const seg of segments) {
    if (value == null) return path;
    value = value[seg];
  }
  if (typeof value !== "string") return path;
  if (!params) return value;
  return value.replace(/\{(\w+)\}/g, (_, k) => String(params[k] ?? `{${k}}`));
}

export function tArray(locale: Locale, path: string): string[] {
  const segments = path.split(".");
  let value: any = dicts[locale];
  for (const seg of segments) {
    if (value == null) return [];
    value = value[seg];
  }
  if (Array.isArray(value)) return value as string[];
  // Fallback to English so the UI never breaks in the alternate locale.
  let en: any = dicts.en;
  for (const seg of segments) {
    if (en == null) return [];
    en = en[seg];
  }
  return Array.isArray(en) ? (en as string[]) : [];
}

export const availableLocales: Locale[] = ["en", "bn"];
export const defaultLocale: Locale = "en";
