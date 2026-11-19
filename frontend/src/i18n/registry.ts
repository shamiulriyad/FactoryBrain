import { en } from "./en";
import type { Dict } from "./en";

/** The interface is English only. */
export type Locale = "en";

const dict: Dict = en;

function lookup(segments: string[]): unknown {
  let value: any = dict;
  for (const seg of segments) {
    if (value == null) return undefined;
    value = value[seg];
  }
  return value;
}

export function t(_locale: Locale, path: string, params?: Record<string, string | number>): string {
  const value = lookup(path.split("."));
  if (typeof value !== "string") return path;
  if (!params) return value;
  return value.replace(/\{(\w+)\}/g, (_, k) => String(params[k] ?? `{${k}}`));
}

export function tArray(_locale: Locale, path: string): string[] {
  const value = lookup(path.split("."));
  return Array.isArray(value) ? (value as string[]) : [];
}

export const availableLocales: Locale[] = ["en"];
export const defaultLocale: Locale = "en";
