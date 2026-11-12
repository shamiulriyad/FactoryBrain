"use client";

import Link from "next/link";
import { cn } from "@/lib/cn";
import type { NavItem } from "@/lib/navigation";
import type { FloorStatus } from "@/lib/useFloorStatus";
import { FloorKey } from "./FloorKey";
import { StatusMark } from "./StatusMark";

/** Shows or hides the shared tooltip of the collapsed sidebar. */
export type HintHandler = (text: string | null, anchor?: HTMLElement) => void;

export const rowBase =
  "group relative flex min-h-11 w-full items-center rounded-lg text-[14px] leading-5 transition-colors " +
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sb-accent";

export function rowState(active: boolean) {
  return active ? "bg-sb-active font-semibold text-sb-text" : "text-sb-dim hover:bg-sb-hover hover:text-sb-text";
}

export function rowLayout(collapsed: boolean) {
  return collapsed ? "mx-auto w-11 justify-center px-0" : "gap-3 px-2.5";
}

/** Badge colours: alerts are urgent (red), approvals are a to-do (accent). */
const BADGE_STYLE = {
  alerts: "bg-sb-act text-sb-ink",
  approvals: "bg-sb-accent text-sb-ink",
} as const;

interface Props {
  item: NavItem;
  label: string;
  active: boolean;
  collapsed: boolean;
  /** Accessible name when it should say more than the label ("Floor 2, Sewing, Act"). */
  ariaLabel?: string;
  badgeCount?: number;
  status?: FloorStatus;
  statusLabel?: string;
  demoLabel?: string;
  testId?: string;
  onNavigate?: () => void;
  onHint?: HintHandler;
}

export function SidebarItem({
  item,
  label,
  active,
  collapsed,
  ariaLabel,
  badgeCount = 0,
  status,
  statusLabel,
  demoLabel,
  testId,
  onNavigate,
  onHint,
}: Props) {
  const Icon = item.icon;
  const name = ariaLabel ?? label;
  const showHint = (e: { currentTarget: HTMLElement }) => collapsed && onHint?.(name, e.currentTarget);
  const hideHint = () => collapsed && onHint?.(null);
  const badgeText = badgeCount > 99 ? "99+" : String(badgeCount);

  return (
    <li>
      <Link
        href={item.href}
        onClick={onNavigate}
        aria-current={active ? "page" : undefined}
        aria-label={collapsed || ariaLabel ? name : undefined}
        data-testid={testId}
        onMouseEnter={showHint}
        onMouseLeave={hideHint}
        onFocus={showHint}
        onBlur={hideHint}
        className={cn(rowBase, rowState(active), rowLayout(collapsed))}
      >
        {item.floorCode ? (
          <FloorKey code={item.floorCode} active={active} status={status} showDot={collapsed} />
        ) : Icon ? (
          <Icon
            size={18}
            strokeWidth={1.8}
            aria-hidden="true"
            className={cn("shrink-0", active ? "text-sb-accent" : "text-sb-muted group-hover:text-sb-dim")}
          />
        ) : null}

        {!collapsed && <span className="min-w-0 flex-1 truncate">{label}</span>}

        {!collapsed && item.demo && demoLabel && (
          <span className="shrink-0 rounded px-1.5 py-0.5 font-plexmono text-[10px] uppercase tracking-[0.06em] text-sb-label ring-1 ring-inset ring-sb-border">
            {demoLabel}
          </span>
        )}

        {!collapsed && status && statusLabel && <StatusMark status={status} label={statusLabel} />}

        {badgeCount > 0 && item.badge && (
          <span
            aria-hidden="true"
            className={cn(
              "inline-flex shrink-0 items-center justify-center rounded-full font-plexmono font-semibold leading-none",
              BADGE_STYLE[item.badge],
              collapsed
                ? "absolute right-0.5 top-0.5 h-4 min-w-4 px-1 text-[10px] shadow-[0_0_0_2px_#0d1218]"
                : "h-5 min-w-5 px-1.5 text-[11px]",
            )}
          >
            {badgeText}
          </span>
        )}
      </Link>
    </li>
  );
}
