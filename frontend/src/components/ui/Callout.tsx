"use client";

import type { ReactNode } from "react";
import { AlertTriangle, CheckCircle2, FlaskConical, Info, ShieldAlert } from "lucide-react";
import { cn } from "@/lib/cn";

export type CalloutTone = "info" | "success" | "warning" | "danger" | "demo";

const TONES: Record<CalloutTone, { cls: string; icon: ReactNode }> = {
  info: {
    cls: "bg-[var(--info-soft)] border-[var(--info-border)] [--callout-fg:var(--info)]",
    icon: <Info size={16} />
  },
  success: {
    cls: "bg-[var(--risk-low-soft)] border-[var(--risk-low-border)] [--callout-fg:var(--risk-low)]",
    icon: <CheckCircle2 size={16} />
  },
  warning: {
    cls: "bg-[var(--risk-medium-soft)] border-[var(--risk-medium-border)] [--callout-fg:var(--risk-medium)]",
    icon: <AlertTriangle size={16} />
  },
  danger: {
    cls: "bg-[var(--risk-high-soft)] border-[var(--risk-high-border)] [--callout-fg:var(--risk-high)]",
    icon: <ShieldAlert size={16} />
  },
  demo: {
    cls: "bg-[var(--accent-soft)] border-[var(--accent-border)] [--callout-fg:var(--accent)]",
    icon: <FlaskConical size={16} />
  }
};

type Props = {
  tone?: CalloutTone;
  title?: ReactNode;
  children?: ReactNode;
  /** Right-aligned action (a button or link). */
  action?: ReactNode;
  className?: string;
  /** "alert" for errors the user must notice; "status" (polite) otherwise. */
  role?: "alert" | "status";
  testId?: string;
};

/** Inline message block: errors, warnings, permission notes, demo labels. */
export function Callout({ tone = "info", title, children, action, className, role, testId }: Props) {
  const t = TONES[tone];
  return (
    <div
      role={role ?? (tone === "danger" ? "alert" : undefined)}
      data-testid={testId}
      data-tone={tone}
      className={cn("flex items-start gap-3 rounded-[var(--radius-control)] border px-3.5 py-3", t.cls, className)}
    >
      <span className="mt-0.5 shrink-0 text-[var(--callout-fg)]" aria-hidden>
        {t.icon}
      </span>
      <div className="min-w-0 flex-1">
        {title && <p className="text-body font-semibold text-fg-primary">{title}</p>}
        {children && <div className={cn("text-caption text-fg-secondary", title && "mt-0.5")}>{children}</div>}
      </div>
      {action && <div className="shrink-0 self-center">{action}</div>}
    </div>
  );
}

/**
 * Standard label for screens whose data is a browser-only demo dataset with
 * no backing API. Keeps demo content visibly separate from live data.
 */
export function DemoDataBanner({ what, className }: { what: ReactNode; className?: string }) {
  return (
    <Callout tone="demo" title="Demo data — not connected to the API" className={className} testId="demo-data-banner">
      {what}
    </Callout>
  );
}
