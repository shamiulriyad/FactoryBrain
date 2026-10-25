"use client";

import Link from "next/link";
import type { ReactNode } from "react";
import { cn } from "@/lib/cn";
import { Skeleton } from "./Skeleton";

export type StatTone = "neutral" | "good" | "warn" | "bad";

const TONE_TEXT: Record<StatTone, string> = {
  neutral: "text-fg-primary",
  good: "text-[var(--risk-low)]",
  warn: "text-[var(--risk-medium)]",
  bad: "text-[var(--risk-high)]"
};

type Props = {
  label: ReactNode;
  value: ReactNode;
  /** Small line under the value: unit, comparison, source. */
  hint?: ReactNode;
  tone?: StatTone;
  icon?: ReactNode;
  href?: string;
  loading?: boolean;
  /** Error text replaces the value (never a made-up number). */
  error?: string | null;
  testId?: string;
};

/** KPI tile. Shows a skeleton while loading and the error instead of a number on failure. */
export function StatCard({ label, value, hint, tone = "neutral", icon, href, loading, error, testId }: Props) {
  const body = (
    <div className={cn("surface p-4 h-full", href && "card-link")} data-testid={testId}>
      <div className="flex items-center justify-between gap-2">
        <p className="text-caption font-medium text-fg-tertiary">{label}</p>
        {icon && <span className="text-fg-tertiary" aria-hidden>{icon}</span>}
      </div>
      {loading ? (
        <Skeleton className="mt-2 h-8 w-20" />
      ) : error ? (
        <p className="mt-2 text-caption text-[var(--risk-high)]">{error}</p>
      ) : (
        <p className={cn("mt-1.5 text-[26px] leading-[32px] font-semibold tracking-tight tabular-nums", TONE_TEXT[tone])}>{value}</p>
      )}
      {hint && !loading && <p className="mt-1 text-caption text-fg-tertiary">{hint}</p>}
    </div>
  );
  return href ? (
    <Link href={href} className="block rounded-[var(--radius-card)]">
      {body}
    </Link>
  ) : (
    body
  );
}
