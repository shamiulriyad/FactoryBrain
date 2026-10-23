"use client";

import { useAppStore } from "@/store/app.store";
import { motion, useReducedMotion } from "framer-motion";
import { cn } from "@/lib/cn";
import type { Locale } from "@/i18n/registry";

export function LanguageToggle({ className }: { className?: string }) {
  const locale = useAppStore((s) => s.locale);
  const setLocale = useAppStore((s) => s.setLocale);
  const reduceMotion = useReducedMotion();
  const options: { value: Locale; label: string }[] = [
    { value: "en", label: "EN" },
    { value: "bn", label: "বাং" }
  ];
  return (
    <div
      className={cn("relative inline-flex rounded-md border border-border-subtle p-0.5 bg-surface-2", className)}
      role="radiogroup"
      aria-label="Language"
    >
      {options.map((o) => {
        const active = locale === o.value;
        return (
          <motion.button
            key={o.value}
            type="button"
            role="radio"
            aria-checked={active}
            onClick={() => setLocale(o.value)}
            whileTap={reduceMotion ? undefined : { scale: 0.94 }}
            transition={{ type: "spring", stiffness: 420, damping: 30 }}
            className={cn(
              "relative px-2 h-7 text-caption rounded transition-colors z-[1]",
              active ? "text-[var(--btn-primary-fg)]" : "text-fg-secondary hover:text-fg-primary"
            )}
          >
            {active && !reduceMotion && (
              <motion.span
                layoutId="lang-active"
                className="absolute inset-0 rounded bg-[var(--btn-primary-bg)] -z-[1]"
                transition={{ type: "spring", stiffness: 380, damping: 30 }}
              />
            )}
            {active && reduceMotion && (
              <span className="absolute inset-0 rounded bg-[var(--btn-primary-bg)] -z-[1]" />
            )}
            <span className="relative">{o.label}</span>
          </motion.button>
        );
      })}
    </div>
  );
}