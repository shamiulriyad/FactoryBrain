"use client";

// Morning brief card for /app Overview.
// Fetches GET /api/brief/morning, renders locale-aware bullets with
// deep-links (top-bottleneck → /app/brain?node=<lineId>), and shows
// pending-approval / risk counts that link to /app/approvals and
// /app/insights. A DemoChip keeps the "Simulated / no LLM" honesty
// label visible. Auto-refreshes when the shared live-tick store
// advances (same coupling as LineBoardPanel).

import { useCallback, useEffect, useRef, useState } from "react";
import Link from "next/link";
import { AlertTriangle, ArrowUpRight, RefreshCw } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { EmptyState } from "@/components/ui/EmptyState";
import { Button, DemoChip } from "@/components/ui/Button";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { formatRelative } from "@/lib/format";
import { useFactoryBrainLiveStore } from "@/store/factoryBrain.live.store";
import type { BriefResponse } from "@/services/brief.server";

// --- Helpers ---------------------------------------------------------------

// --- Component -------------------------------------------------------------

export function BriefCard() {
  const { t, locale } = useT();

  const [brief, setBrief] = useState<BriefResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Re-fetch when the shared sim tick advances (Simulate tick / polling).
  const simTick = useFactoryBrainLiveStore((s) => s.snapshot?.tick);

  const inflight = useRef<AbortController | null>(null);

  const refresh = useCallback(async () => {
    if (inflight.current) inflight.current.abort();
    const ctrl = new AbortController();
    inflight.current = ctrl;
    setLoading(true);
    setError(null);
    try {
      const res = await fetch("/api/brief/morning", {
        method: "GET",
        cache: "no-store",
        signal: ctrl.signal
      });
      if (!res.ok) throw new Error(`brief_${res.status}`);
      const data = (await res.json()) as BriefResponse;
      setBrief(data);
    } catch (e) {
      if ((e as Error).name !== "AbortError") {
        setError((e as Error).message ?? "brief_failed");
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

  const bullets: string[] = brief
    ? (locale === "bn" ? brief.bulletsBn : brief.bulletsEn)
    : [];
  const topLineId = brief?.topBottleneckLineId;
  const simulated = brief?.meta?.simulated === true;
  const dateLabel = brief?.date ?? "";
  const generatedRel = brief?.meta?.generatedAt
    ? formatRelative(brief.meta.generatedAt, locale as "en" | "bn")
    : null;

  const subtitle = generatedRel
    ? `${dateLabel} · ${generatedRel}`
    : dateLabel || t("brief.simulatedChip");

  const headerRight = (
    <div className="flex items-center gap-2">
      {simulated && <DemoChip>{t("brief.simulatedChip")}</DemoChip>}
      <Button
        variant="secondary"
        size="sm"
        onClick={refresh}
        disabled={loading}
        aria-label={t("brief.refreshAria")}
      >
        <RefreshCw size={14} className={cn(loading && "animate-spin")} />
      </Button>
    </div>
  );

  // --- Body ---------------------------------------------------------------

  let body: React.ReactNode;
  if (error && !brief) {
    body = (
      <div>
        <EmptyState
          icon={<AlertTriangle size={20} />}
          title={t("brief.loadFailed")}
          body={t("brief.loadFailedBody")}
        />
        <div className="mt-3 flex justify-center">
          <Button variant="secondary" size="sm" onClick={refresh}>
            {t("brief.retry")}
          </Button>
        </div>
      </div>
    );
  } else if (loading && !brief) {
    body = (
      <ul className="space-y-2" aria-hidden>
        {[0, 1, 2].map((i) => (
          <li key={i} className="surface-2 h-9 rounded-md border border-border-subtle animate-pulse" />
        ))}
      </ul>
    );
  } else if (brief) {
    const risksLabel =
      brief.riskCount === 1
        ? t("brief.risksLabelOne", { n: brief.riskCount })
        : t("brief.risksLabelOther", { n: brief.riskCount });
    const approvalsLabel =
      brief.pendingApprovals === 1
        ? t("brief.approvalsLabelOne", { n: brief.pendingApprovals })
        : t("brief.approvalsLabelOther", { n: brief.pendingApprovals });

    body = (
      <div data-testid="brief-card" data-simulated={simulated ? "true" : "false"}>
        <ul role="list" className="space-y-2">
          {bullets.map((b, i) => {
            const isHeader = i === 0;
            const href = isHeader && topLineId ? `/app/brain?node=${encodeURIComponent(topLineId)}` : null;
            return (
              <li key={`${i}-${b.slice(0, 16)}`} className="flex items-start gap-2">
                <span
                  className={cn(
                    "mono-pill shrink-0 mt-0.5 px-1.5 py-0.5 rounded-sm",
                    isHeader ? "text-[var(--accent-fg-on-bg)] bg-accent" : "text-fg-tertiary bg-surface-2 border border-border-subtle"
                  )}
                  aria-hidden
                >
                  {i + 1}
                </span>
                {href ? (
                  <Link
                    href={href}
                    className="text-body text-fg-primary hover:text-accent inline-flex items-center gap-1 group"
                    data-testid="brief-bottleneck-link"
                  >
                    <span>{b}</span>
                    <ArrowUpRight size={14} className="opacity-60 group-hover:opacity-100" />
                  </Link>
                ) : (
                  <span className="text-body text-fg-secondary">{b}</span>
                )}
              </li>
            );
          })}
        </ul>

        <div className="mt-4 flex flex-wrap items-center gap-2">
          {brief.riskCount > 0 && (
            <Link
              href="/app/insights"
              className="inline-flex items-center gap-1 h-6 px-2 rounded-sm border border-border-subtle bg-surface-2 text-caption text-fg-secondary hover:text-accent mono-pill"
              data-testid="brief-risks-link"
            >
              <span className="size-1 rounded-full bg-[var(--risk-medium)]" aria-hidden />
              {risksLabel}
            </Link>
          )}
          {brief.pendingApprovals > 0 && (
            <Link
              href="/app/approvals"
              className="inline-flex items-center gap-1 h-6 px-2 rounded-sm border border-border-subtle bg-surface-2 text-caption text-fg-secondary hover:text-accent mono-pill"
              data-testid="brief-approvals-link"
            >
              <span className="size-1 rounded-full bg-accent" aria-hidden />
              {approvalsLabel}
            </Link>
          )}
          {!topLineId && (
            <span
              className="inline-flex items-center gap-1 h-6 px-2 rounded-sm border border-border-subtle bg-surface-2 text-caption text-fg-tertiary mono-pill"
              data-testid="brief-all-green"
            >
              <span className="size-1 rounded-full bg-[var(--risk-low)]" aria-hidden />
              {t("brief.allOnTarget")}
            </span>
          )}
        </div>
      </div>
    );
  } else {
    body = null;
  }

  return (
    <Panel
      variant="glass"
      title={t("brief.cardTitle")}
      subtitle={subtitle}
      right={headerRight}
    >
      {body}
    </Panel>
  );
}
