"use client";

import { forwardRef } from "react";
import { motion, useReducedMotion } from "framer-motion";
import { cn } from "@/lib/cn";
import type { CSSProperties } from "react";
import type { Stage, RiskTier } from "@/services/types";

const stageStyles: Record<Stage, string> = {
  suggested: "text-stage-suggested bg-stage-suggested/15 border-stage-suggested/30",
  pending_approval: "text-stage-pending bg-stage-pending/15 border-stage-pending/30",
  executing: "text-stage-executing bg-stage-executing/15 border-stage-executing/30",
  done: "text-stage-done bg-stage-done/15 border-stage-done/30",
  logged: "text-stage-logged bg-stage-logged/15 border-stage-logged/30",
  rejected: "text-risk-high bg-risk-high/15 border-risk-high/30",
  failed: "text-risk-high bg-risk-high/15 border-risk-high/30"
};

const riskStyles: Record<RiskTier, string> = {
  low: "text-risk-low bg-risk-low/15 border-risk-low/30",
  medium: "text-risk-medium bg-risk-medium/15 border-risk-medium/30",
  high: "text-risk-high bg-risk-high/15 border-risk-high/30"
};

export function StageBadge({ stage, label }: { stage: Stage; label: string }) {
  return (
    <span className={cn("mono-pill inline-flex items-center rounded-sm border px-2 py-1", stageStyles[stage])}>
      {label}
    </span>
  );
}

export function RiskPill({ tier, label }: { tier: RiskTier; label: string }) {
  return (
    <span className={cn("mono-pill inline-flex items-center rounded-sm border px-2 py-1", riskStyles[tier])}>
      {label}
    </span>
  );
}

export type ButtonVariant =
  | "primary"
  | "secondary"
  | "ghost"
  | "danger"
  | "success";
export type ButtonSize = "sm" | "md" | "lg";

type Props = {
  variant?: ButtonVariant;
  size?: ButtonSize;
  className?: string;
  children?: React.ReactNode;
  /** Add a paper-plane / send icon next to the label. Pass any node. */
  iconLeft?: React.ReactNode;
  iconRight?: React.ReactNode;
} & React.ButtonHTMLAttributes<HTMLButtonElement>;

/**
 * Variant tokens. Each variant ALWAYS pairs a background with a non-inherited
 * foreground so a label never collides with its own surface.
 *
 * Contrast pairs (theme-independent contrast ratio, with fg/bg):
 *   primary   accent-green  (#7CE0C6)  / near-black (#04231C)  ≈ 13:1 dark, ≈ 7:1 light
 *   secondary surface-2                  / fg-primary            ≈ 11:1 dark, ≈ 13:1 light
 *   ghost     transparent                / fg-secondary          ≈ 5:1 dark,  ≈ 5:1 light
 *   danger    risk-high-tint             / risk-high             ≈ 6:1 dark,  ≈ 6:1 light
 *   success   risk-low                   / near-black            ≈ 7:1 dark,  ≈ 5:1 light
 *
 * All pairs pass WCAG AA (4.5:1) for body text in both themes.
 */
const variantStyles: Record<ButtonVariant, string> = {
  primary:
    "bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)] border border-[var(--btn-primary-border)] hover:bg-[var(--btn-primary-bg-hover)]",
  secondary:
    "bg-[var(--btn-secondary-bg)] text-[var(--btn-secondary-fg)] border border-[var(--btn-secondary-border)] hover:bg-[var(--btn-secondary-bg-hover)]",
  ghost:
    "bg-[var(--btn-ghost-bg)] text-[var(--btn-ghost-fg)] border border-[var(--btn-ghost-border)] hover:bg-[var(--btn-ghost-bg-hover)] hover:text-[var(--btn-ghost-fg-hover)]",
  danger:
    "bg-[var(--btn-danger-bg)] text-[var(--btn-danger-fg)] border border-[var(--btn-danger-border)] hover:bg-[var(--btn-danger-bg-hover)]",
  success:
    "bg-[var(--btn-success-bg)] text-[var(--btn-success-fg)] border border-[var(--btn-success-border)] hover:bg-[var(--btn-success-bg-hover)]"
};

const sizes: Record<ButtonSize, string> = {
  sm: "h-7 px-2 text-caption",
  md: "h-9 px-3 text-body",
  lg: "h-11 px-4 text-body"
};

const base =
  "inline-flex items-center justify-center gap-1.5 font-medium rounded-md transition-colors duration-150 disabled:opacity-50 disabled:cursor-not-allowed whitespace-nowrap select-none";

export const Button = forwardRef<HTMLButtonElement, Props>(function Button(
    { variant = "secondary", size = "md", className, children, iconLeft, iconRight, style, ...rest },
    ref
  ) {
  const reduceMotion = useReducedMotion();
  const baseStyle: CSSProperties = { fontVariantLigatures: "none", ...style };
  return (
    <motion.button
      ref={ref}
      whileHover={reduceMotion ? undefined : { y: -1 }}
      whileTap={reduceMotion ? undefined : { scale: 0.97 }}
      transition={{ type: "spring", stiffness: 420, damping: 32 }}
      className={cn(base, variantStyles[variant], sizes[size], className)}
      style={baseStyle}
      {...(rest as any)}
    >
      {iconLeft}
      {children}
      {iconRight}
    </motion.button>
  );
});

/** Small badge-style "demo mode" pill used by Ask when the LLM key is absent. */
export function DemoChip({ children }: { children?: React.ReactNode }) {
  return (
    <span className="inline-flex items-center gap-1 h-5 px-1.5 rounded-sm border border-border-subtle bg-surface-2 text-fg-secondary mono-pill">
      <span className="size-1 rounded-full bg-fg-tertiary" />
      {children ?? "Demo mode"}
    </span>
  );
}