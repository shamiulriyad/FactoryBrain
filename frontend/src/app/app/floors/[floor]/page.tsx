"use client";

// /app/floors/[floor]: one production floor (by id) with its live status,
// lines, machines, people and recent alerts, from the floor-status rollup.

import Link from "next/link";
import { useEffect } from "react";
import { useParams } from "next/navigation";
import { AlertTriangle, Cctv, Cog, Factory, Layers, RadioTower, RefreshCw, Users } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Fact, MachineStates, SEVERITY_TONE, StatusBadge } from "@/components/floors/FloorViews";
import { refreshFloorStatus, useFloorStatus } from "@/lib/useFloorStatus";
import { formatRelative } from "@/lib/format";
import { useT } from "@/lib/useT";
import { P } from "@/lib/access";

export default function FloorPageRoute() {
  return (
    <RequirePermission perms={[P.ProductionView, P.MonitoringView]} what="Production floor">
      <FloorPage />
    </RequirePermission>
  );
}

function FloorPage() {
  const params = useParams<{ floor: string }>();
  const { locale } = useT();
  const { data, error, loaded, floors } = useFloorStatus();
  useEffect(() => { void refreshFloorStatus(); }, []);
  const floor = floors.find((f) => f.id === params?.floor || f.code.toLowerCase() === params?.floor?.toLowerCase());

  if (!loaded) return <div className="page"><SkeletonRows rows={5} rowHeight="h-20" /></div>;
  if (error && !data) {
    return <div className="page"><ErrorState title="Couldn't load the floor" body={error} retryLabel="Try again" onRetry={() => void refreshFloorStatus()} /></div>;
  }
  if (!floor) {
    return (
      <div className="page page-narrow">
        <EmptyState icon={<Layers size={18} />} title="Floor not found" body="It may belong to another factory or have been removed. Open the floor map to pick one." />
        <p className="text-center"><Link className="text-caption text-[var(--accent)]" href="/app/floor-map">Open the floor map</Link></p>
      </div>
    );
  }

  return (
    <div className="page" data-testid="floor-page">
      <PageHeader
        eyebrow={<Link href="/app/floor-map" className="hover:underline">← Floor map · {data?.factoryCode}</Link>}
        title={`${floor.code} · ${floor.name}`}
        description={floor.reasons.length > 0 ? floor.reasons.join(" · ") : "Nothing on this floor needs attention right now."}
        meta={<StatusBadge status={floor.status} />}
        actions={<Button variant="secondary" size="sm" aria-label="Refresh" onClick={() => void refreshFloorStatus()}><RefreshCw size={13} /></Button>}
      />

      <div className="mt-4 grid gap-2 grid-cols-2 md:grid-cols-3 xl:grid-cols-6 text-caption">
        <Fact icon={<Users size={13} />} label="Checked in / workers" value={`${floor.checkedIn} / ${floor.workers}`} />
        <Fact icon={<Factory size={13} />} label="Lines · workstations" value={`${floor.lines.length} · ${floor.workstations}`} />
        <Fact icon={<Cog size={13} />} label="Machines" value={floor.machines} />
        <Fact icon={<AlertTriangle size={13} />} label="Open alerts (high)" value={`${floor.activeAlerts} (${floor.highAlerts})`} warn={floor.highAlerts > 0} />
        <Fact icon={<RadioTower size={13} />} label="Devices online / offline" value={`${floor.devicesOnline} / ${floor.devicesOffline}`} warn={floor.devicesOffline > 0} />
        <Fact icon={<Cctv size={13} />} label="Cameras" value={floor.cameras} />
      </div>

      <div className="mt-4 grid gap-4 lg:grid-cols-2">
        <Panel title="Lines" subtitle="Lines whose workstations stand on this floor" icon={<Factory size={15} />} testId="floor-lines">
          {floor.lines.length === 0 ? (
            <p className="p-4 text-caption text-fg-tertiary">No line has workstations on this floor yet. Place workstations under <Link className="text-[var(--accent)]" href="/app/assignments/workstations">Workstations</Link>.</p>
          ) : (
            <ul className="divide-y divide-border-subtle">
              {floor.lines.map((l) => (
                <li key={l.id} className="flex flex-wrap items-center gap-3 px-4 py-2.5">
                  <Link className="flex-1 text-body text-fg-primary hover:text-[var(--accent)]" href={`/app/production/lines/${encodeURIComponent(l.id)}`}>{l.name}</Link>
                  <span className="text-caption text-fg-tertiary tabular-nums">{l.workstations} workstations · {l.machines} machines</span>
                  {l.machinesDown > 0 && <Badge tone="danger">{l.machinesDown} down</Badge>}
                  {l.activeAlerts > 0 && <Badge tone="warning">{l.activeAlerts} alert{l.activeAlerts === 1 ? "" : "s"}</Badge>}
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <Panel title="Machines" subtitle="Latest derived state of each machine (real readings only)" icon={<Cog size={15} />}
          right={<Link className="text-caption text-[var(--accent)]" href="/app/monitoring/machines">All machines</Link>}>
          <div className="p-4"><MachineStates states={floor.machineStates} /></div>
        </Panel>

        <Panel className="lg:col-span-2" title="Recent open alerts" icon={<AlertTriangle size={15} />}
          right={<Link className="text-caption text-[var(--accent)]" href="/app/monitoring/alerts">All alerts</Link>} testId="floor-alerts">
          {floor.recentAlerts.length === 0 ? (
            <p className="p-4 text-caption text-fg-tertiary">No open alerts on this floor.</p>
          ) : (
            <ul className="divide-y divide-border-subtle">
              {floor.recentAlerts.map((a) => (
                <li key={a.id} className="flex flex-wrap items-center gap-3 px-4 py-2.5">
                  <Badge tone={SEVERITY_TONE[a.severity] ?? "neutral"}>{a.severity.toLowerCase()}</Badge>
                  <Link className="flex-1 min-w-0 truncate text-body text-fg-primary hover:text-[var(--accent)]" href={`/app/monitoring/alerts/${a.id}`}>#{a.number} {a.title}</Link>
                  <span className="text-caption text-fg-tertiary">{a.status.toLowerCase()} · {formatRelative(a.lastOccurredAt, locale)}</span>
                </li>
              ))}
            </ul>
          )}
          {floor.openExceptions > 0 && (
            <p className="px-4 pb-3 text-caption text-fg-tertiary">
              Also {floor.openExceptions} open production exception{floor.openExceptions === 1 ? "" : "s"} on these lines — see <Link className="text-[var(--accent)]" href="/app/planning/exceptions">Exceptions</Link>.
            </p>
          )}
        </Panel>
      </div>
    </div>
  );
}
