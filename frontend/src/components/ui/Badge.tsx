"use client";

import type { ReactNode } from "react";
import { cn } from "@/lib/cn";

export type BadgeTone = "neutral" | "accent" | "info" | "success" | "warning" | "danger";

const TONES: Record<BadgeTone, string> = {
  neutral: "",
  accent: "!bg-[var(--accent-soft)] !border-[var(--accent-border)] !text-[var(--accent)]",
  info: "!bg-[var(--info-soft)] !border-[var(--info-border)] !text-[var(--info)]",
  success: "!bg-[var(--risk-low-soft)] !border-[var(--risk-low-border)] !text-[var(--risk-low)]",
  warning: "!bg-[var(--risk-medium-soft)] !border-[var(--risk-medium-border)] !text-[var(--risk-medium)]",
  danger: "!bg-[var(--risk-high-soft)] !border-[var(--risk-high-border)] !text-[var(--risk-high)]"
};

type Props = {
  tone?: BadgeTone;
  dot?: boolean;
  children: ReactNode;
  className?: string;
  title?: string;
  testId?: string;
};

export function Badge({ tone = "neutral", dot, children, className, title, testId }: Props) {
  return (
    <span className={cn("badge", TONES[tone], className)} title={title} data-testid={testId}>
      {dot && <span className="badge-dot" aria-hidden />}
      {children}
    </span>
  );
}

export const RISK_TONE: Record<"low" | "medium" | "high", BadgeTone> = {
  low: "success",
  medium: "warning",
  high: "danger"
};

/** Role chip for the signed-in user and user lists. */
export function RoleBadge({ role, testId }: { role: string; testId?: string }) {
  const tone: BadgeTone = role === "Admin" ? "accent" : role === "Operator" ? "info" : "neutral";
  return (
    <Badge tone={tone} testId={testId}>
      {role}
    </Badge>
  );
}
