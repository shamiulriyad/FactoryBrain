"use client";

// Line board for /app Overview: GET /api/line-board rows, rendered as stacked
// cards on phones and a table from md up. The page owns the fetch (it also
// feeds the KPI tiles); this panel only renders and, for Operators, offers
// POST /api/line-board/refresh to advance the simulated board.

import { RefreshCw, StepForward } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { formatRelative } from "@/lib/format";
import { errorMessage } from "@/lib/api";
import type { Resource } from "@/lib/useResource";
import type { LineBoard, LineBoardRow } from "@/services/contracts";

type Bottleneck = LineBoardRow["bottleneck"];

const BOTTLENECK_TONE: Record<Bottleneck, "success" | "warning" | "danger"> = { green: "success", amber: "warning", red: "danger" };
const BOTTLENECK_BAR: Record<Bottleneck, string> = {
  green: "bg-[var(--risk-low)]",
  amber: "bg-[var(--risk-medium)]",
  red: "bg-[var(--risk-high)]"
};

function EffBar({ eff, target, bottleneck }: { eff: number; target: number; bottleneck: Bottleneck }) {
  const pct = Math.max(0, Math.min(100, eff));
  return (
    <div className="relative h-2 w-full rounded-full bg-surface-3 overflow-hidden" role="presentation">
      <div className={cn("absolute inset-y-0 left-0 rounded-full transition-[width] duration-300", BOTTLENECK_BAR[bottleneck])} style={{ width: `${pct}%` }} />
      <div className="absolute -inset-y-0.5 w-0.5 bg-fg-primary/50" style={{ left: `${Math.max(0, Math.min(100, target))}%` }} title={`Target ${target}%`} />
    </div>
  );
}

export function LineBoardPanel({
  board,
  canAdvance,
  advancing,
  onAdvance
}: {
  board: Resource<LineBoard>;
  canAdvance: boolean;
  advancing: boolean;
  onAdvance: () => void;
}) {
  const { t, locale } = useT();
  const rows = board.data?.rows ?? [];
  const meta = board.data?.meta;

  const label = (b: Bottleneck) =>
    b === "green" ? t("overview.bottleneckGreen") : b === "amber" ? t("overview.bottleneckAmber") : t("overview.bottleneckRed");

  return (
    <Panel
      title={t("overview.lineBoard")}
      subtitle={meta ? `${t("overview.lineBoardSubtitle")} · tick #${meta.tick} · updated ${formatRelative(meta.updatedAt, locale)}` : t("overview.lineBoardSubtitle")}
      right={
        <>
          {meta && (
            <span title={meta.source} data-testid="line-board-simulated-pill">
              <Badge tone={meta.simulated ? "accent" : "success"} dot>{meta.simulated ? "Simulated" : "Live"}</Badge>
            </span>
          )}
          {canAdvance && (
            <Button variant="secondary" size="sm" onClick={onAdvance} disabled={advancing} title="Advance the simulated line board by one tick (Operator)" data-testid="line-board-advance">
              <StepForward size={13} /> {advancing ? "Advancing…" : "Advance"}
            </Button>
          )}
          <Button variant="ghost" size="sm" onClick={() => void board.reload()} disabled={board.refreshing} aria-label={t("overview.refreshAria")}>
            <RefreshCw size={13} className={cn(board.refreshing && "animate-spin")} />
          </Button>
        </>
      }
      flush
    >
      {board.error && rows.length === 0 ? (
        <ErrorState
          title={t("overview.loadFailed")}
          body={t("overview.loadFailedBody")}
          detail={errorMessage(board.error)}
          retryLabel={t("overview.retry")}
          onRetry={() => void board.reload()}
        />
      ) : board.loading ? (
        <div className="p-4"><SkeletonRows rows={5} rowHeight="h-11" /></div>
      ) : rows.length === 0 ? (
        <p className="p-5 text-caption text-fg-tertiary">The API returned no lines.</p>
      ) : (
        <div data-testid="line-board" data-row-count={rows.length} data-simulated={meta?.simulated ? "true" : "false"}>
          <ul className="grid grid-cols-1 sm:grid-cols-2 gap-2 p-3 md:hidden" aria-label={t("overview.lineBoard")}>
            {rows.map((r) => (
              <li key={r.lineId} className="surface-2 p-3" data-testid={`line-board-row-${r.lineId}`}>
                <div className="flex items-center justify-between gap-2">
                  <p className="text-body font-medium text-fg-primary truncate">{r.name}</p>
                  <Badge tone={BOTTLENECK_TONE[r.bottleneck]} dot>{label(r.bottleneck)}</Badge>
                </div>
                <div className="mt-3"><EffBar eff={r.efficiencyPct} target={r.sahTarget} bottleneck={r.bottleneck} /></div>
                <div className="mt-2 grid grid-cols-3 gap-2 text-caption">
                  <div><p className="text-fg-tertiary">Efficiency</p><p className="text-fg-primary font-medium tabular-nums">{r.efficiencyPct}% <span className="text-fg-tertiary">/ {r.sahTarget}%</span></p></div>
                  <div><p className="text-fg-tertiary">{t("overview.colWip")}</p><p className="text-fg-primary font-medium tabular-nums">{r.wipBundles}</p></div>
                  <div><p className="text-fg-tertiary">{t("overview.colNpt")}</p><p className="text-fg-primary font-medium tabular-nums">{r.nptMinutes}</p></div>
                </div>
              </li>
            ))}
          </ul>
          <div className="hidden md:block overflow-x-auto">
            <table className="data-table">
              <thead>
                <tr>
                  <th>{t("overview.colLine")}</th>
                  <th>{t("overview.colBottleneck")}</th>
                  <th className="min-w-[200px]">{t("overview.colEff")}</th>
                  <th className="num">{t("overview.colSah")}</th>
                  <th className="num">{t("overview.colWip")}</th>
                  <th className="num">{t("overview.colNpt")}</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r.lineId} data-testid={`line-board-row-${r.lineId}`}>
                    <td>
                      <span className="block text-fg-primary font-medium">{r.name}</span>
                      <span className="block font-mono text-[11px] text-fg-tertiary">{r.lineId}</span>
                    </td>
                    <td><Badge tone={BOTTLENECK_TONE[r.bottleneck]} dot>{label(r.bottleneck)}</Badge></td>
                    <td>
                      <div className="flex items-center gap-3">
                        <div className="flex-1 min-w-[120px]"><EffBar eff={r.efficiencyPct} target={r.sahTarget} bottleneck={r.bottleneck} /></div>
                        <span className="w-10 text-right tabular-nums text-fg-primary font-medium">{r.efficiencyPct}%</span>
                      </div>
                    </td>
                    <td className="num">{r.sahTarget}%</td>
                    <td className="num">{r.wipBundles}</td>
                    <td className="num">{r.nptMinutes}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {meta?.notes && <p className="px-4 py-2.5 border-t border-border-subtle text-caption text-fg-tertiary">{meta.notes}</p>}
        </div>
      )}
    </Panel>
  );
}
