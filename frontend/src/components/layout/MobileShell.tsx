"use client";

import { usePathname } from "next/navigation";
import { cn } from "@/lib/cn";
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
import Link from "next/link";
import { useT } from "@/lib/useT";

const items = [
  { href: "/app", icon: LayoutDashboard, key: "nav.overview" },
  { href: "/app/approvals", icon: ShieldCheck, key: "nav.approvals" },
  { href: "/app/qc", icon: ClipboardCheck, key: "nav.qc" },
  { href: "/app/activity", icon: Activity, key: "nav.activity" },
  { href: "/app/ask", icon: MessageSquareText, key: "nav.ask" },
  { href: "/app/brain", icon: Network, key: "nav.brain" },
  { href: "/app/insights", icon: Lightbulb, key: "nav.insights" },
  { href: "/app/agents", icon: Bot, key: "nav.agents" },
  { href: "/app/vision", icon: ScanLine, key: "nav.vision" },
  { href: "/app/integrations", icon: PlugZap, key: "nav.integrations" },
  { href: "/app/settings", icon: Settings, key: "nav.settings" }
];

export function MobileShell() {
  const pathname = usePathname();
  const { t } = useT();
  return (
    <nav className="md:hidden fixed bottom-0 inset-x-0 z-30 bg-canvas border-t border-border-subtle overflow-x-auto">
      <ul className="flex items-stretch min-w-max">
        {items.map((it) => {
          const active = pathname === it.href || (it.href !== "/app" && pathname.startsWith(it.href + "/"));
          const Icon = it.icon;
          const label = t(it.key) as string;
          return (
            <li key={it.href}>
              <Link
                href={it.href}
                aria-current={active ? "page" : undefined}
                className={cn(
                  "flex flex-col items-center justify-center gap-0.5 px-3 py-2 min-w-[4.5rem] text-caption",
                  active ? "text-fg-primary" : "text-fg-tertiary"
                )}
              >
                <Icon size={16} />
                <span className="max-w-[4.5rem] truncate">{label}</span>
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
