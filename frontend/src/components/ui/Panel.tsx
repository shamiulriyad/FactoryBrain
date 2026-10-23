"use client";

import { cn } from "@/lib/cn";

type Props = {
  className?: string;
  children?: React.ReactNode;
  variant?: "default" | "glass";
  title?: React.ReactNode;
  subtitle?: React.ReactNode;
  right?: React.ReactNode;
};

export function Panel({ className, children, variant = "default", title, subtitle, right }: Props) {
  const wrapper = variant === "glass" ? "glass" : "surface";
  return (
    <section
      className={cn(wrapper, className)}
    >
      {(title || right) && (
        <header className="flex items-start justify-between gap-3 px-4 py-3 border-b border-border-subtle">
          <div className="min-w-0">
            {title && <h3 className="text-title text-fg-primary">{title}</h3>}
            {subtitle && <p className="mt-1 text-caption text-fg-tertiary">{subtitle}</p>}
          </div>
          {right && <div className="shrink-0">{right}</div>}
        </header>
      )}
      <div className="p-4">{children}</div>
    </section>
  );
}