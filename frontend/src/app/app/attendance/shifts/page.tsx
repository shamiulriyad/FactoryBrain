"use client";

// Shift management: the factory's shifts (times, overnight, working days,
// grace periods, breaks, status) and its attendance policy (time zone,
// duplicate window, missing-check-out rules, overtime limits). Shifts are
// never deleted: they are deactivated, and only when no current or future
// assignment uses them.

import { useEffect, useState } from "react";
import { CalendarClock, Moon, Pencil, Plus, Settings2, Trash2 } from "lucide-react";
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
import { useMyFactories } from "@/components/workers/shared";
import { AttendanceTabs, tableCls, useQueryState } from "@/components/attendance/shared";
import { useResource } from "@/lib/useResource";
import { useCan } from "@/lib/auth";
import { apiJson, errorMessage } from "@/lib/api";
import { cn } from "@/lib/cn";
import { formatDateTime } from "@/lib/format";
import { P } from "@/lib/access";
import { BREAK_TYPES, DAY_NAMES, minutes, shiftWindow, type AttendancePolicy, type Shift, type ShiftBreak } from "@/lib/attendance";

export default function ShiftsPage() {
  return (
    <RequirePermission perms={[P.AttendanceView, P.ShiftsManage, P.ShiftsAssign]} what="Shifts">
      <Shifts />
    </RequirePermission>
  );
}

function Shifts() {
  const q = useQueryState("/app/attendance/shifts");
  const { factories } = useMyFactories();
  const factoryParam = q.get("factory");
  const factoryId = factoryParam || (factories?.length ? factories[0].id : "");
  const status = q.get("status");
  const canManage = useCan(P.ShiftsManage);
  const list = useResource<Shift[]>(factoryId ? `/api/shifts?factoryId=${factoryId}&includeInactive=true` : null);
  const [editing, setEditing] = useState<Shift | "new" | null>(null);
  const rows = (list.data ?? []).filter((s) => (status === "active" ? s.isActive : status === "inactive" ? !s.isActive : true));

  return (
    <div className="page" data-testid="shifts-page">
      <PageHeader title="Shifts" description="Shift times are in the factory's local time. An end time at or before the start means the shift ends the next day."
        actions={canManage && factoryId ? <Button variant="primary" size="sm" iconLeft={<Plus size={13} />} onClick={() => setEditing("new")} data-testid="shift-new">New shift</Button> : undefined} />
      <AttendanceTabs className="mt-4" />
      <div className="mt-4 flex flex-wrap items-center gap-3">
        {(factories?.length ?? 0) > 1 && (
          <FilterSelect label="Factory" value={factoryId} onChange={(v) => q.set({ factory: v || null })}>
            {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
          </FilterSelect>
        )}
        <FilterSelect label="Status" value={status} onChange={(v) => q.set({ status: v || null })}>
          <option value="">All</option>
          <option value="active">Active</option>
          <option value="inactive">Inactive</option>
        </FilterSelect>
      </div>

      <Panel className="mt-4" flush title="Shifts" icon={<CalendarClock size={14} />} subtitle={list.data ? `${list.data.length} shift${list.data.length === 1 ? "" : "s"}` : undefined}>
        {!factoryId ? <EmptyState className="m-4" title="No factory" body="You need a factory assignment to see shifts." />
          : list.loading ? <div className="p-4"><SkeletonRows rows={4} /></div>
          : list.error && !list.data ? <ErrorState className="m-4" title="Couldn't load shifts" body={errorMessage(list.error)} retryLabel="Try again" onRetry={() => void list.reload()} />
          : rows.length === 0 ? <EmptyState className="m-4" icon={<CalendarClock size={18} />} title="No shifts yet" body={canManage ? "Create the factory's shifts, then assign workers to them." : "An administrator hasn't set up shifts yet."} />
          : (
            <div className="overflow-x-auto">
              <table className="w-full min-w-[900px]" data-testid="shifts-table">
                <thead className="border-b border-border-subtle">
                  <tr>
                    <th className={tableCls.th}>Shift</th><th className={tableCls.th}>Hours</th><th className={tableCls.th}>Working days</th>
                    <th className={cn(tableCls.th, "text-right")}>Net scheduled</th><th className={tableCls.th}>Breaks</th><th className={tableCls.th}>Grace</th>
                    <th className={cn(tableCls.th, "text-right")}>Workers</th><th className={tableCls.th}>Status</th>
                    {canManage && <th className={tableCls.th}><span className="sr-only">Actions</span></th>}
                  </tr>
                </thead>
                <tbody className="divide-y divide-border-subtle">
                  {rows.map((s) => (
                    <tr key={s.id} data-testid={`shift-${s.code}`}>
                      <td className={tableCls.td}><span className="font-mono">{s.code}</span> {s.name}{s.description && <p className="text-fg-tertiary">{s.description}</p>}</td>
                      <td className={cn(tableCls.td, "whitespace-nowrap")}>{shiftWindow(s)} {s.isOvernight && <Moon size={12} className="inline text-fg-tertiary" aria-label="Overnight" />}</td>
                      <td className={tableCls.td}>{s.workingDays.join(", ")}</td>
                      <td className={cn(tableCls.td, "text-right tabular-nums")} title={`${minutes(s.scheduledMinutes)} scheduled − ${minutes(s.unpaidBreakMinutes)} unpaid breaks`}>{minutes(s.netScheduledMinutes)}</td>
                      <td className={tableCls.td}>{s.breaks.length === 0 ? <span className="text-fg-tertiary">—</span> : s.breaks.map((b) => `${b.name} ${b.durationMinutes}′${b.isPaid ? " (paid)" : ""}`).join(", ")}</td>
                      <td className={cn(tableCls.td, "whitespace-nowrap")}>late {s.lateGraceMinutes}′ · early {s.earlyLeaveGraceMinutes}′</td>
                      <td className={cn(tableCls.td, "text-right tabular-nums")}>{s.currentWorkers}</td>
                      <td className={tableCls.td}><Badge tone={s.isActive ? "success" : "neutral"} dot>{s.isActive ? "Active" : "Inactive"}</Badge></td>
                      {canManage && <td className={cn(tableCls.td, "text-right")}><Button size="sm" variant="ghost" iconLeft={<Pencil size={12} />} onClick={() => setEditing(s)} data-testid={`shift-edit-${s.code}`}>Edit</Button></td>}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
      </Panel>

      {factoryId && <PolicyPanel factoryId={factoryId} />}

      <ShiftDialog open={editing !== null} shift={editing === "new" ? null : editing} factoryId={factoryId}
        onClose={() => setEditing(null)} onDone={() => { setEditing(null); void list.reload(); }} />
    </div>
  );
}

// ─── Create / edit ────────────────────────────────────────────────────────

type BreakDraft = { id?: string | null; breakType: string; name: string; startLocal: string; durationMinutes: string; isPaid: boolean };

function ShiftDialog({ open, shift, factoryId, onClose, onDone }: { open: boolean; shift: Shift | null; factoryId: string; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [code, setCode] = useState("");
  const [name, setName] = useState("");
  const [start, setStart] = useState("08:00");
  const [end, setEnd] = useState("17:00");
  const [days, setDays] = useState<string[]>(["Sat", "Sun", "Mon", "Tue", "Wed", "Thu"]);
  const [late, setLate] = useState("10");
  const [early, setEarly] = useState("5");
  const [active, setActive] = useState(true);
  const [description, setDescription] = useState("");
  const [breaks, setBreaks] = useState<BreakDraft[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setCode(shift?.code ?? "");
    setName(shift?.name ?? "");
    setStart(shift?.startLocal ?? "08:00");
    setEnd(shift?.endLocal ?? "17:00");
    setDays(shift?.workingDays ?? ["Sat", "Sun", "Mon", "Tue", "Wed", "Thu"]);
    setLate(String(shift?.lateGraceMinutes ?? 10));
    setEarly(String(shift?.earlyLeaveGraceMinutes ?? 5));
    setActive(shift?.isActive ?? true);
    setDescription(shift?.description ?? "");
    setBreaks((shift?.breaks ?? [{ breakType: "Lunch", name: "Lunch", startLocal: "13:00", durationMinutes: 60, isPaid: false }])
      .map((b) => ({ id: b.id, breakType: b.breakType, name: b.name, startLocal: b.startLocal ?? "", durationMinutes: String(b.durationMinutes), isPaid: b.isPaid })));
    setError(null);
  }, [open, shift]);

  const overnight = start && end && end <= start;
  const toggleDay = (d: string) => setDays((xs) => (xs.includes(d) ? xs.filter((x) => x !== d) : [...xs, d]));

  async function save() {
    setBusy(true);
    setError(null);
    const breakDtos: ShiftBreak[] = breaks.map((b) => ({ id: b.id ?? null, breakType: b.breakType, name: b.name.trim() || b.breakType, startLocal: b.startLocal || null, durationMinutes: Number(b.durationMinutes), isPaid: b.isPaid }));
    const common = { name: name.trim(), startLocal: start, endLocal: end, workingDays: days, lateGraceMinutes: Number(late), earlyLeaveGraceMinutes: Number(early), description: description.trim() || null, breaks: breakDtos };
    try {
      const s = shift
        ? await apiJson<Shift>(`/api/shifts/${shift.id}`, { method: "PUT", body: { ...common, isActive: active } })
        : await apiJson<Shift>("/api/shifts", { method: "POST", body: { ...common, factoryId, code: code.trim().toUpperCase() } });
      toast.push({ tone: "success", title: `Shift ${s.code} ${shift ? "saved" : "created"}`, description: `${shiftWindow(s)} · ${minutes(s.netScheduledMinutes)} net` });
      onDone();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onClose={onClose} locked={busy} testId="shift-dialog" className="sm:max-w-2xl"
      title={shift ? `Edit shift ${shift.code}` : "New shift"} description="Changes apply to sessions recorded from now on; existing sessions keep their figures."
      footer={
        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" onClick={() => void save()} disabled={busy || !name.trim() || (!shift && !code.trim()) || days.length === 0 || start === end} data-testid="shift-save">{busy ? "Saving…" : "Save"}</Button>
        </div>
      }>
      <div className="space-y-4">
        <div className="grid gap-3 sm:grid-cols-3">
          <Field label="Code" hint="A–Z, 0–9, - or _ (up to 16)">
            {({ id, describedBy }) => <Input id={id} aria-describedby={describedBy} value={code} onChange={(e) => setCode(e.target.value.toUpperCase())} disabled={!!shift} maxLength={16} data-testid="shift-code" />}
          </Field>
          <Field label="Name" className="sm:col-span-2">{({ id }) => <Input id={id} value={name} onChange={(e) => setName(e.target.value)} maxLength={60} data-testid="shift-name" />}</Field>
        </div>
        <div className="grid gap-3 sm:grid-cols-4">
          <Field label="Starts">{({ id }) => <Input id={id} type="time" value={start} onChange={(e) => setStart(e.target.value)} data-testid="shift-start" />}</Field>
          <Field label="Ends" hint={overnight ? "Next day (overnight)" : undefined}>{({ id, describedBy }) => <Input id={id} aria-describedby={describedBy} type="time" value={end} onChange={(e) => setEnd(e.target.value)} data-testid="shift-end" />}</Field>
          <Field label="Late grace (min)">{({ id }) => <Input id={id} type="number" min={0} max={120} value={late} onChange={(e) => setLate(e.target.value)} />}</Field>
          <Field label="Early-leave grace (min)">{({ id }) => <Input id={id} type="number" min={0} max={120} value={early} onChange={(e) => setEarly(e.target.value)} />}</Field>
        </div>
        <fieldset>
          <legend className="field-label">Working days</legend>
          <div className="flex flex-wrap gap-2">
            {DAY_NAMES.map((d) => (
              <label key={d} className={cn("inline-flex items-center gap-1.5 h-8 px-3 rounded-[var(--radius-control)] border text-caption cursor-pointer",
                days.includes(d) ? "border-[var(--accent)] bg-[var(--accent-soft)]" : "border-border-subtle")}>
                <input type="checkbox" className="sr-only" checked={days.includes(d)} onChange={() => toggleDay(d)} /> {d}
              </label>
            ))}
          </div>
        </fieldset>
        <div>
          <div className="flex items-center justify-between">
            <span className="field-label">Breaks</span>
            <Button size="sm" variant="ghost" iconLeft={<Plus size={12} />} onClick={() => setBreaks((b) => [...b, { breakType: "Tea", name: "Tea", startLocal: "", durationMinutes: "15", isPaid: true }])} disabled={breaks.length >= 8}>Add break</Button>
          </div>
          {breaks.length === 0 ? <p className="text-caption text-fg-tertiary">No breaks configured.</p> : (
            <div className="space-y-2">
              {breaks.map((b, i) => (
                <div key={i} className="grid gap-2 grid-cols-2 sm:grid-cols-[1fr_1.4fr_1fr_0.8fr_auto_auto] items-end" data-testid="shift-break-row">
                  <Field label="Type">{({ id }) => (
                    <Select id={id} value={b.breakType} onChange={(e) => setBreaks((xs) => xs.map((x, j) => (j === i ? { ...x, breakType: e.target.value } : x)))}>
                      {BREAK_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
                    </Select>
                  )}</Field>
                  <Field label="Name">{({ id }) => <Input id={id} value={b.name} maxLength={60} onChange={(e) => setBreaks((xs) => xs.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />}</Field>
                  <Field label="Starts (optional)">{({ id }) => <Input id={id} type="time" value={b.startLocal} onChange={(e) => setBreaks((xs) => xs.map((x, j) => (j === i ? { ...x, startLocal: e.target.value } : x)))} />}</Field>
                  <Field label="Minutes">{({ id }) => <Input id={id} type="number" min={1} max={240} value={b.durationMinutes} onChange={(e) => setBreaks((xs) => xs.map((x, j) => (j === i ? { ...x, durationMinutes: e.target.value } : x)))} />}</Field>
                  <label className="flex items-center gap-1.5 h-9 text-caption"><input type="checkbox" checked={b.isPaid} onChange={(e) => setBreaks((xs) => xs.map((x, j) => (j === i ? { ...x, isPaid: e.target.checked } : x)))} /> Paid</label>
                  <Button size="sm" variant="ghost" aria-label={`Remove ${b.name}`} onClick={() => setBreaks((xs) => xs.filter((_, j) => j !== i))}><Trash2 size={13} /></Button>
                </div>
              ))}
            </div>
          )}
          <p className="mt-1 text-caption text-fg-tertiary">Unpaid breaks are taken off the scheduled time. Taken breaks never count as working time.</p>
        </div>
        <Field label="Description (optional)">{({ id }) => <TextArea id={id} rows={2} maxLength={300} value={description} onChange={(e) => setDescription(e.target.value)} />}</Field>
        {shift && (
          <label className="flex items-center gap-2 text-caption">
            <input type="checkbox" checked={active} onChange={(e) => setActive(e.target.checked)} data-testid="shift-active" /> Active
            <span className="text-fg-tertiary">(a shift with current or future assignments can&apos;t be deactivated)</span>
          </label>
        )}
        {error && <Callout tone="danger" role="alert" testId="shift-error">{error}</Callout>}
      </div>
    </Modal>
  );
}

// ─── Attendance policy ────────────────────────────────────────────────────

const POLICY_FIELDS: { key: keyof AttendancePolicy; label: string; hint: string }[] = [
  { key: "duplicateWindowSeconds", label: "Duplicate window (s)", hint: "Repeat reads of the same punch within this window are recorded as duplicates." },
  { key: "missingCheckOutAfterMinutes", label: "Missing check-out after (min)", hint: "After the shift end, an open session is flagged for review." },
  { key: "maxOpenSessionHours", label: "Max open session (h)", hint: "Without a shift, a session is flagged after this many hours." },
  { key: "maxBackdateDays", label: "Max backdate (days)", hint: "How far back a punch may be recorded." },
  { key: "earlyCheckInWindowMinutes", label: "Early check-in window (min)", hint: "How early a check-in still belongs to the coming shift." },
  { key: "overtimeRoundingMinutes", label: "OT rounding (min)", hint: "Payable suggestion is rounded down to this step." },
  { key: "minOvertimeMinutes", label: "Min OT (min)", hint: "Below this, the payable suggestion is 0." },
  { key: "maxDailyOvertimeMinutes", label: "Daily OT warning (min)", hint: "Warn above this per day (Bangladesh: 2 h)." },
  { key: "maxWeeklyOvertimeMinutes", label: "Weekly OT warning (min)", hint: "Warn above this per week." }
];

function PolicyPanel({ factoryId }: { factoryId: string }) {
  const toast = useToast();
  const canEdit = useCan(P.AttendanceManage);
  const policy = useResource<AttendancePolicy>(`/api/attendance/policy?factoryId=${factoryId}`);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!policy.data) return;
    const d: Record<string, string> = { timeZoneId: policy.data.timeZoneId };
    for (const f of POLICY_FIELDS) d[f.key] = String(policy.data[f.key]);
    setDraft(d);
  }, [policy.data]);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const body: Record<string, unknown> = { timeZoneId: draft.timeZoneId };
      for (const f of POLICY_FIELDS) body[f.key] = Number(draft[f.key]);
      await apiJson<AttendancePolicy>(`/api/attendance/policy?factoryId=${factoryId}`, { method: "PUT", body });
      toast.push({ tone: "success", title: "Attendance policy saved" });
      void policy.reload();
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Panel className="mt-6" title="Attendance policy" icon={<Settings2 size={14} />}
      subtitle={policy.data ? (policy.data.isDefault ? "Defaults (not yet changed for this factory)" : `Updated ${policy.data.updatedAt ? formatDateTime(policy.data.updatedAt) : ""} by ${policy.data.updatedByEmail ?? "—"}`) : undefined}
      testId="policy-panel">
      {policy.loading ? <SkeletonRows rows={3} /> : policy.error && !policy.data ? <ErrorState title="Couldn't load the policy" body={errorMessage(policy.error)} retryLabel="Try again" onRetry={() => void policy.reload()} /> : (
        <div className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
            <Field label="Time zone" hint="IANA id, e.g. Asia/Dhaka">{({ id, describedBy }) => <Input id={id} aria-describedby={describedBy} value={draft.timeZoneId ?? ""} disabled={!canEdit} onChange={(e) => setDraft((d) => ({ ...d, timeZoneId: e.target.value }))} />}</Field>
            {POLICY_FIELDS.map((f) => (
              <Field key={f.key} label={f.label} hint={f.hint}>
                {({ id, describedBy }) => <Input id={id} aria-describedby={describedBy} type="number" min={0} value={draft[f.key] ?? ""} disabled={!canEdit} onChange={(e) => setDraft((d) => ({ ...d, [f.key]: e.target.value }))} />}
              </Field>
            ))}
          </div>
          <p className="text-caption text-fg-tertiary">Overtime limits only raise warnings; they never block a request. Labour-law limits can differ by factory agreement, so check them with HR.</p>
          {error && <Callout tone="danger" role="alert">{error}</Callout>}
          {canEdit && <div className="flex justify-end"><Button variant="primary" size="sm" onClick={() => void save()} disabled={busy} data-testid="policy-save">{busy ? "Saving…" : "Save policy"}</Button></div>}
        </div>
      )}
    </Panel>
  );
}
