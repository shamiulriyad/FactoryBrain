"use client";

// Alert management: alerts in scope with filters, sorting and pagination,
// and the actions this user may take (acknowledge, escalate, resolve,
// dismiss). Every action is checked and recorded by the server; the list
// refreshes when any alert in scope changes.

import { useRef, useState } from "react";
import { Bell } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { Callout } from "@/components/ui/Callout";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Pagination } from "@/components/access/shared";
import { useFactoryOrg, useMyFactories } from "@/components/workers/shared";
import { SearchBox, useQueryState } from "@/components/attendance/shared";
import { AlertActionButtons, AlertActionDialog, AlertRow, LiveIndicator, MonitoringTabs, type AlertAction } from "@/components/monitoring/shared";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { P, type PagedResult } from "@/lib/access";
import { ALERT_STATUS_LABEL, ALERT_TYPE_LABEL, MON, qs, type MonitoringAlert } from "@/lib/monitoring";

export default function AlertsPage() {
  return (
    <RequirePermission perms={[P.MonitoringView, P.DevicesView]} what="Monitoring alerts">
      <Alerts />
    </RequirePermission>
  );
}

function Alerts() {
  const q = useQueryState(`${MON}/alerts`);
  const { factories } = useMyFactories();
  const factory = q.get("factory");
  const org = useFactoryOrg(factory || null);
  const status = q.get("status") || "ACTIVE";
  const alerts = useResource<PagedResult<MonitoringAlert>>(`/api/monitoring/alerts${qs({
    factoryId: factory, status: status === "ALL" ? null : status, severity: q.get("severity"), alertType: q.get("type"), sourceType: q.get("source"),
    lineId: q.get("line"), search: q.get("search"), sort: q.get("sort") || "severity", page: q.page, pageSize: 25
  })}`);
  const [acting, setActing] = useState<{ alert: MonitoringAlert; action: AlertAction } | null>(null);
  const pending = useRef<ReturnType<typeof setTimeout> | null>(null);
  const hub = useMonitoringHub(factory || null, {
    onAlert: () => {
      if (pending.current) return;
      pending.current = setTimeout(() => { pending.current = null; void alerts.reload(); }, 1000);
    },
    onResync: () => void alerts.reload()
  });
  const rows = alerts.data?.items ?? [];

  return (
    <div className="page" data-testid="alerts-page">
      <PageHeader eyebrow="Central monitoring" title="Alerts"
        description="Raised from monitoring events. Open → acknowledged → escalated → resolved; dismissed with a reason. Alerts inform people; they never change pay, attendance or discipline."
        meta={<LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} />} />
      <MonitoringTabs className="mb-4" />

      <div className="flex flex-wrap items-center gap-3 mb-4">
        {factories && factories.length > 1 && (
          <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null, line: null })}>
            <option value="">All my factories</option>
            {factories.map((f) => <option key={f.id} value={f.id}>{f.code}</option>)}
          </FilterSelect>
        )}
        <FilterSelect label="Status" value={status} onChange={(v) => q.set({ status: v })} testId="alerts-status">
          <option value="ACTIVE">Active</option>
          {["OPEN", "ACKNOWLEDGED", "ESCALATED", "RESOLVED", "DISMISSED"].map((s) => <option key={s} value={s}>{ALERT_STATUS_LABEL[s]}</option>)}
          <option value="ALL">All</option>
        </FilterSelect>
        <FilterSelect label="Severity" value={q.get("severity")} onChange={(v) => q.set({ severity: v || null })}>
          <option value="">All</option>
          {["CRITICAL", "HIGH", "MEDIUM", "LOW"].map((s) => <option key={s} value={s}>{s}</option>)}
        </FilterSelect>
        <FilterSelect label="Type" value={q.get("type")} onChange={(v) => q.set({ type: v || null })}>
          <option value="">All</option>
          {Object.entries(ALERT_TYPE_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </FilterSelect>
        <FilterSelect label="Source" value={q.get("source")} onChange={(v) => q.set({ source: v || null })} testId="alerts-source">
          <option value="">Real and simulated</option>
          <option value="REAL">Real only</option>
          <option value="SIMULATED">Simulated only</option>
        </FilterSelect>
        {factory && (
          <FilterSelect label="Line" value={q.get("line")} onChange={(v) => q.set({ line: v || null })}>
            <option value="">All</option>
            {(org.lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
          </FilterSelect>
        )}
        <FilterSelect label="Sort" value={q.get("sort") || "severity"} onChange={(v) => q.set({ sort: v })}>
          <option value="severity">Most severe</option>
          <option value="newest">Newest</option>
          <option value="oldest">Oldest</option>
          <option value="updated">Recently changed</option>
        </FilterSelect>
        <SearchBox value={q.get("search")} onChange={(v) => q.set({ search: v || null })} placeholder="Title or ALT-number" label="Search alerts" />
      </div>

      {alerts.error && !alerts.data ? (
        <ErrorState retryLabel="Try again" title="Could not load alerts" body={alerts.error.message} onRetry={() => void alerts.reload()} />
      ) : !alerts.data ? (
        <SkeletonRows rows={6} />
      ) : rows.length === 0 ? (
        <EmptyState icon={<Bell size={20} />} title={status === "ACTIVE" ? "No active alerts" : "No alerts match"} body="Alerts appear when cameras or devices go offline, models fail, a restricted zone is entered or a detection needs review." />
      ) : (
        <Panel flush testId="alerts-list">
          <ul className="divide-y divide-border-subtle">
            {rows.map((a) => (
              <li key={a.id}>
                <AlertRow a={a} />
                <div className="px-4 pb-3 -mt-1 pl-11"><AlertActionButtons a={a} onAction={(action) => setActing({ alert: a, action })} /></div>
              </li>
            ))}
          </ul>
          <div className="px-4 py-3 border-t border-border-subtle">
            <Pagination page={alerts.data.page} pageSize={alerts.data.pageSize} total={alerts.data.total} onPage={(p) => q.set({ page: String(p) }, false)} />
          </div>
        </Panel>
      )}
      {rows.some((a) => a.isSimulated) && <Callout tone="warning" className="mt-3">Alerts labelled SIMULATED come from the monitoring simulator, not real equipment or camera AI.</Callout>}

      <AlertActionDialog alert={acting?.alert ?? null} action={acting?.action ?? null} onClose={() => setActing(null)}
        onDone={() => { setActing(null); void alerts.reload(); }} />
    </div>
  );
}
