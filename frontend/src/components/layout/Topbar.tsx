"use client";

import { Search, Moon, Sun, Monitor, HelpCircle, Menu } from "lucide-react";
import { usePathname } from "next/navigation";
import { LanguageToggle } from "@/components/ui/LanguageToggle";
import { useAppStore, type Theme } from "@/store/app.store";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { currentNavItem } from "./nav";

const THEME_OPTIONS: Array<{ value: Theme; icon: typeof Sun; label: string }> = [
  { value: "light", icon: Sun, label: "Light theme" },
  { value: "dark", icon: Moon, label: "Dark theme" },
  { value: "system", icon: Monitor, label: "Follow system theme" }
];

export function Topbar({ onOpenNav }: { onOpenNav: () => void }) {
  const open = useAppStore((s) => s.openCommandPalette);
  const setTheme = useAppStore((s) => s.setTheme);
  const theme = useAppStore((s) => s.theme);
  const { t } = useT();
  const pathname = usePathname();
  const current = currentNavItem(pathname);

  return (
    <header className="h-14 shrink-0 border-b border-border-subtle bg-[var(--bg-surface)] sticky top-0 z-20 flex items-center gap-2 px-3 md:px-5">
      <button
        type="button"
        onClick={onOpenNav}
        aria-label="Open navigation"
        className="md:hidden size-9 rounded-md inline-flex items-center justify-center text-fg-secondary hover:text-fg-primary hover:bg-surface-2"
      >
        <Menu size={18} />
      </button>

      <div className="min-w-0 flex items-center gap-2">
        {current && (
          <>
            <current.icon size={16} className="hidden sm:block text-fg-tertiary shrink-0" />
            <span className="truncate text-body font-semibold text-fg-primary">{t(current.labelKey)}</span>
          </>
        )}
      </div>

      <div className="ml-auto flex items-center gap-2">
        <button
          type="button"
          onClick={open}
          aria-label={t("common.search")}
          data-tour="search"
          className="flex items-center gap-2 h-9 px-2.5 sm:px-3 rounded-[var(--radius-control)] bg-surface-2 border border-border-subtle text-fg-secondary hover:text-fg-primary hover:border-border-strong transition-colors sm:w-60"
        >
          <Search size={15} />
          <span className="hidden sm:inline text-caption flex-1 text-left">{t("common.search")}</span>
          <span className="hidden sm:inline kbd">Ctrl K</span>
        </button>

        <div className="hidden sm:inline-flex rounded-[var(--radius-control)] border border-border-subtle p-0.5 bg-surface-2" role="radiogroup" aria-label="Theme">
          {THEME_OPTIONS.map((o) => {
            const Icon = o.icon;
            const active = theme === o.value;
            return (
              <button
                key={o.value}
                type="button"
                role="radio"
                aria-checked={active}
                aria-label={o.label}
                title={o.label}
                onClick={() => setTheme(o.value)}
                className={cn(
                  "size-7 rounded-md inline-flex items-center justify-center transition-colors",
                  active ? "bg-[var(--bg-surface)] text-fg-primary shadow-sm" : "text-fg-tertiary hover:text-fg-primary"
                )}
              >
                <Icon size={14} />
              </button>
            );
          })}
        </div>

        <LanguageToggle />

        <button
          type="button"
          onClick={() => window.dispatchEvent(new CustomEvent("bunonbrain:restart-tour"))}
          aria-label={t("tour.restart")}
          title={t("tour.restart")}
          className="hidden sm:inline-flex size-9 rounded-[var(--radius-control)] items-center justify-center text-fg-tertiary hover:text-fg-primary hover:bg-surface-2"
        >
          <HelpCircle size={16} />
        </button>
      </div>
    </header>
  );
}
