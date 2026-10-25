"use client";

// QC defects — Overview summary from GET /api/qc/defects (the same endpoint
// as /app/qc), so the headline numbers on both pages always agree. Bars use
// a fixed 0–QC_RATE_HIGH_THRESHOLD scale so their length means the same thing
// on every load.

import Link from "next/link";
import { ArrowUpRight, ClipboardCheck } from "lucide-react";
import { useT } from "@/lib/useT";
import { QC_RATE_HIGH_THRESHOLD, QC_RATE_MEDIUM_THRESHOLD, qcOperationLabel, qcRateColor } from "@/lib/qc";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Skeleton } from "@/components/ui/Skeleton";
import type { QcResponse } from "@/services/qc.defects.server";

function rateFill(rate: number) {
  if (rate >= QC_RATE_HIGH_THRESHOLD) return "bg-[var(--risk-high)]";
  if (rate >= QC_RATE_MEDIUM_THRESHOLD) return "bg-[var(--risk-medium)]";
  return "bg-[var(--risk-low)]";
}

export function QcSummary() {
  const { t } = useT();
  const qc = useResource<QcResponse>("/api/qc/defects", { pollMs: 60_000 });
  const top = qc.data?.topByDefectRate.slice(0, 5) ?? [];

  return (
    <Panel
      title={t("qc.cardTitle")}
      subtitle="Highest defect rates, operation × line, last 8 weeks"
      icon={<ClipboardCheck size={14} />}
      right={
        <>
          {qc.data?.meta.simulated && <span title={qc.data.meta.source}><Badge tone="accent" dot>Demo seed</Badge></span>}
          <Link href="/app/qc" className="text-caption text-accent hover:underline inline-flex items-center gap-1" data-testid="qc-summary-open">
            {t("qc.summaryOpen")} <ArrowUpRight size={12} />
          </Link>
        </>
      }
      className="h-full"
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
        <>
          <ol className="space-y-3">
            {top.map((row) => (
              <li key={`${row.operation}-${row.lineId}`}>
                <div className="flex items-baseline justify-between gap-2">
                  <p className="min-w-0 text-body text-fg-primary truncate">
                    {qcOperationLabel(row.operation)} <span className="text-fg-tertiary font-mono text-[12px]">{row.lineId}</span>
                  </p>
                  <span className={cn("text-body font-semibold tabular-nums", qcRateColor(row.defectRatePct))}>{row.defectRatePct.toFixed(2)}%</span>
                </div>
                <div className="mt-1.5 flex items-center gap-2">
                  <div className="h-1.5 flex-1 rounded-full bg-surface-3 overflow-hidden" role="presentation">
                    <div className={cn("h-full rounded-full", rateFill(row.defectRatePct))} style={{ width: `${Math.min(100, (row.defectRatePct / QC_RATE_HIGH_THRESHOLD) * 100)}%` }} />
                  </div>
                  <span className="w-20 text-right text-caption text-fg-tertiary tabular-nums">rework {row.reworkRatePct.toFixed(1)}%</span>
                </div>
              </li>
            ))}
          </ol>
          <p className="mt-3 text-caption text-fg-tertiary">Bar scale 0–{QC_RATE_HIGH_THRESHOLD}% defect rate (high-risk threshold).</p>
        </>
      )}
    </Panel>
  );
}
