"use client";

// QC defects — Overview summary from GET /api/qc/defects (the same endpoint
// as /app/qc), so the headline numbers on both pages always agree.

import Link from "next/link";
import { ArrowUpRight, ClipboardCheck } from "lucide-react";
import { useT } from "@/lib/useT";
import { qcOperationLabel, qcRateColor } from "@/lib/qc";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Skeleton } from "@/components/ui/Skeleton";
import type { QcResponse } from "@/services/qc.defects.server";

export function QcSummary() {
  const { t } = useT();
  const qc = useResource<QcResponse>("/api/qc/defects", { pollMs: 60_000 });
  const top = qc.data?.topByDefectRate.slice(0, 3) ?? [];

  return (
    <Panel
      title={t("qc.cardTitle")}
      subtitle="Highest defect rates by operation and line"
      icon={<ClipboardCheck size={14} />}
      right={
        <>
          {qc.data?.meta.simulated && <span title={qc.data.meta.source}><Badge tone="accent" dot>Simulated</Badge></span>}
          <Link href="/app/qc" className="text-caption text-accent hover:underline inline-flex items-center gap-1" data-testid="qc-summary-open">
            {t("qc.summaryOpen")} <ArrowUpRight size={12} />
          </Link>
        </>
      }
      testId="qc-summary"
    >
      {qc.loading ? (
        <div className="space-y-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-9" />)}</div>
      ) : qc.error && !qc.data ? (
        <p className="text-caption text-[var(--risk-high)]" role="alert">
          Could not load QC data: {errorMessage(qc.error)}{" "}
          <button type="button" className="underline" onClick={() => void qc.reload()}>Retry</button>
        </p>
      ) : top.length === 0 ? (
        <p className="text-caption text-fg-tertiary">No inspections recorded.</p>
      ) : (
        <ol className="space-y-2">
          {top.map((row, i) => (
            <li key={`${row.operation}-${row.lineId}`} className="flex items-center gap-3">
              <span className="size-6 shrink-0 rounded-full bg-surface-2 border border-border-subtle text-[11px] font-semibold inline-flex items-center justify-center text-fg-secondary">{i + 1}</span>
              <div className="min-w-0 flex-1">
                <p className="text-body text-fg-primary truncate">{qcOperationLabel(row.operation)} <span className="text-fg-tertiary font-mono text-[12px]">· {row.lineId}</span></p>
              </div>
              <span className={cn("text-caption font-semibold tabular-nums", qcRateColor(row.defectRatePct))}>{row.defectRatePct.toFixed(2)}%</span>
              <span className="w-20 text-right text-caption text-fg-tertiary tabular-nums">rework {row.reworkRatePct.toFixed(1)}%</span>
            </li>
          ))}
        </ol>
      )}
    </Panel>
  );
}
