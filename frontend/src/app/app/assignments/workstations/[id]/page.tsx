"use client";

// Workstation details: placement, capacity and occupancy, the workers
// assigned now (with Transfer / End), its machines, its assignment history,
// and, for administrators, its configuration.

import Link from "next/link";
import { useMemo, useState } from "react";
import { useParams } from "next/navigation";
import { ArrowRightLeft, Cog, Cpu, History, LogOut, Settings2, UserPlus, UsersRound } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Field, Select } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { Callout } from "@/components/ui/Callout";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { Fact, useFactoryOrg } from "@/components/workers/shared";
import {
  AssignDialog, AssignmentHistoryTable, CapacityMeter, EndAssignmentDialog, OccupancyBadge, TransferDialog
} from "@/components/assignments/shared";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { useT } from "@/lib/useT";
import { P } from "@/lib/access";
import { NOT_PRESENCE_NOTE, duration, type Assignment, type Machine, type Workstation } from "@/lib/assignments";

export default function WorkstationDetailsPage() {
  const { id } = useParams<{ id: string }>();
  const ws = useResource<Workstation>(`/api/workstations/${encodeURIComponent(id)}`);
  if (ws.loading) return <div className="page"><SkeletonRows rows={5} rowHeight="h-20" /></div>;
  if (isApiError(ws.error) && ws.error.status === 404)
    return (
      <div className="page page-narrow">
        <EmptyState title="Workstation not found" body="It doesn't exist, or it is outside the lines you can see." />
        <p className="text-center"><Link href="/app/assignments/workstations" className="text-caption text-accent hover:underline">Back to workstations</Link></p>
      </div>
    );
  if (ws.error || !ws.data)
    return <div className="page page-narrow"><ErrorState title="Couldn't load the workstation" body={ws.error ? errorMessage(ws.error) : undefined} retryLabel="Try again" onRetry={() => void ws.reload()} /></div>;
  return <Details ws={ws.data} reload={ws.reload} />;
}

function Details({ ws, reload }: { ws: Workstation; reload: () => Promise<void> }) {
  const { locale } = useT();
  const canAssign = useCan(P.WorkersAssign);
  const canSeeWorkers = useCan(P.WorkersView);
  const canConfigure = useCan(P.LinesManage);
  const history = useResource<Assignment[]>(canSeeWorkers ? `/api/workstations/${ws.id}/assignments?limit=200` : null);
  const active = useMemo(() => (history.data ?? []).filter((a) => a.status === "Active"), [history.data]);
  const [assignOpen, setAssignOpen] = useState(false);
  const [transfer, setTransfer] = useState<Assignment | null>(null);
  const [ending, setEnding] = useState<Assignment | null>(null);
  const [editing, setEditing] = useState(false);
  const refresh = async () => { await Promise.all([reload(), history.reload()]); };
  const room = ws.isActive && ws.assignedCount < ws.capacity;

  return (
    <div className="page" data-testid="workstation-page">
      <PageHeader
        eyebrow={<Link href="/app/assignments/workstations" className="hover:underline">← Workstations</Link>}
        title={<span className="font-mono">{ws.code}</span>}
        description={ws.name ?? undefined}
        meta={
          <>
            <OccupancyBadge occupancy={ws.occupancy} />
            <CapacityMeter assigned={ws.assignedCount} capacity={ws.capacity} />
            <span className="text-caption text-fg-tertiary">{ws.factoryCode} · {ws.line.name}</span>
          </>
        }
        actions={
          <>
            {canConfigure && <Button size="sm" variant="secondary" iconLeft={<Settings2 size={13} />} onClick={() => setEditing(true)} data-testid="ws-edit">Configure</Button>}
            {canAssign && (
              <Button size="sm" variant="primary" iconLeft={<UserPlus size={13} />} onClick={() => setAssignOpen(true)} disabled={!room} data-testid="ws-assign"
                title={!ws.isActive ? "This workstation is inactive." : !room ? "Capacity reached." : undefined}>
                Assign worker
              </Button>
            )}
          </>
        }
      />

      <div className="mt-6 grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.3fr)]">
        <Panel title="Workstation" icon={<Cog size={14} />}>
          <dl className="grid gap-3 sm:grid-cols-2">
            <Fact label="Table ID"><span className="font-mono">{ws.code}</span></Fact>
            <Fact label="Factory">{ws.factoryCode}</Fact>
            <Fact label="Department">{ws.department.name}</Fact>
            <Fact label="Production floor">{ws.floor?.name ?? "—"}</Fact>
            <Fact label="Production line">{ws.line.name} <span className="font-mono text-caption text-fg-tertiary">{ws.line.id}</span></Fact>
            <Fact label="Position on the line">{ws.position}</Fact>
            <Fact label="Capacity">{ws.capacity} worker{ws.capacity === 1 ? "" : "s"}</Fact>
            <Fact label="Status">{ws.isActive ? "Active" : <Badge tone="neutral">Inactive</Badge>}</Fact>
          </dl>
        </Panel>

        <Panel title="Assigned now" icon={<UsersRound size={14} />} subtitle={NOT_PRESENCE_NOTE} flush testId="ws-assigned">
          {!canSeeWorkers ? (
            <p className="p-4 text-caption text-fg-tertiary">{ws.assignedCount} worker(s) assigned. Worker details are not available with your access.</p>
          ) : history.loading ? (
            <div className="p-4"><SkeletonRows rows={2} rowHeight="h-12" /></div>
          ) : active.length === 0 ? (
            <p className="p-4 text-caption text-fg-tertiary">Nobody is assigned to this workstation.</p>
          ) : (
            <ul className="divide-y divide-border-subtle">
              {active.map((a) => (
                <li key={a.id} className="px-4 py-3 flex flex-wrap items-center gap-3" data-testid="ws-assigned-worker">
                  <span className="min-w-0">
                    <Link href={`/app/workers/${a.workerId}`} className="font-mono text-fg-primary hover:underline">{a.workerCode}</Link>{" "}
                    <span className="text-body text-fg-primary">{a.workerName}</span>
                    <span className="block text-caption text-fg-tertiary">
                      Since {formatDateTime(a.startAt, locale)} ({duration(a.startAt)}){a.machine ? ` · machine ${a.machine.code}` : ""} · by {a.assignedByEmail}
                    </span>
                  </span>
                  {canAssign && (
                    <span className="ml-auto flex gap-2">
                      <Button size="sm" variant="secondary" iconLeft={<ArrowRightLeft size={12} />} onClick={() => setTransfer(a)} data-testid="ws-transfer">Transfer</Button>
                      <Button size="sm" variant="ghost" iconLeft={<LogOut size={12} />} onClick={() => setEnding(a)}>End</Button>
                    </span>
                  )}
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <MachinesPanel ws={ws} onChanged={() => void refresh()} />

        {canSeeWorkers && (
          <Panel title="Assignment history" icon={<History size={14} />} flush className="lg:col-span-2">
            {history.error ? (
              <ErrorState className="m-4" title="Couldn't load the history" body={errorMessage(history.error)} retryLabel="Try again" onRetry={() => void history.reload()} />
            ) : history.loading ? (
              <div className="p-4"><SkeletonRows rows={3} rowHeight="h-10" /></div>
            ) : (
              <AssignmentHistoryTable rows={history.data ?? []} showWorker showWorkstation={false} testId="ws-history" />
            )}
          </Panel>
        )}
      </div>

      <AssignDialog open={assignOpen} factoryId={ws.factoryId} workstation={ws} onClose={() => setAssignOpen(false)} onDone={() => { setAssignOpen(false); void refresh(); }} />
      {transfer && <TransferDialog open assignment={transfer} onClose={() => setTransfer(null)} onDone={() => { setTransfer(null); void refresh(); }} />}
      {ending && <EndAssignmentDialog open assignment={ending} onClose={() => setEnding(null)} onDone={() => { setEnding(null); void refresh(); }} />}
      {editing && <ConfigureDialog ws={ws} onClose={() => setEditing(false)} onSaved={() => { setEditing(false); void refresh(); }} />}
    </div>
  );
}

function MachinesPanel({ ws, onChanged }: { ws: Workstation; onChanged: () => void }) {
  const toast = useToast();
  const canManageMachines = useCan(P.LinesManage, P.DevicesManage);
  const unplaced = useResource<Machine[]>(canManageMachines ? `/api/machines?factoryId=${ws.factoryId}&unplaced=true` : null);
  const placed = useResource<Machine[]>(`/api/machines?workstationId=${ws.id}`);
  const [pick, setPick] = useState("");
  const [creating, setCreating] = useState(false);
  const [code, setCode] = useState("");
  const [type, setType] = useState("");
  const [busy, setBusy] = useState(false);

  async function update(m: Machine, workstationId: string | null, isActive = m.isActive) {
    setBusy(true);
    try {
      await apiJson(`/api/machines/${m.id}`, { method: "PUT", body: { machineType: m.machineType, brand: m.brand, serialNumber: m.serialNumber, workstationId, isActive } });
      toast.push({ tone: "success", title: workstationId ? `Machine ${m.code} placed here` : `Machine ${m.code} removed from ${ws.code}` });
      setPick("");
      await Promise.all([placed.reload(), unplaced.reload()]);
      onChanged();
    } catch (e) {
      toast.push({ tone: "error", title: "Not changed", description: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  }

  async function create() {
    setBusy(true);
    try {
      await apiJson("/api/machines", { method: "POST", body: { factoryId: ws.factoryId, code: code.trim(), machineType: type.trim() || null, workstationId: ws.id } });
      toast.push({ tone: "success", title: `Machine ${code.trim().toUpperCase()} added` });
      setCreating(false);
      setCode("");
      setType("");
      await placed.reload();
      onChanged();
    } catch (e) {
      toast.push({ tone: "error", title: "Not added", description: errorMessage(e) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel title="Machines" icon={<Cpu size={14} />} subtitle="Machines standing at this workstation. Machine activity is not tracked here." flush className="lg:col-span-2"
      right={canManageMachines && <Button size="sm" variant="secondary" onClick={() => setCreating(true)} data-testid="machine-new">Add machine</Button>}>
      {placed.loading ? (
        <div className="p-4"><SkeletonRows rows={1} rowHeight="h-9" /></div>
      ) : (placed.data?.length ?? 0) === 0 ? (
        <p className="p-4 text-caption text-fg-tertiary">No machine placed here.</p>
      ) : (
        <table className="data-table">
          <thead><tr><th>Machine</th><th>Type</th><th>Serial</th><th>Status</th>{canManageMachines && <th />}</tr></thead>
          <tbody>
            {placed.data!.map((m) => (
              <tr key={m.id}>
                <td className="font-mono">{m.code}</td>
                <td className="text-caption">{m.machineType ?? "—"}{m.brand ? ` · ${m.brand}` : ""}</td>
                <td className="text-caption font-mono">{m.serialNumber ?? "—"}</td>
                <td><Badge tone={m.isActive ? "success" : "neutral"}>{m.isActive ? "Active" : "Inactive"}</Badge></td>
                {canManageMachines && (
                  <td className="text-right whitespace-nowrap">
                    <Button size="sm" variant="ghost" disabled={busy} onClick={() => void update(m, ws.id, !m.isActive)}>{m.isActive ? "Deactivate" : "Reactivate"}</Button>
                    <Button size="sm" variant="ghost" disabled={busy} onClick={() => void update(m, null)}>Remove</Button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {canManageMachines && (unplaced.data?.length ?? 0) > 0 && (
        <div className="p-4 border-t border-border-subtle flex flex-wrap items-end gap-2">
          <Field label="Place a machine that stands nowhere yet" className="flex-1 min-w-[220px]">
            {({ id }) => (
              <Select id={id} value={pick} onChange={(e) => setPick(e.target.value)}>
                <option value="">Choose…</option>
                {unplaced.data!.filter((m) => m.isActive).map((m) => <option key={m.id} value={m.id}>{m.code}{m.machineType ? ` · ${m.machineType}` : ""}</option>)}
              </Select>
            )}
          </Field>
          <Button variant="secondary" disabled={!pick || busy} onClick={() => { const m = unplaced.data!.find((x) => x.id === pick); if (m) void update(m, ws.id); }}>Place here</Button>
        </div>
      )}
      <Modal open={creating} onClose={() => setCreating(false)} locked={busy} title={`Add a machine at ${ws.code}`}
        footer={
          <div className="flex justify-end gap-2">
            <Button variant="ghost" onClick={() => setCreating(false)} disabled={busy}>Cancel</Button>
            <Button variant="primary" onClick={() => void create()} disabled={busy || !/^[A-Za-z0-9][A-Za-z0-9\-_/.]{0,23}$/.test(code.trim())} data-testid="machine-create">Add machine</Button>
          </div>
        }>
        <div className="space-y-3">
          <Field label="Asset tag / code" hint="Unique in the factory, e.g. M-0101.">
            {({ id, describedBy }) => <Input id={id} value={code} maxLength={24} className="font-mono" aria-describedby={describedBy} onChange={(e) => setCode(e.target.value.toUpperCase())} data-autofocus data-testid="machine-code" />}
          </Field>
          <Field label="Type (optional)">{({ id }) => <Input id={id} value={type} maxLength={80} placeholder="e.g. Single needle lockstitch" onChange={(e) => setType(e.target.value)} />}</Field>
        </div>
      </Modal>
    </Panel>
  );
}

function ConfigureDialog({ ws, onClose, onSaved }: { ws: Workstation; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const { departments, floors } = useFactoryOrg(ws.factoryId);
  const lines = useResource<{ id: string; name: string; isActive: boolean }[]>(`/api/factories/${ws.factoryId}/lines`);
  const [lineId, setLineId] = useState(ws.line.id);
  const [departmentId, setDepartmentId] = useState(ws.department.id);
  const [floorId, setFloorId] = useState(ws.floor?.id ?? "");
  const [name, setName] = useState(ws.name ?? "");
  const [capacity, setCapacity] = useState(ws.capacity);
  const [position, setPosition] = useState(ws.position);
  const [isActive, setIsActive] = useState(ws.isActive);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const occupied = ws.assignedCount > 0;

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await apiJson(`/api/workstations/${ws.id}`, { method: "PUT", body: { lineId, departmentId, floorId: floorId || null, name: name.trim() || null, capacity, position, isActive } });
      toast.push({ tone: "success", title: `Workstation ${ws.code} saved` });
      onSaved();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open onClose={onClose} locked={busy} title={`Configure ${ws.code}`} testId="ws-configure"
      description={occupied ? `${ws.assignedCount} worker(s) are assigned: the placement can't change, it can't be deactivated, and capacity can't go below ${ws.assignedCount}.` : undefined}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void save()} disabled={busy || capacity < Math.max(1, ws.assignedCount) || capacity > 20} data-testid="ws-configure-save">{busy ? "Saving…" : "Save"}</Button>
        </div>
      }>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label="Production line">
          {({ id }) => <Select id={id} value={lineId} disabled={occupied} onChange={(e) => setLineId(e.target.value)}>{(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}</Select>}
        </Field>
        <Field label="Department">
          {({ id }) => <Select id={id} value={departmentId} disabled={occupied} onChange={(e) => setDepartmentId(e.target.value)}>{(departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</Select>}
        </Field>
        <Field label="Production floor">
          {({ id }) => (
            <Select id={id} value={floorId} disabled={occupied} onChange={(e) => setFloorId(e.target.value)}>
              <option value="">Not recorded</option>
              {(floors.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
            </Select>
          )}
        </Field>
        <Field label="Description">{({ id }) => <Input id={id} value={name} maxLength={120} onChange={(e) => setName(e.target.value)} />}</Field>
        <Field label="Capacity (workers)" hint={occupied ? `At least ${ws.assignedCount}.` : "1 to 20."}>
          {({ id, describedBy }) => <Input id={id} type="number" min={Math.max(1, ws.assignedCount)} max={20} value={capacity} aria-describedby={describedBy} onChange={(e) => setCapacity(Number(e.target.value))} data-testid="ws-configure-capacity" />}
        </Field>
        <Field label="Position on the line">{({ id }) => <Input id={id} type="number" min={0} value={position} onChange={(e) => setPosition(Number(e.target.value))} />}</Field>
        <label className="sm:col-span-2 flex items-center gap-2 text-body">
          <input type="checkbox" checked={isActive} disabled={occupied && isActive} onChange={(e) => setIsActive(e.target.checked)} className="accent-[var(--accent)]" />
          Active (inactive workstations take no new assignments)
        </label>
        {error && <Callout className="sm:col-span-2" tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}
