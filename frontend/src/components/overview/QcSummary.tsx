"use client";

// QC defects — Overview one-liner.
//
// Pulls from /api/qc/defects (same endpoint as /app/qc) so the headline
// number on Overview and the table on /app/qc always agree. The full
// panel (sort, sparklines, Flag-issue) lives at /app/qc only.

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { ArrowUpRight, ClipboardCheck } from "lucide-react";
import { useT } from "@/lib/useT";
import { qcOperationLabel, qcRateColor } from "@/lib/qc";
import { useFactoryBrainLiveStore } from "@/store/factoryBrain.live.store";
import { cn } from "@/lib/cn";
import type { QcResponse } from "@/services/qc.defects.server";

export function QcSummary() {
  const { t } = useT();
  const [data, setData] = useState<QcResponse | null>(null);
  const inflight = useRef<AbortController | null>(null);

  const refresh = useCallback(async () => {
    if (inflight.current) inflight.current.abort();
    const ctrl = new AbortController();
    inflight.current = ctrl;
    try {
      const res = await fetch("/api/qc/defects", {
        method: "GET",
        cache: "no-store",
        signal: ctrl.signal
      });
      if (!res.ok) throw new Error(`qc_${res.status}`);
      setData((await res.json()) as QcResponse);
    } catch {
      // Silently swallow: the summary is intentionally non-blocking; if the
      // API hiccups we render the fallback link to /app/qc.
    } finally {
      if (inflight.current === ctrl) inflight.current = null;
    }
  }, []);

  // Track sim tick so generatedAt stays fresh; intentionally skip the very
  // first run because the mount effect below already fires then.
  const simTick = useFactoryBrainLiveStore((s) => s.snapshot?.tick);
  const firstTickRef = useRef(true);
  useEffect(() => {
    if (simTick === undefined) return;
    if (firstTickRef.current) {
      firstTickRef.current = false;
      return;
    }
    void refresh();
  }, [simTick, refresh]);

  useEffect(() => {
    void refresh();
    return () => {
      inflight.current?.abort();
    };
  }, [refresh]);

  const worst = data?.topByDefectRate?.[0];

  return (
    <div
      className="mt-4 surface-2 p-3 rounded-md border border-border-subtle flex items-center gap-3 flex-wrap"
      data-testid="qc-summary"
      data-tour="qc-summary"
    >
      <span className="size-7 rounded-md bg-surface flex items-center justify-center text-fg-tertiary shrink-0">
        <ClipboardCheck size={14} />
      </span>
      <div className="min-w-0 flex-1 flex items-center gap-2 flex-wrap">
        <p className="text-caption text-fg-secondary">
          {t("qc.summaryLabel")}
          {worst ? (
            <>
              {" "}
              <span className="text-fg-primary">{qcOperationLabel(worst.operation)}</span>
              {" · "}
              <span className="mono-pill text-fg-tertiary">{worst.lineId}</span>
              {" · "}
              <span className={cn("tabular-nums", qcRateColor(worst.defectRatePct))}>
                {t("qc.defectPct", { pct: worst.defectRatePct.toFixed(2) })}
              </span>
              <span className="text-fg-tertiary"> · </span>
              <span className="text-fg-tertiary tabular-nums">
                {t("qc.reworkPct", { pct: worst.reworkRatePct.toFixed(2) })}
              </span>
            </>
          ) : (
            <span className="text-fg-tertiary">{t("qc.summaryLoading")}</span>
          )}
        </p>
      </div>
      <Link
        href="/app/qc"
        className="text-caption text-accent hover:underline inline-flex items-center gap-1 shrink-0"
        data-testid="qc-summary-open"
      >
        {t("qc.summaryOpen")} <ArrowUpRight size={12} />
      </Link>
    </div>
  );
}