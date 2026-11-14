"use client";

// Device health overview inside the monitoring room: the heartbeat-based
// health summary (real and SIMULATED counted apart), what needs attention,
// and device / camera status changes as they arrive. The full event log and
// device administration stay in Cameras & devices.

import Link from "next/link";
import { useEffect, useRef, useState } from "react";
import { ArrowRight, Cpu, HeartPulse } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { StatCard } from "@/components/ui/StatCard";
import { FilterSelect } from "@/components/ui/Field";
import { Callout } from "@/components/ui/Callout";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useMyFactories } from "@/components/workers/shared";
import { useQueryState, tableCls } from "@/components/attendance/shared";
import { DeviceHealthBadge, ModeBadge } from "@/components/cameras/shared";
import { EventList, LiveIndicator, MonitoringTabs, mergeEvents } from "@/components/monitoring/shared";
import { useResource } from "@/lib/useResource";
import { useMonitoringHub } from "@/lib/useMonitoringHub";
import { P, type PagedResult } from "@/lib/access";
import { cn } from "@/lib/cn";
import { DEVICE_CATEGORY_LABEL, SIMULATED_DEVICE_NOTE, type DeviceHealthOverview } from "@/lib/cameras";
import { MON, ago, dhaka, qs, type MonitoringEvent } from "@/lib/monitoring";

export default function MonitoringDevicesPage() {
  return (
    <RequirePermission perms={[P.DevicesView]} what="Device health">
      <Devices />
    </RequirePermission>
  );
}

function Devices() {
  const q = useQueryState(`${MON}/devices`);
  const { factories } = useMyFactories();
  const factory = q.get("factory");
  const health = useResource<DeviceHealthOverview>(`/api/devices/health${qs({ factoryId: factory })}`, { pollMs: 30_000 });
  const changes = useResource<PagedResult<MonitoringEvent>>(`/api/monitoring/events${qs({ factoryId: factory, category: "SYSTEM", pageSize: 30 })}`);
  const [feed, setFeed] = useState<MonitoringEvent[]>([]);
  useEffect(() => { if (changes.data) setFeed(changes.data.items); }, [changes.data]);
  const pending = useRef<ReturnType<typeof setTimeout> | null>(null);
  const hub = useMonitoringHub(factory || null, {
    onEvent: (e) => { if (e.category === "SYSTEM") setFeed((cur) => mergeEvents(cur, e, 30)); },
    onDeviceStatus: () => {
      if (pending.current) return;
      pending.current = setTimeout(() => { pending.current = null; void health.reload(); }, 1500);
    },
    onResync: () => { void health.reload(); void changes.reload(); }
  });
  const h = health.data;
  const { th, td } = tableCls;
  const count = (k: string) => h?.byHealth[k] ?? 0;

  return (
    <div className="page" data-testid="monitoring-devices">
      <PageHeader eyebrow="Central monitoring" title="Device health"
        description="Heartbeat-based health of cameras' health devices, RFID readers, sensors and gateways. Simulated devices are counted apart and never shown as physically connected."
        meta={<LiveIndicator state={hub.state} lastMessageAt={hub.lastMessageAt} />}
        actions={
          <div className="flex flex-wrap items-center gap-2">
            {factories && factories.length > 1 && (
              <FilterSelect label="Factory" value={factory} onChange={(v) => q.set({ factory: v || null })}>
                <option value="">All my factories</option>
                {factories.map((f) => <option key={f.id} value={f.id}>{f.code}</option>)}
              </FilterSelect>
            )}
            <Link href={`/app/cameras-devices/health${factory ? `?factory=${factory}` : ""}`} className="text-caption text-[var(--accent)] inline-flex items-center gap-1">Full health log <ArrowRight size={12} /></Link>
          </div>
        } />
      <MonitoringTabs className="mb-4" />

      {health.error && !h ? <ErrorState retryLabel="Try again" title="Could not load device health" body={health.error.message} onRetry={() => void health.reload()} /> : !h ? <SkeletonRows rows={5} /> : (
        <>
          <section className="grid gap-3 grid-cols-2 lg:grid-cols-4 mb-4" aria-label="Device health counts">
            <StatCard label="Online" value={count("Online")} tone={count("Online") > 0 ? "good" : "neutral"} hint={`${h.physicallyConnected} physically connected · ${h.simulatedOnline} simulated`} testId="dev-online" />
            <StatCard label="Offline" value={count("Offline")} tone={count("Offline") > 0 ? "bad" : "neutral"} hint={`${h.heartbeatOverdue} heartbeat overdue`} testId="dev-offline" />
            <StatCard label="Unknown / maintenance" value={count("Unknown") + count("Maintenance")} hint={`${count("Disabled")} disabled`} />
            <StatCard label="Errors (24 h)" value={h.errorsLast24Hours} tone={h.errorsLast24Hours > 0 ? "warn" : "neutral"} hint={`${h.real} real · ${h.simulated} simulated devices`} />
          </section>
          {h.simulated > 0 && <Callout tone="warning" className="mb-4">{SIMULATED_DEVICE_NOTE}</Callout>}

          <div className="grid gap-4 xl:grid-cols-5">
            <Panel className="xl:col-span-3" title="Needs attention" subtitle="Offline, overdue or reporting errors" icon={<HeartPulse size={15} />} flush testId="dev-attention">
              {h.needsAttention.length === 0 ? <p className="px-4 py-8 text-center text-caption text-fg-tertiary">All clear: no device is offline, overdue or reporting errors.</p> : (
                <div className="overflow-x-auto">
                  <table className="w-full min-w-[36rem]">
                    <thead><tr><th className={th}>Device</th><th className={th}>Category</th><th className={th}>Health</th><th className={th}>Mode</th><th className={th}>Last seen</th></tr></thead>
                    <tbody>
                      {h.needsAttention.map((d) => (
                        <tr key={d.id}>
                          <td className={td}><Link className="font-mono text-[var(--accent)]" href={`/app/cameras-devices/devices/${d.id}`}>{d.code}</Link><p className="text-caption text-fg-tertiary">{d.name}</p></td>
                          <td className={cn(td, "text-caption")}>{DEVICE_CATEGORY_LABEL[d.category] ?? d.category}</td>
                          <td className={td}><DeviceHealthBadge d={d} />{d.lastErrorMessage && <p className="text-caption text-[var(--risk-high)] mt-0.5">{d.lastErrorMessage}</p>}</td>
                          <td className={td}><ModeBadge simulated={d.isSimulated} /></td>
                          <td className={cn(td, "text-caption")} title={dhaka(d.lastSeenAt)}>{ago(d.lastSeenAt)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </Panel>
            <Panel className="xl:col-span-2" title="Status changes" subtitle="Cameras, devices and models · live · Dhaka time" icon={<Cpu size={15} />} flush testId="dev-changes">
              <div className="max-h-[26rem] overflow-y-auto">
                <EventList rows={feed} dense empty={<p className="px-4 py-6 text-caption text-fg-tertiary">No status changes yet.</p>} />
              </div>
            </Panel>
          </div>
          <p className="text-caption text-fg-tertiary mt-3">Updated {dhaka(h.asOf)} (Dhaka).</p>
        </>
      )}
    </div>
  );
}
