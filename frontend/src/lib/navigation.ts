// The app's navigation, in one place. The sidebar, the mobile drawer, the
// top bar title, the Ctrl/Cmd K palette and the app layout's page gate all
// render from this data, so a page's required permissions are declared once.
// Hiding an item is a convenience: the API enforces every permission on
// every request.

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
import { KeyRound, ShieldHalf, UserCircle2, UserCog, Building2, IdCard, Inbox, Globe, ArrowRightLeft } from "lucide-react";
import { P } from "@/lib/access";

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
  /**
   * Permissions that open the page (any one is enough). Empty = every
   * signed-in user. Items the user can't open are hidden, and the app
   * layout shows Access denied if the URL is opened directly.
   */
  perms: string[];
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
      { id: "overview", labelKey: "nav.overview", href: "/app", icon: LayoutGrid, perms: [P.ProductionView, P.MonitoringView, P.ReportsView], keywords: "dashboard home line board brief" },
      { id: "floor-map", labelKey: "nav.floorMap", href: "/app/floor-map", icon: Layers, perms: [P.MonitoringView, P.ProductionView], keywords: "building floors map live" },
      { id: "alerts", labelKey: "nav.alerts", href: "/app/activity", icon: Bell, badge: "alerts", perms: [P.MonitoringView, P.ProductionView], keywords: "alerts whatsapp floor notifications activity" },
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
      perms: [P.ProductionView, P.MonitoringView],
      keywords: `floor ${f.code} ${f.id}`,
    })),
  },
  {
    id: "plan",
    labelKey: "sidebar.plan",
    items: [
      { id: "orders", labelKey: "nav.orders", href: "/app/merchandising", icon: Truck, perms: [P.MerchandisingView], keywords: "merchandising buyer order po purchase order shipment tech pack milestones deadlines" },
      { id: "inventory", labelKey: "nav.inventory", href: "/app/inventory", icon: Box, perms: [P.ProductionView], keywords: "stock fabric trims warehouse" },
      { id: "reports", labelKey: "nav.reports", href: "/app/reports", icon: BarChart3, perms: [P.ReportsView], keywords: "report export summary" },
    ],
  },
  {
    // Pages that predate the floor-based layout; kept reachable here.
    id: "tools",
    labelKey: "sidebar.tools",
    items: [
      { id: "approvals", labelKey: "nav.approvals", href: "/app/approvals", icon: ShieldCheck, badge: "approvals", perms: [P.ApprovalsView], keywords: "approve reject decisions queue" },
      { id: "production", labelKey: "nav.production", href: "/app/production", icon: Factory, perms: [P.ProductionView], keywords: "production planning plan output efficiency wip bottleneck line balancing smv sam target delay shift" },
      { id: "qc", labelKey: "nav.qc", href: "/app/qc", icon: ClipboardCheck, perms: [P.QcView], keywords: "quality defects rework inspections history dhu taxonomy" },
      { id: "vision", labelKey: "nav.vision", href: "/app/vision", icon: ScanLine, perms: [P.QcView], keywords: "camera photo defect image inspect upload vision" },
      { id: "sensors", labelKey: "nav.sensors", href: "/app/sensors", icon: Gauge, perms: [P.DevicesView, P.MonitoringView], keywords: "telemetry energy rfid readings machines" },
      { id: "assignments", labelKey: "nav.assignments", href: "/app/assignments", icon: ArrowRightLeft, perms: [P.WorkersView], keywords: "assignment assign workstation table transfer occupancy capacity line layout machine t-025" },
      { id: "workers", labelKey: "nav.workers", href: "/app/workers", icon: IdCard, perms: [P.WorkersView], keywords: "workers worker directory register employee staff id w-001 import csv export status hr profile" },
      { id: "workforce", labelKey: "nav.workforce", href: "/app/workforce", icon: UsersRound, perms: [P.WorkersView], demo: true, keywords: "workers hr attendance leave overtime wellbeing grievance skills training bonus" },
      { id: "ask", labelKey: "nav.ask", href: "/app/ask", icon: MessageSquareText, perms: [P.KnowledgeView], keywords: "question rag assistant search manuals" },
      { id: "knowledge", labelKey: "nav.knowledge", href: "/app/knowledge", icon: BookOpenText, perms: [P.KnowledgeView], keywords: "documents rag ingest reindex embeddings manuals" },
      { id: "agents", labelKey: "nav.agents", href: "/app/agents", icon: Bot, perms: [P.KnowledgeView], keywords: "run agent roster" },
      { id: "audit", labelKey: "nav.audit", href: "/app/audit", icon: ScrollText, perms: [P.AuditView], keywords: "log history trail" },
      { id: "insights", labelKey: "nav.insights", href: "/app/insights", icon: Lightbulb, perms: [P.KnowledgeView], demo: true },
      { id: "brain", labelKey: "nav.brain", href: "/app/brain", icon: Network, perms: [P.KnowledgeView], demo: true, keywords: "graph knowledge" },
      { id: "integrations", labelKey: "nav.integrations", href: "/app/integrations", icon: PlugZap, perms: [P.DevicesView, P.KnowledgeView], demo: true },
    ],
  },
];

/** Users, roles, factories: shown to administrators. */
export const ADMIN_GROUP: NavGroup = {
  id: "admin",
  labelKey: "sidebar.admin",
  items: [
    { id: "admin-users", labelKey: "nav.users", href: "/app/admin/users", icon: UserCog, perms: [P.UsersView], keywords: "users accounts people create deactivate reset password roles" },
    { id: "admin-roles", labelKey: "nav.roles", href: "/app/admin/roles", icon: ShieldHalf, perms: [P.RolesView], keywords: "roles rbac access" },
    { id: "admin-permissions", labelKey: "nav.permissions", href: "/app/admin/permissions", icon: KeyRound, perms: [P.RolesView], keywords: "permissions matrix rbac" },
    { id: "admin-factories", labelKey: "nav.factories", href: "/app/admin/factories", icon: Building2, perms: [P.FactoriesManage, P.LinesManage, P.DepartmentsManage], keywords: "factory factories tenant lines departments floors" },
    { id: "admin-website", labelKey: "nav.website", href: "/app/admin/website", icon: Globe, perms: [P.PlatformSettingsManage], keywords: "website homepage hero photo image rotation public site" },
    { id: "admin-inquiries", labelKey: "nav.inquiries", href: "/app/admin/inquiries", icon: Inbox, perms: [P.InquiriesManage], keywords: "website inquiries demo requests contact support leads messages" },
  ],
};
NAV_GROUPS.push(ADMIN_GROUP);

/** Personal pages (also reachable from the account menu). */
export const PERSONAL_ITEMS: NavItem[] = [
  { id: "me", labelKey: "nav.me", href: "/app/me", icon: IdCard, perms: [P.SelfView], keywords: "my profile workstation shift worker" },
  { id: "account", labelKey: "nav.account", href: "/app/account", icon: UserCircle2, perms: [], keywords: "account profile password security my access" },
];

/** Pinned to the bottom of the sidebar, above the account button. */
export const FOOTER_ITEMS: NavItem[] = [
  { id: "settings", labelKey: "nav.settings", href: "/app/settings", icon: Settings, perms: [], keywords: "llm ai model api key users roles appearance" },
  { id: "help", labelKey: "nav.help", href: "/app/help", icon: HelpCircle, perms: [], keywords: "help tour support guide" },
];

/** Every navigable item, for the palette and page titles. */
export const ALL_NAV_ITEMS: NavItem[] = [...NAV_GROUPS.flatMap((g) => g.items), ...FOOTER_ITEMS, ...PERSONAL_ITEMS];

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


