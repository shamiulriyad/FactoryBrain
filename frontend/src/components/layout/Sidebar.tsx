"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import { useEffect } from "react";
import { LogOut } from "lucide-react";
import { cn } from "@/lib/cn";
import { useT } from "@/lib/useT";
import { hasRole, logout, useAuthStore } from "@/lib/auth";
import { refreshPendingApprovals, usePendingApprovals } from "@/services/approvals.api";
import { refreshUnreadAlerts, useUnreadAlerts } from "@/services/floorAlerts.api";
import { BrandMark } from "@/components/brand/BrandMark";
import { RoleBadge } from "@/components/ui/Badge";
import { NAV_GROUPS, isActivePath } from "./nav";

/** Poll interval for the sidebar badges (pending approvals, unread alerts). */
const BADGE_POLL_MS = 20_000;

export function Sidebar({ onNavigate, className }: { onNavigate?: () => void; className?: string }) {
  const pathname = usePathname();
  const router = useRouter();
  const { t } = useT();
  const user = useAuthStore((s) => s.user);
  const pending = usePendingApprovals((s) => s.pendingCount);
  const unread = useUnreadAlerts((s) => s.unread);

  useEffect(() => {
    const tick = () => {
      if (document.visibilityState === "hidden") return;
      void refreshPendingApprovals();
      void refreshUnreadAlerts();
    };
    tick();
    const id = setInterval(tick, BADGE_POLL_MS);
    return () => clearInterval(id);
  }, []);

  const badges: Record<string, number> = { "/app/approvals": pending, "/app/activity": unread };

  async function signOut() {
    await logout();
    router.replace("/login");
  }

  return (
    <aside
      className={cn("h-full w-[248px] shrink-0 flex flex-col bg-[var(--bench)] text-[var(--bench-fg)] border-r border-[var(--bench-border)]", className)}
      aria-label="Sidebar"
    >
      <Link href="/app" onClick={onNavigate} className="h-14 px-4 flex items-center gap-2.5 border-b border-[var(--bench-border)] shrink-0">
        <BrandMark size={28} framed />
        <span className="min-w-0">
          <span className="block text-body font-semibold tracking-tight text-white">{t("app.name")}</span>
          <span className="block text-[11px] leading-4 text-[var(--bench-muted)]">Factory operations</span>
        </span>
      </Link>

      <nav className="flex-1 overflow-y-auto px-3 py-4 space-y-5" aria-label="Primary">
        {NAV_GROUPS.map((group) => {
          const items = group.items.filter((it) => hasRole(user, it.role));
          if (items.length === 0) return null;
          return (
            <div key={group.labelKey}>
              <p className="px-2.5 mb-1.5 text-[10.5px] font-semibold tracking-[0.08em] uppercase text-[var(--bench-muted)]/80">
                {t(group.labelKey)}
              </p>
              <ul className="space-y-0.5">
                {items.map((item) => {
                  const Icon = item.icon;
                  const active = isActivePath(pathname, item.href);
                  const badge = badges[item.href] ?? 0;
                  return (
                    <li key={item.href}>
                      <Link
                        href={item.href}
                        onClick={onNavigate}
                        aria-current={active ? "page" : undefined}
                        className="nav-item"
                        data-testid={`nav-${item.href.replace(/^\/app\/?/, "") || "overview"}`}
                      >
                        <Icon size={16} className={active ? "text-[var(--accent)]" : undefined} />
                        <span className="truncate flex-1">{t(item.labelKey)}</span>
                        {item.demo && (
                          <span className="text-[10px] font-semibold uppercase tracking-wide px-1.5 rounded bg-white/10 text-[var(--bench-muted)]">
                            Demo
                          </span>
                        )}
                        {badge > 0 && (
                          <span
                            className="min-w-5 h-5 px-1.5 rounded-full text-[11px] leading-5 text-center font-semibold bg-[var(--accent)] text-[#1C1206]"
                            aria-label={`${badge} new`}
                          >
                            {badge > 99 ? "99+" : badge}
                          </span>
                        )}
                      </Link>
                    </li>
                  );
                })}
              </ul>
            </div>
          );
        })}
      </nav>

      {user && (
        <div className="shrink-0 border-t border-[var(--bench-border)] p-3">
          <div className="flex items-center gap-2.5 rounded-lg bg-[var(--bench-2)] p-2.5">
            <span
              className="size-8 shrink-0 rounded-full bg-[var(--accent)] text-[#1C1206] inline-flex items-center justify-center text-caption font-bold uppercase"
              aria-hidden
            >
              {user.email.slice(0, 1)}
            </span>
            <div className="min-w-0 flex-1" data-testid="session-chip">
              <span className="block truncate text-caption text-white" title={user.email}>
                {user.email}
              </span>
              <span className="mt-1 block">
                <RoleBadge role={user.role} testId="session-role" />
              </span>
            </div>
            <button
              type="button"
              onClick={() => void signOut()}
              aria-label={t("auth.signOut")}
              title={t("auth.signOut")}
              className="size-8 shrink-0 rounded-md inline-flex items-center justify-center text-[var(--bench-muted)] hover:text-white hover:bg-white/10 transition-colors"
            >
              <LogOut size={15} />
            </button>
          </div>
        </div>
      )}
    </aside>
  );
}
