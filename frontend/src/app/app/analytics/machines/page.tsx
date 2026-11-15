"use client";

// Machine performance from the derived machine state history. Uptime =
// running ÷ confirmed-state time; offline / unknown time is a data gap, not
// downtime; maintenance is excluded. RUNNING doesn't prove anyone operated it.

import Link from "next/link";
import { Activity, Cpu, Rows3 } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { AN, AnalyticsFilterBar, AnalyticsTabs, DataTable, KpiGrid, ScopeLine, TrendChart, hrs, pct, useAnalyticsFilters, useDefinitions } from "@/components/analytics/shared";
import { useResource } from "@/lib/useResource";
import { P } from "@/lib/access";
import { MON } from "@/lib/monitoring";
import type { MachineAnalytics } from "@/lib/analytics";

export default function MachineAnalyticsPage() {
  return (
    <RequirePermission perms={[P.MonitoringView, P.DevicesView, P.ProductionView]} what="Machine performance">
      <Machines />
    </RequirePermission>
  );
}

function Machines() {
  const { f, set, query, factories } = useAnalyticsFilters(`${AN}/machines`);
  const defs = useDefinitions();
  const r = useResource<MachineAnalytics>(`/api/analytics/machines${query()}`, { pollMs: 60_000 });
  const d = r.data;
  return (
    <div className="page" data-testid="analytics-machines">
      <PageHeader eyebrow="Factory analytics" title="Machine performance"
        description="Running, idle, stopped, error, maintenance, offline and unknown time from machine sensors. Missing data is never counted as downtime." />
      <AnalyticsTabs />
      <AnalyticsFilterBar f={f} set={set} factories={factories} show={{ machine: true }} />
      {r.error && !d ? <ErrorState title="Could not load machine analytics" body={r.error.message} retryLabel="Try again" onRetry={() => void r.reload()} />
        : !d ? <SkeletonRows rows={8} />
        : (
          <>
            <ScopeLine scope={d.scope} warnings={d.warnings} />
            <KpiGrid kpis={d.kpis} defs={defs} />
            <div className="grid gap-4 lg:grid-cols-2 mb-4">
              <Panel title="Daily running time" subtitle="Hours running per day (all monitored machines)" icon={<Activity size={15} />}>
                <div className="p-4"><TrendChart points={d.daily.map((x) => ({ key: x.date, label: x.date.slice(5), start: x.date, value: Math.round(x.runningHours * 10) / 10 }))} unit=" h" /></div>
              </Panel>
              <Panel title="Daily confirmed downtime" subtitle="Stopped + error hours per day; data gaps shown in the table" icon={<Activity size={15} />}>
                <div className="p-4"><TrendChart points={d.daily.map((x) => ({ key: x.date, label: x.date.slice(5), start: x.date, value: Math.round(x.downtimeHours * 10) / 10 }))} unit=" h" /></div>
              </Panel>
            </div>
            <Panel className="mb-4" title="Line availability" icon={<Rows3 size={15} />} testId="machines-lines">
              <DataTable columns={["Line", "Machines", "Monitored", "Running", "Downtime", "Data gap", "Uptime", "Downtime %", "Coverage", "Errors"]}
                empty="No machine is placed on a line in this scope."
                rows={d.lines.map((l) => [<button key={l.lineId} type="button" className="text-[var(--accent)]" onClick={() => set({ line: l.lineId })}>{l.lineName}</button>,
                  l.machines, l.monitored, hrs(l.runningHours), hrs(l.downtimeHours), hrs(l.gapHours), pct(l.uptimePct), pct(l.downtimePct), pct(l.coveragePct), l.errors])} />
            </Panel>
            <Panel className="mb-4" title="Machines" icon={<Cpu size={15} />} testId="machines-table">
              <DataTable columns={["Machine", "Line", "Running", "Idle", "Stopped", "Error", "Maint.", "Offline", "Unknown", "Uptime", "Errors", "Pieces"]}
                empty="No machine in this scope."
                rows={d.machines.map((m) => [<Link key={m.machineId} className="text-[var(--accent)]" href={`${MON}/machines/${m.machineId}`}>{m.code}</Link>, m.line?.name,
                  ...(m.monitored ? [hrs(m.runningHours), hrs(m.idleHours), hrs(m.stoppedHours), hrs(m.errorHours), hrs(m.maintenanceHours), hrs(m.offlineHours),
                    hrs(m.unknownHours), pct(m.uptimePct)] : [<Badge key="n">No sensor</Badge>, "", "", "", "", "", "", ""]), m.errors, m.pieces.toLocaleString("en-US")])} />
            </Panel>
            <Panel title="Daily detail">
              <DataTable columns={["Date", "Running", "Idle", "Downtime", "Data gap", "Uptime", "Errors"]}
                rows={d.daily.map((x) => [x.date, hrs(x.runningHours), hrs(x.idleHours), hrs(x.downtimeHours), hrs(x.gapHours), pct(x.uptimePct), x.errors])} />
            </Panel>
          </>
        )}
    </div>
  );
}
