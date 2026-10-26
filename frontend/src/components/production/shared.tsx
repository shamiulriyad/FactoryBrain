"use client";

// Building blocks shared by the production pages: the plant configuration
// and master data, status badges, metric definitions, WIP and data-quality
// notes. Every value shown comes from /api/production/**.

import Link from "next/link";
import type { ReactNode } from "react";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { useOnDataChanged, useResource } from "@/lib/useResource";
import { offsetMinutes, PLAN_STATUS_LABEL, PLAN_STATUS_TONE, PROPOSAL_STATUS_LABEL, PROPOSAL_STATUS_TONE, RISK_LABEL, RISK_TONE, WIP_STATE_LABEL, fmt } from "@/lib/production";
import { productionPaths } from "@/services/production.api";
import type { PlanStatus, ProductionConfig, ProductionLine, ProductionShift, ProductionStyle, ProposalStatus, RiskLevel, WipBuffer } from "@/services/contracts";

export function useProductionConfig() {
  const res = useResource<ProductionConfig>(productionPaths.config);
  return { ...res, offset: offsetMinutes(res.data?.utcOffsetNow) };
}

export function useMasterData(includeInactive = false) {
  const lines = useResource<ProductionLine[]>(productionPaths.lines(includeInactive));
  const shifts = useResource<ProductionShift[]>(productionPaths.shifts(includeInactive));
  const styles = useResource<ProductionStyle[]>(productionPaths.styles(includeInactive));
  useOnDataChanged(() => { void lines.reload(); void shifts.reload(); void styles.reload(); });
  return { lines, shifts, styles, reload: () => Promise.all([lines.reload(), shifts.reload(), styles.reload()]) };
}

export function RiskLevelBadge({ level, testId }: { level: RiskLevel; testId?: string }) {
  return <Badge tone={RISK_TONE[level]} testId={testId} title="A rule-based level from the observed rate and remaining scheduled time, not a probability.">{RISK_LABEL[level]}</Badge>;
}

export function PlanStatusBadge({ status }: { status: PlanStatus }) {
  return <Badge tone={PLAN_STATUS_TONE[status]} testId="plan-status">{PLAN_STATUS_LABEL[status]}</Badge>;
}

export function ProposalStatusBadge({ status }: { status: ProposalStatus }) {
  return <Badge tone={PROPOSAL_STATUS_TONE[status]} testId="proposal-status">{PROPOSAL_STATUS_LABEL[status]}</Badge>;
}

/** "How these numbers are calculated", from the API's own definitions. */
export function Definitions({ definitions, className }: { definitions: Record<string, string>; className?: string }) {
  return (
    <details className={className ?? "surface p-4 text-caption text-fg-secondary"} data-testid="production-definitions">
      <summary className="cursor-pointer text-fg-primary font-medium">How these numbers are calculated</summary>
      <dl className="mt-2 space-y-1.5">
        {Object.entries(definitions).map(([k, v]) => (
          <div key={k}>
            <dt className="inline font-mono text-fg-primary">{k}</dt>: <dd className="inline">{v}</dd>
          </div>
        ))}
      </dl>
    </details>
  );
}

export function WipList({ buffers, empty = "No stage counts recorded." }: { buffers: WipBuffer[]; empty?: string }) {
  if (buffers.length === 0) return <p className="text-caption text-fg-tertiary">{empty}</p>;
  return (
    <ul className="space-y-1.5 text-body" data-testid="wip-list">
      {buffers.map((b) => (
        <li key={b.key} className="flex flex-wrap items-baseline justify-between gap-2">
          <span className="text-fg-secondary">{b.label}</span>
          <span className="tabular-nums text-fg-primary">
            {b.pieces == null ? "—" : `${fmt(b.pieces)} pcs`}
            {b.state !== "ok" && <span className="ml-2 text-caption text-[var(--risk-medium)]">{WIP_STATE_LABEL[b.state]}</span>}
          </span>
        </li>
      ))}
    </ul>
  );
}

export function NoteList({ title, items, tone = "info", testId }: { title: string; items: string[]; tone?: "info" | "warning"; testId?: string }) {
  if (items.length === 0) return null;
  return (
    <Callout tone={tone} title={title} testId={testId}>
      <ul className="list-disc pl-4 space-y-0.5">
        {items.map((n, i) => <li key={i}>{n}</li>)}
      </ul>
    </Callout>
  );
}

/** Shown when the plant hasn't configured lines, shifts or styles yet. */
export function SetupGaps({ config, admin }: { config: ProductionConfig; admin: boolean }) {
  const gaps: string[] = [];
  if (config.activeLines === 0) gaps.push("no production lines");
  if (config.activeShifts === 0) gaps.push("no shifts in the working calendar");
  if (config.activeStyles === 0) gaps.push("no styles with standard minutes");
  if (gaps.length === 0) return null;
  return (
    <Callout tone="info" title="Production isn't set up yet" testId="production-setup-gaps"
      action={<Link href="/app/production?tab=setup" className="text-caption text-accent hover:underline">Open setup</Link>}>
      The plant has {gaps.join(", ")}. {admin ? "Add them under Setup;" : "An Admin adds them under Setup;"} nothing is filled in or estimated meanwhile.
    </Callout>
  );
}

export function KeyValue({ label, children }: { label: ReactNode; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-caption text-fg-tertiary">{label}</dt>
      <dd className="text-body text-fg-primary tabular-nums break-words">{children}</dd>
    </div>
  );
}

export function ApprovalLine({ approval, label = "Approval" }: { approval?: { id: string; status: string; riskTier: string; decidedByEmail?: string | null; decisionReason?: string | null } | null; label?: string }) {
  if (!approval) return null;
  const tone = approval.status === "approved" ? "success" : approval.status === "rejected" ? "danger" : "warning";
  return (
    <p className="text-caption text-fg-secondary" data-testid="approval-line">
      {label}: <Badge tone={tone}>{approval.status === "pending_approval" ? "Waiting for a decision" : approval.status}</Badge>{" "}
      <span className="text-fg-tertiary">({approval.riskTier} risk{approval.riskTier === "high" ? ": an Admin decides" : ""})</span>
      {approval.decidedByEmail && <> · decided by {approval.decidedByEmail}</>}
      {approval.decisionReason && <> · “{approval.decisionReason}”</>}
      {" · "}
      <Link href="/app/approvals" className="text-accent hover:underline">Approval queue</Link>
    </p>
  );
}
