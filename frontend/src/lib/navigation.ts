// The app's navigation, in one place. The sidebar, the mobile drawer, the
// top bar title and the Ctrl/Cmd K palette all render from this data, so a
// page's required role is declared once. Hiding an item is a convenience:
// every page re-checks the role and the API enforces it on every request.

import {
  BarChart3,
  Bell,
  BookOpenText,
  Bot,
  Box,
  ClipboardCheck,
  Factory,
  Gauge,
  HelpCircle,
  Layers,
  LayoutGrid,
  Lightbulb,
  MessageSquareText,
  Network,
  PlugZap,
  ScanLine,
  ScrollText,
  Settings,
  ShieldCheck,
  Truck,
  UsersRound,
} from "lucide-react";
import type { LucideIcon } from "lucide-react";
import type { RequiredRole } from "@/lib/auth";

export type FloorId = "fabric" | "cutting" | "sewing" | "qc" | "packing";

/** Which live count a badge shows. */
export type BadgeSource = "alerts" | "approvals";

export interface NavItem {
  id: string;
  /** i18n key for the label. */
  labelKey: string;
  href: string;
  icon?: LucideIcon;
  /** Production floors show their floor code in a round "lift button" instead of an icon. */
  floorCode?: string;
  floorId?: FloorId;
  badge?: BadgeSource;
  /** Lowest role that may open the page. Items above the user's role are hidden. */
  role: RequiredRole;
  /** Browser-only demo dataset; no API behind it. */
  demo?: boolean;
  /** Extra words for the command palette. */
  keywords?: string;
}

export interface NavGroup {
  id: string;
  /** i18n key for the group label. */
  labelKey: string;
  items: NavItem[];
}

export const FLOORS: { id: FloorId; code: string; labelKey: string; href: string }[] = [
  { id: "fabric", code: "G", labelKey: "nav.floors.fabric", href: "/app/floors/fabric" },
  { id: "cutting", code: "1", labelKey: "nav.floors.cutting", href: "/app/floors/cutting" },
  { id: "sewing", code: "2", labelKey: "nav.floors.sewing", href: "/app/floors/sewing" },
  { id: "qc", code: "3", labelKey: "nav.floors.qc", href: "/app/floors/qc" },
  { id: "packing", code: "4", labelKey: "nav.floors.packing", href: "/app/floors/packing" },
];

export const NAV_GROUPS: NavGroup[] = [
  {
    id: "monitor",
    labelKey: "sidebar.monitor",
    items: [
      { id: "overview", labelKey: "nav.overview", href: "/app", icon: LayoutGrid, role: "Viewer", keywords: "dashboard home line board brief" },
      { id: "floor-map", labelKey: "nav.floorMap", href: "/app/floor-map", icon: Layers, role: "Viewer", keywords: "building floors map live" },
      { id: "alerts", labelKey: "nav.alerts", href: "/app/activity", icon: Bell, badge: "alerts", role: "Viewer", keywords: "alerts whatsapp floor notifications activity" },
    ],
  },
  {
    id: "floors",
    labelKey: "sidebar.floors",
    // Material-flow order: fabric comes in at G, cartons leave from 4.
    items: FLOORS.map((f) => ({
      id: `floor-${f.id}`,
      labelKey: f.labelKey,
      href: f.href,
      floorCode: f.code,
      floorId: f.id,
      role: "Viewer" as const,
      keywords: `floor ${f.code} ${f.id}`,
    })),
  },
  {
    id: "plan",
    labelKey: "sidebar.plan",
    items: [
      { id: "orders", labelKey: "nav.orders", href: "/app/merchandising", icon: Truck, role: "Viewer", keywords: "merchandising buyer order po purchase order shipment tech pack milestones deadlines" },
      { id: "inventory", labelKey: "nav.inventory", href: "/app/inventory", icon: Box, role: "Viewer", keywords: "stock fabric trims warehouse" },
      { id: "reports", labelKey: "nav.reports", href: "/app/reports", icon: BarChart3, role: "Viewer", keywords: "report export summary" },
    ],
  },
  {
    // Pages that predate the floor-based layout; kept reachable here.
    id: "tools",
    labelKey: "sidebar.tools",
    items: [
      { id: "approvals", labelKey: "nav.approvals", href: "/app/approvals", icon: ShieldCheck, badge: "approvals", role: "Viewer", keywords: "approve reject decisions queue" },
      { id: "production", labelKey: "nav.production", href: "/app/production", icon: Factory, role: "Viewer", keywords: "production planning plan output efficiency wip bottleneck line balancing smv sam target delay shift" },
      { id: "qc", labelKey: "nav.qc", href: "/app/qc", icon: ClipboardCheck, role: "Viewer", keywords: "quality defects rework inspections history dhu taxonomy" },
      { id: "vision", labelKey: "nav.vision", href: "/app/vision", icon: ScanLine, role: "Viewer", keywords: "camera photo defect image inspect upload vision" },
      { id: "sensors", labelKey: "nav.sensors", href: "/app/sensors", icon: Gauge, role: "Viewer", keywords: "telemetry energy rfid readings machines" },
      { id: "workforce", labelKey: "nav.workforce", href: "/app/workforce", icon: UsersRound, role: "Viewer", demo: true, keywords: "workers hr attendance leave overtime wellbeing grievance skills training bonus" },
      { id: "ask", labelKey: "nav.ask", href: "/app/ask", icon: MessageSquareText, role: "Viewer", keywords: "question rag assistant search manuals" },
      { id: "knowledge", labelKey: "nav.knowledge", href: "/app/knowledge", icon: BookOpenText, role: "Viewer", keywords: "documents rag ingest reindex embeddings manuals" },
      { id: "agents", labelKey: "nav.agents", href: "/app/agents", icon: Bot, role: "Viewer", keywords: "run agent roster" },
      { id: "audit", labelKey: "nav.audit", href: "/app/audit", icon: ScrollText, role: "Admin", keywords: "log history trail" },
      { id: "insights", labelKey: "nav.insights", href: "/app/insights", icon: Lightbulb, role: "Viewer", demo: true },
      { id: "brain", labelKey: "nav.brain", href: "/app/brain", icon: Network, role: "Viewer", demo: true, keywords: "graph knowledge" },
      { id: "integrations", labelKey: "nav.integrations", href: "/app/integrations", icon: PlugZap, role: "Viewer", demo: true },
    ],
  },
];

/** Pinned to the bottom of the sidebar, above the account button. */
export const FOOTER_ITEMS: NavItem[] = [
  { id: "settings", labelKey: "nav.settings", href: "/app/settings", icon: Settings, role: "Viewer", keywords: "llm ai model api key users roles appearance" },
  { id: "help", labelKey: "nav.help", href: "/app/help", icon: HelpCircle, role: "Viewer", keywords: "help tour support guide" },
];

/** Every navigable item, for the palette and page titles. */
export const ALL_NAV_ITEMS: NavItem[] = [...NAV_GROUPS.flatMap((g) => g.items), ...FOOTER_ITEMS];

/** Stable test id per item, matching the e2e suite (`nav-<path after /app/>`). */
export function navTestId(item: NavItem): string {
  return `nav-${item.href.replace(/^\/app\/?/, "").replace(/\//g, "-") || "overview"}`;
}

export function isActivePath(pathname: string | null, href: string): boolean {
  if (!pathname) return false;
  if (href === "/app") return pathname === "/app";
  return pathname === href || pathname.startsWith(href + "/");
}

/** The nav item for the current path (longest match), for page titles. */
export function currentNavItem(pathname: string | null): NavItem | null {
  let best: NavItem | null = null;
  for (const it of ALL_NAV_ITEMS) {
    if (isActivePath(pathname, it.href) && (!best || it.href.length > best.href.length)) best = it;
  }
  return best;
}

/* ------------------------------------------------------------------ */
/* Factory switcher (stub until the API exposes the user's factories)  */
/* ------------------------------------------------------------------ */

export interface FactoryOption {
  id: string;
  code: string;
  name: string;
  /** e.g. "5 floors · Shift A" */
  detail: string;
}

// TODO: load from the API once a user can belong to several factories.
export const FACTORIES: FactoryOption[] = [
  { id: "unit-2", code: "U2", name: "Unit 2", detail: "5 floors · Shift A" },
  { id: "unit-1", code: "U1", name: "Unit 1", detail: "4 floors · Shift A" },
  { id: "unit-3", code: "U3", name: "Unit 3", detail: "3 floors · Shift B" },
];
