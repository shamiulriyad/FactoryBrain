"use client";

import { useCallback, useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type KeyboardEvent } from "react";

export type Placement = "below" | "right" | "above";

const MENU_WIDTH = 240;
const ITEM_SELECTOR = '[role="menuitem"],[role="menuitemradio"]';

/**
 * A menu anchored to a button. The menu is position: fixed so the sidebar's
 * overflow (it scrolls, and clips while its width animates) never cuts it off.
 * Closes on Esc (focus returns to the button), outside click, resize or scroll.
 */
export function usePopover(placement: Placement) {
  const [open, setOpen] = useState(false);
  const buttonRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);
  const [style, setStyle] = useState<CSSProperties>({});

  const close = useCallback((refocus = false) => {
    setOpen(false);
    if (refocus) buttonRef.current?.focus();
  }, []);

  useLayoutEffect(() => {
    if (!open || !buttonRef.current) return;
    const r = buttonRef.current.getBoundingClientRect();
    const width = Math.max(MENU_WIDTH, placement === "right" ? 0 : r.width);
    const left = Math.min(placement === "right" ? r.right + 8 : r.left, window.innerWidth - width - 8);
    if (placement === "below") setStyle({ top: r.bottom + 6, left, width });
    else if (placement === "right") setStyle({ top: r.top, left, width });
    else setStyle({ bottom: window.innerHeight - r.top + 6, left, width });
  }, [open, placement]);

  useEffect(() => {
    if (!open) return;
    // Move focus into the menu: the checked item, or the first one.
    const items = menuRef.current?.querySelectorAll<HTMLElement>(ITEM_SELECTOR);
    const checked = menuRef.current?.querySelector<HTMLElement>('[aria-checked="true"]');
    (checked ?? items?.[0])?.focus();

    const onPointer = (e: PointerEvent) => {
      const target = e.target as Node;
      if (!menuRef.current?.contains(target) && !buttonRef.current?.contains(target)) close();
    };
    // Capture phase, so Esc closes only this menu and not the mobile drawer around it.
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (e.key !== "Escape") return;
      e.stopPropagation();
      close(true);
    };
    const onViewport = () => close();
    document.addEventListener("pointerdown", onPointer);
    document.addEventListener("keydown", onKey, true);
    window.addEventListener("resize", onViewport);
    return () => {
      document.removeEventListener("pointerdown", onPointer);
      document.removeEventListener("keydown", onKey, true);
      window.removeEventListener("resize", onViewport);
    };
  }, [open, close]);

  /** Arrow keys, Home and End move between menu items; Tab leaves the menu. */
  const onMenuKeyDown = useCallback(
    (e: KeyboardEvent<HTMLDivElement>) => {
      const items = Array.from(menuRef.current?.querySelectorAll<HTMLElement>(ITEM_SELECTOR) ?? []);
      if (items.length === 0) return;
      const i = items.indexOf(document.activeElement as HTMLElement);
      let next = -1;
      if (e.key === "ArrowDown") next = (i + 1) % items.length;
      else if (e.key === "ArrowUp") next = (i - 1 + items.length) % items.length;
      else if (e.key === "Home") next = 0;
      else if (e.key === "End") next = items.length - 1;
      else if (e.key === "Tab") close();
      if (next >= 0) {
        e.preventDefault();
        items[next].focus();
      }
    },
    [close],
  );

  return { open, setOpen, close, buttonRef, menuRef, style, onMenuKeyDown };
}

export const menuPanel =
  "fixed z-[60] rounded-lg border border-sb-border bg-sb-input p-1 font-plex text-sb-text shadow-[0_12px_32px_rgba(0,0,0,0.45)]";

export const menuItem =
  "flex min-h-11 w-full items-center gap-3 rounded-md px-2.5 text-left text-[14px] text-sb-dim transition-colors " +
  "hover:bg-sb-hover hover:text-sb-text focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sb-accent";
