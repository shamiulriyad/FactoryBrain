"use client";

// One alert: what happened and where, its evidence metadata, the actions
// this user may take, the complete history of who did what and when, and
// the events behind it. Updates live when anyone acts on it.

import Link from "next/link";
import { useParams } from "next/navigation";
import { useState } from "react";
import { ArrowLeft, Bell, History, ShieldCheck } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Callout } from "@/components/ui/Callout";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import {
  AlertActionButtons, AlertActionDialog, AlertStatusBadge, EventList, LiveIndicator, MonitoringTabs, SeverityBadge, SourceBadge, type AlertAction
} from "@/components/monitoring/shared";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { P } from "@/lib/access";
import { cn } from "@/lib/cn";
import { ALERT_STATUS_LABEL, ALERT_TYPE_LABEL, ESCALATION_TARGETS, MON, RESOLUTION_LABEL, dhaka, type MonitoringAlertDetail } from "@/lib/monitoring";

export default function AlertDetailPage() {
  return (
    <RequirePermission perms={[P.MonitoringView, P.DevicesView]} what="Monitoring alert">
      <Detail />
    </RequirePermission>
  );
}

const ACTION_LABEL: Record<string, string> = {
  CREATED: "Raised",
  REPEATED: "Happened again",
  SOURCE_RECOVERED: "Source back to normal",
  ACKNOWLEDGED: "Acknowledged",
  ESCALATED: "Escalated",
  RESOLVED: "Resolved",
  DISMISSED: "Dismissed"
};

function Detail() {
  const { id } = useParams<{ id: string }>();
  const detail = useResource<MonitoringAlertDetail>(`/api/monitoring/alerts/${id}`);
  const [action, setAction] = useState<AlertAction | null>(null);
  const d = detail.data;
  const hub = useMonitoringHub(d?.alert.factoryId ?? null, {
    onAlert: (_, a) => { if (a.id === id) void detail.reload(); },
    onResync: () => void detail.reload()
  }, !!d);

  if (detail.error && !d) return <div className="page"><ErrorState retryLabel="Try again" title="Alert not available" body={detail.error.message} onRetry={() => void detail.reload()} /></div>;
  if (!d) return <div className="page"><SkeletonRows rows={6} /></div>;
  const a = d.alert;
  const target = (role?: string | null) => ESCALATION_TARGETS.find((t) => t.id === role)?.label ?? role;

  return (
    <div className="page" data-testid="alert-detail">
      <PageHeader eyebrow={<Link href={`${MON}/alerts`} className="inline-flex items-center gap-1"><ArrowLeft size={12} /> Alerts</Link>}
        title={<span className="inline-flex flex-wrap items-center gap-3"><span className="font-mono text-fg-tertiary">{a.code}</span>{a.title}</span>}
        description={ALERT_TYPE_LABEL[a.alertType] ?? a.alertType}
        meta={<div className="flex flex-wrap items-center gap-2"><SeverityBadge severity={a.severity} /><AlertStatusBadge status={a.status} /><SourceBadge source={a.sourceType} /><LiveIndicator state={hub.state} /></div>}
        actions={<AlertActionButtons a={a} onAction={setAction} size="md" />} />
      <MonitoringTabs className="mb-4" />

      {a.isSimulated && <Callout tone="warning" className="mb-4">This alert was raised from SIMULATED data (the monitoring simulator or a simulated device). It is not real equipment or camera AI.</Callout>}

      <div className="grid gap-4 xl:grid-cols-3">
        <div className="xl:col-span-2 space-y-4">
          <Panel title="What happened" icon={<Bell size={15} />} testId="alert-what">
            <div className="p-4 space-y-4">
              <p className="text-body">{a.description}</p>
              <dl className="grid grid-cols-2 sm:grid-cols-3 gap-x-4 gap-y-3 text-caption">
                <D label="Factory">{a.factoryCode}</D>
                <D label="Line">{a.line?.name ?? "—"}</D>
                <D label="Department / floor">{[a.departmentName, a.floorName].filter(Boolean).join(" / ") || "—"}</D>
                <D label="Camera">{a.camera ? <Link className="text-[var(--accent)]" href={`${MON}/cameras/${a.camera.id}`}>{a.camera.code}</Link> : "—"}</D>
                <D label="Device">{a.device?.code ?? "—"}</D>
                <D label="Zone / workstation">{[a.zone?.name, a.workstation?.code].filter(Boolean).join(" / ") || "—"}</D>
                <D label="First (Dhaka)">{dhaka(a.firstOccurredAt)}</D>
                <D label="Last (Dhaka)">{dhaka(a.lastOccurredAt)}</D>
                <D label="Occurrences">{a.occurrenceCount}</D>
                <D label="Acknowledged">{a.acknowledgedAt ? `${a.acknowledgedByEmail} · ${dhaka(a.acknowledgedAt)}` : "—"}</D>
                <D label="Escalated">{a.escalatedAt ? `Level ${a.escalationLevel} to ${target(a.escalatedToRole)} · ${dhaka(a.escalatedAt)}` : "—"}</D>
                <D label={a.status === "DISMISSED" ? "Dismissed" : "Resolved"}>
                  {a.resolvedAt ? `${RESOLUTION_LABEL[a.resolutionCode ?? ""] ?? a.resolutionCode} · ${a.resolvedByEmail}` : a.dismissedAt ? `${a.dismissedByEmail} · ${dhaka(a.dismissedAt)}` : "—"}
                </D>
              </dl>
              {a.resolutionNote && <Callout tone="success" title="Resolution">{a.resolutionNote}</Callout>}
              {a.dismissReason && <Callout tone="info" title="Dismissed because">{a.dismissReason}</Callout>}
            </div>
          </Panel>

          <Panel title="Events behind this alert" subtitle="Dhaka time" flush testId="alert-events">
            <EventList rows={d.relatedEvents} empty={<p className="px-4 py-6 text-caption text-fg-tertiary">The raising event isn&apos;t visible to you.</p>} />
            <div className="px-4 py-2 border-t border-border-subtle text-caption">
              <Link className="text-[var(--accent)]" href={`${MON}/events?event=${a.eventId}`}>Open the raising event in the timeline</Link>
            </div>
          </Panel>

          <Panel title="Evidence metadata" subtitle="References only: no images, no identities" testId="alert-evidence">
            <pre className="m-4 text-[11px] bg-surface-2 rounded-[var(--radius-control)] p-3 overflow-x-auto">{JSON.stringify(a.evidence, null, 2)}</pre>
          </Panel>
        </div>

        <div className="space-y-4">
          <Panel title="History" subtitle="Every action, kept permanently" icon={<History size={15} />} testId="alert-history">
            <ol className="p-4 space-y-4">
              {d.history.map((h) => (
                <li key={h.id} className="relative pl-5" data-testid="alert-history-item" data-action={h.action}>
                  <span className={cn("absolute left-0 top-1.5 h-2.5 w-2.5 rounded-full",
                    h.action === "RESOLVED" ? "bg-[var(--risk-low,#16a34a)]" : h.action === "ESCALATED" || h.action === "DISMISSED" ? "bg-[var(--risk-medium)]" : "bg-border-strong")} aria-hidden />
                  <p className="text-body font-medium">{ACTION_LABEL[h.action] ?? h.action}
                    {h.fromStatus && h.fromStatus !== h.toStatus && <span className="text-caption text-fg-tertiary font-normal"> · {ALERT_STATUS_LABEL[h.fromStatus]} → {ALERT_STATUS_LABEL[h.toStatus]}</span>}
                  </p>
                  <p className="text-caption text-fg-secondary">{h.actorEmail}{h.actorRole ? ` (${h.actorRole})` : ""} · {dhaka(h.at)}</p>
                  {h.escalatedToRole && <p className="text-caption">To {target(h.escalatedToRole)}</p>}
                  {h.resolutionCode && <p className="text-caption">{RESOLUTION_LABEL[h.resolutionCode] ?? h.resolutionCode}</p>}
                  {h.note && <p className="text-caption text-fg-secondary mt-0.5">{h.note}</p>}
                </li>
              ))}
            </ol>
          </Panel>
          <p className="text-caption text-fg-tertiary inline-flex items-center gap-1.5"><ShieldCheck size={12} aria-hidden /> Decisions about people need an authorised person; an alert never changes pay, attendance or discipline.</p>
        </div>
      </div>

      <AlertActionDialog alert={action ? a : null} action={action} onClose={() => setAction(null)}
        onDone={(next) => { setAction(null); detail.setData(next); }} />
    </div>
  );
}

function D({ label, children }: { label: string; children: React.ReactNode }) {
  return <div><dt className="text-fg-tertiary">{label}</dt><dd className="text-fg-primary mt-0.5">{children}</dd></div>;
}
