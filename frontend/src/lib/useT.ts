"use client";

import { useAppStore } from "@/store/app.store";
import { t } from "@/i18n/registry";

export function useT() {
  const locale = useAppStore((s) => s.locale);
  return {
    locale,
    t: (path: string, params?: Record<string, string | number>) => t(locale, path, params)
  };
}