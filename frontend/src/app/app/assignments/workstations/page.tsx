"use client";

// Workstation overview: every workstation in scope with its placement,
// capacity, occupancy, assigned workers and machines. Administrators
// (lines.manage) add workstations one by one or as a numbered series.

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { LayoutGrid, ListPlus, Plus, RefreshCw, Search, Table2 } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Field, FilterSelect, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { Pagination } from "@/components/access/shared";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { CapacityMeter, OccupancyBadge } from "@/components/assignments/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { cn } from "@/lib/cn";
import { P, type PagedResult } from "@/lib/access";
import { OCCUPANCY, OCCUPANCY_LABEL, type Workstation } from "@/lib/assignments";

const PAGE_SIZE = 50;

export default function WorkstationsPage() {
  return (
    <RequirePermission perms={[P.WorkersView, P.LinesView, P.WorkersAssign]} what="Workstations">
      <Workstations />
    </RequirePermission>
  );
}

function Workstations() {
  const router = useRouter();
  const search = useSearchParams();
  const canConfigure = useCan(P.LinesManage);
  const { factories } = useMyFactories();

  const q = search?.get("q") ?? "";
  const factoryParam = search?.get("factory") ?? "";
  const department = search?.get("department") ?? "";
  const floor = search?.get("floor") ?? "";
  const line = search?.get("line") ?? "";
  const occupancy = search?.get("occupancy") ?? "";
  const page = Math.max(1, Number(search?.get("page")) || 1);
  const factoryId = factoryParam || (factories?.length === 1 ? factories[0].id : "");
  const { departments, floors, lines } = useFactoryOrg(factoryId || null);

  const [draft, setDraft] = useState(q);
  const [creating, setCreating] = useState<"one" | "bulk" | null>(null);
  useEffect(() => setDraft(q), [q]);

  function setParams(next: Record<string, string | null>, resetPage = true) {
    const params = new URLSearchParams(search?.toString() ?? "");
    for (const [k, v] of Object.entries(next)) {
      if (v) params.set(k, v);
      else params.delete(k);
    }
    if (resetPage) params.delete("page");
    router.replace(`/app/assignments/workstations${params.toString() ? `?${params}` : ""}`);
  }

  useEffect(() => {
    if (draft === q) return;
    const id = window.setTimeout(() => setParams({ q: draft.trim() || null }), 300);
    return () => window.clearTimeout(id);
  }, [draft]); // eslint-disable-line react-hooks/exhaustive-deps

  const path = useMemo(() => {
    const p = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
    if (q) p.set("search", q);
    if (factoryParam) p.set("factoryId", factoryParam);
    if (department) p.set("departmentId", department);
    if (floor) p.set("floorId", floor);
    if (line) p.set("lineId", line);
    if (occupancy) p.set("occupancy", occupancy);
    return `/api/workstations?${p}`;
  }, [q, factoryParam, department, floor, line, occupancy, page]);
  const list = useResource<PagedResult<Workstation>>(path);
  const filtered = !!(q || department || floor || line || occupancy || (factoryParam && (factories?.length ?? 0) > 1));

  return (
    <div className="page" data-testid="workstations-page">
      <PageHeader
        eyebrow={<Link href="/app/assignments" className="hover:underline">← Assignments</Link>}
        title="Workstations"
        description="Tables of each production line, their capacity and who is assigned to them. Occupancy shows recorded assignments, not presence."
        actions={
          <>
            <Link href="/app/assignments"><Button size="sm" variant="secondary" iconLeft={<LayoutGrid size={13} />}>Line layout</Button></Link>
            {canConfigure && (
              <>
                <Button size="sm" variant="secondary" iconLeft={<ListPlus size={13} />} onClick={() => setCreating("bulk")} data-testid="ws-bulk">Add a series</Button>
                <Button size="sm" variant="primary" iconLeft={<Plus size={13} />} onClick={() => setCreating("one")} data-testid="ws-new">Add workstation</Button>
              </>
            )}
          </>
        }
      />

      <Panel className="mt-6" flush title="Workstations" icon={<Table2 size={14} />}
        subtitle={list.data ? `${list.data.total} workstation${list.data.total === 1 ? "" : "s"}${filtered ? " match" : ""}` : undefined}
        right={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void list.reload()}><RefreshCw size={13} className={cn(list.refreshing && "animate-spin")} /></Button>}>
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <div className="relative flex-1 min-w-[200px]">
            <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
            <input type="search" value={draft} onChange={(e) => setDraft(e.target.value)} placeholder="Search table code or name"
              aria-label="Search workstations" className="control !h-8 !pl-8 !text-caption" data-testid="ws-search" />
          </div>
          {(factories?.length ?? 0) > 1 && (
            <FilterSelect label="Factory" value={factoryParam} onChange={(v) => setParams({ factory: v || null, department: null, floor: null, line: null })}>
              <option value="">All factories</option>
              {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
            </FilterSelect>
          )}
          {factoryId && (
            <>
              <FilterSelect label="Department" value={department} onChange={(v) => setParams({ department: v || null })}>
                <option value="">All</option>
                {(departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
              </FilterSelect>
              {(floors.data?.length ?? 0) > 0 && (
                <FilterSelect label="Floor" value={floor} onChange={(v) => setParams({ floor: v || null })}>
                  <option value="">All</option>
                  {floors.data!.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
                </FilterSelect>
              )}
              <FilterSelect label="Line" value={line} onChange={(v) => setParams({ line: v || null })} testId="ws-line-filter">
                <option value="">All</option>
                {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </FilterSelect>
            </>
          )}
          <FilterSelect label="Status" value={occupancy} onChange={(v) => setParams({ occupancy: v || null })} testId="ws-occupancy-filter">
            <option value="">Any</option>
            {OCCUPANCY.map((o) => <option key={o} value={o}>{OCCUPANCY_LABEL[o]}</option>)}
          </FilterSelect>
          {filtered && <Button size="sm" variant="ghost" onClick={() => { setDraft(""); setParams({ q: null, department: null, floor: null, line: null, occupancy: null }); }}>Clear filters</Button>}
        </div>

        {list.loading ? (
          <div className="p-4"><SkeletonRows rows={8} rowHeight="h-11" /></div>
        ) : list.error && !list.data ? (
          <ErrorState className="m-4" title="Couldn't load workstations" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
        ) : (list.data?.items.length ?? 0) === 0 ? (
          <EmptyState className="m-4" icon={<Table2 size={18} />} title={filtered ? "No workstations match" : "No workstations yet"}
            body={filtered ? "Change or clear the filters." : canConfigure ? "Add the tables of a line with Add a series (e.g. T-001 … T-030)." : "Your administrator adds the workstations of each line."} />
        ) : (
          <>
            <div className="overflow-x-auto">
              <table className="data-table" data-testid="ws-table">
                <thead>
                  <tr><th>Table</th><th>Line</th><th>Department / floor</th><th>Capacity</th><th>Status</th><th>Assigned workers</th><th>Machines</th></tr>
                </thead>
                <tbody>
                  {list.data!.items.map((w) => (
                    <tr key={w.id} className="cursor-pointer" onClick={() => router.push(`/app/assignments/workstations/${w.id}`)} data-testid="ws-row" data-code={w.code}>
                      <td className="whitespace-nowrap">
                        <Link href={`/app/assignments/workstations/${w.id}`} onClick={(e) => e.stopPropagation()} className="font-mono text-fg-primary hover:underline">{w.code}</Link>
                        {w.name && <span className="block text-caption text-fg-tertiary">{w.name}</span>}
                      </td>
                      <td className="text-caption">{w.line.name}{(factories?.length ?? 0) > 1 && <span className="block text-fg-tertiary">{w.factoryCode}</span>}</td>
                      <td className="text-caption">{w.department.name}{w.floor && <span className="block text-fg-tertiary">{w.floor.name}</span>}</td>
                      <td><CapacityMeter assigned={w.assignedCount} capacity={w.capacity} /></td>
                      <td><OccupancyBadge occupancy={w.occupancy} /></td>
                      <td className="text-caption">
                        {w.workers == null ? <span className="text-fg-tertiary">{w.assignedCount ? `${w.assignedCount} assigned` : "—"}</span>
                          : w.workers.length === 0 ? <span className="text-fg-tertiary">—</span>
                          : w.workers.map((x) => (
                            <span key={x.assignmentId} className="block whitespace-nowrap">
                              <Link href={`/app/workers/${x.workerId}`} onClick={(e) => e.stopPropagation()} className="font-mono hover:underline">{x.workerCode}</Link> {x.fullName}
                            </span>
                          ))}
                      </td>
                      <td className="text-caption text-fg-secondary">{w.machines.length ? w.machines.map((m) => m.code).join(", ") : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination page={list.data!.page} pageSize={list.data!.pageSize} total={list.data!.total} onPage={(p) => setParams({ page: String(p) }, false)} />
          </>
        )}
      </Panel>

      {creating && (
        <CreateWorkstationsDialog mode={creating} defaultFactory={factoryId} defaultLine={line} onClose={() => setCreating(null)}
          onCreated={() => { setCreating(null); void list.reload(); }} />
      )}
    </div>
  );
}

function CreateWorkstationsDialog({ mode, defaultFactory, defaultLine, onClose, onCreated }: {
  mode: "one" | "bulk"; defaultFactory: string; defaultLine: string; onClose: () => void; onCreated: () => void;
}) {
  const toast = useToast();
  const { factories } = useMyFactories();
  const [factoryId, setFactoryId] = useState(defaultFactory);
  const { departments, floors, lines } = useFactoryOrg(factoryId || null);
  const [lineId, setLineId] = useState(defaultLine);
  const [departmentId, setDepartmentId] = useState("");
  const [floorId, setFloorId] = useState("");
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [prefix, setPrefix] = useState("T-");
  const [from, setFrom] = useState(1);
  const [count, setCount] = useState(10);
  const [capacity, setCapacity] = useState(1);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const codeOk = mode === "bulk" ? /^[A-Za-z0-9][A-Za-z0-9\-_/.]{0,15}$/.test(prefix) : /^[A-Za-z0-9][A-Za-z0-9\-_/.]{0,23}$/.test(code.trim());
  const ready = factoryId && lineId && departmentId && codeOk && capacity >= 1 && capacity <= 20 && (mode === "one" || (count >= 1 && count <= 200 && from >= 0));
  const preview = mode === "bulk" && prefix ? `${prefix.toUpperCase()}${String(from).padStart(3, "0")} … ${prefix.toUpperCase()}${String(from + Math.max(1, count) - 1).padStart(3, "0")}` : "";

  async function save() {
    setBusy(true);
    setError(null);
    const base = { factoryId, lineId, departmentId, floorId: floorId || null, capacity };
    try {
      if (mode === "one") {
        const w = await apiJson<Workstation>("/api/workstations", { method: "POST", body: { ...base, code: code.trim(), name: name.trim() || null } });
        toast.push({ tone: "success", title: `Workstation ${w.code} added` });
      } else {
        const r = await apiJson<{ created: number; codes: string[]; skipped: string[] }>("/api/workstations/bulk", { method: "POST", body: { ...base, prefix, from, count } });
        toast.push({ tone: "success", title: `${r.created} workstation${r.created === 1 ? "" : "s"} added`, description: r.skipped.length ? `${r.skipped.length} already existed and were skipped.` : undefined });
      }
      onCreated();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open onClose={onClose} locked={busy} testId="ws-create-dialog" title={mode === "one" ? "Add a workstation" : "Add a series of workstations"}
      description="Workstations belong to one production line and department. Capacity is how many workers may be assigned at the same time."
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void save()} disabled={busy || !ready} data-testid="ws-create-confirm">{busy ? "Saving…" : mode === "one" ? "Add workstation" : `Add ${count}`}</Button>
        </div>
      }>
      <div className="grid gap-3 sm:grid-cols-2">
        {(factories?.length ?? 0) > 1 && (
          <Field label="Factory" className="sm:col-span-2">
            {({ id }) => (
              <Select id={id} value={factoryId} onChange={(e) => { setFactoryId(e.target.value); setLineId(""); setDepartmentId(""); setFloorId(""); }}>
                <option value="">Choose…</option>
                {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
              </Select>
            )}
          </Field>
        )}
        <Field label="Production line">
          {({ id }) => (
            <Select id={id} value={lineId} onChange={(e) => setLineId(e.target.value)} data-testid="ws-create-line">
              <option value="">Choose…</option>
              {(lines.data ?? []).filter((l) => l.isActive).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </Select>
          )}
        </Field>
        <Field label="Department">
          {({ id }) => (
            <Select id={id} value={departmentId} onChange={(e) => setDepartmentId(e.target.value)} data-testid="ws-create-department">
              <option value="">Choose…</option>
              {(departments.data ?? []).filter((d) => d.isActive).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </Select>
          )}
        </Field>
        <Field label="Production floor (optional)">
          {({ id }) => (
            <Select id={id} value={floorId} onChange={(e) => setFloorId(e.target.value)}>
              <option value="">Not recorded</option>
              {(floors.data ?? []).filter((d) => d.isActive).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </Select>
          )}
        </Field>
        <Field label="Capacity (workers)" hint="1 for most tables; up to 20.">
          {({ id, describedBy }) => <Input id={id} type="number" min={1} max={20} value={capacity} aria-describedby={describedBy} onChange={(e) => setCapacity(Number(e.target.value))} />}
        </Field>
        {mode === "one" ? (
          <>
            <Field label="Table code" hint="As written on the table, e.g. T-025." error={code && !codeOk ? "Letters, digits, '-', '_', '/' and '.'." : null}>
              {({ id, describedBy, invalid }) => <Input id={id} value={code} maxLength={24} className="font-mono" aria-invalid={invalid} aria-describedby={describedBy} onChange={(e) => setCode(e.target.value.toUpperCase())} data-testid="ws-create-code" />}
            </Field>
            <Field label="Description (optional)" hint="e.g. Collar attach">
              {({ id }) => <Input id={id} value={name} maxLength={120} onChange={(e) => setName(e.target.value)} />}
            </Field>
          </>
        ) : (
          <>
            <Field label="Code prefix" error={prefix && !codeOk ? "Letters, digits, '-', '_', '/' and '.'." : null}>
              {({ id, invalid }) => <Input id={id} value={prefix} maxLength={16} className="font-mono" aria-invalid={invalid} onChange={(e) => setPrefix(e.target.value.toUpperCase())} />}
            </Field>
            <div className="grid grid-cols-2 gap-2">
              <Field label="From number">{({ id }) => <Input id={id} type="number" min={0} value={from} onChange={(e) => setFrom(Number(e.target.value))} />}</Field>
              <Field label="How many">{({ id }) => <Input id={id} type="number" min={1} max={200} value={count} onChange={(e) => setCount(Number(e.target.value))} data-testid="ws-create-count" />}</Field>
            </div>
            {preview && <p className="sm:col-span-2 text-caption text-fg-secondary">Creates <span className="font-mono">{preview}</span>. Codes that already exist are skipped.</p>}
          </>
        )}
        {error && <Callout className="sm:col-span-2" tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}
