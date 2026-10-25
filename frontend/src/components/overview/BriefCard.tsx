"use client";

// Morning brief for /app Overview: GET /api/brief/morning. The brief is a
// deterministic summary of the line board and approval queue (no language
// model), and is labelled as such; the API's own source line is shown under it.

import { useEffect, useRef } from "react";
import Link from "next/link";
import { AlertTriangle, ArrowUpRight, RefreshCw, Sunrise } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Skeleton } from "@/components/ui/Skeleton";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { formatRelative } from "@/lib/format";
import { errorMessage } from "@/lib/api";
import { useResource } from "@/lib/useResource";
import type { MorningBrief } from "@/services/contracts";

/** `tick`: the simulation tick; the brief is re-read when it advances. */
export function BriefCard({ tick }: { tick?: number }) {
  const { t, locale } = useT();
  const brief = useResource<MorningBrief>("/api/brief/morning");
  const lastTick = useRef(tick);
  useEffect(() => {
    if (tick === undefined || tick === lastTick.current) return;
    lastTick.current = tick;
    void brief.reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tick]);
  const b = brief.data;
  const bullets = b ? (locale === "bn" ? b.bulletsBn : b.bulletsEn) : [];
  const [headline, ...rest] = bullets;

  return (
    <Panel
      title={t("brief.cardTitle")}
      subtitle={b ? `For ${b.date} · generated ${formatRelative(b.meta.generatedAt, locale)}` : undefined}
      icon={<Sunrise size={14} />}
      right={
        <>
          {b?.meta.simulated && <span title={b.meta.notes || b.meta.source}><Badge tone="accent">{t("brief.simulatedChip")}</Badge></span>}
          <Button variant="ghost" size="sm" onClick={() => void brief.reload()} disabled={brief.refreshing} aria-label={t("brief.refreshAria")}>
            <RefreshCw size={13} className={cn(brief.refreshing && "animate-spin")} />
          </Button>
        </>
      }
    >
      {brief.error && !b ? (
        <div className="text-center py-4">
          <AlertTriangle size={18} className="mx-auto text-[var(--risk-high)]" />
          <p className="mt-2 text-body text-fg-primary">{t("brief.loadFailed")}</p>
          <p className="text-caption text-fg-tertiary">{errorMessage(brief.error)}</p>
          <Button className="mt-3" variant="secondary" size="sm" onClick={() => void brief.reload()}>{t("brief.retry")}</Button>
        </div>
      ) : brief.loading ? (
        <div className="space-y-2"><Skeleton className="h-14" />{[0, 1].map((i) => <Skeleton key={i} className="h-5" />)}</div>
      ) : b ? (
        <div data-testid="brief-card" data-simulated={b.meta.simulated ? "true" : "false"}>
          {headline && (
            <div
              className={cn(
                "rounded-[var(--radius-control)] border px-3.5 py-3",
                b.topBottleneckLineId ? "border-[var(--risk-high-border)] bg-[var(--risk-high-soft)]" : "border-[var(--risk-low-border)] bg-[var(--risk-low-soft)]"
              )}
            >
              <p className="eyebrow !text-[10.5px]">{b.topBottleneckLineId ? "Top issue" : "Headline"}</p>
              <p className="mt-1 text-[15px] leading-[22px] font-semibold text-fg-primary">{headline}</p>
            </div>
          )}
          {rest.length > 0 && (
            <ol className="mt-3 space-y-2">
              {rest.map((text, i) => (
                <li key={`${i}-${text.slice(0, 16)}`} className="flex items-start gap-2.5">
                  <span className="mt-0.5 size-5 shrink-0 rounded-md bg-surface-2 border border-border-subtle text-[11px] font-semibold inline-flex items-center justify-center text-fg-tertiary" aria-hidden>
                    {i + 2}
                  </span>
                  <span className="text-body text-fg-secondary">{text}</span>
                </li>
              ))}
            </ol>
          )}
          <div className="mt-4 flex flex-wrap items-center gap-2">
            {b.pendingApprovals > 0 && (
              <Link href="/app/approvals" data-testid="brief-approvals-link" className="badge hover:border-[var(--accent-border)] hover:text-accent">
                <span className="badge-dot text-accent" aria-hidden />
                {b.pendingApprovals === 1 ? t("brief.approvalsLabelOne", { n: 1 }) : t("brief.approvalsLabelOther", { n: b.pendingApprovals })}
                <ArrowUpRight size={11} />
              </Link>
            )}
            {b.riskCount > 0 && (
              <span className="badge" data-testid="brief-risks" title="Medium- and high-risk insights stored on the server">
                <span className="badge-dot text-[var(--risk-medium)]" aria-hidden />
                {b.riskCount === 1 ? t("brief.risksLabelOne", { n: 1 }) : t("brief.risksLabelOther", { n: b.riskCount })}
              </span>
            )}
            {!b.topBottleneckLineId && (
              <span className="badge" data-testid="brief-all-green">
                <span className="badge-dot text-[var(--risk-low)]" aria-hidden />
                {t("brief.allOnTarget")}
              </span>
            )}
          </div>
          {b.meta.simulated && <p className="mt-3 text-caption text-fg-tertiary">{b.meta.source}</p>}
        </div>
      ) : null}
    </Panel>
  );
}
