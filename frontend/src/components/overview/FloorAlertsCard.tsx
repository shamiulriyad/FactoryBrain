"use client";

// Floor alerts for /app Overview: the newest items of GET /api/floor-alerts.
// Delivery is simulated (whatsapp_sim) and the feed includes the database's
// demo-seed alerts; both are labelled, using the API's own source line.

import Link from "next/link";
import { AlertOctagon, AlertTriangle, ArrowUpRight, Bell, Info } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Skeleton } from "@/components/ui/Skeleton";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import { formatRelative } from "@/lib/format";
import { errorMessage } from "@/lib/api";
import type { Resource } from "@/lib/useResource";
import { isSeedAlert } from "@/services/floorAlerts.api";
import type { FloorAlert, FloorAlertList } from "@/services/contracts";

const SEVERITY: Record<FloorAlert["severity"], { icon: typeof Info; tile: string; label: string }> = {
  critical: { icon: AlertOctagon, tile: "bg-[var(--risk-high-soft)] text-[var(--risk-high)] border-[var(--risk-high-border)]", label: "Critical" },
  warn: { icon: AlertTriangle, tile: "bg-[var(--risk-medium-soft)] text-[var(--risk-medium)] border-[var(--risk-medium-border)]", label: "Warning" },
  info: { icon: Info, tile: "bg-[var(--info-soft)] text-[var(--info)] border-[var(--info-border)]", label: "Info" }
};

export function FloorAlertsCard({ alerts, limit = 5 }: { alerts: Resource<FloorAlertList>; limit?: number }) {
  const { t, locale } = useT();
  const list = alerts.data?.alerts ?? [];
  const unread = list.filter((a) => !a.read).length;

  return (
    <Panel
      title={t("nav.floorAlerts")}
      subtitle={alerts.data ? `${unread} unread of ${alerts.data.count}` : undefined}
      icon={<Bell size={14} />}
      right={
        <>
          {alerts.data?.simulated && <span title={alerts.data.source}><Badge tone="accent" dot>Simulated delivery</Badge></span>}
          <Link href="/app/activity" className="text-caption text-accent hover:underline inline-flex items-center gap-1">Feed <ArrowUpRight size={12} /></Link>
        </>
      }
      className="h-full"
      flush
    >
      {alerts.loading ? (
        <div className="p-4 space-y-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-12" />)}</div>
      ) : alerts.error && !alerts.data ? (
        <p className="p-4 text-caption text-[var(--risk-high)]" role="alert">{errorMessage(alerts.error)}</p>
      ) : list.length === 0 ? (
        <p className="p-4 text-caption text-fg-tertiary">No alerts.</p>
      ) : (
        <>
          <ul className="divide-y divide-border-subtle">
            {list.slice(0, limit).map((a) => {
              const sev = SEVERITY[a.severity] ?? SEVERITY.info;
              const Icon = sev.icon;
              const seed = isSeedAlert(a);
              return (
                <li key={a.id} className="flex items-start gap-3 px-4 py-3" data-testid="overview-alert" data-seed={seed ? "true" : "false"}>
                  <span className={cn("mt-0.5 size-8 shrink-0 rounded-md border inline-flex items-center justify-center", sev.tile, a.read && "opacity-55")} aria-label={sev.label}>
                    <Icon size={15} />
                  </span>
                  <div className="min-w-0 flex-1">
                    <p className={cn("text-body line-clamp-2", a.read ? "text-fg-tertiary" : "text-fg-primary font-medium")}>{locale === "bn" ? a.bodyBn : a.bodyEn}</p>
                    <p className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-caption text-fg-tertiary">
                      {!a.read && <span className="inline-flex items-center gap-1 text-accent font-medium"><span className="size-1.5 rounded-full bg-accent" aria-hidden />Unread</span>}
                      <span>{formatRelative(a.createdAt, locale)}</span>
                      {seed && <Badge className="!h-[18px] !px-1.5 !text-[11px]" title="Written by the database seed: the line and approval it mentions are sample values">Demo seed</Badge>}
                    </p>
                  </div>
                </li>
              );
            })}
          </ul>
          {alerts.data?.source && <p className="px-4 py-2.5 border-t border-border-subtle text-caption text-fg-tertiary">{alerts.data.source}</p>}
        </>
      )}
    </Panel>
  );
}
