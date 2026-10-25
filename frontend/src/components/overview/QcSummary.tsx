"use client";

// QC on the Overview: last 7 days from GET /api/qc/summary (the same numbers
// as the Quality summary tab on /app/qc), calculated from reviewed
// inspections. With no inspections it says so instead of showing a rate.

import Link from "next/link";
import { ArrowUpRight, ClipboardCheck } from "lucide-react";
import { useT } from "@/lib/useT";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { formatPct } from "@/lib/qcInspection";
import { Panel } from "@/components/ui/Panel";
import { Skeleton } from "@/components/ui/Skeleton";
import type { QcSummary as QcSummaryData } from "@/services/contracts";

export function QcSummary() {
  const { t } = useT();
  const qc = useResource<QcSummaryData>("/api/qc/summary?days=7", { pollMs: 60_000 });
  const s = qc.data;

  return (
    <Panel
      title={t("qc.cardTitle")}
      subtitle="Reviewed inspections, last 7 days"
      icon={<ClipboardCheck size={14} />}
      right={
        <Link href="/app/qc?tab=summary" className="text-caption text-accent hover:underline inline-flex items-center gap-1" data-testid="qc-summary-open">
          {t("qc.summaryOpen")} <ArrowUpRight size={12} />
        </Link>
      }
      className="h-full"
      testId="qc-summary"
    >
      {qc.loading ? (
        <div className="space-y-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-9" />)}</div>
      ) : qc.error && !s ? (
        <p className="text-caption text-[var(--risk-high)]" role="alert">
          Could not load QC data: {errorMessage(qc.error)}{" "}
          <button type="button" className="underline" onClick={() => void qc.reload()}>Retry</button>
        </p>
      ) : !s || s.totals.inspections === 0 ? (
        <p className="text-caption text-fg-tertiary">No inspections in the last 7 days. <Link href="/app/vision" className="text-accent hover:underline">Inspect a garment</Link></p>
      ) : (
        <>
          <dl className="grid grid-cols-3 gap-3">
            <div>
              <dt className="text-caption text-fg-tertiary">Defective</dt>
              <dd className="text-[22px] font-semibold tabular-nums text-fg-primary">{formatPct(s.totals.defectiveRatePct)}</dd>
            </div>
            <div>
              <dt className="text-caption text-fg-tertiary">DHU</dt>
              <dd className="text-[22px] font-semibold tabular-nums text-fg-primary">{s.totals.dhu == null ? "—" : s.totals.dhu.toFixed(1)}</dd>
            </div>
            <div>
              <dt className="text-caption text-fg-tertiary">Awaiting review</dt>
              <dd className="text-[22px] font-semibold tabular-nums text-fg-primary">{s.totals.awaitingReview}</dd>
            </div>
          </dl>
          <p className="mt-2 text-caption text-fg-tertiary">{s.totals.reviewed} reviewed of {s.totals.inspections} submitted · {s.totals.verifiedDefects} verified defects</p>
          {s.byCategory.length > 0 && (
            <ol className="mt-3 space-y-1.5">
              {s.byCategory.slice(0, 3).map((c) => (
                <li key={c.code ?? "unmapped"} className="flex items-baseline justify-between gap-2 text-body">
                  <span className="truncate text-fg-primary">{c.name}</span>
                  <span className="tabular-nums text-fg-secondary">{c.count}</span>
                </li>
              ))}
            </ol>
          )}
        </>
      )}
    </Panel>
  );
}
