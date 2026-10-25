"use client";

import { useRef, type ReactNode } from "react";
import { cn } from "@/lib/cn";

export type TabItem<K extends string> = { id: K; label: ReactNode; count?: number; testId?: string };

type Props<K extends string> = {
  items: TabItem<K>[];
  value: K;
  onChange: (id: K) => void;
  label: string;
  className?: string;
};

/** Accessible tab strip (arrow keys move between tabs). Panels are rendered by the caller. */
export function Tabs<K extends string>({ items, value, onChange, label, className }: Props<K>) {
  const refs = useRef<Array<HTMLButtonElement | null>>([]);

  function onKeyDown(e: React.KeyboardEvent, idx: number) {
    let next = -1;
    if (e.key === "ArrowRight") next = (idx + 1) % items.length;
    else if (e.key === "ArrowLeft") next = (idx - 1 + items.length) % items.length;
    else if (e.key === "Home") next = 0;
    else if (e.key === "End") next = items.length - 1;
    if (next < 0) return;
    e.preventDefault();
    refs.current[next]?.focus();
    onChange(items[next].id);
  }

  return (
    <div role="tablist" aria-label={label} className={cn("flex items-center gap-1 border-b border-border-subtle overflow-x-auto", className)}>
      {items.map((it, i) => {
        const active = it.id === value;
        return (
          <button
            key={it.id}
            ref={(el) => {
              refs.current[i] = el;
            }}
            type="button"
            role="tab"
            aria-selected={active}
            tabIndex={active ? 0 : -1}
            data-testid={it.testId}
            onClick={() => onChange(it.id)}
            onKeyDown={(e) => onKeyDown(e, i)}
            className={cn(
              "relative h-10 px-3 text-body font-medium whitespace-nowrap transition-colors",
              active ? "text-fg-primary" : "text-fg-tertiary hover:text-fg-primary"
            )}
          >
            <span className="inline-flex items-center gap-2">
              {it.label}
              {typeof it.count === "number" && (
                <span
                  className={cn(
                    "min-w-5 h-5 px-1.5 rounded-full text-[11px] leading-5 text-center",
                    active ? "bg-[var(--accent-soft)] text-accent" : "bg-surface-2 text-fg-tertiary"
                  )}
                >
                  {it.count}
                </span>
              )}
            </span>
            {active && <span className="absolute left-2 right-2 -bottom-px h-0.5 rounded-full bg-accent" aria-hidden />}
          </button>
        );
      })}
    </div>
  );
}
