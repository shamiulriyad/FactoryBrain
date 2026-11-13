"use client";

import { Check, ChevronsUpDown } from "lucide-react";
import { cn } from "@/lib/cn";
import { type FactoryOption } from "@/lib/navigation";
import type { HintHandler } from "./SidebarItem";
import { menuItem, menuPanel, usePopover } from "./usePopover";

function FactoryBadge({ code }: { code: string }) {
  return (
    <span
      aria-hidden="true"
      className="inline-flex size-8 shrink-0 items-center justify-center rounded-md bg-sb-active font-display text-[15px] font-bold leading-none text-sb-accent"
    >
      {code}
    </span>
  );
}

export function FactorySwitcher({
  current,
  onChange,
  collapsed,
  label,
  menuLabel,
  onHint,
  factories,
}: {
  current: FactoryOption;
  /** The signed-in user's factories (from the session). */
  factories: FactoryOption[];
  onChange: (f: FactoryOption) => void;
  collapsed: boolean;
  /** "Switch factory" */
  label: string;
  /** "Your factories" */
  menuLabel: string;
  onHint?: HintHandler;
}) {
  const pop = usePopover(collapsed ? "right" : "below");
  const name = `${current.name}, ${current.detail}. ${label}`;

  return (
    <div className={cn(collapsed ? "px-0" : "px-3")}>
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
          "flex min-h-11 items-center rounded-lg border border-sb-border bg-sb-input text-left transition-colors hover:bg-sb-hover",
          "focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-sb-accent",
          collapsed ? "mx-auto size-11 justify-center border-transparent bg-transparent" : "w-full gap-2.5 px-2 py-1.5",
        )}
      >
        <FactoryBadge code={current.code} />
        {!collapsed && (
          <>
            <span className="min-w-0 flex-1">
              <span className="block truncate text-[14px] font-semibold leading-5 text-sb-text">{current.name}</span>
              <span className="block truncate text-[12px] leading-4 text-sb-muted">{current.detail}</span>
            </span>
            <ChevronsUpDown size={16} aria-hidden="true" className="shrink-0 text-sb-muted" />
          </>
        )}
      </button>

      {pop.open && (
        <div ref={pop.menuRef} role="menu" aria-label={menuLabel} className={menuPanel} style={pop.style} onKeyDown={pop.onMenuKeyDown}>
          <p aria-hidden="true" className="px-2.5 pb-1 pt-2 font-plexmono text-[11px] uppercase tracking-[0.08em] text-sb-label">
            {menuLabel}
          </p>
          {factories.map((f) => {
            const selected = f.id === current.id;
            return (
              <button
                key={f.id}
                type="button"
                role="menuitemradio"
                aria-checked={selected}
                tabIndex={-1}
                onClick={() => {
                  onChange(f);
                  pop.close(true);
                }}
                className={cn(menuItem, "gap-2.5 py-1.5", selected && "text-sb-text")}
              >
                <FactoryBadge code={f.code} />
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-medium leading-5">{f.name}</span>
                  <span className="block truncate text-[12px] leading-4 text-sb-muted">{f.detail}</span>
                </span>
                {selected && <Check size={16} aria-hidden="true" className="shrink-0 text-sb-accent" />}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
