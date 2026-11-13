"use client";

// Worker shift assignments: who works which shift from when to when, with
// filters, and assigning one worker, several workers, a whole line or a
// department at once. Reassigning ends the current assignment the day
// before; an assignment that would overlap a later one is refused. Nothing
// is overwritten: the history keeps every assignment.

import { useEffect, useMemo, useState } from "react";
import { CalendarRange, RefreshCw, UsersRound, X } from "lucide-react";
import { RequirePermission } from "@/components/auth/RequireRole";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { EmptyState } from "@/components/ui/EmptyState";
import { ErrorState, SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { Pagination } from "@/components/access/shared";
import { useMyFactories, useFactoryOrg } from "@/components/workers/shared";
import { AnyWorkerPicker, AttendanceTabs, DateFilter, SearchBox, ShiftAssignmentTable, useQueryState } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { apiJson, errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { P, type PagedResult } from "@/lib/access";
import type { Worker } from "@/lib/workers";
import { shiftWindow, todayIso, type AssignShiftResult, type Shift, type ShiftAssignment } from "@/lib/attendance";

const PAGE_SIZE = 50;

export default function ShiftAssignmentsPage() {
  return (
    <RequirePermission perms={[P.AttendanceView, P.ShiftsAssign]} what="Shift assignments">
      <Assignments />
    </RequirePermission>
  );
}

function Assignments() {
  const q = useQueryState("/app/attendance/shift-assignments");
  const { factories } = useMyFactories();
  const canAssign = useCan(P.ShiftsAssign);
  const f = { q: q.get("q"), factory: q.get("factory"), shift: q.get("shift"), line: q.get("line"), status: q.get("status"), activeOn: q.get("activeOn") };
  const factoryId = f.factory || (factories?.length === 1 ? factories[0].id : "");
  const { lines } = useFactoryOrg(factoryId || null);
  const shifts = useResource<Shift[]>(factoryId ? `/api/shifts?factoryId=${factoryId}` : null);
  const [assigning, setAssigning] = useState(false);
  const [ending, setEnding] = useState<ShiftAssignment | null>(null);

  const path = useMemo(() => {
    const p = new URLSearchParams({ page: String(q.page), pageSize: String(PAGE_SIZE) });
    if (f.q) p.set("search", f.q);
    if (f.factory) p.set("factoryId", f.factory);
    if (f.shift) p.set("shiftId", f.shift);
    if (f.line) p.set("lineId", f.line);
    if (f.status) p.set("status", f.status);
    if (f.activeOn) p.set("activeOn", f.activeOn);
    return `/api/shift-assignments?${p}`;
  }, [f.q, f.factory, f.shift, f.line, f.status, f.activeOn, q.page]); // eslint-disable-line react-hooks/exhaustive-deps
  const list = useResource<PagedResult<ShiftAssignment>>(path);
  const filtered = !!(f.q || f.shift || f.line || f.status || f.activeOn);

  return (
    <div className="page" data-testid="shift-assignments-page">
      <PageHeader title="Shift assignments" description="Which shift each worker works, with effective dates. Reassigning ends the current assignment the day before the new one starts."
        actions={canAssign && factoryId ? <Button variant="primary" size="sm" iconLeft={<UsersRound size={13} />} onClick={() => setAssigning(true)} data-testid="assign-shift">Assign shift</Button> : undefined} />
      <AttendanceTabs className="mt-4" />
      <Panel className="mt-4" flush title="Assignments" icon={<CalendarRange size={14} />}
        subtitle={list.data ? `${list.data.total} record${list.data.total === 1 ? "" : "s"}` : undefined}
        right={<Button size="sm" variant="secondary" aria-label="Refresh" onClick={() => void list.reload()}><RefreshCw size={13} className={cn(list.refreshing && "animate-spin")} /></Button>}>
        <div className="p-4 flex flex-wrap items-center gap-3 border-b border-border-subtle">
          <SearchBox value={f.q} onChange={(v) => q.set({ q: v || null })} placeholder="Worker ID or name" label="Search shift assignments" />
          {(factories?.length ?? 0) > 1 && (
            <FilterSelect label="Factory" value={f.factory} onChange={(v) => q.set({ factory: v || null, shift: null, line: null })}>
              <option value="">All factories</option>
              {factories!.map((x) => <option key={x.id} value={x.id}>{x.code} · {x.name}</option>)}
            </FilterSelect>
          )}
          {factoryId && (
            <>
              <FilterSelect label="Shift" value={f.shift} onChange={(v) => q.set({ shift: v || null })}>
                <option value="">All</option>
                {(shifts.data ?? []).map((s) => <option key={s.id} value={s.id}>{s.code} · {s.name}</option>)}
              </FilterSelect>
              <FilterSelect label="Line" value={f.line} onChange={(v) => q.set({ line: v || null })}>
                <option value="">All</option>
                {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </FilterSelect>
            </>
          )}
          <FilterSelect label="Status" value={f.status} onChange={(v) => q.set({ status: v || null })}>
            <option value="">Any</option>
            <option value="Active">Active</option>
            <option value="Superseded">Superseded</option>
            <option value="Ended">Ended</option>
          </FilterSelect>
          <DateFilter label="Active on" value={f.activeOn} onChange={(v) => q.set({ activeOn: v || null })} />
          {filtered && <Button size="sm" variant="ghost" onClick={() => q.set({ q: null, shift: null, line: null, status: null, activeOn: null })}>Clear filters</Button>}
        </div>
        {list.loading ? <div className="p-4"><SkeletonRows rows={8} rowHeight="h-11" /></div>
          : list.error && !list.data ? <ErrorState className="m-4" title="Couldn't load shift assignments" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
          : (list.data?.items.length ?? 0) === 0 ? <EmptyState className="m-4" icon={<CalendarRange size={18} />} title={filtered ? "No assignments match" : "No shift assignments yet"} body={filtered ? "Change or clear the filters." : "Assign workers to shifts to schedule them."} />
          : (
            <>
              <ShiftAssignmentTable rows={list.data!.items} onEnd={canAssign ? setEnding : undefined} testId="shift-assignments-table" />
              <Pagination page={list.data!.page} pageSize={list.data!.pageSize} total={list.data!.total} onPage={(p) => q.set({ page: String(p) }, false)} />
            </>
          )}
      </Panel>
      {factoryId && <AssignShiftDialog open={assigning} factoryId={factoryId} shifts={(shifts.data ?? []).filter((s) => s.isActive)} onClose={() => setAssigning(false)} onDone={() => void list.reload()} />}
      <EndShiftDialog assignment={ending} onClose={() => setEnding(null)} onDone={() => { setEnding(null); void list.reload(); }} />
    </div>
  );
}

type Target = "workers" | "line" | "department";

function AssignShiftDialog({ open, factoryId, shifts, onClose, onDone }: { open: boolean; factoryId: string; shifts: Shift[]; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const { departments, lines } = useFactoryOrg(open ? factoryId : null);
  const [shiftId, setShiftId] = useState("");
  const [target, setTarget] = useState<Target>("workers");
  const [workers, setWorkers] = useState<Worker[]>([]);
  const [line, setLine] = useState("");
  const [department, setDepartment] = useState("");
  const [from, setFrom] = useState(todayIso());
  const [to, setTo] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<AssignShiftResult | null>(null);

  useEffect(() => {
    if (!open) return;
    setShiftId(shifts[0]?.id ?? "");
    setTarget("workers");
    setWorkers([]);
    setLine("");
    setDepartment("");
    setFrom(todayIso());
    setTo("");
    setReason("");
    setError(null);
    setResult(null);
  }, [open]); // eslint-disable-line react-hooks/exhaustive-deps

  const ready = !!shiftId && !!from && (target === "workers" ? workers.length > 0 : target === "line" ? !!line : !!department);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const r = await apiJson<AssignShiftResult>("/api/shift-assignments", {
        method: "POST",
        body: {
          shiftId, effectiveFrom: from, effectiveTo: to || null, reason: reason.trim() || null,
          workerIds: target === "workers" ? workers.map((w) => w.id) : null,
          lineId: target === "line" ? line : null,
          departmentId: target === "department" ? department : null
        }
      });
      setResult(r);
      toast.push({ tone: r.assigned > 0 ? "success" : "info", title: `${r.assigned} of ${r.requested} worker${r.requested === 1 ? "" : "s"} assigned`, description: r.skipped ? `${r.skipped} skipped: see the details.` : undefined });
      onDone();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} locked={busy} testId="assign-shift-dialog" className="sm:max-w-2xl" title="Assign a shift"
      description="Each worker is assigned on their own: one conflict doesn't stop the others."
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>{result ? "Close" : "Cancel"}</Button>
          {!result && <Button variant="primary" onClick={() => void save()} disabled={busy || !ready} data-testid="assign-shift-confirm">{busy ? "Assigning…" : "Assign"}</Button>}
        </div>
      }>
      {result ? (
        <div className="space-y-2" data-testid="assign-shift-result">
          <p className="text-body">{result.assigned} assigned, {result.skipped} skipped.</p>
          <ul className="max-h-72 overflow-y-auto divide-y divide-border-subtle rounded-[var(--radius-control)] border border-border-subtle">
            {result.results.map((r) => (
              <li key={r.workerId} className="px-3 py-2 flex items-center gap-2 text-caption">
                <span className="font-mono">{r.workerCode}</span>
                <Badge tone={r.outcome === "Assigned" ? "success" : "warning"}>{r.outcome}</Badge>
                {r.message && <span className="text-fg-tertiary">{r.message}</span>}
              </li>
            ))}
          </ul>
        </div>
      ) : (
        <div className="space-y-3">
          {shifts.length === 0 && <Callout tone="warning">No active shift in this factory. Create one under Shifts first.</Callout>}
          <div className="grid gap-3 sm:grid-cols-3">
            <Field label="Shift" className="sm:col-span-3">
              {({ id }) => (
                <Select id={id} value={shiftId} onChange={(e) => setShiftId(e.target.value)} data-testid="assign-shift-select">
                  {shifts.map((s) => <option key={s.id} value={s.id}>{s.code} · {s.name} · {shiftWindow(s)}</option>)}
                </Select>
              )}
            </Field>
            <Field label="From">{({ id }) => <Input id={id} type="date" value={from} onChange={(e) => setFrom(e.target.value)} />}</Field>
            <Field label="To (optional)" hint="Empty: open-ended">{({ id, describedBy }) => <Input id={id} aria-describedby={describedBy} type="date" value={to} min={from} onChange={(e) => setTo(e.target.value)} />}</Field>
            <Field label="Who">
              {({ id }) => (
                <Select id={id} value={target} onChange={(e) => setTarget(e.target.value as Target)} data-testid="assign-shift-target">
                  <option value="workers">Chosen workers</option>
                  <option value="line">A whole line</option>
                  <option value="department">A department</option>
                </Select>
              )}
            </Field>
          </div>
          {target === "workers" && (
            <>
              {workers.length > 0 && (
                <div className="flex flex-wrap gap-1.5" data-testid="assign-shift-chosen">
                  {workers.map((w) => (
                    <span key={w.id} className="inline-flex items-center gap-1 h-7 pl-2 pr-1 rounded-full bg-surface-2 text-caption">
                      <span className="font-mono">{w.workerCode}</span> {w.fullName}
                      <button type="button" aria-label={`Remove ${w.workerCode}`} className="p-0.5 rounded-full hover:bg-surface-2" onClick={() => setWorkers((xs) => xs.filter((x) => x.id !== w.id))}><X size={12} /></button>
                    </span>
                  ))}
                </div>
              )}
              <Field label="Add workers">{() => <AnyWorkerPicker factoryId={factoryId} value="" onChange={(w) => w && setWorkers((xs) => (xs.some((x) => x.id === w.id) ? xs : [...xs, w]))} />}</Field>
            </>
          )}
          {target === "line" && (
            <Field label="Line" hint="All active workers on the line">
              {({ id, describedBy }) => (
                <Select id={id} aria-describedby={describedBy} value={line} onChange={(e) => setLine(e.target.value)}>
                  <option value="">Choose…</option>
                  {(lines.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
                </Select>
              )}
            </Field>
          )}
          {target === "department" && (
            <Field label="Department" hint="All active workers in the department">
              {({ id, describedBy }) => (
                <Select id={id} aria-describedby={describedBy} value={department} onChange={(e) => setDepartment(e.target.value)}>
                  <option value="">Choose…</option>
                  {(departments.data ?? []).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
                </Select>
              )}
            </Field>
          )}
          <Field label="Reason (optional)">{({ id }) => <TextArea id={id} rows={2} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />}</Field>
          {error && <Callout tone="danger" role="alert">{error}</Callout>}
        </div>
      )}
    </Modal>
  );
}

function EndShiftDialog({ assignment, onClose, onDone }: { assignment: ShiftAssignment | null; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [to, setTo] = useState(todayIso());
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    setTo(todayIso());
    setReason("");
    setError(null);
  }, [assignment]);
  if (!assignment) return null;

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await apiJson(`/api/shift-assignments/${assignment!.id}/end`, { method: "POST", body: { effectiveTo: to, reason: reason.trim() } });
      toast.push({ tone: "success", title: `${assignment!.workerCode}: ${assignment!.shift.code} ends ${to}` });
      onDone();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open onClose={onClose} locked={busy} testId="end-shift-dialog" title={`End ${assignment.workerCode}'s ${assignment.shift.code} shift`} description={assignment.workerName}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="danger" onClick={() => void save()} disabled={busy || !to || reason.trim().length < 3}>{busy ? "Saving…" : "End assignment"}</Button>
        </div>
      }>
      <div className="space-y-3">
        <Field label="Last day">{({ id }) => <Input id={id} type="date" value={to} min={assignment.effectiveFrom} onChange={(e) => setTo(e.target.value)} />}</Field>
        <Field label="Reason">{({ id }) => <TextArea id={id} rows={2} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} />}</Field>
        {error && <Callout tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}
