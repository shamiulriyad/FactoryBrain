"use client";

import type { ReactNode } from "react";
import { cn } from "@/lib/cn";

type Props = {
  title: ReactNode;
  description?: ReactNode;
  /** Small uppercase label above the title (section / context). */
  eyebrow?: ReactNode;
  /** Buttons, filters, status chips — wraps under the title on narrow screens. */
  actions?: ReactNode;
  /** Optional row under the description (badges, last-updated, …). */
  meta?: ReactNode;
  className?: string;
};

/** The one page heading every app page uses: h1, description, actions. */
export function PageHeader({ title, description, eyebrow, actions, meta, className }: Props) {
  return (
    <header className={cn("flex flex-col gap-4 md:flex-row md:items-end md:justify-between", className)}>
      <div className="min-w-0">
        {eyebrow && <p className="eyebrow mb-1.5">{eyebrow}</p>}
        <h1 className="text-[24px] leading-[30px] md:text-[26px] md:leading-[32px] font-semibold tracking-tight text-fg-primary">
          {title}
        </h1>
        {description && <p className="mt-1.5 max-w-2xl text-body text-fg-secondary">{description}</p>}
        {meta && <div className="mt-3 flex flex-wrap items-center gap-2">{meta}</div>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2 md:justify-end shrink-0">{actions}</div>}
    </header>
  );
}
