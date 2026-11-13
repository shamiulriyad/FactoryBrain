"use client";

import type { ReactNode } from "react";
import { cn } from "@/lib/cn";
import { roleLabel } from "@/lib/access";

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

const ROLE_TONE: Record<string, BadgeTone> = {
  SuperAdmin: "danger",
  FactoryAdmin: "accent",
  FactoryManager: "info",
  ProductionSupervisor: "info",
  MonitoringOperator: "success",
  HrOfficer: "success",
  DeviceAdmin: "success",
  Worker: "neutral"
};

/** Role chip (system role id → readable name), for the signed-in user and user lists. */
export function RoleBadge({ role, testId }: { role: string; testId?: string }) {
  return (
    <Badge tone={ROLE_TONE[role] ?? "neutral"} testId={testId} title={role}>
      {roleLabel(role)}
    </Badge>
  );
}

/** Every role of a user, highest first; "No role" when empty. */
export function RoleBadges({ roles, className }: { roles: string[]; className?: string }) {
  if (roles.length === 0) return <Badge tone="warning" className={className}>No role</Badge>;
  return (
    <span className={cn("inline-flex flex-wrap gap-1", className)}>
      {roles.map((r) => <RoleBadge key={r} role={r} />)}
    </span>
  );
}
