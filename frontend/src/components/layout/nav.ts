// The app's navigation, in one place. Sidebar, mobile drawer and the command
// palette all render from this list, so a page's required role is declared
// once. Hiding an item is a convenience: every page re-checks the role and
// the API enforces it on every request.

import {
  Activity,
  BookOpenText,
  Bot,
  ClipboardCheck,
  Gauge,
  LayoutDashboard,
  Lightbulb,
  MessageSquareText,
  Network,
  PlugZap,
  ScanLine,
  ScrollText,
  Settings,
  ShieldCheck
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import type { RequiredRole } from "@/lib/auth";

export type NavItem = {
  href: string;
  /** i18n key for the label. */
  labelKey: string;
  icon: LucideIcon;
  /** Lowest role that may open the page. Items above the user's role are hidden. */
  role: RequiredRole;
  /** Browser-only demo dataset; no API behind it. */
  demo?: boolean;
  /** Extra search words for the command palette. */
  keywords?: string;
};

export type NavGroup = { labelKey: string; items: NavItem[] };

export const NAV_GROUPS: NavGroup[] = [
  {
    labelKey: "sidebar.operations",
    items: [
      { href: "/app", labelKey: "nav.overview", icon: LayoutDashboard, role: "Viewer", keywords: "dashboard home line board brief" },
      { href: "/app/approvals", labelKey: "nav.approvals", icon: ShieldCheck, role: "Viewer", keywords: "approve reject decisions queue" },
      { href: "/app/activity", labelKey: "nav.floorAlerts", icon: Activity, role: "Viewer", keywords: "alerts whatsapp floor notifications" },
      { href: "/app/qc", labelKey: "nav.qc", icon: ClipboardCheck, role: "Viewer", keywords: "quality defects rework inspections history dhu taxonomy" },
      { href: "/app/sensors", labelKey: "nav.sensors", icon: Gauge, role: "Viewer", keywords: "telemetry energy rfid readings machines" }
    ]
  },
  {
    labelKey: "sidebar.intelligence",
    items: [
      { href: "/app/ask", labelKey: "nav.ask", icon: MessageSquareText, role: "Viewer", keywords: "question rag assistant search manuals" },
      { href: "/app/knowledge", labelKey: "nav.knowledge", icon: BookOpenText, role: "Viewer", keywords: "documents rag ingest reindex embeddings manuals" },
      { href: "/app/agents", labelKey: "nav.agents", icon: Bot, role: "Viewer", keywords: "run agent roster" },
      { href: "/app/vision", labelKey: "nav.vision", icon: ScanLine, role: "Viewer", keywords: "camera photo defect image inspect upload vision" }
    ]
  },
  {
    labelKey: "sidebar.admin",
    items: [
      { href: "/app/audit", labelKey: "nav.audit", icon: ScrollText, role: "Admin", keywords: "log history trail" },
      { href: "/app/settings", labelKey: "nav.settings", icon: Settings, role: "Viewer", keywords: "llm ai model api key users roles appearance" }
    ]
  },
  {
    labelKey: "sidebar.demo",
    items: [
      { href: "/app/insights", labelKey: "nav.insights", icon: Lightbulb, role: "Viewer", demo: true },
      { href: "/app/brain", labelKey: "nav.brain", icon: Network, role: "Viewer", demo: true, keywords: "graph knowledge" },
      { href: "/app/integrations", labelKey: "nav.integrations", icon: PlugZap, role: "Viewer", demo: true }
    ]
  }
];

export function isActivePath(pathname: string | null, href: string): boolean {
  if (!pathname) return false;
  if (href === "/app") return pathname === "/app";
  return pathname === href || pathname.startsWith(href + "/");
}

/** The nav item for the current path (longest match), for page titles. */
export function currentNavItem(pathname: string | null): NavItem | null {
  let best: NavItem | null = null;
  for (const g of NAV_GROUPS) {
    for (const it of g.items) {
      if (isActivePath(pathname, it.href) && (!best || it.href.length > best.href.length)) best = it;
    }
  }
  return best;
}
