"use client";

// Shared status primitives used by the live panels (Overview, Line Board,
// QC, Floor Alerts, Vision, Agents). All visible strings go through i18n at
// the call site — these primitives stay presentational.

import { AlertTriangle, FlaskConical } from "lucide-react";
import { Button } from "./Button";
import { EmptyState } from "./EmptyState";
import { Skeleton } from "./Skeleton";
import { cn } from "@/lib/cn";

export type ErrorStateProps = {
  title: string;
  body?: string;
  /** Optional error code / message to surface verbatim (e.g. `qc_500`). */
  detail?: string;
  retryLabel: string;
  onRetry?: () => void;
  className?: string;
};

/** Centered alert + retry button. Use inside a `<Panel>` body when a fetch fails. */
export function ErrorState({ title, body, detail, retryLabel, onRetry, className }: ErrorStateProps) {
  return (
    <div className={cn("flex flex-col items-center text-center py-6", className)}>
      <EmptyState icon={<AlertTriangle size={20} />} title={title} body={body} />
      {detail && (
        <p className="mt-2 mono-pill text-[var(--risk-high)]">{detail}</p>
      )}
      {onRetry && (
        <div className="mt-3">
          <Button variant="secondary" size="sm" onClick={onRetry}>
            {retryLabel}
          </Button>
        </div>
      )}
    </div>
  );
}

export type SkeletonRowsProps = {
  /** Number of placeholder rows. */
  rows?: number;
  /** Each row's height. */
  rowHeight?: string;
  className?: string;
};

/** List-shaped loading state. Visual rhythm matches a `surface-2` row. */
export function SkeletonRows({ rows = 5, rowHeight = "h-12", className }: SkeletonRowsProps) {
  return (
    <ul className={cn("space-y-2", className)} aria-hidden>
      {Array.from({ length: rows }).map((_, i) => (
        <li key={i}>
          <Skeleton className={cn("w-full rounded-md border border-border-subtle", rowHeight)} />
        </li>
      ))}
    </ul>
  );
}

export type SimulatedPillProps = {
  /** Override the label. Defaults to "Simulated". */
  label?: string;
  /** When true, prepends "not RL" to the label. */
  notRL?: boolean;
  /** Optional tooltip text. */
  tip?: string;
  /** Test id for e2e hooks. */
  testId?: string;
  className?: string;
};

/** Pill used to mark simulated surfaces. Always visible — never decorative. */
export function SimulatedPill({ label, notRL, tip, testId, className }: SimulatedPillProps) {
  const text = notRL ? `${label ?? "Simulated"} · not RL` : label ?? "Simulated";
  return (
    <span
      title={tip}
      data-testid={testId}
      className={cn(
        "inline-flex items-center gap-1 h-5 px-2 rounded-sm border border-border-subtle bg-[var(--accent-soft)] text-accent mono-pill",
        className
      )}
    >
      <FlaskConical size={10} aria-hidden />
      {text}
    </span>
  );
}
