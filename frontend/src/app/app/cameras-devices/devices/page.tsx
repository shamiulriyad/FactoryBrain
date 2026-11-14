"use client";

// Device directory: RFID readers, sensors, counters, gateways and camera
// health devices, with search, filters (category, health, real / simulated,
// status), sorting and pagination. Simulated devices are always marked.

import Link from "next/link";
import { useMemo } from "react";
import { Cpu, Plus, RefreshCw } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Pagination } from "@/components/access/shared";
import { useMyFactories } from "@/components/workers/shared";
import { SearchBox, tableCls, useQueryState } from "@/components/attendance/shared";
import { BASE, CameraDeviceTabs, DeviceHealthBadge, ModeBadge } from "@/components/cameras/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDateTime } from "@/lib/format";
import { P, type PagedResult } from "@/lib/access";
import { DEVICE_CATEGORIES, DEVICE_CATEGORY_LABEL, DEVICE_STATUSES, HEALTH_STATUSES, type Device } from "@/lib/cameras";

const PAGE_SIZE = 25;
const SORTS = [
  { id: "code", label: "Device ID" },
  { id: "name", label: "Name" },
  { id: "category", label: "Category" },
  { id: "-lastSeen", label: "Last seen" },
  { id: "health", label: "Status" }
];

export default function DeviceDirectoryPage() {
  return (
    <RequirePermission perms={[P.DevicesView]} what="Device directory">
      <Directory />
    </RequirePermission>
  );
}

function Directory() {
  const q = useQueryState(`${BASE}/devices`);
  const { factories } = useMyFactories();
  const canManage = useCan(P.DevicesManage);
  const f = { q: q.get("q"), factory: q.get("factory"), category: q.get("category"), health: q.get("health"), simulated: q.get("simulated"), status: q.get("status"), sort: q.get("sort") || "code" };
  const path = useMemo(() => {
    const p = new URLSearchParams();
    if (f.q) p.set("search", f.q);
    if (f.factory) p.set("factoryId", f.factory);
    if (f.category) p.set("category", f.category);
    if (f.health) p.set("health", f.health);
    if (f.simulated) p.set("simulated", f.simulated);
    if (f.status) p.set("status", f.status);
    p.set("sort", f.sort);
    p.set("page", String(q.page));
    p.set("pageSize", String(PAGE_SIZE));
    return `/api/devices?${p}`;
  }, [f.q, f.factory, f.category, f.health, f.simulated, f.status, f.sort, q.page]); // eslint-disable-line react-hooks/exhaustive-deps
  const list = useResource<PagedResult<Device>>(path, { pollMs: 30_000 });
  const filtered = Object.entries(f).some(([k, v]) => k !== "sort" && !!v);
  const { th, td } = tableCls;

  return (
    <div className="page" data-testid="device-directory">
      <PageHeader title="Device directory" description="Devices are registered before (or without) physical hardware. Health comes from heartbeats; a device with none shows as never connected."
        actions={canManage ? <Link href={`${BASE}/devices/new`}><Button size="sm" variant="primary" iconLeft={<Plus size={13} />} data-testid="device-new">Register device</Button></Link> : undefined} />
      <CameraDeviceTabs className="mt-4" />
      <Panel className="mt-4" flush title="Devices" icon={<Cpu size={14} />}
        subtitle={list.data ? `${list.data.total} device${list.data.total === 1 ? "" : "s"}` : undefined}
        right={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void list.reload()}><RefreshCw size={13} className={cn(list.refreshing && "animate-spin")} /></Button>}>
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <SearchBox value={f.q} onChange={(v) => q.set({ q: v || null })} placeholder="Device ID, name or serial" label="Search devices" testId="device-search" />
          {(factories?.length ?? 0) > 1 && (
            <FilterSelect label="Factory" value={f.factory} onChange={(v) => q.set({ factory: v || null })}>
              <option value="">All factories</option>
              {factories!.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
            </FilterSelect>
          )}
          <FilterSelect label="Category" value={f.category} onChange={(v) => q.set({ category: v || null })}>
            <option value="">Any</option>
            {DEVICE_CATEGORIES.map((c) => <option key={c} value={c}>{DEVICE_CATEGORY_LABEL[c]}</option>)}
          </FilterSelect>
          <FilterSelect label="Health" value={f.health} onChange={(v) => q.set({ health: v || null })} testId="device-health-filter">
            <option value="">Any</option>
            {HEALTH_STATUSES.map((h) => <option key={h} value={h}>{h}</option>)}
          </FilterSelect>
          <FilterSelect label="Mode" value={f.simulated} onChange={(v) => q.set({ simulated: v || null })}>
            <option value="">Real and simulated</option>
            <option value="false">Real only</option>
            <option value="true">Simulated only</option>
          </FilterSelect>
          <FilterSelect label="Status" value={f.status} onChange={(v) => q.set({ status: v || null })}>
            <option value="">Any</option>
            {DEVICE_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
          </FilterSelect>
          <FilterSelect label="Sort" value={f.sort} onChange={(v) => q.set({ sort: v === "code" ? null : v }, false)}>
            {SORTS.map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
          </FilterSelect>
          {filtered && <Button size="sm" variant="ghost" onClick={() => q.set({ q: null, factory: null, category: null, health: null, simulated: null, status: null })}>Clear filters</Button>}
        </div>
        {list.loading ? (
          <div className="p-4"><SkeletonRows rows={6} rowHeight="h-11" /></div>
        ) : list.error && !list.data ? (
          <ErrorState className="m-4" title="Couldn't load devices" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
        ) : (list.data?.items.length ?? 0) === 0 ? (
          <EmptyState className="m-4" icon={<Cpu size={18} />} title={filtered ? "No devices match" : "No devices yet"}
            body={filtered ? "Change or clear the filters." : canManage ? "Register a device. Use a simulated device to demonstrate health monitoring without hardware." : "Devices appear here once an administrator registers them."} />
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full min-w-[860px]" data-testid="device-table">
                <thead className="border-b border-border-subtle">
                  <tr>
                    <th className={th}>Device</th>
                    <th className={th}>Category</th>
                    <th className={th}>Mode</th>
                    <th className={th}>Placement</th>
                    <th className={th}>Health</th>
                    <th className={th}>Last seen</th>
                    <th className={th}>Last error</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border-subtle">
                  {list.data!.items.map((d) => (
                    <tr key={d.id} className="hover:bg-surface-2">
                      <td className={td}>
                        <Link href={`${BASE}/devices/${d.id}`} className="font-mono text-body text-fg-primary hover:underline" data-testid={`device-row-${d.code}`}>{d.code}</Link>
                        <div className="text-fg-secondary">{d.name}</div>
                      </td>
                      <td className={td}>{DEVICE_CATEGORY_LABEL[d.category] ?? d.category}</td>
                      <td className={td}><ModeBadge simulated={d.isSimulated} /></td>
                      <td className={td}>
                        <div>{d.factoryCode}{d.line ? ` · ${d.line.name}` : ""}</div>
                        <div className="text-fg-tertiary font-mono">{[d.workstation?.code, d.machine?.code, d.camera?.code, d.attendanceReader?.code].filter(Boolean).join(" · ") || ""}</div>
                      </td>
                      <td className={td}><DeviceHealthBadge d={d} />{d.status !== "Active" && d.statusReason && <div className="mt-0.5 text-fg-tertiary">{d.statusReason}</div>}</td>
                      <td className={cn(td, "whitespace-nowrap")}>{d.lastSeenAt ? formatDateTime(d.lastSeenAt) : <span className="text-fg-tertiary">Never</span>}</td>
                      <td className={td}>{d.lastErrorAt ? <span title={d.lastErrorMessage ?? undefined}><span className="font-mono">{d.lastErrorCode ?? "Error"}</span> <span className="text-fg-tertiary">{formatDateTime(d.lastErrorAt)}</span></span> : <span className="text-fg-tertiary">—</span>}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={list.data!.page} pageSize={list.data!.pageSize} total={list.data!.total} onPage={(p) => q.set({ page: String(p) }, false)} />
          </>
        )}
      </Panel>
    </div>
  );
}
