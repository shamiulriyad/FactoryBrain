"use client";

import { cn } from "@/lib/cn";

type Props = {
  className?: string;
  children?: React.ReactNode;
  variant?: "default" | "glass";
  title?: React.ReactNode;
  subtitle?: React.ReactNode;
  right?: React.ReactNode;
  /** Optional icon shown before the title. */
  icon?: React.ReactNode;
  /** Remove body padding (for edge-to-edge tables). */
  flush?: boolean;
  /** Accessible name when the title is not plain text. */
  ariaLabel?: string;
  testId?: string;
};

export function Panel({ className, children, variant = "default", title, subtitle, right, icon, flush, ariaLabel, testId }: Props) {
  const wrapper = variant === "glass" ? "glass" : "surface";
  return (
    <section className={cn(wrapper, "min-w-0", className)} aria-label={ariaLabel} data-testid={testId}>
      {(title || right) && (
        <header className="flex flex-wrap items-start justify-between gap-x-3 gap-y-2 px-4 md:px-5 py-3.5 border-b border-border-subtle">
          <div className="min-w-0 flex items-start gap-2.5">
            {icon && (
              <span className="mt-0.5 size-7 shrink-0 rounded-md bg-surface-2 border border-border-subtle inline-flex items-center justify-center text-fg-secondary" aria-hidden>
                {icon}
              </span>
            )}
            <div className="min-w-0">
              {title && <h2 className="text-[15px] leading-[22px] font-semibold text-fg-primary">{title}</h2>}
              {subtitle && <p className="mt-0.5 text-caption text-fg-tertiary">{subtitle}</p>}
            </div>
          </div>
          {right && <div className="shrink-0 flex flex-wrap items-center gap-2">{right}</div>}
        </header>
      )}
      <div className={flush ? undefined : "p-4 md:p-5"}>{children}</div>
    </section>
  );
}
