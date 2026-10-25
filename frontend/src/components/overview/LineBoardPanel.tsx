"use client";

// Line status board for /app Overview: GET /api/line-board rows, rendered as
// an andon-style tile wall (one tile per line). The page owns the fetch (it
// also feeds the KPI strip); this panel only renders and, for Operators,
// offers POST /api/line-board/refresh to advance the simulated board.
//
// The board's bottleneck flag and the efficiency gap are shown separately:
// the API can leave a line unflagged ("green") while it is a few points
// below target, so the tile states the gap instead of calling it on target.

import { Factory, RefreshCw, StepForward } from "lucide-react";
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

const FLAG_TONE: Record<Bottleneck, "success" | "warning" | "danger"> = { green: "success", amber: "warning", red: "danger" };
const FLAG_FILL: Record<Bottleneck, string> = {
  green: "bg-[var(--risk-low)]",
  amber: "bg-[var(--risk-medium)]",
  red: "bg-[var(--risk-high)]"
};
const FLAG_TEXT: Record<Bottleneck, string> = {
  green: "text-[var(--risk-low)]",
  amber: "text-[var(--risk-medium)]",
  red: "text-[var(--risk-high)]"
};
const FLAG_ORDER: Bottleneck[] = ["red", "amber", "green"];

/** "Line 3 — V-Neck" → ["Line 3", "V-Neck"]. */
function splitName(name: string): [string, string | null] {
  const i = name.indexOf(" — ");
  return i < 0 ? [name, null] : [name.slice(0, i), name.slice(i + 3)];
}

function Gauge({ eff, target, bottleneck }: { eff: number; target: number; bottleneck: Bottleneck }) {
  const pct = Math.max(0, Math.min(100, eff));
  const tgt = Math.max(0, Math.min(100, target));
  return (
    <div className="relative h-2.5 w-full rounded-sm bg-surface-3" role="img" aria-label={`Efficiency ${eff}% against a target of ${target}%`}>
      <div className={cn("absolute inset-y-0 left-0 rounded-sm transition-[width] duration-500", FLAG_FILL[bottleneck])} style={{ width: `${pct}%` }} />
      <div className="absolute -top-1 -bottom-1 w-[2px] rounded-full bg-fg-primary" style={{ left: `calc(${tgt}% - 1px)` }} title={`Target ${target}%`} />
    </div>
  );
}

function LineTile({ row, flagLabel }: { row: LineBoardRow; flagLabel: string }) {
  const [line, product] = splitName(row.name);
  const gap = row.efficiencyPct - row.sahTarget;
  const gapText = gap === 0 ? "On target" : `${gap > 0 ? "+" : "−"}${Math.abs(gap)} pts vs target`;
  return (
    <li
      className={cn(
        "relative overflow-hidden rounded-[var(--radius-control)] border bg-surface-2 pl-4 pr-3.5 py-3.5",
        row.bottleneck === "red" ? "border-[var(--risk-high-border)]" : row.bottleneck === "amber" ? "border-[var(--risk-medium-border)]" : "border-border-subtle"
      )}
      data-testid={`line-board-row-${row.lineId}`}
      data-bottleneck={row.bottleneck}
    >
      <span className={cn("absolute left-0 inset-y-0 w-1", FLAG_FILL[row.bottleneck])} aria-hidden />
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="text-[11px] leading-4 font-semibold uppercase tracking-[0.06em] text-fg-tertiary">{line}</p>
          <p className="text-body font-semibold text-fg-primary truncate">{product ?? row.lineId}</p>
        </div>
        {row.bottleneck === "green" ? (
          <span className="text-caption text-fg-tertiary shrink-0 mt-0.5">{flagLabel}</span>
        ) : (
          <Badge tone={FLAG_TONE[row.bottleneck]} dot className="shrink-0">{flagLabel}</Badge>
        )}
      </div>

      <div className="mt-3 flex items-end justify-between gap-2">
        <p className="leading-none">
          <span className="text-[30px] font-semibold tracking-tight tabular-nums text-fg-primary">{row.efficiencyPct}</span>
          <span className="text-body font-semibold text-fg-secondary">%</span>
          <span className="ml-1.5 text-caption text-fg-tertiary">efficiency</span>
        </p>
        <p className="text-right text-caption leading-4">
          <span className="block text-fg-tertiary">Target {row.sahTarget}%</span>
          <span className={cn("block font-semibold tabular-nums", gap >= 0 ? "text-[var(--risk-low)]" : row.bottleneck === "green" ? "text-fg-secondary" : FLAG_TEXT[row.bottleneck])}>{gapText}</span>
        </p>
      </div>

      <div className="mt-2.5"><Gauge eff={row.efficiencyPct} target={row.sahTarget} bottleneck={row.bottleneck} /></div>

      <dl className="mt-3 grid grid-cols-2 gap-2 border-t border-border-subtle pt-2.5 text-caption">
        <div>
          <dt className="text-fg-tertiary">WIP</dt>
          <dd className="text-fg-primary font-semibold tabular-nums">{row.wipBundles} <span className="font-normal text-fg-tertiary">bundles</span></dd>
        </div>
        <div>
          <dt className="text-fg-tertiary">Non-productive time</dt>
          <dd className="text-fg-primary font-semibold tabular-nums">{row.nptMinutes} <span className="font-normal text-fg-tertiary">min</span></dd>
        </div>
      </dl>
    </li>
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
    b === "green" ? t("overview.bottleneckNone") : b === "amber" ? t("overview.bottleneckAmber") : t("overview.bottleneckRed");
  const counts = FLAG_ORDER.map((b) => ({ b, n: rows.filter((r) => r.bottleneck === b).length }));

  return (
    <Panel
      title="Line status board"
      subtitle={meta ? `${rows.length} sewing lines · tick #${meta.tick} · updated ${formatRelative(meta.updatedAt, locale)}` : "Efficiency against target, WIP and non-productive time per line"}
      icon={<Factory size={14} />}
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
      className="h-full"
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
        <SkeletonRows rows={3} rowHeight="h-36" />
      ) : rows.length === 0 ? (
        <p className="text-caption text-fg-tertiary">The API returned no lines.</p>
      ) : (
        <div data-testid="line-board" data-row-count={rows.length} data-simulated={meta?.simulated ? "true" : "false"}>
          <div className="mb-3 flex flex-wrap items-center gap-x-4 gap-y-1 text-caption text-fg-secondary" aria-label="Board flags">
            {counts.map(({ b, n }) => (
              <span key={b} className="inline-flex items-center gap-1.5">
                <span className={cn("size-2 rounded-sm", FLAG_FILL[b])} aria-hidden />
                <span className="tabular-nums font-semibold text-fg-primary">{n}</span> {label(b).toLowerCase()}
              </span>
            ))}
            <span className="inline-flex items-center gap-1.5 text-fg-tertiary">
              <span className="h-3 w-[2px] rounded-full bg-fg-primary" aria-hidden /> target marker
            </span>
          </div>
          <ul className="grid grid-cols-1 sm:grid-cols-2 2xl:grid-cols-3 gap-3" aria-label="Lines">
            {rows.map((r) => <LineTile key={r.lineId} row={r} flagLabel={label(r.bottleneck)} />)}
          </ul>
          {meta?.notes && <p className="mt-3 text-caption text-fg-tertiary">{meta.notes}</p>}
        </div>
      )}
    </Panel>
  );
}
