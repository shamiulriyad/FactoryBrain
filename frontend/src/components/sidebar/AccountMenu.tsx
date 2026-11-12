"use client";

import { useRouter } from "next/navigation";
import { Check, LogOut } from "lucide-react";
import { cn } from "@/lib/cn";
import { logout, useAuthStore } from "@/lib/auth";
import { useAppStore } from "@/store/app.store";
import { useT } from "@/lib/useT";
import type { Locale } from "@/i18n/registry";
import type { HintHandler } from "./SidebarItem";
import { menuItem, menuPanel, usePopover } from "./usePopover";

const LANGUAGES: { value: Locale; label: string }[] = [
  { value: "en", label: "English" },
  { value: "bn", label: "বাংলা" },
];

/** "rahim.uddin@x.com" → "RU"; "admin@x.com" → "A". */
function initialsOf(email: string): string {
  const local = email.split("@")[0] ?? "";
  const parts = local.split(/[._-]+/).filter(Boolean);
  return ((parts[0]?.[0] ?? "") + (parts[1]?.[0] ?? "")).toUpperCase() || "?";
}

/** Account button pinned to the bottom of the sidebar, with language and sign-out. */
export function AccountMenu({
  collapsed,
  factoryName,
  onHint,
}: {
  collapsed: boolean;
  factoryName: string;
  onHint?: HintHandler;
}) {
  const user = useAuthStore((s) => s.user);
  const locale = useAppStore((s) => s.locale);
  const setLocale = useAppStore((s) => s.setLocale);
  const router = useRouter();
  const { t } = useT();
  const pop = usePopover(collapsed ? "right" : "above");

  if (!user) return null;
  const role = String(user.role);
  const languageName = t("sidebar.languageName");
  const name = `${t("sidebar.account")}: ${user.email}, ${role}`;

  async function signOut() {
    pop.close();
    await logout();
    router.replace("/login");
  }

  return (
    <div data-testid="session-chip">
      <button
        ref={pop.buttonRef}
        type="button"
        aria-haspopup="menu"
        aria-expanded={pop.open}
        aria-label={name}
        onClick={() => {
          onHint?.(null);
          pop.setOpen((o) => !o);
        }}
        onMouseEnter={(e) => collapsed && !pop.open && onHint?.(name, e.currentTarget)}
        onMouseLeave={() => collapsed && onHint?.(null)}
        onFocus={(e) => collapsed && !pop.open && onHint?.(name, e.currentTarget)}
        onBlur={() => collapsed && onHint?.(null)}
        className={cn(
          "flex min-h-11 items-center rounded-lg text-left transition-colors hover:bg-sb-hover",
          "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sb-accent",
          collapsed ? "mx-auto size-11 justify-center" : "w-full gap-2.5 px-2 py-1.5",
        )}
      >
        <span
          aria-hidden="true"
          className="inline-flex size-8 shrink-0 items-center justify-center rounded-full bg-sb-accent text-[12px] font-semibold text-sb-ink"
        >
          {initialsOf(user.email)}
        </span>
        <span className={cn("min-w-0 flex-1", collapsed && "sr-only")}>
          <span className="block truncate text-[13px] font-medium leading-5 text-sb-text" title={user.email}>
            {user.email}
          </span>
          <span className="block truncate text-[12px] leading-4 text-sb-muted">
            <span data-testid="session-role">{role}</span> · {factoryName} · {languageName}
          </span>
        </span>
      </button>

      {pop.open && (
        <div ref={pop.menuRef} role="menu" aria-label={t("sidebar.account")} className={menuPanel} style={pop.style} onKeyDown={pop.onMenuKeyDown}>
          <div className="px-2.5 pb-2 pt-2">
            <p className="truncate text-[13px] font-medium text-sb-text">{user.email}</p>
            <p className="text-[12px] text-sb-muted">{role}</p>
          </div>
          <div role="group" aria-label={t("sidebar.language")} className="border-t border-sb-divider pt-1">
            <p aria-hidden="true" className="px-2.5 pb-1 pt-1.5 font-plexmono text-[11px] uppercase tracking-[0.08em] text-sb-label">
              {t("sidebar.language")}
            </p>
            {LANGUAGES.map((l) => (
              <button
                key={l.value}
                type="button"
                role="menuitemradio"
                aria-checked={locale === l.value}
                tabIndex={-1}
                lang={l.value}
                onClick={() => setLocale(l.value)}
                className={cn(menuItem, locale === l.value && "text-sb-text")}
              >
                <span className="flex-1">{l.label}</span>
                {locale === l.value && <Check size={16} aria-hidden="true" className="text-sb-accent" />}
              </button>
            ))}
          </div>
          <div className="mt-1 border-t border-sb-divider pt-1">
            <button type="button" role="menuitem" tabIndex={-1} onClick={() => void signOut()} className={menuItem}>
              <LogOut size={16} aria-hidden="true" className="text-sb-muted" />
              <span>{t("auth.signOut")}</span>
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
