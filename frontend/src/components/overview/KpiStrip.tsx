"use client";

// Overview KPI strip: one segmented instrument bar instead of separate cards.
// Every cell renders a number the API returned (or a count of what it
// returned); a failed source shows "Unavailable", never a placeholder number.

import Link from "next/link";
import type { ReactNode } from "react";
import { ArrowUpRight } from "lucide-react";
import { Skeleton } from "@/components/ui/Skeleton";
import { cn } from "@/lib/cn";

export type KpiTone = "neutral" | "good" | "warn" | "bad";

const TONE_TEXT: Record<KpiTone, string> = {
  neutral: "text-fg-primary",
  good: "text-[var(--risk-low)]",
  warn: "text-[var(--risk-medium)]",
  bad: "text-[var(--risk-high)]"
};

const TONE_RULE: Record<KpiTone, string> = {
  neutral: "bg-border-strong",
  good: "bg-[var(--risk-low)]",
  warn: "bg-[var(--risk-medium)]",
  bad: "bg-[var(--risk-high)]"
};

export function KpiStrip({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <section
      aria-label="Key figures"
      data-tour="overview-kpis"
      className={cn(
        "surface overflow-hidden grid grid-cols-2 md:grid-cols-3 xl:grid-cols-5",
        // Hairlines between cells in every layout.
        "gap-px bg-border-subtle [&>*]:bg-surface",
        className
      )}
    >
      {children}
    </section>
  );
}

type CellProps = {
  label: string;
  value: ReactNode;
  /** Unit or qualifier shown next to the value. */
  unit?: ReactNode;
  detail?: ReactNode;
  tone?: KpiTone;
  /** Small visual under the value (segment bar, stacked bar…). */
  visual?: ReactNode;
  href?: string;
  loading?: boolean;
  error?: string | null;
  testId?: string;
  className?: string;
};

export function KpiCell({ label, value, unit, detail, tone = "neutral", visual, href, loading, error, testId, className }: CellProps) {
  const body = (
    <div className={cn("relative h-full p-4 md:p-5 flex flex-col", href && "group", className)} data-testid={testId}>
      <span className={cn("absolute left-0 top-4 bottom-4 w-[3px] rounded-r", loading || error ? "bg-border-subtle" : TONE_RULE[tone])} aria-hidden />
      <div className="flex items-center justify-between gap-2">
        <p className="eyebrow !text-[10.5px]">{label}</p>
        {href && <ArrowUpRight size={13} className="text-fg-tertiary group-hover:text-accent transition-colors" aria-hidden />}
      </div>
      {loading ? (
        <Skeleton className="mt-3 h-9 w-24" />
      ) : error ? (
        <p className="mt-3 text-body font-medium text-[var(--risk-high)]">{error}</p>
      ) : (
        <p className="mt-2 flex items-baseline gap-1.5">
          <span className={cn("text-[34px] leading-[40px] font-semibold tracking-tight tabular-nums", TONE_TEXT[tone])}>{value}</span>
          {unit && <span className="text-caption font-medium text-fg-tertiary">{unit}</span>}
        </p>
      )}
      {!loading && !error && visual && <div className="mt-2.5">{visual}</div>}
      {!loading && !error && detail && <p className="mt-auto pt-2.5 text-caption text-fg-secondary">{detail}</p>}
    </div>
  );
  return href ? (
    <Link href={href} className="block hover:!bg-surface-2 transition-colors focus-visible:outline-offset-[-2px]">
      {body}
    </Link>
  ) : (
    <div>{body}</div>
  );
}

/** One segment per item, coloured by its tone (e.g. one per line). */
export function SegmentBar({ segments, label }: { segments: { key: string; tone: KpiTone; title: string }[]; label: string }) {
  return (
    <div className="flex gap-1" role="img" aria-label={label}>
      {segments.map((s) => (
        <span key={s.key} title={s.title} className={cn("h-1.5 flex-1 rounded-full", TONE_RULE[s.tone], s.tone === "neutral" && "opacity-60")} />
      ))}
    </div>
  );
}

/** Proportional stacked bar (e.g. machines down / at risk / healthy). */
export function StackBar({ parts, label }: { parts: { key: string; value: number; tone: KpiTone }[]; label: string }) {
  const total = parts.reduce((a, p) => a + p.value, 0);
  return (
    <div className="flex h-1.5 w-full overflow-hidden rounded-full bg-surface-3 gap-px" role="img" aria-label={label}>
      {total > 0 &&
        parts.filter((p) => p.value > 0).map((p) => (
          <span key={p.key} className={TONE_RULE[p.tone]} style={{ width: `${(p.value / total) * 100}%` }} />
        ))}
    </div>
  );
}
