"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useCallback, useEffect, useRef, useState, type CSSProperties } from "react";
import { ChevronDown, PanelLeftClose, Search, X } from "lucide-react";
import { cn } from "@/lib/cn";
import { appFontVariables } from "@/lib/fonts";
import { hasRole, useAuthStore } from "@/lib/auth";
import { useT } from "@/lib/useT";
import { useAppStore } from "@/store/app.store";
import { FACTORIES, FOOTER_ITEMS, NAV_GROUPS, isActivePath, navTestId, type NavItem } from "@/lib/navigation";
import { useFloorStatus, usePendingCount } from "@/lib/useFloorStatus";
import { AccountMenu } from "./AccountMenu";
import { FactorySwitcher } from "./FactorySwitcher";
import { SidebarItem, type HintHandler } from "./SidebarItem";

/* ------------------------------------------------------------------ */
/* Collapsed / expanded, remembered per browser                        */
/* ------------------------------------------------------------------ */

const STORAGE_KEY = "factorybrain:sidebar";
const NARROW = "(max-width: 1023.98px)";

function readPreference(): boolean | null {
  try {
    const v = window.localStorage.getItem(STORAGE_KEY);
    return v === "collapsed" ? true : v === "expanded" ? false : null;
  } catch {
    return null;
  }
}

function writePreference(collapsed: boolean) {
  try {
    window.localStorage.setItem(STORAGE_KEY, collapsed ? "collapsed" : "expanded");
  } catch {
    /* private mode or storage blocked: the choice just isn't remembered */
  }
}

/** Below 1024px the sidebar starts collapsed; above, it follows the saved choice. */
function useCollapsed(): [boolean, () => void] {
  const [collapsed, setCollapsed] = useState(() => {
    if (typeof window === "undefined") return false;
    if (window.matchMedia(NARROW).matches) return true;
    return readPreference() ?? false;
  });

  useEffect(() => {
    const mq = window.matchMedia(NARROW);
    const onChange = () => setCollapsed(mq.matches ? true : readPreference() ?? false);
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, []);

  const toggle = useCallback(() => {
    setCollapsed((c) => {
      writePreference(!c);
      return !c;
    });
  }, []);

  return [collapsed, toggle];
}

/* ------------------------------------------------------------------ */
/* Logo                                                                */
/* ------------------------------------------------------------------ */

function LogoMark({ size = 26 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 26 26" aria-hidden="true" focusable="false" className="shrink-0">
      <rect x="3.5" y="2.5" width="19" height="21" rx="1.5" fill="none" stroke="#ece7df" strokeWidth="1.6" />
      <path d="M3.5 8.2h19M3.5 13h19M3.5 17.8h19" stroke="#ece7df" strokeWidth="1.2" opacity="0.55" />
      <rect x="5.2" y="9.4" width="15.6" height="2.4" fill="#f2c27a" />
      <circle cx="16.5" cy="10.6" r="1.5" fill="#ff6b45" />
    </svg>
  );
}

/* ------------------------------------------------------------------ */
/* Sidebar                                                             */
/* ------------------------------------------------------------------ */

const iconButton =
  "inline-flex size-11 shrink-0 items-center justify-center rounded-lg text-sb-muted transition-colors hover:bg-sb-hover hover:text-sb-text " +
  "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sb-accent";

export function Sidebar({ mode, onClose }: { mode: "rail" | "drawer"; onClose?: () => void }) {
  const [railCollapsed, toggleCollapsed] = useCollapsed();
  const collapsed = mode === "rail" && railCollapsed;
  const pathname = usePathname();
  const { t } = useT();
  const user = useAuthStore((s) => s.user);
  const openPalette = useAppStore((s) => s.openCommandPalette);
  const { statuses, alertCount } = useFloorStatus();
  const pendingCount = usePendingCount();
  const [factory, setFactory] = useState(FACTORIES[0]);
  const searchRef = useRef<HTMLInputElement>(null);
  const navRef = useRef<HTMLElement>(null);
  const [hiddenBelow, setHiddenBelow] = useState(0);
  const [scrolledDown, setScrolledDown] = useState(false);
  const [query, setQuery] = useState("");
  const [hint, setHint] = useState<{ text: string; style: CSSProperties } | null>(null);

  const onHint: HintHandler = useCallback((text, anchor) => {
    if (!text || !anchor) return setHint(null);
    const r = anchor.getBoundingClientRect();
    setHint({ text, style: { top: r.top + r.height / 2, left: r.right + 10 } });
  }, []);

  useEffect(() => setHint(null), [collapsed, pathname]);

  // The menu is taller than most laptop screens. Track how many items sit
  // below the fold so the "N more" bar can say so, and keep the current
  // page's item in view after every navigation.
  const measureOverflow = useCallback(() => {
    const nav = navRef.current;
    if (!nav) return;
    const bottom = nav.getBoundingClientRect().bottom;
    const below = Array.from(nav.querySelectorAll("a")).filter((a) => a.getBoundingClientRect().bottom > bottom + 1).length;
    setHiddenBelow(below);
    setScrolledDown(nav.scrollTop > 2);
  }, []);

  useEffect(() => {
    const nav = navRef.current;
    if (!nav) return;
    // The "N more" row can appear and shrink the list, so re-check on resize.
    const keepActiveInView = () => {
      nav.querySelector<HTMLElement>('[aria-current="page"]')?.scrollIntoView({ block: "nearest" });
      measureOverflow();
    };
    keepActiveInView();
    const ro = new ResizeObserver(keepActiveInView);
    ro.observe(nav);
    return () => ro.disconnect();
  }, [pathname, collapsed, user, measureOverflow]);

  const scrollMenu = () => {
    const nav = navRef.current;
    if (!nav) return;
    const reduced = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    nav.scrollBy({ top: nav.clientHeight * 0.8, behavior: reduced ? "auto" : "smooth" });
  };

  // Ctrl/Cmd+K focuses this search box when it's on screen. Registered in
  // the capture phase so it wins over the palette's own Ctrl+K toggle; when
  // the box is hidden (collapsed rail, phone layout) the palette opens.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== "k") return;
      const input = searchRef.current;
      if (!input || input.offsetParent === null) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      input.focus();
    };
    window.addEventListener("keydown", onKey, true);
    return () => window.removeEventListener("keydown", onKey, true);
  }, []);

  const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform);
  const statusWord = (s: keyof typeof statuses) => t(`sidebar.status.${statuses[s]}`);

  const renderItem = (item: NavItem) => {
    const label = t(item.labelKey);
    const active = isActivePath(pathname, item.href);
    const badgeCount = item.badge === "alerts" ? alertCount : item.badge === "approvals" ? pendingCount : 0;
    const status = item.floorId ? statuses[item.floorId] : undefined;
    let ariaLabel: string | undefined;
    if (item.floorId && item.floorCode) {
      ariaLabel = t("sidebar.floorAria", { code: item.floorCode, name: label, status: statusWord(item.floorId) });
    } else if (badgeCount > 0) {
      ariaLabel = t("sidebar.countAria", { name: label, n: badgeCount });
    }
    return (
      <SidebarItem
        key={item.id}
        item={item}
        label={label}
        active={active}
        collapsed={collapsed}
        ariaLabel={ariaLabel}
        badgeCount={badgeCount}
        status={status}
        statusLabel={item.floorId ? statusWord(item.floorId) : undefined}
        demoLabel={t("sidebar.demoTag")}
        testId={navTestId(item)}
        onNavigate={onClose}
        onHint={onHint}
      />
    );
  };

  return (
    <aside
      className={cn(
        appFontVariables,
        "flex h-full shrink-0 flex-col overflow-hidden border-r border-sb-border bg-sb-bg font-plex text-sb-text",
        mode === "drawer" ? "w-full" : collapsed ? "w-[76px]" : "w-[264px]",
        mode === "rail" && "transition-[width] duration-200 ease-out motion-reduce:transition-none",
      )}
      aria-label={mode === "drawer" ? undefined : "Sidebar"}
    >
      {/* Header: logo + wordmark, collapse (rail) or close (drawer). */}
      <div className={cn("flex h-14 shrink-0 items-center", collapsed ? "justify-center px-0" : "gap-2 pl-4 pr-2.5")}>
        {collapsed ? (
          <button
            type="button"
            onClick={toggleCollapsed}
            aria-label={t("sidebar.expand")}
            aria-expanded={false}
            onMouseEnter={(e) => onHint(t("sidebar.expand"), e.currentTarget)}
            onMouseLeave={() => onHint(null)}
            onFocus={(e) => onHint(t("sidebar.expand"), e.currentTarget)}
            onBlur={() => onHint(null)}
            className={iconButton}
          >
            <LogoMark />
          </button>
        ) : (
          <>
            <Link
              href="/app"
              onClick={onClose}
              className="flex min-h-11 min-w-0 flex-1 items-center gap-2.5 rounded-lg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sb-accent"
            >
              <LogoMark />
              <span className="truncate font-display text-[22px] font-bold leading-none tracking-[0.01em] text-sb-text">
                FactoryBrain
              </span>
            </Link>
            {mode === "drawer" ? (
              <button type="button" onClick={onClose} aria-label={t("sidebar.close")} className={iconButton} data-drawer-close>
                <X size={18} aria-hidden="true" />
              </button>
            ) : (
              <button
                type="button"
                onClick={toggleCollapsed}
                aria-label={t("sidebar.collapse")}
                aria-expanded={true}
                title={t("sidebar.collapse")}
                className={iconButton}
              >
                <PanelLeftClose size={18} aria-hidden="true" />
              </button>
            )}
          </>
        )}
      </div>

      {/* Factory switcher and search. */}
      <div className="shrink-0 space-y-2 pb-3">
        <FactorySwitcher
          current={factory}
          onChange={setFactory}
          collapsed={collapsed}
          label={t("sidebar.switchFactory")}
          menuLabel={t("sidebar.factories")}
          onHint={onHint}
        />
        <div className={collapsed ? "px-0" : "px-3"} data-tour="search">
          {collapsed ? (
            <button
              type="button"
              onClick={() => openPalette()}
              aria-label={t("sidebar.searchLabel")}
              aria-keyshortcuts="Control+K Meta+K"
              onMouseEnter={(e) => onHint(t("sidebar.search"), e.currentTarget)}
              onMouseLeave={() => onHint(null)}
              onFocus={(e) => onHint(t("sidebar.search"), e.currentTarget)}
              onBlur={() => onHint(null)}
              className={cn(iconButton, "mx-auto flex")}
            >
              <Search size={18} aria-hidden="true" />
            </button>
          ) : (
            <div className="relative">
              <label htmlFor={`sidebar-search-${mode}`} className="sr-only">
                {t("sidebar.searchLabel")}
              </label>
              <Search size={15} aria-hidden="true" className="pointer-events-none absolute left-2.5 top-1/2 -translate-y-1/2 text-sb-muted" />
              <input
                ref={searchRef}
                id={`sidebar-search-${mode}`}
                type="search"
                autoComplete="off"
                placeholder={t("sidebar.search")}
                aria-keyshortcuts="Control+K Meta+K"
                aria-describedby={`sidebar-search-${mode}-hint`}
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                // Enter (or Down) hands the text to the command palette, which
                // searches pages and can send the question to Ask.
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === "ArrowDown") {
                    e.preventDefault();
                    openPalette(query.trim());
                    setQuery("");
                  } else if (e.key === "Escape") {
                    if (query) setQuery("");
                    else e.currentTarget.blur();
                  }
                }}
                className="h-11 w-full rounded-lg border border-sb-border bg-sb-input pl-8 pr-[3.25rem] text-[13px] text-sb-text placeholder:text-sb-label focus:outline-none focus-visible:ring-2 focus-visible:ring-sb-accent [&::-webkit-search-cancel-button]:hidden"
              />
              <kbd
                id={`sidebar-search-${mode}-hint`}
                className="pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 rounded border border-sb-border px-1 py-0.5 font-plexmono text-[10.5px] leading-none text-sb-label"
              >
                {query ? "Enter" : isMac ? "⌘K" : "Ctrl K"}
              </kbd>
            </div>
          )}
        </div>
      </div>

      {/* Navigation groups; this part scrolls on its own. */}
      <div className="flex min-h-0 flex-1 flex-col">
      <nav
        ref={navRef}
        aria-label="Main"
        onScroll={measureOverflow}
        className={cn(
          "sb-scroll min-h-0 flex-1 overflow-y-auto overflow-x-hidden border-t pb-3",
          scrolledDown ? "border-sb-divider" : "border-transparent",
        )}
      >
        {NAV_GROUPS.map((group) => {
          const items = group.items.filter((it) => hasRole(user, it.role));
          if (items.length === 0) return null;
          const headingId = `sb-${mode}-${group.id}`;
          return (
            <div key={group.id} className={cn("pt-2", collapsed ? "px-0" : "px-3")}>
              {collapsed ? (
                <div aria-hidden="true" className="mx-auto mb-2 h-px w-6 bg-sb-divider" />
              ) : (
                <p id={headingId} className="mb-1 px-2.5 font-plexmono text-[11px] font-medium uppercase leading-5 tracking-[0.1em] text-sb-label">
                  {t(group.labelKey)}
                </p>
              )}
              <ul className="space-y-0.5" aria-labelledby={collapsed ? undefined : headingId} aria-label={collapsed ? t(group.labelKey) : undefined}>
                {items.map(renderItem)}
              </ul>
            </div>
          );
        })}
      </nav>
      {/* "N more" row while items are hidden below the fold. It sits under the
          list (never over it). Mouse and touch convenience only: keyboard
          users reach every item with Tab. */}
      {hiddenBelow > 0 && (
        <button
          type="button"
          tabIndex={-1}
          aria-hidden="true"
          onClick={scrollMenu}
          className={cn(
            "flex h-9 shrink-0 items-center justify-center gap-1.5 border-t border-sb-divider bg-sb-bg font-plexmono text-[11px] uppercase tracking-[0.08em] text-sb-label transition-colors hover:text-sb-text",
          )}
        >
          {!collapsed && t("sidebar.moreBelow", { n: hiddenBelow })}
          <ChevronDown size={14} aria-hidden="true" />
        </button>
      )}
      </div>

      {/* Footer, pinned to the bottom. */}
      <div className={cn("shrink-0 space-y-0.5 border-t border-sb-divider py-2", collapsed ? "px-0" : "px-3")}>
        <ul className="space-y-0.5">{FOOTER_ITEMS.filter((it) => hasRole(user, it.role)).map(renderItem)}</ul>
        <AccountMenu collapsed={collapsed} factoryName={factory.name} onHint={onHint} />
      </div>

      {/* One shared tooltip for the collapsed rail (fixed, so the scroll area can't clip it). */}
      {collapsed && hint && (
        <div
          role="presentation"
          aria-hidden="true"
          className="pointer-events-none fixed z-[60] -translate-y-1/2 whitespace-nowrap rounded-md border border-sb-border bg-sb-input px-2.5 py-1.5 text-[13px] text-sb-text shadow-[0_8px_24px_rgba(0,0,0,0.4)]"
          style={hint.style}
        >
          {hint.text}
        </div>
      )}
    </aside>
  );
}
