"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { Bell, CheckCheck, ExternalLink, RefreshCw } from "lucide-react";
import { useT } from "@/lib/useT";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Tabs } from "@/components/ui/Tabs";
import { FilterSelect } from "@/components/ui/Field";
import { ConfirmDialog } from "@/components/ui/Modal";
import { EmptyState } from "@/components/ui/EmptyState";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useResource, useOnDataChanged } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import { formatDateTime, formatRelative } from "@/lib/format";
import { cn } from "@/lib/cn";
import { markAlertRead, setUnreadFrom } from "@/services/floorAlerts.api";
import type { FloorAlert, FloorAlertList } from "@/services/contracts";

type View = "unread" | "all";
const SEVERITY_TONE = { info: "info", warn: "warning", critical: "danger" } as const;
const PAGE_SIZE = 25;

export default function FloorAlertsPage() {
  const { t, locale } = useT();
  const toast = useToast();
  const operator = canOperate(useAuthStore((s) => s.user));
  const alerts = useResource<FloorAlertList>("/api/floor-alerts", { pollMs: 10_000 });
  useOnDataChanged(() => void alerts.reload());

  const [view, setView] = useState<View>("unread");
  const [severity, setSeverity] = useState("");
  const [busyId, setBusyId] = useState<string | null>(null);
  const [bulk, setBulk] = useState<{ done: number; total: number } | null>(null);
  const [confirmAll, setConfirmAll] = useState(false);
  const [visible, setVisible] = useState(PAGE_SIZE);

  useEffect(() => {
    if (alerts.data) setUnreadFrom(alerts.data);
  }, [alerts.data]);

  const all = useMemo(() => alerts.data?.alerts ?? [], [alerts.data]);
  const unreadCount = all.filter((a) => !a.read).length;
  const rows = useMemo(
    () => all.filter((a) => (view === "all" || !a.read) && (!severity || a.severity === severity)),
    [all, view, severity]
  );
  const unreadInView = rows.filter((a) => !a.read);

  function replace(updated: FloorAlert) {
    alerts.setData((cur) => (cur ? { ...cur, alerts: cur.alerts.map((a) => (a.id === updated.id ? updated : a)) } : cur));
  }

  async function toggle(a: FloorAlert) {
    if (busyId) return;
    setBusyId(a.id);
    try {
      replace(await markAlertRead(a.id, !a.read));
    } catch (e) {
      toast.push({ tone: "error", title: "Alert not updated", description: errorMessage(e) });
      await alerts.reload();
    } finally {
      setBusyId(null);
    }
  }

  async function markAllRead() {
    const targets = unreadInView.slice();
    setBulk({ done: 0, total: targets.length });
    let failed = 0;
    for (const [i, a] of targets.entries()) {
      try {
        replace(await markAlertRead(a.id, true));
      } catch {
        failed++;
      }
      setBulk({ done: i + 1, total: targets.length });
    }
    setBulk(null);
    setConfirmAll(false);
    toast.push({
      tone: failed ? "error" : "success",
      title: `Marked ${targets.length - failed} alert(s) as read`,
      description: failed ? `${failed} could not be updated.` : undefined
    });
    await alerts.reload();
  }

  return (
    <div className="page page-narrow" data-tour="activity">
      <PageHeader
        eyebrow="Operations"
        title={t("nav.floorAlerts")}
        description="Alerts raised by agent runs, vision analyses and the line simulator, as sent to the floor channel."
        meta={
          alerts.data?.simulated ? (
            <span title={alerts.data.source} data-testid="floor-alerts-simulated-pill">
              <Badge tone="accent" dot>Simulated WhatsApp channel · nothing is sent</Badge>
            </span>
          ) : null
        }
        actions={
          <>
            {operator && unreadInView.length > 0 && (
              <Button variant="secondary" size="sm" onClick={() => setConfirmAll(true)} data-testid="alerts-mark-all">
                <CheckCheck size={13} /> Mark {unreadInView.length} as read
              </Button>
            )}
            <Button variant="secondary" size="sm" onClick={() => void alerts.reload()} disabled={alerts.refreshing} aria-label="Refresh alerts">
              <RefreshCw size={13} className={cn(alerts.refreshing && "animate-spin")} /> Refresh
            </Button>
          </>
        }
      />

      {!operator && (
        <Callout tone="info" className="mt-5">You can read alerts. Operators and Admins acknowledge them.</Callout>
      )}

      <Panel className="mt-5" flush>
        <div className="flex flex-wrap items-end justify-between gap-3 px-4 pt-2">
          <Tabs
            label="Alert view"
            value={view}
            onChange={(v) => { setView(v); setVisible(PAGE_SIZE); }}
            items={[
              { id: "unread", label: "Unread", count: unreadCount, testId: "alerts-tab-unread" },
              { id: "all", label: "All", count: all.length, testId: "alerts-tab-all" }
            ]}
            className="border-b-0"
          />
          <FilterSelect label="Severity" value={severity} onChange={setSeverity} className="pb-2">
            <option value="">All</option>
            <option value="critical">Critical</option>
            <option value="warn">Warning</option>
            <option value="info">Info</option>
          </FilterSelect>
        </div>
        <div className="border-t border-border-subtle">
          {alerts.error && !alerts.data ? (
            <div className="p-4">
              <Callout tone="danger" title={t("floor.alerts.title")} action={<Button size="sm" onClick={() => void alerts.reload()}>{t("floor.alerts.retry")}</Button>}>
                {errorMessage(alerts.error)}
              </Callout>
            </div>
          ) : alerts.loading ? (
            <div className="p-4"><SkeletonRows rows={5} rowHeight="h-16" /></div>
          ) : rows.length === 0 ? (
            <EmptyState
              icon={<Bell size={22} />}
              title={view === "unread" ? "No unread alerts." : t("floor.alerts.emptyTitle")}
              body={view === "unread" ? "Everything has been acknowledged." : t("floor.alerts.emptyBody")}
            />
          ) : (
            <ul className="divide-y divide-border-subtle" data-testid="floor-alerts">
              {rows.slice(0, visible).map((a) => (
                <li key={a.id} className={cn("px-4 py-3.5 flex items-start gap-3", !a.read && "bg-[var(--accent-soft)]/40")} data-testid="floor-alert" data-read={a.read ? "true" : "false"}>
                  <span
                    className={cn("mt-1.5 size-2.5 rounded-full shrink-0", a.read ? "bg-fg-tertiary/40" : a.severity === "critical" ? "bg-[var(--risk-high)]" : a.severity === "warn" ? "bg-[var(--risk-medium)]" : "bg-[var(--info)]")}
                    aria-hidden
                  />
                  <div className="min-w-0 flex-1">
                    <p className={cn("text-body whitespace-pre-line", a.read ? "text-fg-secondary" : "text-fg-primary font-medium")}>
                      {locale === "bn" ? a.bodyBn : a.bodyEn}
                    </p>
                    <div className="mt-1.5 flex flex-wrap items-center gap-2 text-caption text-fg-tertiary">
                      <Badge tone={SEVERITY_TONE[a.severity]}>{a.severity}</Badge>
                      <span title={formatDateTime(a.createdAt, locale)}>{formatRelative(a.createdAt, locale)}</span>
                      <span className="font-mono">{a.channel}</span>
                      {(a.approvalId || a.insightId) && (
                        <Link href={`/app/approvals?focus=${encodeURIComponent(a.approvalId ?? a.insightId!)}`} className="text-accent hover:underline inline-flex items-center gap-1">
                          <ExternalLink size={11} /> Related approval
                        </Link>
                      )}
                    </div>
                  </div>
                  {operator && (
                    <Button variant={a.read ? "ghost" : "secondary"} size="sm" disabled={busyId === a.id || bulk !== null} onClick={() => void toggle(a)} data-testid="floor-alert-toggle">
                      {a.read ? t("floor.alerts.markUnread") : t("floor.alerts.markRead")}
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}
          {rows.length > visible && (
            <div className="p-3 flex justify-center border-t border-border-subtle">
              <Button variant="secondary" size="sm" onClick={() => setVisible((v) => v + PAGE_SIZE)}>Show more ({rows.length - visible})</Button>
            </div>
          )}
        </div>
      </Panel>

      <ConfirmDialog
        open={confirmAll}
        title={`Mark ${unreadInView.length} alert(s) as read?`}
        description="Each alert is updated on the server; other people see them as read too."
        confirmLabel={bulk ? `Updating ${bulk.done}/${bulk.total}…` : "Mark as read"}
        busy={bulk !== null}
        onConfirm={() => void markAllRead()}
        onCancel={() => setConfirmAll(false)}
      />
    </div>
  );
}
