"use client";

// /app/qc — dedicated QC defects & rework page.
//
// Real GET /api/qc/defects API (no mock-only component state). Renders
// the same panel as the Overview slot, plus a full operations table for
// every (operation × line) cell so QA leads can drill down past the
// top-5. Flag issue still POSTs /api/qc/flag, which persists via
// insightService.upsertCustom() and surfaces in /app/insights.
//
// Demo data only — Simulated chip stays visible.

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Flag, RefreshCw } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { ErrorState, SkeletonRows, SimulatedPill } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { formatRelative } from "@/lib/format";
import { qcOperationLabel, qcRateColor, QC_RATE_HIGH_THRESHOLD, QC_RATE_MEDIUM_THRESHOLD } from "@/lib/qc";
import { useFactoryBrainLiveStore } from "@/store/factoryBrain.live.store";
import type {
  QcResponse,
  QcOperationStats,
  QcTopOperation
} from "@/services/qc.defects.server";

type SortKey = "operation" | "line" | "defect" | "rework";

const rateColor = qcRateColor;

function rateBg(rate: number): string {
  if (rate >= QC_RATE_HIGH_THRESHOLD) return "bg-risk-high/15 border-risk-high/30 text-[var(--risk-high)]";
  if (rate >= QC_RATE_MEDIUM_THRESHOLD) return "bg-risk-medium/15 border-risk-medium/30 text-[var(--risk-medium)]";
  return "bg-risk-low/15 border-risk-low/30 text-[var(--risk-low)]";
}

function defectRatePct(c: QcOperationStats): number {
  return c.totals.inspected > 0 ? (c.totals.defects / c.totals.inspected) * 100 : 0;
}
function reworkRatePct(c: QcOperationStats): number {
  return c.totals.inspected > 0 ? (c.totals.rework / c.totals.inspected) * 100 : 0;
}

const operationLabel = qcOperationLabel;

export default function QcPage() {
  const { t, locale } = useT();
  const toast = useToast();

  const [data, setData] = useState<QcResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [sortKey, setSortKey] = useState<SortKey>("defect");
  const [flagging, setFlagging] = useState<string | null>(null);

  const simTick = useFactoryBrainLiveStore((s) => s.snapshot?.tick);
  const inflight = useRef<AbortController | null>(null);

  const refresh = useCallback(async () => {
    if (inflight.current) inflight.current.abort();
    const ctrl = new AbortController();
    inflight.current = ctrl;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch("/api/qc/defects", {
        method: "GET",
        cache: "no-store",
        signal: ctrl.signal
      });
      if (!res.ok) throw new Error(`qc_${res.status}`);
      setData((await res.json()) as QcResponse);
    } catch (e) {
      if ((e as Error).name !== "AbortError") {
        setError((e as Error).message ?? "qc_failed");
      }
    } finally {
      if (inflight.current === ctrl) inflight.current = null;
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
    return () => {
      if (inflight.current) inflight.current.abort();
    };
  }, [refresh]);

  useEffect(() => {
    if (simTick === undefined) return;
    void refresh();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [simTick]);

  const cells = useMemo(() => {
    if (!data) return [];
    const list = [...data.operations];
    list.sort((a, b) => {
      if (sortKey === "operation") return a.operation.localeCompare(b.operation);
      if (sortKey === "line") return a.lineId.localeCompare(b.lineId);
      if (sortKey === "defect") return defectRatePct(b) - defectRatePct(a);
      return reworkRatePct(b) - reworkRatePct(a);
    });
    return list;
  }, [data, sortKey]);

  const flagIssue = useCallback(
    async (row: QcTopOperation | QcOperationStats) => {
      const key = `${row.operation}|${row.lineId}`;
      setFlagging(key);
      try {
        const res = await fetch("/api/qc/flag", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            operation: row.operation,
            lineId: row.lineId,
            defectRatePct:
              "defectRatePct" in row ? row.defectRatePct : defectRatePct(row as QcOperationStats),
            reworkRatePct:
              "reworkRatePct" in row ? row.reworkRatePct : reworkRatePct(row as QcOperationStats)
          }),
          cache: "no-store"
        });
        if (!res.ok) throw new Error(`flag_${res.status}`);
        toast.push({
          title: t("qc.flagToastTitle") as string,
          description: `${operationLabel(row.operation)} · ${row.lineId} · ${t("qc.flagToastHref") as string}`,
          ttlMs: 5000
        });
      } catch (e) {
        toast.push({
          title: t("qc.flagFailedTitle") as string,
          description: (e as Error).message ?? "flag_failed",
          ttlMs: 4000
        });
      } finally {
        setFlagging(null);
      }
    },
    [t, toast]
  );

  const headerRight = (
    <div className="flex items-center gap-2">
      {data?.meta?.simulated && <SimulatedPill label={t("qc.simulatedChip") as string} testId="qc-page-simulated-pill" />}
      <Button
        variant="secondary"
        size="sm"
        onClick={refresh}
        disabled={loading}
        aria-label={t("qc.refreshAria")}
      >
        <RefreshCw size={14} className={cn(loading && "animate-spin")} />
      </Button>
    </div>
  );

  // --- Render -------------------------------------------------------------

  const tableBody = (() => {
    if (error && !data) {
      return (
        <ErrorState
          title={t("qc.loadFailed") as string}
          body={t("qc.loadFailedBody") as string}
          detail={error}
          retryLabel={t("qc.retry") as string}
          onRetry={refresh}
        />
      );
    }
    if (loading && !data) {
      return <SkeletonRows rows={8} />;
    }
    if (!data) return null;

    return (
      <div data-testid="qc-page-table" data-simulated={data.meta.simulated ? "true" : "false"}>
        {/* Top-5 by defect + rework quick view */}
        <div className="grid grid-cols-1 lg:grid-cols-2 gap-3 mb-4">
          <Panel title={t("qc.topDefect")} subtitle={t("qc.topDefectSubtitle")}>
            <ul role="list" className="space-y-2">
              {data.topByDefectRate.map((row) => {
                const key = `${row.operation}|${row.lineId}`;
                const flagBusy = flagging === key;
                return (
                  <li
                    key={`d-${key}`}
                    className="surface-2 p-3 rounded-md border border-border-subtle flex items-center justify-between gap-2"
                  >
                    <div className="min-w-0">
                      <p className="text-body text-fg-primary truncate">
                        {operationLabel(row.operation)} · {row.lineId}
                      </p>
                      <p className={cn("text-caption tabular-nums", rateColor(row.defectRatePct))}>
                        {t("qc.defectPct", { pct: row.defectRatePct.toFixed(2) })}
                      </p>
                    </div>
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => void flagIssue(row)}
                      disabled={flagBusy}
                      data-testid={`qc-flag-top-defect-${row.operation}-${row.lineId}`}
                    >
                      <Flag size={12} /> {t("qc.flagIssue")}
                    </Button>
                  </li>
                );
              })}
            </ul>
          </Panel>
          <Panel title={t("qc.topRework")} subtitle={t("qc.topReworkSubtitle")}>
            <ul role="list" className="space-y-2">
              {data.topByReworkRate.map((row) => {
                const key = `${row.operation}|${row.lineId}`;
                const flagBusy = flagging === key;
                return (
                  <li
                    key={`r-${key}`}
                    className="surface-2 p-3 rounded-md border border-border-subtle flex items-center justify-between gap-2"
                  >
                    <div className="min-w-0">
                      <p className="text-body text-fg-primary truncate">
                        {operationLabel(row.operation)} · {row.lineId}
                      </p>
                      <p className={cn("text-caption tabular-nums", rateColor(row.reworkRatePct))}>
                        {t("qc.reworkPct", { pct: row.reworkRatePct.toFixed(2) })}
                      </p>
                    </div>
                    <Button
                      variant="secondary"
                      size="sm"
                      onClick={() => void flagIssue(row)}
                      disabled={flagBusy}
                      data-testid={`qc-flag-top-rework-${row.operation}-${row.lineId}`}
                    >
                      <Flag size={12} /> {t("qc.flagIssue")}
                    </Button>
                  </li>
                );
              })}
            </ul>
          </Panel>
        </div>

        {/* Full grid */}
        <Panel
          title={t("qc.fullGrid")}
          subtitle={t("qc.fullGridSubtitle")}
          right={
            <div className="flex items-center gap-1">
              <span className="mono-pill text-fg-tertiary mr-1 hidden sm:inline">{t("qc.sortBy")}</span>
              <div className="inline-flex surface-2 rounded-md border border-border-subtle p-0.5">
                {(["operation", "line", "defect", "rework"] as SortKey[]).map((k) => (
                  <button
                    key={k}
                    type="button"
                    onClick={() => setSortKey(k)}
                    className={cn(
                      "h-7 px-2 rounded-sm text-caption mono-pill",
                      sortKey === k
                        ? "bg-accent text-[var(--accent-fg-on-bg)]"
                        : "text-fg-secondary"
                    )}
                    aria-pressed={sortKey === k}
                  >
                    {k === "operation"
                      ? t("qc.colOp")
                      : k === "line"
                      ? t("qc.colLine")
                      : k === "defect"
                      ? t("qc.colDefect")
                      : t("qc.colRework")}
                  </button>
                ))}
              </div>
            </div>
          }
        >
          <div className="overflow-x-auto">
            <table className="w-full text-caption" data-testid="qc-full-grid">
              <thead>
                <tr className="text-fg-tertiary">
                  <th className="text-left font-medium py-2 pr-3">{t("qc.colOp")}</th>
                  <th className="text-left font-medium py-2 pr-3">{t("qc.colLine")}</th>
                  <th className="text-left font-medium py-2 pr-3">{t("qc.colInspected")}</th>
                  <th className="text-right font-medium py-2 pr-3">{t("qc.colDefects")}</th>
                  <th className="text-right font-medium py-2 pr-3">{t("qc.colMajor")}</th>
                  <th className="text-right font-medium py-2 pr-3">{t("qc.colRework")}</th>
                  <th className="text-right font-medium py-2 pr-3">{t("qc.colDefectRate")}</th>
                  <th className="text-right font-medium py-2">{t("qc.colReworkRate")}</th>
                </tr>
              </thead>
              <tbody>
                {cells.map((c) => {
                  const key = `${c.operation}|${c.lineId}`;
                  const dRate = defectRatePct(c);
                  const rRate = reworkRatePct(c);
                  const flagBusy = flagging === key;
                  return (
                    <tr
                      key={key}
                      className="border-t border-border-subtle hover:bg-surface-2/60"
                      data-testid={`qc-grid-row-${c.operation}-${c.lineId}`}
                    >
                      <td className="py-2 pr-3 align-middle text-body text-fg-primary">
                        {operationLabel(c.operation)}
                      </td>
                      <td className="py-2 pr-3 align-middle mono-pill text-fg-tertiary">
                        {c.lineId}
                      </td>
                      <td className="py-2 pr-3 align-middle tabular-nums text-fg-secondary">
                        {c.totals.inspected.toLocaleString(locale)}
                      </td>
                      <td className="py-2 pr-3 align-middle tabular-nums text-fg-secondary text-right">
                        {c.totals.defects.toLocaleString(locale)}
                      </td>
                      <td className="py-2 pr-3 align-middle tabular-nums text-fg-secondary text-right">
                        {c.totals.major.toLocaleString(locale)}
                      </td>
                      <td className="py-2 pr-3 align-middle tabular-nums text-fg-secondary text-right">
                        {c.totals.rework.toLocaleString(locale)}
                      </td>
                      <td className="py-2 pr-3 align-middle text-right">
                        <span
                          className={cn(
                            "mono-pill inline-flex items-center h-6 px-2 rounded-sm border",
                            rateBg(dRate)
                          )}
                        >
                          {dRate.toFixed(2)}%
                        </span>
                      </td>
                      <td className="py-2 align-middle text-right">
                        <span
                          className={cn(
                            "mono-pill inline-flex items-center h-6 px-2 rounded-sm border",
                            rateBg(rRate)
                          )}
                        >
                          {rRate.toFixed(2)}%
                        </span>
                      </td>
                      <td className="py-2 align-middle text-right">
                        <Button
                          variant="secondary"
                          size="sm"
                          onClick={() =>
                            void flagIssue({
                              operation: c.operation,
                              lineId: c.lineId,
                              defectRatePct: dRate,
                              reworkRatePct: rRate
                            })
                          }
                          disabled={flagBusy}
                          data-testid={`qc-flag-grid-${c.operation}-${c.lineId}`}
                          aria-label={t("qc.flagAria", { op: c.operation, line: c.lineId })}
                        >
                          <Flag size={12} />
                        </Button>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <p className="mt-3 text-caption text-fg-tertiary">
            {data.meta.notes}
          </p>
          {data.meta.generatedAt && (
            <p className="mt-1 text-caption text-fg-tertiary">
              {t("qc.generated", { rel: formatRelative(data.meta.generatedAt, locale as "en" | "bn") })}
            </p>
          )}
        </Panel>
      </div>
    );
  })();

  return (
    <div className="px-6 md:px-8 py-6 max-w-5xl mx-auto" data-tour="qc">
      <div className="flex items-end justify-between gap-3 flex-wrap">
        <div>
          <h1 className="text-display font-semibold tracking-tight">{t("qc.cardTitle")}</h1>
          <p className="mt-1 text-caption text-fg-tertiary">{t("qc.pageSubtitle")}</p>
        </div>
        {headerRight}
      </div>

      <div className="mt-6">{tableBody}</div>
    </div>
  );
}
