"use client";

// Write forms for the production pages. Each submits to the API and shows
// exactly what the API answered: the stored record, or its validation and
// conflict messages. Client-side checks only catch empty required fields.

import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Upload } from "lucide-react";
import { Modal } from "@/components/ui/Modal";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { useToast } from "@/components/ui/Toast";
import { announceDataChanged, useResource } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { IMPORT_TEMPLATE, STAGE_LABEL, addDays, csvToImportRows, fmt, plantDateTime, plantIso, plantTime, plantToday } from "@/lib/production";
import {
  correctOutput,
  createOrder,
  createPlan,
  importOutput,
  productionPaths,
  recordOutput,
  updateOrder,
  updatePlan
} from "@/services/production.api";
import {
  ORDER_STATUSES,
  PRODUCTION_STAGES,
  type OutputEvent,
  type OutputImportResult,
  type OutputImportRow,
  type Paged,
  type PlanDetail,
  type ProductionLine,
  type ProductionOrder,
  type ProductionOrderListItem,
  type ProductionShift,
  type ProductionStage,
  type ProductionStyle
} from "@/services/contracts";

// ── Shared form plumbing ────────────────────────────────────────────────────

function useSubmit() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  async function run<T>(fn: () => Promise<T>): Promise<T | null> {
    setBusy(true);
    setError(null);
    try {
      return await fn();
    } catch (e) {
      setError(e);
      return null;
    } finally {
      setBusy(false);
    }
  }
  const fieldErrors = isApiError(error) ? error.fieldErrors : {};
  const fe = (k: string) => fieldErrors[k]?.join(" ") ?? null;
  return { busy, error, run, fe, reset: () => setError(null) };
}

function FormError({ error }: { error: unknown }) {
  if (!error) return null;
  const fields = isApiError(error) ? Object.entries(error.fieldErrors) : [];
  return (
    <Callout tone="danger" title={isApiError(error) && error.kind === "conflict" ? "Not saved: conflict" : "Not saved"} testId="form-error">
      <p>{errorMessage(error)}</p>
      {fields.length > 1 && (
        <ul className="mt-1 list-disc pl-4">
          {fields.map(([k, v]) => <li key={k}><span className="font-mono">{k}</span>: {v.join(" ")}</li>)}
        </ul>
      )}
    </Callout>
  );
}

function Footer({ onCancel, busy, label, testId, disabled }: { onCancel: () => void; busy: boolean; label: ReactNode; testId?: string; disabled?: boolean }) {
  return (
    <>
      <Button variant="ghost" onClick={onCancel} disabled={busy}>Cancel</Button>
      <Button variant="primary" type="submit" form="production-form" disabled={busy || disabled} data-testid={testId}>
        {busy ? "Saving…" : label}
      </Button>
    </>
  );
}

const num = (v: string) => (v.trim() === "" ? undefined : Number(v));

// ── Orders ──────────────────────────────────────────────────────────────────

export function OrderFormModal({ open, onClose, styles, order, onSaved }: {
  open: boolean;
  onClose: () => void;
  styles: ProductionStyle[];
  /** Edit this order instead of creating one. */
  order?: ProductionOrder | null;
  onSaved: (o: ProductionOrder) => void;
}) {
  const toast = useToast();
  const s = useSubmit();
  const [f, setF] = useState({ orderNumber: "", styleId: "", buyer: "", quantity: "", dueDate: "", notes: "", status: "open" });
  useEffect(() => {
    if (!open) return;
    s.reset();
    setF(order
      ? { orderNumber: order.orderNumber, styleId: order.styleId, buyer: order.buyer ?? "", quantity: String(order.quantity), dueDate: order.dueDate, notes: order.notes ?? "", status: order.status }
      : { orderNumber: "", styleId: styles.find((x) => x.isActive)?.id ?? "", buyer: "", quantity: "", dueDate: "", notes: "", status: "open" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, order]);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const saved = await s.run(() => order
      ? updateOrder(order.id, { quantity: num(f.quantity), dueDate: f.dueDate, buyer: f.buyer, notes: f.notes, status: f.status, styleId: f.styleId, expectedVersion: order.version })
      : createOrder({ orderNumber: f.orderNumber.trim(), styleId: f.styleId, buyer: f.buyer.trim() || undefined, quantity: num(f.quantity), dueDate: f.dueDate, notes: f.notes.trim() || undefined }));
    if (!saved) return;
    toast.push({ title: order ? "Order updated" : `Order ${saved.orderNumber} created` });
    announceDataChanged();
    onSaved(saved);
    onClose();
  }

  return (
    <Modal open={open} onClose={onClose} locked={s.busy} title={order ? `Edit order ${order.orderNumber}` : "New production order"}
      description="A quantity of one style due by an ex-factory date (plant-local). Changes are audited."
      testId="order-form" footer={<Footer onCancel={onClose} busy={s.busy} label={order ? "Save" : "Create order"} testId="order-save" />}>
      <form id="production-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        {!order && (
          <Field label="Order number" error={s.fe("orderNumber")}>
            {(a) => <Input id={a.id} aria-describedby={a.describedBy} value={f.orderNumber} onChange={set("orderNumber")} maxLength={64} required data-testid="order-number" />}
          </Field>
        )}
        <Field label="Style" error={s.fe("styleId")} hint={styles.length === 0 ? "No styles yet: an Admin adds them under Setup." : undefined}>
          {(a) => (
            <Select id={a.id} aria-describedby={a.describedBy} value={f.styleId} onChange={set("styleId")} required data-testid="order-style">
              <option value="" disabled>Choose a style</option>
              {styles.filter((x) => x.isActive || x.id === f.styleId).map((x) => (
                <option key={x.id} value={x.id}>{x.code} · {x.name} (SMV {x.effectiveSmv == null ? "missing" : fmt(x.effectiveSmv, 2)})</option>
              ))}
            </Select>
          )}
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Quantity (pieces)" error={s.fe("quantity")}>
            {(a) => <Input id={a.id} type="number" min={1} value={f.quantity} onChange={set("quantity")} required data-testid="order-quantity" />}
          </Field>
          <Field label="Due date (plant-local)" error={s.fe("dueDate")}>
            {(a) => <Input id={a.id} type="date" value={f.dueDate} onChange={set("dueDate")} required data-testid="order-due" />}
          </Field>
        </div>
        <Field label="Buyer (optional)" error={s.fe("buyer")}>
          {(a) => <Input id={a.id} value={f.buyer} onChange={set("buyer")} maxLength={120} />}
        </Field>
        {order && (
          <Field label="Status" error={s.fe("status")}>
            {(a) => (
              <Select id={a.id} value={f.status} onChange={set("status")}>
                {ORDER_STATUSES.map((st) => <option key={st} value={st}>{st}</option>)}
              </Select>
            )}
          </Field>
        )}
        <Field label="Notes (optional)" error={s.fe("notes")}>
          {(a) => <TextArea id={a.id} rows={2} value={f.notes} onChange={set("notes")} maxLength={1000} />}
        </Field>
      </form>
    </Modal>
  );
}

// ── Plans ───────────────────────────────────────────────────────────────────

export function PlanFormModal({ open, onClose, lines, shifts, plan, orderId, onSaved }: {
  open: boolean;
  onClose: () => void;
  lines: ProductionLine[];
  shifts: ProductionShift[];
  /** Edit this plan (a draft directly; an active plan through a change request). */
  plan?: PlanDetail | null;
  /** Preselect the order when creating. */
  orderId?: string;
  onSaved: (planId: string, message: string) => void;
}) {
  const toast = useToast();
  const s = useSubmit();
  const orders = useResource<Paged<ProductionOrderListItem>>(open && !plan ? productionPaths.orders({ status: "open", pageSize: 100, includeRisk: false }) : null);
  const [f, setF] = useState({
    orderId: "", lineId: "", startDate: "", endDate: "", plannedQuantity: "", operators: "", targetEfficiencyPct: "",
    shiftCodes: [] as string[], overtimeMinutesPerDay: "0", notes: "", reason: ""
  });
  useEffect(() => {
    if (!open) return;
    s.reset();
    const v = plan?.values;
    setF({
      orderId: plan?.summary.orderId ?? orderId ?? "",
      lineId: v?.lineId ?? lines.find((l) => l.isActive)?.id ?? "",
      startDate: v?.startDate ?? "", endDate: v?.endDate ?? "",
      plannedQuantity: v ? String(v.plannedQuantity) : "", operators: v ? String(v.operators) : "",
      targetEfficiencyPct: v ? String(v.targetEfficiencyPct) : "",
      shiftCodes: v?.shiftCodes ?? shifts.filter((x) => x.isActive).map((x) => x.code),
      overtimeMinutesPerDay: v ? String(v.overtimeMinutesPerDay) : "0", notes: v?.notes ?? "", reason: ""
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, plan, orderId]);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));
  const live = plan?.summary.status === "active";

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const body = {
      lineId: f.lineId, startDate: f.startDate, endDate: f.endDate, plannedQuantity: num(f.plannedQuantity), operators: num(f.operators),
      targetEfficiencyPct: num(f.targetEfficiencyPct), shiftCodes: f.shiftCodes, overtimeMinutesPerDay: num(f.overtimeMinutesPerDay) ?? 0,
      notes: f.notes, reason: f.reason.trim() || undefined
    };
    if (plan) {
      const r = await s.run(() => updatePlan(plan.summary.id, { ...body, expectedVersion: plan.version }));
      if (!r) return;
      toast.push({ title: r.outcome === "change_requested" ? "Change sent for approval" : "Plan updated", description: r.message });
      announceDataChanged();
      onSaved(plan.summary.id, r.message);
    } else {
      const r = await s.run(() => createPlan({ ...body, orderId: f.orderId }));
      if (!r) return;
      toast.push({ title: `Draft plan ${r.summary.reference} created`, description: "Submit it for approval when it's ready." });
      announceDataChanged();
      onSaved(r.summary.id, "Created as a draft.");
    }
    onClose();
  }

  return (
    <Modal open={open} onClose={onClose} locked={s.busy} className="sm:max-w-2xl" testId="plan-form"
      title={plan ? (live ? `Request a change to ${plan.summary.reference}` : `Edit ${plan.summary.reference}`) : "New production plan"}
      description={live
        ? "The live plan keeps its values until this change is approved in the approval queue and then applied."
        : "Targets are calculated by the API from these inputs and the style's approved SMV. A draft has no operational effect."}
      footer={<Footer onCancel={onClose} busy={s.busy} label={plan ? (live ? "Request change" : "Save draft") : "Create draft"} testId="plan-save" />}>
      <form id="production-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        {!plan && (
          <Field label="Order" error={s.fe("orderId")} hint={orders.data && orders.data.items.length === 0 ? "No open orders: create one first." : undefined}>
            {(a) => (
              <Select id={a.id} value={f.orderId} onChange={set("orderId")} required data-testid="plan-order">
                <option value="" disabled>{orders.loading ? "Loading orders…" : "Choose an open order"}</option>
                {(orders.data?.items ?? []).map((o) => <option key={o.id} value={o.id}>{o.orderNumber} · {o.styleCode} · {fmt(o.quantity)} pcs · due {o.dueDate}</option>)}
                {f.orderId && !(orders.data?.items ?? []).some((o) => o.id === f.orderId) && <option value={f.orderId}>Selected order</option>}
              </Select>
            )}
          </Field>
        )}
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <Field label="Line" error={s.fe("lineId")}>
            {(a) => (
              <Select id={a.id} value={f.lineId} onChange={set("lineId")} required data-testid="plan-line">
                <option value="" disabled>Choose a line</option>
                {lines.filter((l) => l.isActive || l.id === f.lineId).map((l) => <option key={l.id} value={l.id}>{l.name} ({l.workstations} workstations)</option>)}
              </Select>
            )}
          </Field>
          <Field label="Start date" error={s.fe("startDate")}>
            {(a) => <Input id={a.id} type="date" value={f.startDate} onChange={set("startDate")} required data-testid="plan-start" />}
          </Field>
          <Field label="End date" error={s.fe("endDate")}>
            {(a) => <Input id={a.id} type="date" value={f.endDate} onChange={set("endDate")} required data-testid="plan-end" />}
          </Field>
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-4 gap-3">
          <Field label="Planned quantity (pcs)" error={s.fe("plannedQuantity")}>
            {(a) => <Input id={a.id} type="number" min={1} value={f.plannedQuantity} onChange={set("plannedQuantity")} required data-testid="plan-quantity" />}
          </Field>
          <Field label="Operators" error={s.fe("operators")}>
            {(a) => <Input id={a.id} type="number" min={1} value={f.operators} onChange={set("operators")} required data-testid="plan-operators" />}
          </Field>
          <Field label="Target efficiency (%)" error={s.fe("targetEfficiencyPct")}>
            {(a) => <Input id={a.id} type="number" min={1} max={150} step="0.1" value={f.targetEfficiencyPct} onChange={set("targetEfficiencyPct")} required data-testid="plan-efficiency" />}
          </Field>
          <Field label="Overtime (min/day)" error={s.fe("overtimeMinutesPerDay")}>
            {(a) => <Input id={a.id} type="number" min={0} max={480} value={f.overtimeMinutesPerDay} onChange={set("overtimeMinutesPerDay")} />}
          </Field>
        </div>
        <fieldset>
          <legend className="field-label">Shifts</legend>
          {shifts.length === 0 && <p className="text-caption text-fg-tertiary">No shifts yet: an Admin adds them under Setup.</p>}
          <div className="flex flex-wrap gap-3">
            {shifts.filter((x) => x.isActive || f.shiftCodes.includes(x.code)).map((x) => (
              <label key={x.code} className="flex items-center gap-2 text-body">
                <input type="checkbox" checked={f.shiftCodes.includes(x.code)} data-testid={`plan-shift-${x.code}`}
                  onChange={(e) => setF((v) => ({ ...v, shiftCodes: e.target.checked ? [...v.shiftCodes, x.code] : v.shiftCodes.filter((c) => c !== x.code) }))} />
                {x.name} <span className="text-caption text-fg-tertiary">{x.start}–{x.end}</span>
              </label>
            ))}
          </div>
          {s.fe("shiftCodes") && <p className="field-error" role="alert">{s.fe("shiftCodes")}</p>}
        </fieldset>
        <Field label="Notes (optional)" error={s.fe("notes")}>
          {(a) => <TextArea id={a.id} rows={2} value={f.notes} onChange={set("notes")} maxLength={1000} />}
        </Field>
        {plan && (
          <Field label={live ? "Reason for the change (required)" : "Reason (optional)"} error={s.fe("reason")}>
            {(a) => <Input id={a.id} value={f.reason} onChange={set("reason")} maxLength={500} required={live} data-testid="plan-reason" />}
          </Field>
        )}
      </form>
    </Modal>
  );
}

// ── Output ──────────────────────────────────────────────────────────────────

export function OutputFormModal({ open, onClose, lines, offset, defaults, onSaved }: {
  open: boolean;
  onClose: () => void;
  lines: ProductionLine[];
  offset: number;
  defaults?: { lineId?: string; orderId?: string };
  onSaved?: (e: OutputEvent) => void;
}) {
  const toast = useToast();
  const s = useSubmit();
  const orders = useResource<Paged<ProductionOrderListItem>>(open ? productionPaths.orders({ status: "open", pageSize: 100, includeRisk: false }) : null);
  const blank = () => {
    const now = new Date(Date.now() + offset * 60_000);
    const hour = Math.max(0, now.getUTCHours() - 1);
    const hh = (h: number) => `${String(h).padStart(2, "0")}:00`;
    return {
      lineId: defaults?.lineId ?? lines.find((l) => l.isActive)?.id ?? "", orderId: defaults?.orderId ?? "", stage: "sewing_output" as ProductionStage,
      date: plantToday(offset), start: hh(hour), end: hour === 23 ? "23:59" : hh(hour + 1), quantity: "", rework: "", reject: "",
      operators: "", downtime: "", downtimeReason: "", notes: ""
    };
  };
  const [f, setF] = useState(blank);
  useEffect(() => {
    if (open) { s.reset(); setF(blank()); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    // An end time before the start time means the interval ends after midnight.
    const endDate = f.end <= f.start ? addDays(f.date, 1) : f.date;
    const saved = await s.run(() => recordOutput({
      lineId: f.lineId, orderId: f.orderId, stage: f.stage,
      intervalStart: plantIso(f.date, f.start, offset), intervalEnd: plantIso(endDate, f.end, offset),
      quantity: num(f.quantity) ?? 0, reworkQuantity: num(f.rework), rejectQuantity: num(f.reject),
      operatorsPresent: num(f.operators) ?? null, downtimeMinutes: num(f.downtime) ?? null,
      downtimeReason: f.downtimeReason.trim() || undefined, notes: f.notes.trim() || undefined
    }));
    if (!saved) return;
    toast.push({ title: "Output recorded", description: `${fmt(saved.quantity)} pcs · production date ${saved.productionDate} · ${saved.shiftCode ? `shift ${saved.shiftCode}` : "outside shifts (overtime)"}` });
    announceDataChanged();
    onSaved?.(saved);
    onClose();
  }

  return (
    <Modal open={open} onClose={onClose} locked={s.busy} className="sm:max-w-2xl" testId="output-form" title="Record output"
      description="Pieces counted at one stage for one order and line over one interval, in plant-local time. Overlapping records are refused: correct the existing one instead."
      footer={<Footer onCancel={onClose} busy={s.busy} label="Record" testId="output-save" />}>
      <form id="production-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          <Field label="Line" error={s.fe("lineId")}>
            {(a) => (
              <Select id={a.id} value={f.lineId} onChange={set("lineId")} required data-testid="output-line">
                <option value="" disabled>Choose a line</option>
                {lines.filter((l) => l.isActive).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Order" error={s.fe("orderId")}>
            {(a) => (
              <Select id={a.id} value={f.orderId} onChange={set("orderId")} required data-testid="output-order">
                <option value="" disabled>{orders.loading ? "Loading…" : "Choose an order"}</option>
                {(orders.data?.items ?? []).map((o) => <option key={o.id} value={o.id}>{o.orderNumber} · {o.styleCode}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Stage" error={s.fe("stage")}>
            {(a) => (
              <Select id={a.id} value={f.stage} onChange={set("stage")} data-testid="output-stage">
                {PRODUCTION_STAGES.map((st) => <option key={st} value={st}>{STAGE_LABEL[st]}</option>)}
              </Select>
            )}
          </Field>
        </div>
        <div className="grid grid-cols-3 gap-3">
          <Field label="Date (plant-local)" error={s.fe("intervalStart")}>
            {(a) => <Input id={a.id} type="date" value={f.date} onChange={set("date")} required data-testid="output-date" />}
          </Field>
          <Field label="From" >
            {(a) => <Input id={a.id} type="time" value={f.start} onChange={set("start")} required data-testid="output-start" />}
          </Field>
          <Field label="To" error={s.fe("intervalEnd")}>
            {(a) => <Input id={a.id} type="time" value={f.end} onChange={set("end")} required data-testid="output-end" />}
          </Field>
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <Field label="Good pieces" error={s.fe("quantity")}>
            {(a) => <Input id={a.id} type="number" min={0} value={f.quantity} onChange={set("quantity")} required data-testid="output-quantity" />}
          </Field>
          <Field label="Rework" error={s.fe("reworkQuantity")}>
            {(a) => <Input id={a.id} type="number" min={0} value={f.rework} onChange={set("rework")} />}
          </Field>
          <Field label="Reject" error={s.fe("rejectQuantity")}>
            {(a) => <Input id={a.id} type="number" min={0} value={f.reject} onChange={set("reject")} />}
          </Field>
          <Field label="Operators present" error={s.fe("operatorsPresent")} hint="Needed for efficiency">
            {(a) => <Input id={a.id} aria-describedby={a.describedBy} type="number" min={1} value={f.operators} onChange={set("operators")} data-testid="output-operators" />}
          </Field>
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-[120px_1fr] gap-3">
          <Field label="Downtime (min)" error={s.fe("downtimeMinutes")}>
            {(a) => <Input id={a.id} type="number" min={0} step="1" value={f.downtime} onChange={set("downtime")} />}
          </Field>
          <Field label="Downtime reason" error={s.fe("downtimeReason")}>
            {(a) => <Input id={a.id} value={f.downtimeReason} onChange={set("downtimeReason")} maxLength={200} placeholder="e.g. needle break, no input" />}
          </Field>
        </div>
        <Field label="Notes (optional)" error={s.fe("notes")}>
          {(a) => <Input id={a.id} value={f.notes} onChange={set("notes")} maxLength={500} />}
        </Field>
      </form>
    </Modal>
  );
}

export function CorrectOutputModal({ event, offset, onClose, onSaved }: { event: OutputEvent | null; offset: number; onClose: () => void; onSaved?: () => void }) {
  const toast = useToast();
  const s = useSubmit();
  const [f, setF] = useState({ quantity: "", rework: "", reject: "", operators: "", downtime: "", reason: "" });
  useEffect(() => {
    if (!event) return;
    s.reset();
    setF({
      quantity: String(event.quantity), rework: String(event.reworkQuantity), reject: String(event.rejectQuantity),
      operators: event.operatorsPresent == null ? "" : String(event.operatorsPresent),
      downtime: event.downtimeMinutes == null ? "" : String(event.downtimeMinutes), reason: ""
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [event]);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!event) return;
    const saved = await s.run(() => correctOutput(event.id, {
      quantity: num(f.quantity), reworkQuantity: num(f.rework), rejectQuantity: num(f.reject),
      operatorsPresent: num(f.operators), downtimeMinutes: num(f.downtime), reason: f.reason.trim()
    }));
    if (!saved) return;
    toast.push({ title: "Correction recorded", description: "The original stays in history, marked superseded." });
    announceDataChanged();
    onSaved?.();
    onClose();
  }

  return (
    <Modal open={!!event} onClose={onClose} locked={s.busy} testId="correct-form" title="Correct an output record"
      description={event ? `${event.lineId} · ${event.orderNumber} · ${STAGE_LABEL[event.stage]} · ${plantDateTime(event.intervalStart, offset)}–${plantTime(event.intervalEnd, offset)}. A replacement is stored; the original is kept and marked superseded.` : undefined}
      footer={<Footer onCancel={onClose} busy={s.busy} label="Save correction" testId="correct-save" disabled={!f.reason.trim()} />}>
      <form id="production-form" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        <div className="grid grid-cols-2 sm:grid-cols-5 gap-3">
          <Field label="Good" error={s.fe("quantity")}>{(a) => <Input id={a.id} type="number" min={0} value={f.quantity} onChange={set("quantity")} data-testid="correct-quantity" />}</Field>
          <Field label="Rework" error={s.fe("reworkQuantity")}>{(a) => <Input id={a.id} type="number" min={0} value={f.rework} onChange={set("rework")} />}</Field>
          <Field label="Reject" error={s.fe("rejectQuantity")}>{(a) => <Input id={a.id} type="number" min={0} value={f.reject} onChange={set("reject")} />}</Field>
          <Field label="Operators" error={s.fe("operatorsPresent")}>{(a) => <Input id={a.id} type="number" min={1} value={f.operators} onChange={set("operators")} />}</Field>
          <Field label="Downtime (min)" error={s.fe("downtimeMinutes")}>{(a) => <Input id={a.id} type="number" min={0} value={f.downtime} onChange={set("downtime")} />}</Field>
        </div>
        <Field label="Reason (required)" error={s.fe("reason")}>
          {(a) => <Input id={a.id} value={f.reason} onChange={set("reason")} maxLength={500} required data-testid="correct-reason" />}
        </Field>
      </form>
    </Modal>
  );
}

// ── Import ──────────────────────────────────────────────────────────────────

export function ImportOutputModal({ open, onClose, offset }: { open: boolean; onClose: () => void; offset: number }) {
  const toast = useToast();
  const s = useSubmit();
  const [fileName, setFileName] = useState("");
  const [rows, setRows] = useState<OutputImportRow[]>([]);
  const [problems, setProblems] = useState<string[]>([]);
  const [result, setResult] = useState<OutputImportResult | null>(null);
  useEffect(() => {
    if (open) { s.reset(); setFileName(""); setRows([]); setProblems([]); setResult(null); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);
  const template = useMemo(() => `data:text/csv;charset=utf-8,${encodeURIComponent(IMPORT_TEMPLATE)}`, []);
  const rejected = isApiError(s.error) ? ((s.error.body as { result?: OutputImportResult } | null)?.result ?? null) : null;
  const report = result ?? rejected;

  async function pick(file: File | undefined) {
    setResult(null);
    s.reset();
    if (!file) return;
    setFileName(file.name);
    const parsed = csvToImportRows(await file.text(), offset);
    setRows(parsed.rows);
    setProblems(parsed.problems);
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const r = await s.run(() => importOutput(fileName, rows));
    if (!r) return;
    setResult(r);
    toast.push({ title: `Imported ${r.created} record${r.created === 1 ? "" : "s"}`, description: r.duplicates ? `${r.duplicates} already imported row(s) skipped.` : undefined });
    announceDataChanged();
  }

  return (
    <Modal open={open} onClose={onClose} locked={s.busy} className="sm:max-w-2xl" testId="import-form" title="Import output from a CSV file"
      description="Rows are checked by the API. If any row is rejected nothing is stored, so a fixed file can simply be imported again; rows imported before are recognised and skipped."
      footer={result
        ? <Button variant="primary" onClick={onClose}>Done</Button>
        : <Footer onCancel={onClose} busy={s.busy} label={`Import ${rows.length} row${rows.length === 1 ? "" : "s"}`} testId="import-save" disabled={rows.length === 0 || problems.length > 0} />}>
      <form id="production-form" onSubmit={submit} className="space-y-3">
        <p className="text-caption text-fg-secondary">
          Columns: <span className="font-mono">{"line_id, order_number, stage, interval_start, interval_end, quantity"}</span> (required) and optional
          <span className="font-mono"> source_ref, rework_quantity, reject_quantity, operators_present, downtime_minutes, downtime_reason, notes</span>.
          Times are ISO 8601 with an offset, or <span className="font-mono">yyyy-MM-dd HH:mm</span> in plant-local time.{" "}
          <a href={template} download="production-output-template.csv" className="text-accent hover:underline">Download a template</a>
        </p>
        <label className="flex items-center gap-3">
          <span className="inline-flex h-9 items-center gap-1.5 px-3.5 rounded-[var(--radius-control)] border border-[var(--btn-secondary-border)] bg-[var(--btn-secondary-bg)] text-body cursor-pointer">
            <Upload size={14} /> Choose file
          </span>
          <input type="file" accept=".csv,text/csv" className="sr-only" data-testid="import-file" onChange={(e) => void pick(e.target.files?.[0])} />
          <span className="text-caption text-fg-tertiary truncate">{fileName || "No file chosen"}</span>
        </label>
        {problems.length > 0 && <Callout tone="danger" title="The file can't be read">{problems.join(" ")}</Callout>}
        {rows.length > 0 && !report && <p className="text-caption text-fg-secondary" data-testid="import-preview">{rows.length} data row{rows.length === 1 ? "" : "s"} read from {fileName}.</p>}
        {s.error && !rejected ? <FormError error={s.error} /> : null}
        {report && (
          <div className="space-y-2" data-testid="import-report">
            <Callout tone={result ? "success" : "danger"} title={result ? "Import finished" : "Nothing was imported"}>
              {report.created} created · {report.duplicates} already imported · {report.invalid} rejected of {report.received} rows.
            </Callout>
            <div className="max-h-56 overflow-auto">
              <table className="data-table">
                <thead><tr><th className="num">Row</th><th>Outcome</th><th>Details</th></tr></thead>
                <tbody>
                  {report.rows.filter((r) => r.outcome !== "created").slice(0, 100).map((r) => (
                    <tr key={r.row}><td className="num">{r.row}</td><td>{r.outcome.replace("_", " ")}</td><td className="text-caption">{(r.errors ?? []).join(" ") || r.sourceRef}</td></tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </form>
    </Modal>
  );
}
