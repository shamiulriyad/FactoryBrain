"use client";

// Pending decisions for /app Overview: the newest items of the server
// approval queue (GET /api/approvals?status=pending, shared with the sidebar
// badge). Deciding happens on /app/approvals, where the role rules apply.

import Link from "next/link";
import { ArrowUpRight, ChevronRight, ShieldCheck } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Badge, RISK_TONE } from "@/components/ui/Badge";
import { Skeleton } from "@/components/ui/Skeleton";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { formatRelative } from "@/lib/format";
import { approvalInsight, usePendingApprovals } from "@/services/approvals.api";

const RISK_STRIPE = { low: "bg-[var(--risk-low)]", medium: "bg-[var(--risk-medium)]", high: "bg-[var(--risk-high)]" } as const;

export function DecisionsPanel({ limit = 4 }: { limit?: number }) {
  const { t, locale } = useT();
  const pending = usePendingApprovals((s) => s.pending);
  const pendingCount = usePendingApprovals((s) => s.pendingCount);
  const loaded = usePendingApprovals((s) => s.loaded);
  const error = usePendingApprovals((s) => s.error);

  return (
    <Panel
      title="Pending decisions"
      subtitle={loaded && !error ? (pendingCount ? `${pendingCount} waiting in the approval queue` : "Approval queue is clear") : "Server approval queue"}
      icon={<ShieldCheck size={14} />}
      right={<Link href="/app/approvals" className="text-caption text-accent hover:underline inline-flex items-center gap-1">All approvals <ArrowUpRight size={12} /></Link>}
      flush
    >
      <div data-tour="overview-actions">
        {!loaded ? (
          <div className="p-4 space-y-2">{[0, 1].map((i) => <Skeleton key={i} className="h-14" />)}</div>
        ) : error && pending.length === 0 ? (
          <p className="p-4 text-caption text-[var(--risk-high)]" role="alert">Could not load approvals: {error}</p>
        ) : pending.length === 0 ? (
          <p className="p-4 text-caption text-fg-tertiary">{t("approvals.empty")}</p>
        ) : (
          <ul className="divide-y divide-border-subtle">
            {pending.slice(0, limit).map((a) => {
              const ins = approvalInsight(a);
              return (
                <li key={a.id}>
                  <Link href={`/app/approvals?focus=${a.id}`} className="relative flex items-center gap-3 pl-5 pr-4 py-3 group hover:bg-surface-2 transition-colors" data-testid="overview-decision">
                    <span className={cn("absolute left-0 top-2 bottom-2 w-[3px] rounded-r", RISK_STRIPE[a.riskTier])} aria-hidden />
                    <div className="min-w-0 flex-1">
                      <p className="text-body font-medium text-fg-primary line-clamp-2 group-hover:text-accent">{locale === "bn" ? a.titleBn || a.title : a.title}</p>
                      <p className="mt-0.5 text-caption text-fg-tertiary truncate">{ins.agentLabel} · raised {formatRelative(a.createdAt, locale)}</p>
                    </div>
                    <Badge tone={RISK_TONE[a.riskTier]}>{t(`risk.${a.riskTier}`)}</Badge>
                    <ChevronRight size={15} className="text-fg-tertiary group-hover:text-accent shrink-0" aria-hidden />
                  </Link>
                </li>
              );
            })}
          </ul>
        )}
        {loaded && pendingCount > limit && (
          <p className="px-4 py-2.5 border-t border-border-subtle text-caption text-fg-tertiary">+{pendingCount - limit} more in the queue</p>
        )}
      </div>
    </Panel>
  );
}
