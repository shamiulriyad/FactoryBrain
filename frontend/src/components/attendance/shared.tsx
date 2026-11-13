"use client";

// Building blocks for shifts, attendance, breaks and overtime: the section
// tabs, badges, the worker picker, the punch / correction / overtime dialogs
// and the tables the pages share. The API checks every rule again; the UI
// only avoids offering what it would refuse.

import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState, type ReactNode } from "react";
import { FlaskConical, Search } from "lucide-react";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { useResource } from "@/lib/useResource";
import { apiJson, errorMessage } from "@/lib/api";
import { useCan } from "@/lib/auth";
import { cn } from "@/lib/cn";
import { formatDate, formatDateTime } from "@/lib/format";
import { P, type PagedResult } from "@/lib/access";
import type { Worker } from "@/lib/workers";
import {
  BREAK_TYPES,
  EVENT_TYPE_LABEL,
  OVERTIME_STATUS_LABEL,
  OVERTIME_STATUS_TONE,
  SESSION_STATUS_LABEL,
  SESSION_STATUS_TONE,
  clock,
  isoToLocal,
  localToIso,
  minutes,
  newKey,
  shiftWindow,
  type AttendanceSession,
  type BreakSession,
  type Correction,
  type EventResult,
  type Overtime,
  type ShiftAssignment
} from "@/lib/attendance";

// ─── Section tabs ─────────────────────────────────────────────────────────

const SECTIONS: { href: string; label: string; perms: string[]; exact?: boolean }[] = [
  { href: "/app/attendance", label: "Dashboard", perms: [P.AttendanceView], exact: true },
  { href: "/app/attendance/records", label: "Records", perms: [P.AttendanceView] },
  { href: "/app/attendance/breaks", label: "Breaks", perms: [P.AttendanceView] },
  { href: "/app/attendance/corrections", label: "Corrections", perms: [P.AttendanceManage, P.AttendanceApprove] },
  { href: "/app/attendance/shifts", label: "Shifts", perms: [P.AttendanceView, P.ShiftsManage, P.ShiftsAssign] },
  { href: "/app/attendance/shift-assignments", label: "Shift assignments", perms: [P.AttendanceView, P.ShiftsAssign] },
  { href: "/app/attendance/overtime", label: "Overtime", perms: [P.OvertimeView, P.OvertimeApprove], exact: true },
  { href: "/app/attendance/overtime/approvals", label: "OT approvals", perms: [P.OvertimeApprove] },
  { href: "/app/attendance/simulator", label: "RFID simulator", perms: [P.AttendanceManage] },
  { href: "/app/attendance/reports", label: "Reports", perms: [P.AttendanceView] }
];

function SectionLink({ s, active }: { s: (typeof SECTIONS)[number]; active: boolean }) {
  const allowed = useCan(...s.perms);
  if (!allowed) return null;
  return (
    <Link href={s.href} aria-current={active ? "page" : undefined}
      className={cn("px-3 h-9 inline-flex items-center whitespace-nowrap text-caption border-b-2 -mb-px",
        active ? "border-[var(--accent)] text-fg-primary font-medium" : "border-transparent text-fg-secondary hover:text-fg-primary")}>
      {s.label}
    </Link>
  );
}

export function AttendanceTabs({ className }: { className?: string }) {
  const pathname = usePathname() ?? "";
  return (
    <nav aria-label="Attendance sections" className={cn("flex items-center gap-1 border-b border-border-subtle overflow-x-auto", className)} data-testid="attendance-tabs">
      {SECTIONS.map((s) => (
        <SectionLink key={s.href} s={s} active={s.exact ? pathname === s.href : pathname === s.href || pathname.startsWith(s.href + "/")} />
      ))}
    </nav>
  );
}

// ─── URL-backed filters ───────────────────────────────────────────────────

/** Read and write the page's query string (filters survive reloads and links). */
export function useQueryState(basePath: string) {
  const router = useRouter();
  const search = useSearchParams();
  const get = (k: string) => search?.get(k) ?? "";
  function set(next: Record<string, string | null>, resetPage = true) {
    const params = new URLSearchParams(search?.toString() ?? "");
    for (const [k, v] of Object.entries(next)) {
      if (v) params.set(k, v);
      else params.delete(k);
    }
    if (resetPage) params.delete("page");
    router.replace(`${basePath}${params.toString() ? `?${params}` : ""}`);
  }
  return { get, set, page: Math.max(1, Number(search?.get("page")) || 1) };
}

/** A search box that writes to the URL after a short pause. */
export function SearchBox({ value, onChange, placeholder, label, testId }: { value: string; onChange: (v: string) => void; placeholder: string; label: string; testId?: string }) {
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  useEffect(() => {
    if (draft === value) return;
    const id = window.setTimeout(() => onChange(draft.trim()), 300);
    return () => window.clearTimeout(id);
  }, [draft]); // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <div className="relative flex-1 min-w-[200px]">
      <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
      <input type="search" value={draft} onChange={(e) => setDraft(e.target.value)} placeholder={placeholder} aria-label={label}
        className="control !h-8 !pl-8 !text-caption" data-testid={testId} />
    </div>
  );
}

export function DateFilter({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <label className="flex items-center gap-2 text-caption text-fg-tertiary">
      {label} <input type="date" value={value} onChange={(e) => onChange(e.target.value)} className="control !h-8 !w-auto !text-caption" />
    </label>
  );
}

// ─── Badges ───────────────────────────────────────────────────────────────

export function SessionStatusBadge({ status }: { status: string }) {
  return <Badge tone={SESSION_STATUS_TONE[status] ?? "neutral"} dot testId="session-status">{SESSION_STATUS_LABEL[status] ?? status}</Badge>;
}

export function OvertimeStatusBadge({ status }: { status: string }) {
  return <Badge tone={OVERTIME_STATUS_TONE[status] ?? "neutral"} dot testId="overtime-status">{OVERTIME_STATUS_LABEL[status] ?? status}</Badge>;
}

/** SIMULATED is always spelled out; it is never shown as real presence. */
export function SimulatedBadge({ className }: { className?: string }) {
  return (
    <span className={cn("inline-flex items-center gap-1 h-5 px-1.5 rounded-sm border border-[var(--risk-medium)] text-[var(--risk-medium)] text-[10px] font-semibold tracking-wide", className)}
      title="Simulated event: not a real reader" data-testid="simulated-badge">
      <FlaskConical size={10} aria-hidden /> SIMULATED
    </span>
  );
}

export function SourceBadge({ source, simulated }: { source?: string | null; simulated?: boolean }) {
  if (!source) return <span className="text-fg-tertiary">—</span>;
  if (simulated || source === "Simulator") return <SimulatedBadge />;
  const tone = source === "Device" ? "success" : source === "Correction" ? "accent" : source === "Manual" ? "info" : "neutral";
  return <Badge tone={tone}>{source === "Device" ? "Reader (real)" : source}</Badge>;
}

export function OutcomeBadge({ outcome }: { outcome: string }) {
  return <Badge tone={outcome === "Accepted" ? "success" : outcome === "Duplicate" ? "neutral" : "danger"}>{outcome}</Badge>;
}

export function SessionFlags({ s }: { s: AttendanceSession }) {
  return (
    <span className="inline-flex flex-wrap items-center gap-1">
      {s.isLate && <Badge tone="warning">Late {minutes(s.figures.lateMinutes)}</Badge>}
      {s.isEarlyDeparture && <Badge tone="warning">Left early {minutes(s.figures.earlyDepartureMinutes)}</Badge>}
      {s.onBreak && <Badge tone="info">On break</Badge>}
      {s.needsReview && <Badge tone="danger" title={s.reviewReason ?? undefined}>Needs review</Badge>}
      {s.isCorrected && <Badge tone="accent">Corrected</Badge>}
      {s.hasSimulatedEvents && <SimulatedBadge />}
    </span>
  );
}

// ─── Worker picker ────────────────────────────────────────────────────────

/** Active workers in the caller's scope (optionally one factory), searchable. */
export function AnyWorkerPicker({ factoryId, value, onChange, lineId }: { factoryId?: string; value: string; onChange: (w: Worker | null) => void; lineId?: string }) {
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  useEffect(() => {
    const id = window.setTimeout(() => setDebounced(search.trim()), 250);
    return () => window.clearTimeout(id);
  }, [search]);
  const p = new URLSearchParams({ status: "Active", pageSize: "30", sort: "name" });
  if (factoryId) p.set("factoryId", factoryId);
  if (lineId) p.set("lineId", lineId);
  if (debounced) p.set("search", debounced);
  const list = useResource<PagedResult<Worker>>(`/api/workers?${p}`);
  return (
    <div className="space-y-2">
      <div className="relative">
        <Search size={13} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
        <Input aria-label="Search workers" placeholder="Search Worker ID or name" value={search} onChange={(e) => setSearch(e.target.value)} className="!pl-8" data-testid="att-worker-search" />
      </div>
      {list.loading ? (
        <SkeletonRows rows={3} rowHeight="h-9" />
      ) : list.error ? (
        <p className="text-caption text-[var(--risk-high)]">{errorMessage(list.error)}</p>
      ) : (list.data?.items.length ?? 0) === 0 ? (
        <p className="text-caption text-fg-tertiary">{debounced ? "No active worker matches." : "No active workers in your scope."}</p>
      ) : (
        <ul role="listbox" aria-label="Workers" className="max-h-52 overflow-y-auto divide-y divide-border-subtle rounded-[var(--radius-control)] border border-border-subtle" data-testid="att-worker-picker">
          {list.data!.items.map((w) => (
            <li key={w.id} role="option" aria-selected={w.id === value}>
              <button type="button" onClick={() => onChange(w.id === value ? null : w)}
                className={cn("w-full text-left px-3 py-2 flex items-center gap-2 text-caption", w.id === value ? "bg-[var(--accent-soft)]" : "hover:bg-surface-2")}
                data-testid={`att-worker-${w.workerCode}`}>
                <span className="font-mono text-fg-primary">{w.workerCode}</span>
                <span className="text-body text-fg-primary">{w.fullName}</span>
                <span className="ml-auto text-fg-tertiary">{w.line ? w.line.name : ""}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function WhenInput({ label, value, onChange, hint }: { label: string; value: string; onChange: (v: string) => void; hint?: ReactNode }) {
  return (
    <Field label={label} hint={hint}>
      {({ id, describedBy }) => <Input id={id} type="datetime-local" aria-describedby={describedBy} value={value} onChange={(e) => onChange(e.target.value)} />}
    </Field>
  );
}

// ─── Punch dialog (manual check-in / check-out / break) ───────────────────

export type PunchAction = "check-in" | "check-out" | "break-start" | "break-end";
const PUNCH_LABEL: Record<PunchAction, string> = { "check-in": "Check in", "check-out": "Check out", "break-start": "Start break", "break-end": "End break" };

/** Records a manual punch. The idempotency key is made once per dialog, so a retry can't record twice. */
export function PunchDialog({ open, action, worker, factoryId, onClose, onDone }: {
  open: boolean;
  action: PunchAction;
  worker?: { id: string; workerCode: string; workerName: string };
  factoryId?: string;
  onClose: () => void;
  onDone: (r: EventResult) => void;
}) {
  const toast = useToast();
  const [picked, setPicked] = useState<Worker | null>(null);
  const [when, setWhen] = useState("");
  const [breakType, setBreakType] = useState("Lunch");
  const [note, setNote] = useState("");
  const [key, setKey] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setPicked(null);
    setWhen("");
    setNote("");
    setError(null);
    setKey(newKey(`ui-${action}`));
  }, [open, action]);

  const workerId = worker?.id ?? picked?.id ?? "";
  const isBreak = action.startsWith("break");

  async function save() {
    if (!workerId) return;
    setBusy(true);
    setError(null);
    try {
      const path = isBreak ? `/api/breaks/${action === "break-start" ? "start" : "end"}` : `/api/attendance/${action}`;
      const body = isBreak
        ? { workerId, breakType: action === "break-start" ? breakType : undefined, occurredAt: localToIso(when), idempotencyKey: key, note: note.trim() || undefined }
        : { workerId, occurredAt: localToIso(when), idempotencyKey: key, note: note.trim() || undefined };
      const r = await apiJson<EventResult>(path, { method: "POST", body });
      const who = worker?.workerCode ?? picked?.workerCode ?? "";
      toast.push({
        tone: r.event.outcome === "Accepted" ? "success" : "info",
        title: r.event.outcome === "Duplicate" ? `${who}: already recorded (duplicate)` : `${who}: ${EVENT_TYPE_LABEL[r.event.eventType] ?? r.event.eventType} recorded`,
        description: r.event.outcomeMessage ?? undefined
      });
      onDone(r);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} locked={busy} testId="punch-dialog" className="sm:max-w-xl"
      title={`${PUNCH_LABEL[action]}${worker ? ` · ${worker.workerCode}` : ""}`}
      description={worker ? worker.workerName : "Recorded as a manual entry with your name, and audited."}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void save()} disabled={busy || !workerId} data-testid="punch-confirm">{busy ? "Saving…" : PUNCH_LABEL[action]}</Button>
        </div>
      }>
      <div className="space-y-3">
        {!worker && <Field label="Worker">{() => <AnyWorkerPicker factoryId={factoryId} value={picked?.id ?? ""} onChange={setPicked} />}</Field>}
        <div className="grid gap-3 sm:grid-cols-2">
          <WhenInput label="Time" value={when} onChange={setWhen} hint="Leave empty for now. Up to the policy's backdate limit (7 days by default)." />
          {action === "break-start" && (
            <Field label="Break type">
              {({ id }) => (
                <Select id={id} value={breakType} onChange={(e) => setBreakType(e.target.value)}>
                  {BREAK_TYPES.map((b) => <option key={b} value={b}>{b}</option>)}
                </Select>
              )}
            </Field>
          )}
        </div>
        <Field label="Note (optional)">{({ id }) => <TextArea id={id} rows={2} maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />}</Field>
        {error && <Callout tone="danger" role="alert" testId="punch-error">{error}</Callout>}
      </div>
    </Modal>
  );
}

// ─── Corrections ──────────────────────────────────────────────────────────

/** Request a correction of a session, or (no session) a session for a day without punches. Applied only after someone else approves. */
export function CorrectionDialog({ open, session, worker, onClose, onDone }: {
  open: boolean;
  session?: AttendanceSession | null;
  worker?: { id: string; workerCode: string; workerName: string };
  onClose: () => void;
  onDone: (c: Correction) => void;
}) {
  const toast = useToast();
  const [checkIn, setCheckIn] = useState("");
  const [checkOut, setCheckOut] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setCheckIn(isoToLocal(session?.checkInAt));
    setCheckOut(isoToLocal(session?.checkOutAt));
    setReason("");
    setError(null);
  }, [open, session]);

  const missingDay = !session;
  const changed = missingDay ? !!checkIn && !!checkOut : isoToLocal(session!.checkInAt) !== checkIn || isoToLocal(session!.checkOutAt) !== checkOut;

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const c = missingDay
        ? await apiJson<Correction>("/api/attendance/corrections", { method: "POST", body: { workerId: worker!.id, checkInAt: localToIso(checkIn), checkOutAt: localToIso(checkOut), reason: reason.trim() } })
        : await apiJson<Correction>(`/api/attendance/${session!.id}/correction`, {
            method: "POST",
            body: {
              checkInAt: isoToLocal(session!.checkInAt) !== checkIn ? localToIso(checkIn) ?? null : null,
              checkOutAt: isoToLocal(session!.checkOutAt) !== checkOut ? localToIso(checkOut) ?? null : null,
              reason: reason.trim()
            }
          });
      toast.push({ tone: "success", title: "Correction requested", description: "It applies once another authorised person approves it." });
      onDone(c);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  const who = session ? `${session.workerCode} · ${formatDate(session.workDate)}` : worker ? `${worker.workerCode} · a day without punches` : "";
  return (
    <Modal open={open} onClose={onClose} locked={busy} testId="correction-dialog" className="sm:max-w-xl"
      title="Request an attendance correction" description={who}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void save()} disabled={busy || !changed || reason.trim().length < 3} data-testid="correction-confirm">{busy ? "Sending…" : "Request correction"}</Button>
        </div>
      }>
      <div className="space-y-3">
        <Callout tone="info">The original punches are kept. Nothing changes until a second person approves; the request, the decision and the original values are audited.</Callout>
        <div className="grid gap-3 sm:grid-cols-2">
          <WhenInput label="Check-in" value={checkIn} onChange={setCheckIn} />
          <WhenInput label="Check-out" value={checkOut} onChange={setCheckOut} />
        </div>
        <Field label="Reason" hint="Required: what happened, and how you know (e.g. gate log, supervisor confirmation).">
          {({ id, describedBy }) => <TextArea id={id} aria-describedby={describedBy} rows={3} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} data-testid="correction-reason" />}
        </Field>
        {error && <Callout tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}

// ─── Overtime ─────────────────────────────────────────────────────────────

export function OvertimeRequestDialog({ open, worker, factoryId, onClose, onDone }: {
  open: boolean;
  worker?: { id: string; workerCode: string; workerName: string };
  factoryId?: string;
  onClose: () => void;
  onDone: (o: Overtime) => void;
}) {
  const toast = useToast();
  const [picked, setPicked] = useState<Worker | null>(null);
  const [start, setStart] = useState("");
  const [end, setEnd] = useState("");
  const [reason, setReason] = useState("");
  const [submit, setSubmit] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setPicked(null);
    setStart("");
    setEnd("");
    setReason("");
    setSubmit(true);
    setError(null);
  }, [open]);

  const workerId = worker?.id ?? picked?.id ?? "";
  const planned = start && end ? Math.round((new Date(end).getTime() - new Date(start).getTime()) / 60000) : 0;

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const o = await apiJson<Overtime>("/api/overtime/request", { method: "POST", body: { workerId, plannedStart: localToIso(start), plannedEnd: localToIso(end), reason: reason.trim(), submit } });
      toast.push({ tone: "success", title: `Overtime ${submit ? "requested" : "saved as draft"} for ${o.workerCode}`, description: `${minutes(o.requestedMinutes)} · ${OVERTIME_STATUS_LABEL[o.status]}` });
      if (o.warnings.length) toast.push({ tone: "info", title: "Policy warning", description: o.warnings.join(" ") });
      onDone(o);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} locked={busy} testId="overtime-dialog" className="sm:max-w-xl"
      title={`Request overtime${worker ? ` · ${worker.workerCode}` : ""}`} description={worker?.workerName}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void save()} disabled={busy || !workerId || planned <= 0 || reason.trim().length < 3} data-testid="overtime-confirm">{busy ? "Saving…" : submit ? "Request" : "Save draft"}</Button>
        </div>
      }>
      <div className="space-y-3">
        {!worker && <Field label="Worker">{() => <AnyWorkerPicker factoryId={factoryId} value={picked?.id ?? ""} onChange={setPicked} />}</Field>}
        <div className="grid gap-3 sm:grid-cols-2">
          <WhenInput label="Planned start" value={start} onChange={setStart} />
          <WhenInput label="Planned end" value={end} onChange={setEnd} hint={planned > 0 ? `${minutes(planned)} requested` : undefined} />
        </div>
        <Field label="Reason">{({ id }) => <TextArea id={id} rows={2} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} data-testid="overtime-reason" />}</Field>
        <label className="flex items-center gap-2 text-caption text-fg-secondary">
          <input type="checkbox" checked={submit} onChange={(e) => setSubmit(e.target.checked)} /> Send for approval now
        </label>
        <p className="text-caption text-fg-tertiary">{OVERTIME_NOTE_SHORT}</p>
        {error && <Callout tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}

const OVERTIME_NOTE_SHORT = "Approval limits the minutes; actual minutes come from attendance inside the approved window, breaks excluded. HR confirms payable minutes separately.";

export type OvertimeAction = "approve" | "reject" | "cancel" | "submit" | "confirm-payable";
const OT_ACTION_LABEL: Record<OvertimeAction, string> = { approve: "Approve", reject: "Reject", cancel: "Cancel request", submit: "Send for approval", "confirm-payable": "Confirm payable minutes" };

export function OvertimeActionDialog({ open, action, overtime, onClose, onDone }: {
  open: boolean;
  action: OvertimeAction;
  overtime: Overtime | null;
  onClose: () => void;
  onDone: (o: Overtime) => void;
}) {
  const toast = useToast();
  const [mins, setMins] = useState("");
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open || !overtime) return;
    setMins(action === "approve" ? String(overtime.requestedMinutes) : action === "confirm-payable" ? String(overtime.payableSuggestionMinutes ?? 0) : "");
    setText("");
    setError(null);
  }, [open, action, overtime]);

  if (!overtime) return null;
  const needsReason = action === "reject" || action === "cancel";
  const needsMinutes = action === "approve" || action === "confirm-payable";
  const cap = action === "approve" ? overtime.requestedMinutes : Math.min(overtime.approvedMinutes ?? 0, overtime.actualMinutes ?? 0);
  const n = Number(mins);
  const valid = (!needsMinutes || (Number.isInteger(n) && n >= 0 && n <= cap && (action !== "approve" || n > 0))) && (!needsReason || text.trim().length >= 3);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const body =
        action === "approve" ? { approvedMinutes: n, note: text.trim() || undefined }
        : action === "confirm-payable" ? { payableMinutes: n, note: text.trim() || undefined }
        : needsReason ? { reason: text.trim() }
        : {};
      const o = await apiJson<Overtime>(`/api/overtime/${overtime!.id}/${action}`, { method: "POST", body });
      toast.push({ tone: "success", title: `${o.workerCode}: ${OVERTIME_STATUS_LABEL[o.status] ?? o.status}`, description: action === "confirm-payable" ? `${minutes(o.payableMinutes)} payable, confirmed` : undefined });
      onDone(o);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} locked={busy} testId="overtime-action-dialog" className="sm:max-w-lg"
      title={`${OT_ACTION_LABEL[action]} · ${overtime.workerCode}`}
      description={`${overtime.workerName} · ${formatDateTime(overtime.plannedStart)} → ${clock(overtime.plannedEnd)} · ${minutes(overtime.requestedMinutes)} requested`}
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Close</Button>
          <Button variant={action === "reject" || action === "cancel" ? "danger" : "primary"} onClick={() => void save()} disabled={busy || !valid} data-testid="overtime-action-confirm">
            {busy ? "Saving…" : OT_ACTION_LABEL[action]}
          </Button>
        </div>
      }>
      <div className="space-y-3">
        <p className="text-caption text-fg-secondary">Reason: {overtime.reason}</p>
        {overtime.warnings.length > 0 && <Callout tone="warning" title="Policy warnings">{overtime.warnings.join(" ")}</Callout>}
        {needsMinutes && (
          <Field label={action === "approve" ? "Approved minutes" : "Payable minutes"}
            hint={action === "approve" ? `Up to ${overtime.requestedMinutes} (all of the request).`
              : `Up to ${cap}: the lower of approved (${overtime.approvedMinutes ?? 0}) and actual (${overtime.actualMinutes ?? 0}). Suggestion after rounding: ${overtime.payableSuggestionMinutes ?? 0}.`}>
            {({ id, describedBy }) => <Input id={id} aria-describedby={describedBy} type="number" min={0} max={cap} value={mins} onChange={(e) => setMins(e.target.value)} data-testid="overtime-minutes" />}
          </Field>
        )}
        {action !== "submit" && (
          <Field label={needsReason ? "Reason" : "Note (optional)"}>
            {({ id }) => <TextArea id={id} rows={2} maxLength={500} value={text} onChange={(e) => setText(e.target.value)} data-testid="overtime-note" />}
          </Field>
        )}
        {error && <Callout tone="danger" role="alert">{error}</Callout>}
      </div>
    </Modal>
  );
}

/** The action buttons an overtime row offers, from the API's capabilities. */
export function OvertimeActions({ o, onAction }: { o: Overtime; onAction: (a: OvertimeAction, o: Overtime) => void }) {
  return (
    <span className="inline-flex flex-wrap gap-1 justify-end">
      {o.can.submit && <Button size="sm" variant="secondary" onClick={() => onAction("submit", o)}>Submit</Button>}
      {o.can.approve && <Button size="sm" variant="primary" onClick={() => onAction("approve", o)} data-testid={`ot-approve-${o.workerCode}`}>Approve</Button>}
      {o.can.reject && <Button size="sm" variant="secondary" onClick={() => onAction("reject", o)}>Reject</Button>}
      {o.can.confirmPayable && <Button size="sm" variant="primary" onClick={() => onAction("confirm-payable", o)}>Confirm payable</Button>}
      {o.can.cancel && <Button size="sm" variant="ghost" onClick={() => onAction("cancel", o)}>Cancel</Button>}
    </span>
  );
}

// ─── Tables ───────────────────────────────────────────────────────────────

const th = "px-3 py-2 text-left text-caption font-medium text-fg-tertiary whitespace-nowrap";
const td = "px-3 py-2 align-top text-caption";
export const tableCls = { th, td };

export function SessionTable({ rows, showWorker = true, onCorrect, testId }: { rows: AttendanceSession[]; showWorker?: boolean; onCorrect?: (s: AttendanceSession) => void; testId?: string }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[900px]" data-testid={testId}>
        <thead className="border-b border-border-subtle">
          <tr>
            <th className={th}>Work date</th>
            {showWorker && <th className={th}>Worker</th>}
            <th className={th}>Shift</th>
            <th className={th}>Check-in</th>
            <th className={th}>Check-out</th>
            <th className={th}>Status</th>
            <th className={cn(th, "text-right")}>Worked</th>
            <th className={cn(th, "text-right")}>Breaks</th>
            <th className={cn(th, "text-right")}>After shift</th>
            <th className={cn(th, "text-right")}>Approved OT</th>
            <th className={th}>Flags</th>
            {onCorrect && <th className={th}><span className="sr-only">Actions</span></th>}
          </tr>
        </thead>
        <tbody className="divide-y divide-border-subtle">
          {rows.map((s) => (
            <tr key={s.id} data-testid={`session-${s.workerCode}`}>
              <td className={cn(td, "whitespace-nowrap")}>{formatDate(s.workDate)}</td>
              {showWorker && (
                <td className={td}>
                  <Link href={`/app/attendance/workers/${s.workerId}`} className="hover:underline"><span className="font-mono">{s.workerCode}</span> {s.workerName}</Link>
                </td>
              )}
              <td className={cn(td, "whitespace-nowrap")}>{s.shift ? <span title={shiftWindow(s.shift)}>{s.shift.code}</span> : <span className="text-fg-tertiary">No shift</span>}</td>
              <td className={cn(td, "whitespace-nowrap")}>{clock(s.checkInAt)} {s.checkInSource && <SourceBadge source={s.checkInSource} />}</td>
              <td className={cn(td, "whitespace-nowrap")}>{clock(s.checkOutAt)} {s.checkOutSource && <SourceBadge source={s.checkOutSource} />}</td>
              <td className={td}><SessionStatusBadge status={s.status} /></td>
              <td className={cn(td, "text-right tabular-nums whitespace-nowrap")} title={`Attendance ${minutes(s.figures.attendanceMinutes)}, net of breaks`}>{minutes(s.figures.netWorkingMinutes)}</td>
              <td className={cn(td, "text-right tabular-nums whitespace-nowrap")}>{minutes(s.figures.breakMinutes)}{s.figures.overBreakMinutes > 0 && <span className="text-[var(--risk-medium)]"> (+{s.figures.overBreakMinutes})</span>}</td>
              <td className={cn(td, "text-right tabular-nums whitespace-nowrap")}>{minutes(s.figures.beyondShiftMinutes)}</td>
              <td className={cn(td, "text-right tabular-nums whitespace-nowrap")}>{minutes(s.figures.approvedOvertimeMinutes)}</td>
              <td className={td}><SessionFlags s={s} /></td>
              {onCorrect && <td className={cn(td, "text-right")}><Button size="sm" variant="ghost" onClick={() => onCorrect(s)} data-testid={`correct-${s.workerCode}`}>Correct</Button></td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function BreakTable({ rows, showWorker = true, testId }: { rows: BreakSession[]; showWorker?: boolean; testId?: string }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[720px]" data-testid={testId}>
        <thead className="border-b border-border-subtle">
          <tr>
            {showWorker && <th className={th}>Worker</th>}
            <th className={th}>Type</th>
            <th className={th}>Started</th>
            <th className={th}>Ended</th>
            <th className={cn(th, "text-right")}>Allowed</th>
            <th className={cn(th, "text-right")}>Taken</th>
            <th className={th}>Status</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border-subtle">
          {rows.map((b) => (
            <tr key={b.id} data-testid={`break-${b.workerCode}`}>
              {showWorker && <td className={td}><Link href={`/app/attendance/workers/${b.workerId}`} className="hover:underline"><span className="font-mono">{b.workerCode}</span> {b.workerName}</Link></td>}
              <td className={td}>{b.breakType} {b.isSimulated && <SimulatedBadge />}</td>
              <td className={cn(td, "whitespace-nowrap")}>{formatDateTime(b.startAt)}</td>
              <td className={cn(td, "whitespace-nowrap")}>{b.endAt ? clock(b.endAt) : <span className="text-fg-tertiary">running</span>}</td>
              <td className={cn(td, "text-right tabular-nums")}>{minutes(b.allowedMinutes)}</td>
              <td className={cn(td, "text-right tabular-nums")}>{b.actualMinutes === null || b.actualMinutes === undefined ? "—" : minutes(b.actualMinutes)}</td>
              <td className={td}>
                <span className="inline-flex flex-wrap gap-1">
                  <Badge tone={b.status === "Active" ? "info" : b.status === "AutoClosed" ? "warning" : "success"}>{b.status === "AutoClosed" ? "Closed at check-out" : b.status}</Badge>
                  {b.isOverdue && <Badge tone="danger" testId="break-overdue">Over by {minutes(b.overMinutes)}</Badge>}
                </span>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function OvertimeTable({ rows, onAction, testId }: { rows: Overtime[]; onAction?: (a: OvertimeAction, o: Overtime) => void; testId?: string }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[1000px]" data-testid={testId}>
        <thead className="border-b border-border-subtle">
          <tr>
            <th className={th}>Worker</th>
            <th className={th}>Work date</th>
            <th className={th}>Planned</th>
            <th className={th}>Status</th>
            <th className={cn(th, "text-right")}>Requested</th>
            <th className={cn(th, "text-right")}>Approved</th>
            <th className={cn(th, "text-right")}>Actual</th>
            <th className={cn(th, "text-right")}>Payable (HR)</th>
            <th className={th}>Reason</th>
            {onAction && <th className={th}><span className="sr-only">Actions</span></th>}
          </tr>
        </thead>
        <tbody className="divide-y divide-border-subtle">
          {rows.map((o) => (
            <tr key={o.id} data-testid={`ot-${o.workerCode}`}>
              <td className={td}><Link href={`/app/attendance/workers/${o.workerId}`} className="hover:underline"><span className="font-mono">{o.workerCode}</span> {o.workerName}</Link></td>
              <td className={cn(td, "whitespace-nowrap")}>{formatDate(o.workDate)}</td>
              <td className={cn(td, "whitespace-nowrap")}>{clock(o.plannedStart)} – {clock(o.plannedEnd)}</td>
              <td className={td}>
                <span className="inline-flex flex-wrap gap-1"><OvertimeStatusBadge status={o.status} />{o.warnings.length > 0 && <Badge tone="warning" title={o.warnings.join(" ")}>Limit warning</Badge>}</span>
              </td>
              <td className={cn(td, "text-right tabular-nums")}>{minutes(o.requestedMinutes)}</td>
              <td className={cn(td, "text-right tabular-nums")}>{minutes(o.approvedMinutes)}</td>
              <td className={cn(td, "text-right tabular-nums")}>{minutes(o.actualMinutes)}</td>
              <td className={cn(td, "text-right tabular-nums")} title={o.payableConfirmedByEmail ? `Confirmed by ${o.payableConfirmedByEmail}` : o.payableSuggestionMinutes !== null && o.payableSuggestionMinutes !== undefined ? `Suggestion: ${o.payableSuggestionMinutes} min (not confirmed)` : undefined}>
                {o.payableMinutes !== null && o.payableMinutes !== undefined ? minutes(o.payableMinutes) : <span className="text-fg-tertiary">not confirmed</span>}
              </td>
              <td className={cn(td, "max-w-[220px] truncate")} title={o.reason}>{o.reason}</td>
              {onAction && <td className={cn(td, "text-right")}><OvertimeActions o={o} onAction={onAction} /></td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function ShiftAssignmentTable({ rows, showWorker = true, onEnd, testId }: { rows: ShiftAssignment[]; showWorker?: boolean; onEnd?: (a: ShiftAssignment) => void; testId?: string }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full min-w-[760px]" data-testid={testId}>
        <thead className="border-b border-border-subtle">
          <tr>
            {showWorker && <th className={th}>Worker</th>}
            <th className={th}>Shift</th>
            <th className={th}>From</th>
            <th className={th}>To</th>
            <th className={th}>Status</th>
            <th className={th}>Reason</th>
            <th className={th}>By</th>
            {onEnd && <th className={th}><span className="sr-only">Actions</span></th>}
          </tr>
        </thead>
        <tbody className="divide-y divide-border-subtle">
          {rows.map((a) => (
            <tr key={a.id} data-testid={`shift-assignment-${a.workerCode}`}>
              {showWorker && <td className={td}><Link href={`/app/attendance/workers/${a.workerId}`} className="hover:underline"><span className="font-mono">{a.workerCode}</span> {a.workerName}</Link></td>}
              <td className={cn(td, "whitespace-nowrap")}><span className="font-mono">{a.shift.code}</span> <span className="text-fg-tertiary">{shiftWindow(a.shift)}</span></td>
              <td className={cn(td, "whitespace-nowrap")}>{formatDate(a.effectiveFrom)}</td>
              <td className={cn(td, "whitespace-nowrap")}>{a.effectiveTo ? formatDate(a.effectiveTo) : <span className="text-fg-tertiary">open-ended</span>}</td>
              <td className={td}><Badge tone={a.status === "Active" ? "success" : a.status === "Superseded" ? "info" : "neutral"} dot>{a.status}</Badge></td>
              <td className={cn(td, "max-w-[220px] truncate")} title={a.endReason ?? a.reason ?? undefined}>{a.endReason ?? a.reason ?? <span className="text-fg-tertiary">—</span>}</td>
              <td className={cn(td, "text-fg-tertiary")}>{a.endedByEmail ?? a.assignedByEmail}</td>
              {onEnd && <td className={cn(td, "text-right")}>{a.status === "Active" && !a.effectiveTo && <Button size="sm" variant="ghost" onClick={() => onEnd(a)}>End</Button>}</td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
