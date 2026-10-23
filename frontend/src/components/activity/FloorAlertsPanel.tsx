"use client";

// Floor-alerts panel — WhatsApp-styled, fed live by GET /api/floor-alerts.
// Mirrors the chrome of components/activity/WhatsAppAlert.tsx but does NOT
// share its store-derived bubbles. Mark-read goes through PATCH on the same
// route. Empty state when the buffer is empty.

import { useCallback, useEffect, useState } from "react";
import { motion, AnimatePresence } from "framer-motion";
import { Bell, ExternalLink } from "lucide-react";
import Link from "next/link";
import { Button } from "@/components/ui/Button";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SimulatedPill } from "@/components/ui/Status";
import { useT } from "@/lib/useT";
import { formatRelative } from "@/lib/format";
import type { FloorAlertT } from "@/services/sensors.schemas";

type Severity = FloorAlertT["severity"];

const severityStyles: Record<Severity, string> = {
  info: "text-fg-secondary bg-surface-2 border-border-subtle",
  warn: "text-[var(--risk-medium)] bg-[var(--risk-medium-soft)] border-[var(--risk-medium-border)]",
  critical: "text-[var(--risk-high)] bg-[var(--risk-high-soft)] border-[var(--risk-high-border)]"
};

export function FloorAlertsPanel() {
  const { t, locale } = useT();
  const [alerts, setAlerts] = useState<FloorAlertT[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [pendingId, setPendingId] = useState<string | null>(null);

  const refresh = useCallback(async (signal?: AbortSignal) => {
    try {
      const res = await fetch("/api/floor-alerts", { cache: "no-store", signal });
      if (!res.ok) throw new Error(`list_${res.status}`);
      const data = (await res.json()) as { alerts: FloorAlertT[] };
      setAlerts(data.alerts ?? []);
      setError(null);
    } catch (e) {
      if ((e as Error).name !== "AbortError") setError((e as Error).message ?? "fetch_failed");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const ctrl = new AbortController();
    void refresh(ctrl.signal);
    // Poll so alerts raised by agent runs land without a page reload.
    const id = setInterval(() => void refresh(), 7000);
    const onRefresh = () => void refresh();
    window.addEventListener("bunonbrain:insights-refresh", onRefresh as EventListener);
    return () => {
      clearInterval(id);
      ctrl.abort();
      window.removeEventListener("bunonbrain:insights-refresh", onRefresh as EventListener);
    };
  }, [refresh]);

  const markRead = useCallback(async (id: string, read: boolean) => {
    setPendingId(id);
    try {
      const res = await fetch(`/api/floor-alerts/${encodeURIComponent(id)}`, {
        method: "PATCH",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ read })
      });
      if (!res.ok) throw new Error(`patch_${res.status}`);
      const data = (await res.json()) as { alert: FloorAlertT };
      setAlerts((cur) => (cur ? cur.map((a) => (a.id === id ? data.alert : a)) : cur));
    } catch (e) {
      if ((e as Error).name !== "AbortError") setError((e as Error).message ?? "patch_failed");
    } finally {
      setPendingId(null);
    }
  }, []);

  const unread = alerts?.filter((a) => !a.read).length ?? 0;
  const total = alerts?.length ?? 0;

  // Subtitle adapts to the current state — same rhythm as other panels.
  const subtitle = error
    ? (t("floor.alerts.retryHint") as string)
    : loading
    ? (t("floor.alerts.syncing") as string)
    : (t("floor.alerts.countUnread", { total, unread }) as string);

  return (
    <section className="surface p-4" aria-label={t("floor.alerts.title") as string}>
      <header className="flex items-center justify-between gap-2">
        <div className="flex items-center gap-2 min-w-0">
          <span className="inline-flex size-7 items-center justify-center rounded-full bg-[#25D366]/20 border border-[#25D366]/40 text-[#1a8c4a] shrink-0">
            <Bell size={14} />
          </span>
          <div className="min-w-0">
            <p className="text-body text-fg-primary truncate">
              {locale === "bn" ? "ফ্লোর অ্যালার্ট (whatsapp_sim)" : "Floor alerts (whatsapp_sim)"}
            </p>
            <p className="text-caption text-fg-tertiary truncate">{subtitle}</p>
          </div>
        </div>
        <SimulatedPill
          label={t("floor.alerts.simulatedChannel") as string}
          testId="floor-alerts-simulated-pill"
        />
      </header>

      <div
        className="mt-3 h-72 overflow-y-auto rounded-md p-3 space-y-2"
        style={{ background: "linear-gradient(180deg, #ECE5DD 0%, #D9CBB7 100%)" }}
      >
        {loading && alerts === null ? (
          <p className="text-caption text-fg-tertiary text-center mt-8">
            {t("floor.alerts.syncing") as string}
          </p>
        ) : error ? (
          <div className="mt-4">
            <ErrorState
              title={t("floor.alerts.title") as string}
              body={t("floor.alerts.retryHint") as string}
              detail={error}
              retryLabel={t("floor.alerts.retry") as string}
              onRetry={() => void refresh()}
            />
          </div>
        ) : alerts && alerts.length === 0 ? (
          <EmptyState
            className="mt-6 bg-transparent"
            title={t("floor.alerts.emptyTitle") as string}
            body={t("floor.alerts.emptyBody") as string}
            icon={<Bell size={20} />}
          />
        ) : (
          <AnimatePresence initial={false}>
            {(alerts ?? []).map((a) => (
              <motion.div
                key={a.id}
                initial={{ opacity: 0, y: 6, scale: 0.96 }}
                animate={{ opacity: 1, y: 0, scale: 1 }}
                exit={{ opacity: 0, scale: 0.96 }}
                transition={{ duration: 0.2 }}
                className={
                  a.read
                    ? "wp-bubble-out relative mr-auto max-w-[80%] bg-surface px-3 py-2 rounded-md text-caption text-fg-tertiary shadow"
                    : "wp-bubble-in relative mr-auto max-w-[80%] bg-surface px-3 py-2 rounded-md text-caption text-fg-primary shadow border border-[var(--accent-border)]"
                }
                data-testid="floor-alert"
              >
                <div className="flex items-start justify-between gap-2">
                  <p className="min-w-0 flex-1 whitespace-pre-line">
                    {locale === "bn" ? a.bodyBn : a.bodyEn}
                  </p>
                  <span
                    className={`shrink-0 mono-pill border px-2 py-0.5 text-caption ${severityStyles[a.severity]}`}
                  >
                    {a.severity}
                  </span>
                </div>
                <div className="mt-2 flex items-center justify-between gap-2 flex-wrap">
                  <span className="text-caption text-fg-tertiary">
                    {formatRelative(a.createdAt, locale)}
                  </span>
                  <div className="flex items-center gap-2 shrink-0">
                    {a.insightId && (
                      <Link
                        href={`/app/insights?focus=${a.insightId}`}
                        className="text-caption text-accent hover:underline inline-flex items-center gap-1"
                      >
                        <ExternalLink size={12} />
                        insight
                      </Link>
                    )}
                    {a.approvalId && (
                      <Link
                        href={`/app/approvals?focus=${a.approvalId}`}
                        className="text-caption text-accent hover:underline inline-flex items-center gap-1"
                      >
                        <ExternalLink size={12} />
                        approval
                      </Link>
                    )}
                    <Button
                      variant={a.read ? "secondary" : "primary"}
                      size="sm"
                      disabled={pendingId === a.id}
                      onClick={() => void markRead(a.id, !a.read)}
                    >
                      {a.read
                        ? (t("floor.alerts.markUnread") as string)
                        : (t("floor.alerts.markRead") as string)}
                    </Button>
                  </div>
                </div>
              </motion.div>
            ))}
          </AnimatePresence>
        )}
      </div>
    </section>
  );
}