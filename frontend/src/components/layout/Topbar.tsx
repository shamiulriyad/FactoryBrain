"use client";

import { Search, Moon, Sun, Monitor, HelpCircle, Factory } from "lucide-react";
import Link from "next/link";
import { motion, useReducedMotion } from "framer-motion";
import { LanguageToggle } from "@/components/ui/LanguageToggle";
import { useAppStore } from "@/store/app.store";
import { businessService } from "@/services/business.service";
import { useT } from "@/lib/useT";
import { useMounted } from "@/lib/persist";
import { cn } from "@/lib/cn";
import { Tooltip } from "@/components/ui/Tooltip";
import { BrandMark } from "@/components/brand/BrandMark";

const THEME_OPTIONS: Array<{ value: "light" | "dark" | "system"; icon: any; label: string }> = [
  { value: "light", icon: Sun, label: "Light" },
  { value: "dark", icon: Moon, label: "Dark" },
  { value: "system", icon: Monitor, label: "System" }
];

export function Topbar({ onHelp }: { onHelp?: () => void }) {
  const open = useAppStore((s) => s.openCommandPalette);
  const setTheme = useAppStore((s) => s.setTheme);
  const theme = useAppStore((s) => s.theme);
  const { t } = useT();
  const mounted = useMounted();
  const factoryName = mounted ? (businessService.getProfile().factoryName || "BunonBrain Demo Plant") : "BunonBrain Demo Plant";
  const reduceMotion = useReducedMotion();

  function triggerHelp() {
    if (typeof window !== "undefined") {
      window.dispatchEvent(new CustomEvent("bunonbrain:restart-tour"));
    }
    onHelp?.();
  }

  return (
    <header className="h-14 border-b border-border-subtle bg-canvas sticky top-0 z-20 flex items-center px-4 gap-3">
      <Link
        href="/app"
        aria-label="BunonBrain home"
        data-tour="brand"
        className="md:hidden inline-flex items-center gap-2 rounded-md px-1.5 py-1 hover:bg-surface-2 transition-colors press"
      >
        <BrandMark size={26} framed />
        <span className="hidden md:inline text-body font-semibold tracking-tight text-fg-primary">{t("app.name")}</span>
      </Link>

      <motion.button
        type="button"
        onClick={open}
        aria-label={t("common.search") as string}
        data-tour="search"
        className="flex items-center gap-2 h-9 px-3 rounded-md bg-surface border border-border-subtle text-fg-secondary hover:text-fg-primary hover:border-border-strong transition-colors w-72"
      >
        <Search size={14} />
        <span className="text-caption flex-1 text-left">{t("common.search")}</span>
        <span className="kbd">⌘</span>
        <span className="kbd">K</span>
      </motion.button>

      <FactoryScopeChip name={factoryName} />

      <div className="ml-auto flex items-center gap-2">

        {/* Theme switcher */}
        <div
          className="inline-flex rounded-md border border-border-subtle p-0.5 bg-surface-2"
          role="radiogroup"
          aria-label="Theme"
        >
          {THEME_OPTIONS.map((o) => {
            const Icon = o.icon;
            const active = theme === o.value;
            return (
              <Tooltip key={o.value} content={o.label}>
                <motion.button
                  type="button"
                  role="radio"
                  aria-checked={active}
                  aria-label={o.label}
                  onClick={() => setTheme(o.value)}
                  whileHover={reduceMotion ? undefined : { y: -1 }}
                  whileTap={reduceMotion ? undefined : { scale: 0.94 }}
                  transition={{ type: "spring", stiffness: 420, damping: 30 }}
                  className={cn(
                    "size-7 rounded inline-flex items-center justify-center transition-colors",
                    active
                      ? "bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)]"
                      : "text-fg-secondary hover:text-fg-primary"
                  )}
                >
                  <Icon size={13} />
                </motion.button>
              </Tooltip>
            );
          })}
        </div>

        <LanguageToggle />

        {onHelp && (
          <Tooltip content={t("tour.restart") as string}>
            <motion.button
              type="button"
              onClick={triggerHelp}
              aria-label={t("tour.restart") as string}
              whileHover={reduceMotion ? undefined : { y: -1 }}
              whileTap={reduceMotion ? undefined : { scale: 0.94 }}
              transition={{ type: "spring", stiffness: 420, damping: 30 }}
              className="size-8 rounded-md border border-border-subtle bg-surface-2 text-fg-secondary hover:text-fg-primary hover:border-border-strong inline-flex items-center justify-center transition-colors"
            >
              <HelpCircle size={14} />
            </motion.button>
          </Tooltip>
        )}
      </div>
    </header>
  );
}

function FactoryScopeChip({ name }: { name: string }) {
  const { t } = useT();
  return (
    <div
      className="hidden lg:inline-flex items-center gap-2 h-9 pl-2 pr-2.5 rounded-md border border-border-subtle bg-surface-2 text-caption text-fg-secondary"
      title={`${t("topbar.factory")}: ${name}`}
    >
      <Factory size={13} className="text-accent" />
      <span className="text-fg-primary truncate max-w-[140px]">{name}</span>
      <span className="mono-pill text-fg-tertiary">{t("topbar.scope")}</span>
    </div>
  );
}