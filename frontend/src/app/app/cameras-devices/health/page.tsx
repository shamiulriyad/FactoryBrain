"use client";

// Device health dashboard: health by status and category, real vs simulated,
// devices needing attention, and the event log (connections, missed
// heartbeats, errors, status changes). Health is heartbeat-based: a device
// with no message inside its offline window counts as offline at once, and
// the health monitor records the transition every minute (or on demand).

import Link from "next/link";
import { useMemo, useState } from "react";
import { Activity, AlertTriangle, FlaskConical, HeartPulse, RefreshCw, Stethoscope, Wifi } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { StatCard } from "@/components/ui/StatCard";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { Pagination } from "@/components/access/shared";
import { useMyFactories } from "@/components/workers/shared";
import { tableCls, useQueryState } from "@/components/attendance/shared";
import { BASE, CameraDeviceTabs, DeviceEventTable, DeviceHealthBadge, ModeBadge } from "@/components/cameras/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { apiJson, errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDateTime } from "@/lib/format";
import { P, type PagedResult } from "@/lib/access";
import { DEVICE_CATEGORY_LABEL, EVENT_SOURCE_LABEL, EVENT_TYPE_LABEL, SIMULATED_DEVICE_NOTE, type DeviceEvent, type DeviceHealthOverview } from "@/lib/cameras";

export default function DeviceHealthPage() {
  return (
    <RequirePermission perms={[P.DevicesView]} what="Device health">
      <Health />
    </RequirePermission>
  );
}

function Health() {
  const q = useQueryState(`${BASE}/health`);
  const toast = useToast();
  const { factories } = useMyFactories();
  const factory = q.get("factory");
  const canManage = useCan(P.DevicesManage);
  const canLogs = useCan(P.DevicesLogsView, P.DevicesManage);
  const health = useResource<DeviceHealthOverview>(`/api/devices/health${factory ? `?factoryId=${factory}` : ""}`, { pollMs: 30_000 });
  const [type, setType] = useState(q.get("type"));
  const [source, setSource] = useState("");
  const [simulated, setSimulated] = useState("");
  const [page, setPage] = useState(1);
  const [checking, setChecking] = useState(false);
  const eventsPath = useMemo(() => {
    const p = new URLSearchParams({ page: String(page), pageSize: "25" });
    if (factory) p.set("factoryId", factory);
    if (type) p.set("type", type);
    if (source) p.set("source", source);
    if (simulated) p.set("simulated", simulated);
    return `/api/devices/events?${p}`;
  }, [factory, type, source, simulated, page]);
  const events = useResource<PagedResult<DeviceEvent>>(canLogs ? eventsPath : null, { pollMs: 30_000 });
  const h = health.data;
  const { th, td } = tableCls;

  async function check() {
    setChecking(true);
    try {
      const r = await apiJson<{ checked: number; markedOffline: number }>("/api/devices/health/check", { method: "POST", body: {} });
      toast.push({ tone: "success", title: r.markedOffline > 0 ? `${r.markedOffline} device(s) marked offline` : "No device needed marking offline" });
      void health.reload();
      void events.reload();
    } catch (e) {
      toast.push({ tone: "error", title: "Health check failed", description: errorMessage(e) });
    } finally {
      setChecking(false);
    }
  }

  return (
    <div className="page" data-testid="device-health-page">
      <PageHeader title="Device health" description="Heartbeat-based health of every device in your scope. Real hardware and simulated devices are counted separately."
        actions={canManage ? <Button size="sm" variant="secondary" iconLeft={<Stethoscope size={13} />} onClick={() => void check()} disabled={checking} data-testid="health-check">{checking ? "Checking…" : "Run health check"}</Button> : undefined} />
      <CameraDeviceTabs className="mt-4" />
      <div className="mt-4 flex flex-wrap items-center gap-3">
        {(factories?.length ?? 0) > 1 && (
          <FilterSelect label="Factory" value={factory} onChange={(v) => { q.set({ factory: v || null }); setPage(1); }}>
            <option value="">All factories</option>
            {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        )}
        {h && <span className="text-caption text-fg-tertiary">As of {formatDateTime(h.asOf)} · refreshes every 30 s</span>}
        <Button size="sm" variant="secondary" className="ml-auto" aria-label="Refresh" onClick={() => { void health.reload(); void events.reload(); }}>
          <RefreshCw size={13} className={cn(health.refreshing && "animate-spin")} />
        </Button>
      </div>
      {health.error && !h && <ErrorState className="mt-4" title="Couldn't load device health" body={errorMessage(health.error)} retryLabel="Try again" onRetry={() => void health.reload()} />}

      <div className="mt-4 grid gap-3 grid-cols-2 md:grid-cols-3 xl:grid-cols-6" data-testid="health-stats">
        <StatCard label="Online" value={h?.byHealth.Online ?? "—"} tone="good" icon={<Wifi size={14} />} hint={h ? `${h.physicallyConnected} physical · ${h.simulatedOnline} simulated` : undefined} loading={health.loading} href={`${BASE}/devices?health=Online`} testId="health-online" />
        <StatCard label="Offline" value={h?.byHealth.Offline ?? "—"} tone={h && h.byHealth.Offline > 0 ? "bad" : "neutral"} loading={health.loading} href={`${BASE}/devices?health=Offline`} testId="health-offline" />
        <StatCard label="Unknown" value={h?.byHealth.Unknown ?? "—"} hint="Never heard from" loading={health.loading} href={`${BASE}/devices?health=Unknown`} />
        <StatCard label="Maintenance" value={h?.byHealth.Maintenance ?? "—"} tone={h && h.byHealth.Maintenance > 0 ? "warn" : "neutral"} loading={health.loading} href={`${BASE}/devices?health=Maintenance`} />
        <StatCard label="Disabled" value={h?.byHealth.Disabled ?? "—"} loading={health.loading} href={`${BASE}/devices?health=Disabled`} />
        <StatCard label="Errors (24 h)" value={h?.errorsLast24Hours ?? "—"} tone={h && h.errorsLast24Hours > 0 ? "warn" : "neutral"} icon={<AlertTriangle size={14} />} loading={health.loading} />
      </div>
      {h && h.simulated > 0 && <Callout className="mt-4" tone="warning" title={`${h.simulated} simulated device${h.simulated === 1 ? "" : "s"}`}>{SIMULATED_DEVICE_NOTE}</Callout>}

      <div className="mt-6 grid gap-6 xl:grid-cols-2">
        <Panel flush title="By category" icon={<HeartPulse size={14} />}>
          {health.loading ? <div className="p-4"><SkeletonRows rows={3} /></div> : (h?.byCategory.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<HeartPulse size={18} />} title="No devices" body="Register devices to see their health here." /> : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[480px]">
                <thead className="border-b border-border-subtle"><tr>{["Category", "Total", "Online", "Offline", "Unknown", "Maint.", "Disabled"].map((x) => <th key={x} className={th}>{x}</th>)}</tr></thead>
                <tbody className="divide-y divide-border-subtle">
                  {h!.byCategory.map((c) => (
                    <tr key={c.category}>
                      <td className={td}><Link href={`${BASE}/devices?category=${c.category}`} className="hover:underline">{DEVICE_CATEGORY_LABEL[c.category] ?? c.category}</Link></td>
                      <td className={cn(td, "tabular-nums")}>{c.total}</td>
                      <td className={cn(td, "tabular-nums text-[var(--risk-low)]")}>{c.online}</td>
                      <td className={cn(td, "tabular-nums", c.offline > 0 && "text-[var(--risk-high)]")}>{c.offline}</td>
                      <td className={cn(td, "tabular-nums")}>{c.unknown}</td>
                      <td className={cn(td, "tabular-nums")}>{c.maintenance}</td>
                      <td className={cn(td, "tabular-nums")}>{c.disabled}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Panel>
        <Panel flush title="Needs attention" icon={<AlertTriangle size={14} />} subtitle="Offline, heartbeat overdue, or an error in the last 24 hours">
          {health.loading ? <div className="p-4"><SkeletonRows rows={3} /></div> : (h?.needsAttention.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<Wifi size={18} />} title="All clear" body="No device is offline, overdue or reporting errors." /> : (
            <ul className="divide-y divide-border-subtle" data-testid="health-attention">
              {h!.needsAttention.map((d) => (
                <li key={d.id} className="px-4 py-2.5 flex flex-wrap items-center gap-2 text-caption">
                  <Link href={`${BASE}/devices/${d.id}`} className="font-mono text-body hover:underline">{d.code}</Link>
                  <ModeBadge simulated={d.isSimulated} />
                  <span className="text-fg-tertiary">last seen {d.lastSeenAt ? formatDateTime(d.lastSeenAt) : "never"}</span>
                  <span className="ml-auto"><DeviceHealthBadge d={d} /></span>
                  {d.lastErrorMessage && <span className="w-full text-fg-secondary"><span className="font-mono">{d.lastErrorCode}</span> {d.lastErrorMessage}</span>}
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>

      {canLogs && (
        <Panel className="mt-6" flush title="Event log" icon={<Activity size={14} />} subtitle={events.data ? `${events.data.total} event${events.data.total === 1 ? "" : "s"}` : undefined}>
          <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
            <FilterSelect label="Event" value={type} onChange={(v) => { setType(v); setPage(1); }}>
              <option value="">All</option>
              {Object.entries(EVENT_TYPE_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </FilterSelect>
            <FilterSelect label="Source" value={source} onChange={(v) => { setSource(v); setPage(1); }}>
              <option value="">All</option>
              {Object.entries(EVENT_SOURCE_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </FilterSelect>
            <FilterSelect label="Devices" value={simulated} onChange={(v) => { setSimulated(v); setPage(1); }}>
              <option value="">Real and simulated</option>
              <option value="false">Real only</option>
              <option value="true">Simulated only</option>
            </FilterSelect>
            <span className="ml-auto inline-flex items-center gap-1 text-caption text-fg-tertiary"><FlaskConical size={12} /> Simulated events are always labelled</span>
          </div>
          {events.loading ? <div className="p-4"><SkeletonRows rows={5} /></div>
            : events.error ? <ErrorState className="m-4" title="Couldn't load events" body={errorMessage(events.error)} retryLabel="Try again" onRetry={() => void events.reload()} />
            : (events.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<Activity size={18} />} title="No events" body="Device events appear here as they happen." />
            : <><DeviceEventTable rows={events.data!.items} testId="health-events" /><Pagination page={page} pageSize={25} total={events.data!.total} onPage={setPage} /></>}
        </Panel>
      )}
    </div>
  );
}
