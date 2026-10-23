"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import {
  LayoutDashboard,
  MessageSquareText,
  Network,
  Lightbulb,
  Bot,
  ShieldCheck,
  Activity,
  PlugZap,
  Settings,
  ScanLine,
  ClipboardCheck
} from "lucide-react";
import { motion, useReducedMotion } from "framer-motion";
import { cn } from "@/lib/cn";
import { useT } from "@/lib/useT";
import { approvalService } from "@/services/approval.service";
import { insightService } from "@/services/insight.service";
import { useBusinessStore } from "@/store/business.store";
import { useMounted } from "@/lib/persist";
import { useEffect, useState } from "react";
import { Brain3D } from "@/components/agents/Brain3D";

export function Sidebar() {
  const pathname = usePathname();
  const { t } = useT();
  const mounted = useMounted();
  const reduceMotion = useReducedMotion();

  // Live counts for unread badges. We re-derive on a soft tick.
  const [tick, setTick] = useState(0);
  useEffect(() => {
    const id = setInterval(() => setTick((n) => n + 1), 5000);
    return () => clearInterval(id);
  }, []);

  const approvalCount = mounted ? approvalService.queue().length : 0;
  const insightNewCount = mounted ? insightService.feed({ stage: "suggested" }).length : 0;
  const sources = useBusinessStore.getState().sources;
  const integrationsCount = mounted ? sources.filter((s) => s.connected).length : 0;

  const isActive = (href: string) => pathname === href || pathname.startsWith(href + "/");

  const groups: Array<{
    label: string;
    items: Array<{ href: string; label: string; icon: React.ComponentType<any>; badge?: number }>;
  }> = [
    {
      // Operations — the day-to-day floor view. Overview is the home page
      // and includes the live line board; there is no separate line-board
      // route, so the two collapse here.
      label: t("sidebar.operations"),
      items: [
        { href: "/app", label: t("nav.overview"), icon: LayoutDashboard },
        { href: "/app/approvals", label: t("nav.approvals"), icon: ShieldCheck, badge: approvalCount },
        { href: "/app/qc", label: t("nav.qc"), icon: ClipboardCheck },
        { href: "/app/activity", label: t("nav.activity"), icon: Activity }
      ]
    },
    {
      // Intelligence — Q&A, graph, insights, agents, vision. Vision is
      // listed unconditionally under this group (was previously gated by
      // a feature flag in the business store; that flag is still respected
      // by /app/vision's own page-level guard, which keeps the old
      // toggle behaviour intact while always surfacing the route).
      label: t("sidebar.intelligence"),
      items: [
        { href: "/app/ask", label: t("nav.ask"), icon: MessageSquareText },
        { href: "/app/brain", label: t("nav.brain"), icon: Network },
        { href: "/app/insights", label: t("nav.insights"), icon: Lightbulb, badge: insightNewCount },
        { href: "/app/agents", label: t("nav.agents"), icon: Bot },
        { href: "/app/vision", label: t("nav.vision"), icon: ScanLine }
      ]
    },
    {
      // Floor — integrations and settings. Integrations surface the
      // connected-source count as the only live signal.
      label: t("sidebar.floor"),
      items: [
        { href: "/app/integrations", label: t("nav.integrations"), icon: PlugZap, badge: integrationsCount ? integrationsCount : undefined },
        { href: "/app/settings", label: t("nav.settings"), icon: Settings }
      ]
    }
  ];

  return (
    <aside className="h-full w-[232px] shrink-0 flex flex-col bg-[var(--bench)] text-[var(--bench-fg)]">
      <Link href="/app/agents" className="px-4 pt-4 pb-3 border-b border-white/10 flex items-center gap-3">
        <span className="shrink-0">
          <Brain3D size={56} />
        </span>
        <span>
          <span className="block text-body font-semibold tracking-tight">{t("app.name")}</span>
          <span className="block text-caption text-[var(--bench-muted)]">Factory Brain</span>
        </span>
      </Link>

      <nav className="flex-1 overflow-y-auto py-4 relative" aria-label="Primary">
        <div className="absolute left-[22px] top-6 bottom-6 w-px bg-white/15" aria-hidden />
        {groups.map((group) => (
          <div key={group.label} className="px-3 mb-4 relative">
            <div className="text-caption text-[var(--bench-muted)] px-2 mb-2 pl-6">{group.label}</div>
            <ul className="space-y-0.5">
              {group.items.map((item) => {
                const Icon = item.icon;
                const active = isActive(item.href) && (item.href !== "/app" || pathname === "/app");
                return (
                  <li key={item.href}>
                    <Link
                      href={item.href}
                      aria-current={active ? "page" : undefined}
                      className={cn(
                        "group relative flex items-center gap-2.5 h-8 pl-6 pr-2 text-caption",
                        "transition-colors duration-150",
                        active
                          ? "bg-white/10 text-white"
                          : "text-[var(--bench-muted)] hover:text-white hover:bg-white/5"
                      )}
                    >
                      <span
                        className={cn(
                          "absolute left-[7px] size-2 rounded-full border border-[var(--bench)]",
                          active ? "bg-[var(--accent)]" : "bg-white/30"
                        )}
                        aria-hidden
                      />
                      <Icon size={15} className={active ? "text-[var(--accent)]" : undefined} />
                      <span className="truncate flex-1">{item.label}</span>
                      {item.badge != null && item.badge > 0 && (
                        <motion.span
                          key={item.badge}
                          initial={reduceMotion ? false : { scale: 0.8, opacity: 0 }}
                          animate={{ scale: 1, opacity: 1 }}
                          transition={{ type: "spring", stiffness: 460, damping: 30 }}
                          className="text-[10px] text-[var(--accent-fg-on-bg)] bg-[var(--accent)] rounded-full px-1.5"
                        >
                          {item.badge}
                        </motion.span>
                      )}
                    </Link>
                  </li>
                );
              })}
            </ul>
          </div>
        ))}
      </nav>

      <div className="px-3 py-3 border-t border-white/10 text-caption text-[var(--bench-muted)]">
        <div className="mb-1.5 px-2">{t("common.search")}</div>
        <div className="px-2 flex items-center gap-2">
          <span className="kbd">⌘</span>
          <span className="kbd">K</span>
        </div>
      </div>
    </aside>
  );
}