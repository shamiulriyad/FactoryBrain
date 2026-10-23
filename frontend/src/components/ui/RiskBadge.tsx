"use client";

import type { RiskTier } from "@/services/types";

type Props = {
  tier: RiskTier;
  label: string;
  size?: "sm" | "md";
};

const styles: Record<RiskTier, string> = {
  low: "text-[var(--risk-low)] bg-[var(--risk-low-soft)] border-[var(--risk-low-border)]",
  medium: "text-[var(--risk-medium)] bg-[var(--risk-medium-soft)] border-[var(--risk-medium-border)]",
  high: "text-[var(--risk-high)] bg-[var(--risk-high-soft)] border-[var(--risk-high-border)]"
};

export function RiskBadge({ tier, label, size = "md" }: Props) {
  return (
    <span
      className={`mono-pill inline-flex items-center rounded-sm border ${styles[tier]} ${size === "sm" ? "text-[10px] px-1.5 py-0.5" : "px-2 py-1"}`}
    >
      {label}
    </span>
  );
}