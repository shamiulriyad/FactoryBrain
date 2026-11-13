"use client";

// Building blocks for workstation assignments: badges, the worker and
// workstation pickers, the Assign / Transfer / End dialogs and the history
// table. Pickers load their options from the API and disable choices the
// API would refuse (inactive or full workstations, machines in use), so
// invalid selections can't be made; the API checks everything again.

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { ArrowRight, Search } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { Callout } from "@/components/ui/Callout";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { formatDateTime } from "@/lib/format";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import type { FactoryLine, PagedResult } from "@/lib/access";
import type { Worker } from "@/lib/workers";
import {
  OCCUPANCY_LABEL,
  OCCUPANCY_TONE,
  STATUS_TONE,
  duration,
  localToIso,
  nowLocal,
  type Assignment,
  type Occupancy,
  type TransferResult,
  type Workstation
} from "@/lib/assignments";

export function OccupancyBadge({ occupancy, className }: { occupancy: Occupancy; className?: string }) {
  return (
    <Badge tone={OCCUPANCY_TONE[occupancy]} dot className={className} testId="occupancy">
      {OCCUPANCY_LABEL[occupancy]}
    </Badge>
  );
}

export function AssignmentStatusBadge({ status }: { status: string }) {
  return <Badge tone={STATUS_TONE[status] ?? "neutral"} dot>{status}</Badge>;
}

/** "1 / 2" with a small bar. */
export function CapacityMeter({ assigned, capacity }: { assigned: number; capacity: number }) {
  const pct = Math.min(100, (assigned / Math.max(1, capacity)) * 100);
  return (
    <span className="inline-flex items-center gap-2 tabular-nums text-caption" aria-label={`${assigned} of ${capacity} assigned`}>
      <span className="h-1.5 w-12 rounded-full bg-surface-2 overflow-hidden" aria-hidden>
        <span className={cn("block h-full", assigned >= capacity ? "bg-[var(--risk-medium)]" : "bg-[var(--accent)]")} style={{ width: `${pct}%` }} />
      </span>
      {assigned} / {capacity}
    </span>
  );
}

/** Lines of one factory (for filters and pickers). */
export function useLines(factoryId: string | null) {
  return useResource<FactoryLine[]>(factoryId ? `/api/factories/${factoryId}/lines` : null);
}

/**
 * Workstation picker for one factory: a line filter plus the workstations
 * with their occupancy. Inactive and full workstations can't be chosen.
 */
export function WorkstationPicker({ factoryId, value, onChange, excludeId, initialLine }: {
  factoryId: string;
  value: string;
  onChange: (ws: Workstation | null) => void;
  excludeId?: string;
  initialLine?: string;
}) {
  const lines = useLines(factoryId);
  const [line, setLine] = useState(initialLine ?? "");
  const [search, setSearch] = useState("");
  const path = useMemo(() => {
    const p = new URLSearchParams({ factoryId, pageSize: "200", includeInactive: "true" });
    if (line) p.set("lineId", line);
    if (search.trim()) p.set("search", search.trim());
    return `/api/workstations?${p}`;
  }, [factoryId, line, search]);
  const list = useResource<PagedResult<Workstation>>(path);
  const options = (list.data?.items ?? []).filter((w) => w.id !== excludeId);

  return (
    <div className="space-y-2">
      <div className="grid gap-2 sm:grid-cols-2">
        <Select aria-label="Production line" value={line} onChange={(e) => setLine(e.target.value)} data-testid="ws-picker-line">
          <option value="">All lines</option>
          {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
        </Select>
        <div className="relative">
          <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
          <Input aria-label="Search workstations" placeholder="Search table, e.g. T-025" value={search} onChange={(e) => setSearch(e.target.value)} className="!pl-8" />
        </div>
      </div>
      {list.loading ? (
        <SkeletonRows rows={3} rowHeight="h-9" />
      ) : list.error ? (
        <p className="text-caption text-[var(--risk-high)]">{errorMessage(list.error)}</p>
      ) : options.length === 0 ? (
        <p className="text-caption text-fg-tertiary">No workstations match. Administrators add them under Workstations.</p>
      ) : (
        <ul role="listbox" aria-label="Workstations" className="max-h-64 overflow-y-auto divide-y divide-border-subtle rounded-[var(--radius-control)] border border-border-subtle" data-testid="ws-picker">
          {options.map((w) => {
            const blocked = w.occupancy === "Inactive" || w.occupancy === "CapacityReached";
            const selected = w.id === value;
            return (
              <li key={w.id} role="option" aria-selected={selected} aria-disabled={blocked}>
                <button
                  type="button"
                  disabled={blocked}
                  onClick={() => onChange(selected ? null : w)}
                  className={cn(
                    "w-full text-left px-3 py-2 flex flex-wrap items-center gap-2 text-caption transition-colors",
                    selected ? "bg-[var(--accent-soft)]" : "hover:bg-surface-2",
                    blocked && "opacity-50 cursor-not-allowed hover:bg-transparent"
                  )}
                  data-testid={`ws-option-${w.code}`}
                >
                  <span className="font-mono text-body text-fg-primary">{w.code}</span>
                  <span className="text-fg-secondary">{w.line.name} · {w.department.name}</span>
                  <span className="ml-auto flex items-center gap-2">
                    <CapacityMeter assigned={w.assignedCount} capacity={w.capacity} />
                    <OccupancyBadge occupancy={w.occupancy} />
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

/** Active workers without a workstation, searchable by Worker ID or name. */
export function WorkerPicker({ factoryId, value, onChange }: { factoryId: string; value: string; onChange: (w: Worker | null) => void }) {
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  useEffect(() => {
    const id = window.setTimeout(() => setDebounced(search.trim()), 250);
    return () => window.clearTimeout(id);
  }, [search]);
  const p = new URLSearchParams({ factoryId, status: "Active", assigned: "false", pageSize: "30", sort: "name" });
  if (debounced) p.set("search", debounced);
  const list = useResource<PagedResult<Worker>>(`/api/workers?${p}`);
  return (
    <div className="space-y-2">
      <div className="relative">
        <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
        <Input aria-label="Search workers" placeholder="Search Worker ID or name" value={search} onChange={(e) => setSearch(e.target.value)} className="!pl-8" data-autofocus data-testid="worker-picker-search" />
      </div>
      {list.loading ? (
        <SkeletonRows rows={3} rowHeight="h-9" />
      ) : list.error ? (
        <p className="text-caption text-[var(--risk-high)]">{errorMessage(list.error)}</p>
      ) : (list.data?.items.length ?? 0) === 0 ? (
        <p className="text-caption text-fg-tertiary">{debounced ? "No unassigned active worker matches." : "Every active worker in your scope already has a workstation."}</p>
      ) : (
        <ul role="listbox" aria-label="Unassigned workers" className="max-h-56 overflow-y-auto divide-y divide-border-subtle rounded-[var(--radius-control)] border border-border-subtle" data-testid="worker-picker">
          {list.data!.items.map((w) => (
            <li key={w.id} role="option" aria-selected={w.id === value}>
              <button type="button" onClick={() => onChange(w.id === value ? null : w)}
                className={cn("w-full text-left px-3 py-2 flex items-center gap-2 text-caption", w.id === value ? "bg-[var(--accent-soft)]" : "hover:bg-surface-2")}
                data-testid={`worker-option-${w.workerCode}`}>
                <span className="font-mono text-fg-primary">{w.workerCode}</span>
                <span className="text-body text-fg-primary">{w.fullName}</span>
                <span className="ml-auto text-fg-tertiary">{w.designation ?? ""}{w.line ? ` · ${w.line.name}` : ""}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Machine choice for a workstation: its own machines; the API refuses one already in use. */
function MachineSelect({ ws, value, onChange }: { ws: Workstation | null; value: string; onChange: (v: string) => void }) {
  const inUse = new Set((ws?.workers ?? []).map((w) => w.machine?.id).filter(Boolean) as string[]);
  const machines = (ws?.machines ?? []).filter((m) => m.isActive);
  return (
    <Field label="Machine (optional)" hint={ws && machines.length === 0 ? "No active machine stands at this workstation." : undefined}>
      {({ id, describedBy }) => (
        <Select id={id} aria-describedby={describedBy} value={value} onChange={(e) => onChange(e.target.value)} disabled={!ws || machines.length === 0}>
          <option value="">No machine</option>
          {machines.map((m) => (
            <option key={m.id} value={m.id} disabled={inUse.has(m.id)}>
              {m.code}{m.machineType ? ` · ${m.machineType}` : ""}{inUse.has(m.id) ? " (in use)" : ""}
            </option>
          ))}
        </Select>
      )}
    </Field>
  );
}

function WhenField({ label, value, onChange, hint }: { label: string; value: string; onChange: (v: string) => void; hint: string }) {
  return (
    <Field label={label} hint={hint}>
      {({ id, describedBy }) => <Input id={id} type="datetime-local" max={nowLocal()} value={value} aria-describedby={describedBy} onChange={(e) => onChange(e.target.value)} />}
    </Field>
  );
}

/**
 * Assign a worker to a workstation. Give `worker` to choose the workstation,
 * or `workstation` to choose the worker.
 */
export function AssignDialog({ open, factoryId, worker, workstation, onClose, onDone }: {
  open: boolean;
  factoryId: string;
  worker?: { id: string; workerCode: string; fullName: string; lineId?: string | null };
  workstation?: Workstation;
  onClose: () => void;
  onDone: (a: Assignment) => void;
}) {
  const toast = useToast();
  const [pickedWorker, setPickedWorker] = useState<Worker | null>(null);
  const [pickedWs, setPickedWs] = useState<Workstation | null>(null);
  const [machine, setMachine] = useState("");
  const [startAt, setStartAt] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setPickedWorker(null);
    setPickedWs(null);
    setMachine("");
    setStartAt("");
    setReason("");
    setError(null);
  }, [open]);

  const ws = workstation ?? pickedWs;
  const workerId = worker?.id ?? pickedWorker?.id ?? "";

  async function save() {
    if (!ws || !workerId) return;
    setBusy(true);
    setError(null);
    try {
      const a = await apiJson<Assignment>("/api/assignments", {
        method: "POST",
        body: { workerId, workstationId: ws.id, machineId: machine || null, startAt: localToIso(startAt), reason: reason.trim() || null }
      });
      toast.push({ tone: "success", title: `${a.workerCode} assigned to ${a.workstation.code}`, description: `${a.workerName} · ${a.line.name}` });
      onDone(a);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} locked={busy} testId="assign-dialog" className="sm:max-w-2xl"
      title={worker ? `Assign ${worker.workerCode} to a workstation` : `Assign a worker to ${workstation?.code}`}
      description={worker ? worker.fullName : workstation ? `${workstation.line.name} · ${workstation.department.name} · ${workstation.assignedCount} / ${workstation.capacity} assigned` : undefined}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void save()} disabled={busy || !ws || !workerId} data-testid="assign-confirm">{busy ? "Assigning…" : "Assign"}</Button>
        </div>
      }>
      <div className="space-y-3">
        {worker ? (
          <Field label="Workstation">{() => <WorkstationPicker factoryId={factoryId} value={pickedWs?.id ?? ""} onChange={(w) => { setPickedWs(w); setMachine(""); }} initialLine={worker.lineId ?? undefined} />}</Field>
        ) : (
          <Field label="Worker (active, without a workstation)">{() => <WorkerPicker factoryId={factoryId} value={pickedWorker?.id ?? ""} onChange={setPickedWorker} />}</Field>
        )}
        <div className="grid gap-3 sm:grid-cols-2">
          <MachineSelect ws={ws} value={machine} onChange={setMachine} />
          <WhenField label="Starts" value={startAt} onChange={setStartAt} hint="Leave empty for now. Up to 31 days back." />
        </div>
        <Field label="Reason / note (optional)">
          {({ id }) => <TextArea id={id} rows={2} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />}
        </Field>
        {error && <Callout tone="danger" role="alert" testId="assign-error">{error}</Callout>}
      </div>
    </Modal>
  );
}

/** Transfer: choose the new workstation, then confirm "from → to at time" before anything changes. */
export function TransferDialog({ open, assignment, onClose, onDone }: {
  open: boolean;
  assignment: Assignment;
  onClose: () => void;
  onDone: (r: TransferResult) => void;
}) {
  const { locale } = useT();
  const toast = useToast();
  const [target, setTarget] = useState<Workstation | null>(null);
  const [machine, setMachine] = useState("");
  const [at, setAt] = useState("");
  const [reason, setReason] = useState("");
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setTarget(null);
    setMachine("");
    setAt("");
    setReason("");
    setConfirming(false);
    setError(null);
  }, [open]);

  async function transfer() {
    if (!target) return;
    setBusy(true);
    setError(null);
    try {
      const r = await apiJson<TransferResult>(`/api/assignments/${assignment.id}/transfer`, {
        method: "POST",
        body: { workstationId: target.id, machineId: machine || null, effectiveAt: localToIso(at), reason: reason.trim() }
      });
      toast.push({ tone: "success", title: `${assignment.workerCode} transferred`, description: `${r.closed.workstation.code} → ${r.opened.workstation.code}` });
      onDone(r);
    } catch (e) {
      setError(errorMessage(e));
      setConfirming(false);
    } finally {
      setBusy(false);
    }
  }

  const when = at ? formatDateTime(new Date(at).toISOString(), locale) : "now";
  return (
    <Modal open={open} onClose={onClose} locked={busy} testId="transfer-dialog" className="sm:max-w-2xl"
      title={confirming ? "Confirm the transfer" : `Transfer ${assignment.workerCode}`}
      description={`${assignment.workerName} · currently ${assignment.workstation.code} on ${assignment.line.name}`}
      footer={
        <div className="flex justify-end gap-2">
          {confirming ? (
            <>
              <Button variant="ghost" onClick={() => setConfirming(false)} disabled={busy}>Back</Button>
              <Button variant="primary" onClick={() => void transfer()} disabled={busy} data-testid="transfer-confirm-final">{busy ? "Transferring…" : "Confirm transfer"}</Button>
            </>
          ) : (
            <>
              <Button variant="ghost" onClick={onClose}>Cancel</Button>
              <Button variant="primary" onClick={() => setConfirming(true)} disabled={!target || !reason.trim()} data-testid="transfer-review">Review transfer</Button>
            </>
          )}
        </div>
      }>
      {confirming && target ? (
        <div className="space-y-3" data-testid="transfer-summary">
          <div className="surface-2 rounded-[var(--radius-control)] p-4 flex flex-wrap items-center gap-3">
            <div>
              <p className="text-caption text-fg-tertiary">From</p>
              <p className="font-mono text-body text-fg-primary">{assignment.workstation.code}</p>
              <p className="text-caption text-fg-secondary">{assignment.line.name} · {assignment.department.name}</p>
            </div>
            <ArrowRight size={18} className="text-fg-tertiary" aria-label="to" />
            <div>
              <p className="text-caption text-fg-tertiary">To</p>
              <p className="font-mono text-body text-fg-primary">{target.code}</p>
              <p className="text-caption text-fg-secondary">{target.line.name} · {target.department.name}</p>
            </div>
          </div>
          <ul className="text-caption text-fg-secondary list-disc pl-5 space-y-1">
            <li>Effective: <strong className="text-fg-primary">{when}</strong>. The current assignment ends and the new one starts at this same moment.</li>
            <li>Reason: {reason.trim()}</li>
            {target.line.id !== assignment.line.id && <li>This moves the worker to another production line{target.department.id !== assignment.department.id ? " and department" : ""}.</li>}
            <li>Both records are kept in the assignment history.</li>
          </ul>
          {error && <Callout tone="danger" role="alert">{error}</Callout>}
        </div>
      ) : (
        <div className="space-y-3">
          <Field label="New workstation">
            {() => <WorkstationPicker factoryId={assignment.factoryId} value={target?.id ?? ""} onChange={(w) => { setTarget(w); setMachine(""); }} excludeId={assignment.workstation.id} initialLine={assignment.line.id} />}
          </Field>
          <div className="grid gap-3 sm:grid-cols-2">
            <MachineSelect ws={target} value={machine} onChange={setMachine} />
            <WhenField label="Effective" value={at} onChange={setAt} hint="Leave empty for now, e.g. 11:30 if it happened earlier today." />
          </div>
          <Field label="Reason (required)">
            {({ id }) => <TextArea id={id} rows={2} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Line 2 short of operators" data-testid="transfer-reason" />}
          </Field>
          {error && <Callout tone="danger" role="alert">{error}</Callout>}
        </div>
      )}
    </Modal>
  );
}

export function EndAssignmentDialog({ open, assignment, onClose, onDone }: {
  open: boolean;
  assignment: Assignment;
  onClose: () => void;
  onDone: (a: Assignment) => void;
}) {
  const toast = useToast();
  const [reason, setReason] = useState("");
  const [at, setAt] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    if (open) { setReason(""); setAt(""); setError(null); }
  }, [open]);

  async function end() {
    setBusy(true);
    setError(null);
    try {
      const a = await apiJson<Assignment>(`/api/assignments/${assignment.id}/end`, { method: "POST", body: { reason: reason.trim(), endAt: localToIso(at) } });
      toast.push({ tone: "success", title: "Assignment ended", description: `${a.workerCode} left ${a.workstation.code}` });
      onDone(a);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} locked={busy} testId="end-dialog" title={`End ${assignment.workerCode}'s assignment at ${assignment.workstation.code}?`}
      description="The worker has no workstation afterwards. To move them, use Transfer instead."
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="danger" onClick={() => void end()} disabled={busy || !reason.trim()} data-testid="end-confirm">{busy ? "Ending…" : "End assignment"}</Button>
        </div>
      }>
      <div className="space-y-3">
        <Field label="Reason (required)">
          {({ id }) => <TextArea id={id} rows={2} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Sent for training" data-autofocus />}
        </Field>
        <WhenField label="Ends" value={at} onChange={setAt} hint="Leave empty for now." />
        {error && <Callout tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}

/** The assignment timeline (newest first). */
export function AssignmentHistoryTable({ rows, showWorker, showWorkstation = true, testId }: {
  rows: Assignment[];
  showWorker?: boolean;
  showWorkstation?: boolean;
  testId?: string;
}) {
  const { locale } = useT();
  if (rows.length === 0) return <p className="p-4 text-caption text-fg-tertiary">No assignments recorded yet.</p>;
  return (
    <div className="overflow-x-auto">
      <table className="data-table" data-testid={testId}>
        <thead>
          <tr>
            {showWorker && <th>Worker</th>}
            {showWorkstation && <th>Workstation</th>}
            <th>Line</th>
            <th>From</th>
            <th>Until</th>
            <th>Status</th>
            <th>Reason</th>
            <th>By</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((a) => (
            <tr key={a.id} data-testid="assignment-row" data-status={a.status}>
              {showWorker && (
                <td className="whitespace-nowrap">
                  <Link href={`/app/workers/${a.workerId}`} className="font-mono hover:underline">{a.workerCode}</Link>
                  <span className="block text-caption text-fg-tertiary">{a.workerName}</span>
                </td>
              )}
              {showWorkstation && (
                <td className="whitespace-nowrap">
                  <Link href={`/app/assignments/workstations/${a.workstation.id}`} className="font-mono hover:underline">{a.workstation.code}</Link>
                  {a.machine && <span className="block text-caption text-fg-tertiary">{a.machine.code}</span>}
                </td>
              )}
              <td className="text-caption">{a.line.name}<span className="block text-fg-tertiary">{a.department.name}{a.floor ? ` · ${a.floor.name}` : ""}</span></td>
              <td className="whitespace-nowrap text-caption">{formatDateTime(a.startAt, locale)}</td>
              <td className="whitespace-nowrap text-caption">
                {a.endAt ? formatDateTime(a.endAt, locale) : <span className="text-[var(--risk-low)]">Current</span>}
                <span className="block text-fg-tertiary">{duration(a.startAt, a.endAt)}</span>
              </td>
              <td><AssignmentStatusBadge status={a.status} /></td>
              <td className="text-caption text-fg-secondary min-w-[160px]">
                {a.previousAssignmentId && <span className="block text-fg-tertiary">Transfer in</span>}
                {a.reason ?? "—"}
                {a.endReason && <span className="block text-fg-tertiary">{a.status === "Transferred" ? "Moved out: " : "Ended: "}{a.endReason}</span>}
              </td>
              <td className="text-caption text-fg-secondary">
                {a.assignedByEmail}
                {a.endedByEmail && a.endedByEmail !== a.assignedByEmail && <span className="block text-fg-tertiary">ended by {a.endedByEmail}</span>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
