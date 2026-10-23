"use client";

import { cn } from "@/lib/cn";
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