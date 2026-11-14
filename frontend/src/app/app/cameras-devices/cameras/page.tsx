"use client";

// Camera directory: every camera in scope with search, filters (factory,
// line, status, type, source, health), sorting and pagination. Supervisors
// see the cameras covering their lines; nobody sees camera credentials.

import Link from "next/link";
import { useMemo } from "react";
import { Cctv, Plus, RefreshCw } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { FilterSelect } from "@/components/ui/Field";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { Pagination } from "@/components/access/shared";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { SearchBox, tableCls, useQueryState } from "@/components/attendance/shared";
import { BASE, CameraDeviceTabs, CameraHealthBadge, CameraStatusBadge, SourceTypeBadge } from "@/components/cameras/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDateTime } from "@/lib/format";
import { P, type PagedResult } from "@/lib/access";
import { CAMERA_STATUSES, CAMERA_TYPES, CAMERA_TYPE_LABEL, HEALTH_STATUSES, SOURCE_TYPES, SOURCE_TYPE_LABEL, type Camera } from "@/lib/cameras";

const PAGE_SIZE = 25;
const SORTS = [
  { id: "code", label: "Camera ID" },
  { id: "name", label: "Name" },
  { id: "status", label: "Status" },
  { id: "type", label: "Type" },
  { id: "-updated", label: "Recently changed" }
];

export default function CameraDirectoryPage() {
  return (
    <RequirePermission perms={[P.CamerasView]} what="Camera directory">
      <Directory />
    </RequirePermission>
  );
}

function Directory() {
  const path0 = `${BASE}/cameras`;
  const q = useQueryState(path0);
  const { factories } = useMyFactories();
  const canManage = useCan(P.CamerasManage);
  const f = { q: q.get("q"), factory: q.get("factory"), line: q.get("line"), status: q.get("status"), type: q.get("type"), source: q.get("source"), health: q.get("health"), sort: q.get("sort") || "code" };
  const factoryId = f.factory || (factories?.length === 1 ? factories[0].id : "");
  const { lines } = useFactoryOrg(factoryId || null);

  const path = useMemo(() => {
    const p = new URLSearchParams();
    if (f.q) p.set("search", f.q);
    if (f.factory) p.set("factoryId", f.factory);
    if (f.line) p.set("lineId", f.line);
    if (f.status) p.set("status", f.status);
    if (f.type) p.set("type", f.type);
    if (f.source) p.set("source", f.source);
    if (f.health) p.set("health", f.health);
    p.set("sort", f.sort);
    p.set("page", String(q.page));
    p.set("pageSize", String(PAGE_SIZE));
    return `/api/cameras?${p}`;
  }, [f.q, f.factory, f.line, f.status, f.type, f.source, f.health, f.sort, q.page]); // eslint-disable-line react-hooks/exhaustive-deps
  const list = useResource<PagedResult<Camera>>(path);
  const filtered = Object.entries(f).some(([k, v]) => k !== "sort" && !!v);
  const { th, td } = tableCls;

  return (
    <div className="page" data-testid="camera-directory">
      <PageHeader title="Camera directory" description="Cameras registered in your factories, where they are and what they cover."
        actions={canManage ? <Link href={`${BASE}/cameras/new`}><Button size="sm" variant="primary" iconLeft={<Plus size={13} />} data-testid="camera-new">Register camera</Button></Link> : undefined} />
      <CameraDeviceTabs className="mt-4" />
      <Panel className="mt-4" flush title="Cameras" icon={<Cctv size={14} />}
        subtitle={list.data ? `${list.data.total} camera${list.data.total === 1 ? "" : "s"}` : undefined}
        right={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void list.reload()}><RefreshCw size={13} className={cn(list.refreshing && "animate-spin")} /></Button>}>
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <SearchBox value={f.q} onChange={(v) => q.set({ q: v || null })} placeholder="Camera ID, name or location" label="Search cameras" testId="camera-search" />
          {(factories?.length ?? 0) > 1 && (
            <FilterSelect label="Factory" value={f.factory} onChange={(v) => q.set({ factory: v || null, line: null })}>
              <option value="">All factories</option>
              {factories!.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
            </FilterSelect>
          )}
          {factoryId && (
            <FilterSelect label="Line" value={f.line} onChange={(v) => q.set({ line: v || null })}>
              <option value="">All</option>
              {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </FilterSelect>
          )}
          <FilterSelect label="Status" value={f.status} onChange={(v) => q.set({ status: v || null })} testId="camera-status-filter">
            <option value="">Any</option>
            {CAMERA_STATUSES.map((s) => <option key={s} value={s}>{s}</option>)}
          </FilterSelect>
          <FilterSelect label="Type" value={f.type} onChange={(v) => q.set({ type: v || null })}>
            <option value="">Any</option>
            {CAMERA_TYPES.map((t) => <option key={t} value={t}>{CAMERA_TYPE_LABEL[t]}</option>)}
          </FilterSelect>
          <FilterSelect label="Source" value={f.source} onChange={(v) => q.set({ source: v || null })}>
            <option value="">Any</option>
            {SOURCE_TYPES.map((t) => <option key={t} value={t}>{SOURCE_TYPE_LABEL[t]}</option>)}
          </FilterSelect>
          <FilterSelect label="Health" value={f.health} onChange={(v) => q.set({ health: v || null })}>
            <option value="">Any</option>
            {HEALTH_STATUSES.map((h) => <option key={h} value={h}>{h}</option>)}
            <option value="None">No health device</option>
          </FilterSelect>
          <FilterSelect label="Sort" value={f.sort} onChange={(v) => q.set({ sort: v === "code" ? null : v }, false)}>
            {SORTS.map((s) => <option key={s.id} value={s.id}>{s.label}</option>)}
          </FilterSelect>
          {filtered && <Button size="sm" variant="ghost" onClick={() => q.set({ q: null, factory: null, line: null, status: null, type: null, source: null, health: null })}>Clear filters</Button>}
        </div>
        {list.loading ? (
          <div className="p-4"><SkeletonRows rows={6} rowHeight="h-11" /></div>
        ) : list.error && !list.data ? (
          <ErrorState className="m-4" title="Couldn't load cameras" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
        ) : (list.data?.items.length ?? 0) === 0 ? (
          <EmptyState className="m-4" icon={<Cctv size={18} />} title={filtered ? "No cameras match" : "No cameras yet"}
            body={filtered ? "Change or clear the filters." : canManage ? "Register the first camera. A recorded video can stand in for a physical camera." : "Cameras appear here once an administrator registers them."} />
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="w-full min-w-[900px]" data-testid="camera-table">
                <thead className="border-b border-border-subtle">
                  <tr>
                    <th className={th}>Camera</th>
                    <th className={th}>Factory</th>
                    <th className={th}>Floor · line</th>
                    <th className={th}>Workstations</th>
                    <th className={th}>Status</th>
                    <th className={th}>Video source</th>
                    <th className={th}>Health</th>
                    <th className={th}>Last status update</th>
                  </tr>
                </thead>
                <tbody className="divide-y divide-border-subtle">
                  {list.data!.items.map((c) => {
                    const ws = c.coverage.filter((v) => v.workstation).map((v) => v.workstation!.code);
                    return (
                      <tr key={c.id} className="hover:bg-surface-2">
                        <td className={td}>
                          <Link href={`${BASE}/cameras/${c.id}`} className="font-mono text-body text-fg-primary hover:underline" data-testid={`camera-row-${c.code}`}>{c.code}</Link>
                          <div className="text-fg-secondary">{c.name}</div>
                          <div className="text-fg-tertiary">{CAMERA_TYPE_LABEL[c.cameraType] ?? c.cameraType}</div>
                        </td>
                        <td className={td}>{c.factoryCode}</td>
                        <td className={td}>{c.floor.name}<div className="text-fg-tertiary">{c.line?.name ?? "—"}</div></td>
                        <td className={td}>
                          {ws.length === 0 ? <span className="text-fg-tertiary">—</span>
                            : <span className="font-mono" title={ws.join(", ")}>{ws.slice(0, 4).join(", ")}{ws.length > 4 ? ` +${ws.length - 4}` : ""}</span>}
                        </td>
                        <td className={td}><CameraStatusBadge status={c.status} /></td>
                        <td className={td}><SourceTypeBadge type={c.source?.sourceType} /></td>
                        <td className={td}><CameraHealthBadge h={c.health} /></td>
                        <td className={cn(td, "whitespace-nowrap")}>{formatDateTime(c.lastStatusUpdateAt)}</td>
                      </tr>
                    );
                  })}
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
