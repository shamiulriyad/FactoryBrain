"use client";

// Live line board panel for /app Overview.
// Fetches GET /api/line-board, refreshes whenever the shared live-sim
// snapshot advances (i.e. the Simulate-tick button or its 6s polling).
// Mobile-first: stacked cards below md, real <table> at md+.

import { useCallback, useEffect, useRef, useState } from "react";
import { motion, AnimatePresence, useReducedMotion } from "framer-motion";
import { RefreshCw } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { ErrorState, SkeletonRows, SimulatedPill } from "@/components/ui/Status";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { formatRelative } from "@/lib/format";
import { useFactoryBrainLiveStore } from "@/store/factoryBrain.live.store";
import {
  type LineBoardResponse,
  type LineBoardRow,
  type Bottleneck
} from "@/services/lineBoard.server";

// --- Helpers ---------------------------------------------------------------

const BOTTLENECK_CLASSES: Record<Bottleneck, string> = {
  green: "text-[var(--risk-low)] bg-risk-low/15 border-risk-low/30",
  amber: "text-[var(--risk-medium)] bg-risk-medium/15 border-risk-medium/30",
  red: "text-[var(--risk-high)] bg-risk-high/15 border-risk-high/30"
};

const BOTTLENECK_DOT: Record<Bottleneck, string> = {
  green: "bg-[var(--risk-low)]",
  amber: "bg-[var(--risk-medium)]",
  red: "bg-[var(--risk-high)]"
};

function clamp(n: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, n));
}

function EffBar({ eff, target }: { eff: number; target: number }) {
  // Pure-CSS bar. Width = eff/100 (capped). A small tick marks the target.
  const pct = clamp(eff, 0, 100);
  const onTarget = pct >= target;
  return (
    <div className="relative h-2 w-full rounded-full bg-surface-2 overflow-hidden border border-border-subtle">
      <div
        className={cn(
          "absolute inset-y-0 left-0 rounded-full transition-[width] duration-300 ease-out",
          onTarget ? "bg-[var(--risk-low)]" : pct >= target - 5 ? "bg-[var(--risk-medium)]" : "bg-[var(--risk-high)]"
        )}
        style={{ width: `${pct}%` }}
        aria-hidden
      />
      {/* Target tick */}
      <div
        className="absolute inset-y-[-2px] w-px bg-fg-tertiary/60"
        style={{ left: `${clamp(target, 0, 100)}%` }}
        aria-hidden
      />
    </div>
  );
}

// --- Component -------------------------------------------------------------

export function LineBoardPanel() {
  const { t, locale } = useT();
  const reduceMotion = useReducedMotion();

  const [rows, setRows] = useState<LineBoardRow[]>([]);
  const [meta, setMeta] = useState<LineBoardResponse["meta"] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Refresh coupling: re-fetch whenever the shared sim tick advances.
  // useFactoryBrainLiveStore is mutated by the existing useLiveIngest hook
  // (Simulate-tick button + 6s polling) on /app Overview.
  const simTick = useFactoryBrainLiveStore((s) => s.snapshot?.tick);

  const inflight = useRef<AbortController | null>(null);

  const refresh = useCallback(async () => {
    if (inflight.current) inflight.current.abort();
    const ctrl = new AbortController();
    inflight.current = ctrl;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch("/api/line-board", {
        method: "GET",
        cache: "no-store",
        signal: ctrl.signal
      });
      if (!res.ok) throw new Error(`line_board_${res.status}`);
      const data = (await res.json()) as LineBoardResponse;
      if (Array.isArray(data.rows)) {
        setRows(data.rows);
        setMeta(data.meta ?? null);
      } else {
        throw new Error("line_board_bad_shape");
      }
    } catch (e) {
      if ((e as Error).name !== "AbortError") {
        setError((e as Error).message ?? "line_board_failed");
      }
    } finally {
      if (inflight.current === ctrl) inflight.current = null;
      setLoading(false);
    }
  }, []);

  // Initial fetch + cleanup.
  useEffect(() => {
    void refresh();
    return () => {
      if (inflight.current) inflight.current.abort();
    };
  }, [refresh]);

  // Re-fetch when the shared sim tick advances (Simulate tick / polling).
  useEffect(() => {
    if (simTick === undefined) return;
    void refresh();
    // We intentionally do NOT depend on `refresh`; we only want to react to
    // the tick number changing.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [simTick]);

  const subtitle = meta
    ? `${t("overview.lineBoardSubtitle")} · tick #${meta.tick}`
    : t("overview.lineBoardSubtitle");

  const updatedAtLabel = meta
    ? formatRelative(meta.updatedAt, locale as "en" | "bn")
    : null;

  const headerRight = (
    <div className="flex items-center gap-2">
      <SimulatedPill
        tip={meta?.source ?? "Simulated"}
        testId="line-board-simulated-pill"
      />
      {updatedAtLabel && (
        <span className="mono-pill text-fg-tertiary hidden sm:inline">
          {updatedAtLabel}
        </span>
      )}
      <Button
        variant="secondary"
        size="sm"
        onClick={refresh}
        disabled={loading}
        aria-label={t("overview.refreshAria")}
      >
        <RefreshCw size={14} className={cn(loading && "animate-spin")} />
      </Button>
    </div>
  );

  return (
    <Panel
      variant="glass"
      title={t("overview.lineBoard")}
      subtitle={subtitle}
      right={headerRight}
    >
      {error && !loading && rows.length === 0 ? (
        <ErrorState
          title={t("overview.loadFailed")}
          body={t("overview.loadFailedBody")}
          detail={error}
          retryLabel={t("overview.retry")}
          onRetry={refresh}
        />
      ) : rows.length === 0 && loading ? (
        <SkeletonRows rows={6} rowHeight="h-14" />
      ) : (
        <div
          data-testid="line-board"
          data-row-count={rows.length}
          data-simulated={meta?.simulated === true ? "true" : "false"}
        >
          {/* Mobile: stacked cards */}
          <ul className="grid grid-cols-1 gap-2 md:hidden" aria-label={t("overview.lineBoard")}>
            <AnimatePresence initial={false}>
              {rows.map((r) => (
                <motion.li
                  key={r.lineId}
                  layout={!reduceMotion}
                  initial={reduceMotion ? false : { opacity: 0, y: 4 }}
                  animate={{ opacity: 1, y: 0 }}
                  exit={reduceMotion ? undefined : { opacity: 0, y: -4 }}
                  transition={{ duration: 0.18 }}
                  className="surface-2 p-3 rounded-md border border-border-subtle"
                  data-testid={`line-board-row-${r.lineId}`}
                >
                  <div className="flex items-center justify-between gap-2">
                    <div className="min-w-0">
                      <p className="text-caption text-fg-primary truncate">{r.name}</p>
                      <p className="mono-pill text-fg-tertiary mt-0.5">{r.lineId}</p>
                    </div>
                    <BottleneckBadge bottleneck={r.bottleneck} locale={locale as "en" | "bn"} t={t} />
                  </div>
                  <div className="mt-3">
                    <EffBar eff={r.efficiencyPct} target={r.sahTarget} />
                    <div className="mt-1 flex items-center justify-between text-caption text-fg-tertiary">
                      <span>{t("overview.effShort", { eff: r.efficiencyPct, target: r.sahTarget })}</span>
                      <span className="mono-pill">{r.efficiencyPct}%</span>
                    </div>
                  </div>
                  <div className="mt-3 grid grid-cols-2 gap-2 text-caption">
                    <div>
                      <p className="text-fg-tertiary">{t("overview.colWip")}</p>
                      <p className="text-body text-fg-primary tabular-nums">{r.wipBundles}</p>
                    </div>
                    <div>
                      <p className="text-fg-tertiary">{t("overview.colNpt")}</p>
                      <p className="text-body text-fg-primary tabular-nums">{r.nptMinutes}</p>
                    </div>
                  </div>
                </motion.li>
              ))}
            </AnimatePresence>
          </ul>

          {/* Desktop: table */}
          <div className="hidden md:block overflow-x-auto">
            <table className="w-full text-caption">
              <thead>
                <tr className="text-fg-tertiary">
                  <th className="text-left font-medium py-2 pr-3">{t("overview.colLine")}</th>
                  <th className="text-left font-medium py-2 pr-3">{t("overview.colBottleneck")}</th>
                  <th className="text-left font-medium py-2 pr-3 min-w-[180px]">{t("overview.colEff")}</th>
                  <th className="text-right font-medium py-2 pr-3">{t("overview.colSah")}</th>
                  <th className="text-right font-medium py-2 pr-3">{t("overview.colWip")}</th>
                  <th className="text-right font-medium py-2">{t("overview.colNpt")}</th>
                </tr>
              </thead>
              <tbody>
                <AnimatePresence initial={false}>
                  {rows.map((r) => (
                    <motion.tr
                      key={r.lineId}
                      layout={!reduceMotion}
                      initial={reduceMotion ? false : { opacity: 0 }}
                      animate={{ opacity: 1 }}
                      exit={reduceMotion ? undefined : { opacity: 0 }}
                      transition={{ duration: 0.16 }}
                      className="border-t border-border-subtle hover:bg-surface-2/60"
                      data-testid={`line-board-row-${r.lineId}`}
                    >
                      <td className="py-2.5 pr-3 align-middle">
                        <div className="flex items-center gap-2">
                          <span className={cn("size-2 rounded-full shrink-0", BOTTLENECK_DOT[r.bottleneck])} aria-hidden />
                          <div className="min-w-0">
                            <p className="text-body text-fg-primary truncate">{r.name}</p>
                            <p className="mono-pill text-fg-tertiary">{r.lineId}</p>
                          </div>
                        </div>
                      </td>
                      <td className="py-2.5 pr-3 align-middle">
                        <BottleneckBadge bottleneck={r.bottleneck} locale={locale as "en" | "bn"} t={t} />
                      </td>
                      <td className="py-2.5 pr-3 align-middle min-w-[180px]">
                        <div className="flex items-center gap-2">
                          <div className="flex-1 min-w-[120px]">
                            <EffBar eff={r.efficiencyPct} target={r.sahTarget} />
                          </div>
                          <span className="text-body tabular-nums text-fg-primary shrink-0">{r.efficiencyPct}%</span>
                        </div>
                      </td>
                      <td className="py-2.5 pr-3 align-middle text-right tabular-nums text-fg-secondary">{r.sahTarget}%</td>
                      <td className="py-2.5 pr-3 align-middle text-right tabular-nums text-fg-secondary">{r.wipBundles}</td>
                      <td className="py-2.5 align-middle text-right tabular-nums text-fg-secondary">{r.nptMinutes}</td>
                    </motion.tr>
                  ))}
                </AnimatePresence>
              </tbody>
            </table>
          </div>
        </div>
      )}
    </Panel>
  );
}

// --- Subcomponents ---------------------------------------------------------

function BottleneckBadge({
  bottleneck,
  locale,
  t
}: {
  bottleneck: Bottleneck;
  locale: "en" | "bn";
  t: (path: string, params?: Record<string, string | number>) => string;
}) {
  const label =
    bottleneck === "green" ? t("overview.bottleneckGreen") :
    bottleneck === "amber" ? t("overview.bottleneckAmber") :
    t("overview.bottleneckRed");
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 px-2 py-0.5 rounded-sm border mono-pill",
        BOTTLENECK_CLASSES[bottleneck]
      )}
      aria-label={`${label} (${bottleneck})`}
      data-bottleneck={bottleneck}
    >
      <span className={cn("size-1.5 rounded-full", BOTTLENECK_DOT[bottleneck])} aria-hidden />
      {locale === "bn" ? label : label}
    </span>
  );
}
