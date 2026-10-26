"use client";

// Write forms for the merchandising pages. Each submits to the API and shows
// exactly what the API answered: the stored record, or its validation and
// conflict messages. Client-side checks only catch empty required fields.

import { useEffect, useRef, useState, type ReactNode } from "react";
import { Plus, Trash2 } from "lucide-react";
import { Modal } from "@/components/ui/Modal";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { useToast } from "@/components/ui/Toast";
import { announceDataChanged } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { formatBytes } from "@/lib/merchandising";
import {
  addMilestone,
  createMerchOrder,
  newIdempotencyKey,
  resolveConflict,
  updateMerchOrder,
  updateMilestone,
  uploadDocument,
  uploadVersion
} from "@/services/merchandising.api";
import {
  MERCH_DOCUMENT_TYPES,
  MERCH_ORDER_STATUSES,
  type MerchConfig,
  type MerchConflict,
  type MerchDocumentType,
  type MerchMilestone,
  type MerchOrder,
  type MerchOrderDetail,
  type MerchVersionDetail
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

export function FormError({ error, title = "Not saved" }: { error: unknown; title?: string }) {
  if (!error) return null;
  const fields = isApiError(error) ? Object.entries(error.fieldErrors) : [];
  return (
    <Callout tone="danger" title={isApiError(error) && error.kind === "conflict" ? `${title}: conflict` : title} testId="form-error">
      <p>{errorMessage(error)}</p>
      {fields.length > 1 && (
        <ul className="mt-1 list-disc pl-4">
          {fields.flatMap(([k, v]) => v.map((m, i) => <li key={`${k}-${i}`}>{m}</li>))}
        </ul>
      )}
    </Callout>
  );
}

function Footer({ onCancel, busy, label, testId, disabled, form }: { onCancel: () => void; busy: boolean; label: ReactNode; testId?: string; disabled?: boolean; form: string }) {
  return (
    <>
      <Button variant="ghost" onClick={onCancel} disabled={busy}>Cancel</Button>
      <Button variant="primary" type="submit" form={form} disabled={busy || disabled} data-testid={testId}>
        {busy ? "Saving…" : label}
      </Button>
    </>
  );
}

const DOC_LABEL: Record<MerchDocumentType, string> = {
  purchase_order: "Purchase order", tech_pack: "Tech pack", measurement_sheet: "Measurement / spec sheet", buyer_instruction: "Buyer instruction", other: "Other"
};

// ── Orders ──────────────────────────────────────────────────────────────────

type OrderForm = {
  buyer: string; buyerOrderNumber: string; styleRef: string; productDescription: string; quantity: string; unit: string; deliveryDate: string;
  destination: string; notes: string; status: string; required: MerchDocumentType[]; milestones: { name: string; dueDate: string }[];
};

const COMMON_MILESTONES = ["Lab dip approval", "Fit sample approval", "PP sample approval", "Fabric in-house", "Trims in-house", "Cutting start", "Sewing start", "Final inspection", "Ex-factory"];

/** Register a buyer order (no order given) or edit one. Typed values are recorded as manual and audited. */
export function OrderFormModal({ open, onClose, order, config, onSaved }: {
  open: boolean;
  onClose: () => void;
  order?: MerchOrder | null;
  config?: MerchConfig | null;
  onSaved: (d: MerchOrderDetail) => void;
}) {
  const toast = useToast();
  const s = useSubmit();
  const empty: OrderForm = {
    buyer: "", buyerOrderNumber: "", styleRef: "", productDescription: "", quantity: "", unit: "", deliveryDate: "", destination: "", notes: "",
    status: "active", required: config?.defaultRequiredDocumentTypes ?? ["purchase_order", "tech_pack"], milestones: []
  };
  const [f, setF] = useState<OrderForm>(empty);
  useEffect(() => {
    if (!open) return;
    s.reset();
    setF(order ? {
      buyer: order.buyer, buyerOrderNumber: order.buyerOrderNumber, styleRef: order.styleRef ?? "", productDescription: order.productDescription ?? "",
      quantity: order.quantity != null ? String(order.quantity) : "", unit: order.unit ?? "", deliveryDate: order.deliveryDate ?? "",
      destination: order.destination ?? "", notes: order.notes ?? "", status: order.status, required: order.requiredDocumentTypes, milestones: []
    } : empty);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, order]);
  const set = (k: keyof OrderForm) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const opt = (v: string) => (v.trim() === "" ? undefined : v.trim());
    const saved = await s.run(() => {
      if (!order)
        return createMerchOrder({
          buyer: f.buyer.trim(), buyerOrderNumber: f.buyerOrderNumber.trim(), styleRef: opt(f.styleRef), productDescription: opt(f.productDescription),
          quantity: f.quantity.trim() ? Number(f.quantity) : undefined, unit: opt(f.unit), deliveryDate: opt(f.deliveryDate), destination: opt(f.destination),
          notes: opt(f.notes), requiredDocumentTypes: f.required, milestones: f.milestones.filter((m) => m.name.trim() || m.dueDate)
        });
      // Only changed fields are sent; an emptied field is cleared explicitly.
      const clear: string[] = [];
      const changed = <T,>(cur: T | undefined, next: string, key: string): string | undefined => {
        if ((cur ?? "") === next.trim()) return undefined;
        if (next.trim() === "") { clear.push(key); return undefined; }
        return next.trim();
      };
      const body = {
        buyer: changed(order.buyer, f.buyer, "buyer"),
        buyerOrderNumber: changed(order.buyerOrderNumber, f.buyerOrderNumber, "order_number"),
        styleRef: changed(order.styleRef, f.styleRef, "style"),
        productDescription: changed(order.productDescription, f.productDescription, "product"),
        quantity: (() => { const v = changed(order.quantity != null ? String(order.quantity) : undefined, f.quantity, "quantity"); return v === undefined ? undefined : Number(v); })(),
        unit: changed(order.unit, f.unit, "unit"),
        deliveryDate: changed(order.deliveryDate, f.deliveryDate, "delivery_date"),
        destination: changed(order.destination, f.destination, "destination"),
        notes: f.notes !== (order.notes ?? "") ? f.notes : undefined,
        status: f.status !== order.status ? f.status as MerchOrder["status"] : undefined,
        requiredDocumentTypes: f.required.join() !== order.requiredDocumentTypes.join() ? f.required : undefined,
        clearFields: clear.length ? clear : undefined,
        expectedVersion: order.version
      };
      return updateMerchOrder(order.id, body);
    });
    if (!saved) return;
    toast.push({ title: order ? "Order updated" : `${saved.order.buyer} PO ${saved.order.buyerOrderNumber} registered`, tone: "success" });
    announceDataChanged();
    onSaved(saved);
    onClose();
  }

  return (
    <Modal open={open} onClose={onClose} locked={s.busy} className="sm:max-w-2xl" testId="merch-order-form"
      title={order ? `Edit ${order.reference}` : "Register a buyer order"}
      description={order ? "Values typed here are recorded as entered by you and compared with the documents." : "Only the buyer and PO number are required; the rest can be confirmed from the documents later."}
      footer={<Footer form="merch-order-form-el" onCancel={onClose} busy={s.busy} label={order ? "Save" : "Register order"} testId="merch-order-save" />}>
      <form id="merch-order-form-el" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <Field label="Buyer" error={s.fe("buyer")}>
            {(a) => <Input id={a.id} value={f.buyer} onChange={set("buyer")} maxLength={120} required data-testid="merch-buyer" />}
          </Field>
          <Field label="Buyer PO number" error={s.fe("buyerOrderNumber")}>
            {(a) => <Input id={a.id} value={f.buyerOrderNumber} onChange={set("buyerOrderNumber")} maxLength={64} required data-testid="merch-po" />}
          </Field>
          <Field label="Style (optional)" error={s.fe("styleRef")}>
            {(a) => <Input id={a.id} value={f.styleRef} onChange={set("styleRef")} maxLength={64} data-testid="merch-style" />}
          </Field>
          <Field label="Product (optional)" error={s.fe("productDescription")}>
            {(a) => <Input id={a.id} value={f.productDescription} onChange={set("productDescription")} maxLength={200} />}
          </Field>
          <Field label="Quantity (optional)" error={s.fe("quantity")}>
            {(a) => <Input id={a.id} type="number" min={1} value={f.quantity} onChange={set("quantity")} data-testid="merch-quantity" />}
          </Field>
          <Field label="Unit (optional)" error={s.fe("unit")}>
            {(a) => (
              <Select id={a.id} value={f.unit} onChange={set("unit")}>
                <option value="">—</option>
                {["pcs", "dozen", "sets", "packs", "pairs"].map((u) => <option key={u} value={u}>{u}</option>)}
              </Select>
            )}
          </Field>
          <Field label="Delivery date (optional)" error={s.fe("deliveryDate")}>
            {(a) => <Input id={a.id} type="date" value={f.deliveryDate} onChange={set("deliveryDate")} data-testid="merch-delivery" />}
          </Field>
          <Field label="Destination (optional)" error={s.fe("destination")}>
            {(a) => <Input id={a.id} value={f.destination} onChange={set("destination")} maxLength={120} />}
          </Field>
        </div>
        {order && (
          <Field label="Status" error={s.fe("status")}>
            {(a) => (
              <Select id={a.id} value={f.status} onChange={set("status")}>
                {MERCH_ORDER_STATUSES.map((st) => <option key={st} value={st}>{st}</option>)}
              </Select>
            )}
          </Field>
        )}
        <fieldset>
          <legend className="field-label">Required documents</legend>
          <div className="flex flex-wrap gap-x-4 gap-y-1.5">
            {MERCH_DOCUMENT_TYPES.map((t) => (
              <label key={t} className="flex items-center gap-1.5 text-body text-fg-primary">
                <input type="checkbox" className="accent-[var(--accent)]" checked={f.required.includes(t)}
                  onChange={(e) => setF((x) => ({ ...x, required: e.target.checked ? [...x.required, t] : x.required.filter((r) => r !== t) }))} />
                {DOC_LABEL[t]}
              </label>
            ))}
          </div>
          {s.fe("requiredDocumentTypes") && <p className="field-error">{s.fe("requiredDocumentTypes")}</p>}
        </fieldset>
        {!order && (
          <fieldset>
            <legend className="field-label">Milestones (optional)</legend>
            <datalist id="merch-milestone-names">{COMMON_MILESTONES.map((m) => <option key={m} value={m} />)}</datalist>
            <div className="space-y-2">
              {f.milestones.map((m, i) => (
                <div key={i} className="grid grid-cols-[1fr_150px_auto] gap-2">
                  <Input aria-label={`Milestone ${i + 1} name`} list="merch-milestone-names" value={m.name} maxLength={120} data-testid="merch-milestone-name"
                    onChange={(e) => setF((x) => ({ ...x, milestones: x.milestones.map((y, j) => (j === i ? { ...y, name: e.target.value } : y)) }))} />
                  <Input aria-label={`Milestone ${i + 1} due date`} type="date" value={m.dueDate} data-testid="merch-milestone-due"
                    onChange={(e) => setF((x) => ({ ...x, milestones: x.milestones.map((y, j) => (j === i ? { ...y, dueDate: e.target.value } : y)) }))} />
                  <Button type="button" size="sm" variant="ghost" aria-label={`Remove milestone ${i + 1}`} onClick={() => setF((x) => ({ ...x, milestones: x.milestones.filter((_, j) => j !== i) }))}>
                    <Trash2 size={13} />
                  </Button>
                </div>
              ))}
              <Button type="button" size="sm" variant="secondary" iconLeft={<Plus size={12} />} data-testid="merch-milestone-add"
                onClick={() => setF((x) => ({ ...x, milestones: [...x.milestones, { name: "", dueDate: "" }] }))}>Add milestone</Button>
            </div>
            {Object.entries(isApiError(s.error) ? s.error.fieldErrors : {}).filter(([k]) => k.startsWith("milestones")).map(([k, v]) => (
              <p key={k} className="field-error">{v.join(" ")}</p>
            ))}
          </fieldset>
        )}
        <Field label="Notes (optional)" error={s.fe("notes")}>
          {(a) => <TextArea id={a.id} rows={2} value={f.notes} onChange={set("notes")} maxLength={1000} />}
        </Field>
      </form>
    </Modal>
  );
}

// ── Documents ───────────────────────────────────────────────────────────────

/** Upload a new document, or (with documentId) a new version of an existing one. */
export function UploadModal({ open, onClose, orderId, documentId, documentTitle, config, onUploaded }: {
  open: boolean;
  onClose: () => void;
  orderId: string;
  documentId?: string;
  documentTitle?: string;
  config?: MerchConfig | null;
  onUploaded: (v: MerchVersionDetail) => void;
}) {
  const toast = useToast();
  const s = useSubmit();
  const [file, setFile] = useState<File | null>(null);
  const [type, setType] = useState<MerchDocumentType>("purchase_order");
  const [title, setTitle] = useState("");
  const [note, setNote] = useState("");
  const [localError, setLocalError] = useState<string | null>(null);
  // One key per submission: a double click or a retried request returns the same version.
  const key = useRef(newIdempotencyKey());
  useEffect(() => {
    if (!open) return;
    s.reset();
    setFile(null); setTitle(""); setNote(""); setLocalError(null);
    key.current = newIdempotencyKey();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const accept = (config?.acceptedExtensions ?? [".pdf", ".txt", ".csv", ".docx", ".jpg", ".jpeg", ".png"]).join(",");
  const maxBytes = config?.maxFileBytes;

  function pick(f: File | null) {
    setLocalError(null);
    setFile(f);
    if (f && maxBytes && f.size > maxBytes) setLocalError(`The file is ${formatBytes(f.size)}; the limit is ${formatBytes(maxBytes)}.`);
    key.current = newIdempotencyKey();
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!file) { setLocalError("Choose a file."); return; }
    const res = await s.run(() => documentId
      ? uploadVersion(documentId, { file, changeNote: note }, key.current)
      : uploadDocument(orderId, { file, type, title }, key.current));
    if (!res) return;
    toast.push({ title: res.replayed ? "Already uploaded" : "Uploaded", description: `${res.version.documentTitle} v${res.version.versionNumber} is queued for reading.`, tone: "success" });
    announceDataChanged();
    onUploaded(res.version);
    onClose();
  }

  const image = file && /\.(png|jpe?g)$/i.test(file.name);
  return (
    <Modal open={open} onClose={onClose} locked={s.busy} testId="merch-upload-form"
      title={documentId ? `New version of ${documentTitle}` : "Upload a document"}
      description={`PDF (with a text layer), DOCX, CSV or plain text${maxBytes ? `, up to ${formatBytes(maxBytes)}` : ""}. The original file is kept unchanged.`}
      footer={<Footer form="merch-upload-form-el" onCancel={onClose} busy={s.busy} label="Upload" testId="merch-upload-save" disabled={!file || !!localError} />}>
      <form id="merch-upload-form-el" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} title="Not uploaded" />
        {!documentId && (
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <Field label="Document type" error={s.fe("type")}>
              {(a) => (
                <Select id={a.id} value={type} onChange={(e) => setType(e.target.value as MerchDocumentType)} data-testid="merch-upload-type">
                  {MERCH_DOCUMENT_TYPES.map((t) => <option key={t} value={t}>{DOC_LABEL[t]}</option>)}
                </Select>
              )}
            </Field>
            <Field label="Title (optional)" error={s.fe("title")}>
              {(a) => <Input id={a.id} value={title} onChange={(e) => setTitle(e.target.value)} maxLength={160} placeholder={DOC_LABEL[type]} />}
            </Field>
          </div>
        )}
        <Field label="File" error={localError ?? s.fe("file")}>
          {(a) => (
            <input id={a.id} type="file" accept={accept} aria-describedby={a.describedBy} data-testid="merch-upload-file"
              className="block w-full text-body text-fg-secondary file:mr-3 file:h-9 file:px-3 file:rounded-[var(--radius-control)] file:border file:border-border-subtle file:bg-surface-2 file:text-fg-primary"
              onChange={(e) => pick(e.target.files?.[0] ?? null)} />
          )}
        </Field>
        {image && config && !config.ocrConfigured && (
          <Callout tone="warning" title="This image can't be read automatically" testId="merch-upload-ocr-note">
            {config.ocrNote} It will be stored and can be reviewed by hand.
          </Callout>
        )}
        {documentId && (
          <Field label="What changed? (optional)" error={s.fe("changeNote")}>
            {(a) => <Input id={a.id} value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} data-testid="merch-upload-note" />}
          </Field>
        )}
      </form>
    </Modal>
  );
}

// ── Milestones ──────────────────────────────────────────────────────────────

export function MilestoneModal({ open, onClose, orderId, milestone, onSaved }: {
  open: boolean;
  onClose: () => void;
  orderId: string;
  milestone?: MerchMilestone | null;
  onSaved: () => void;
}) {
  const s = useSubmit();
  const [f, setF] = useState({ name: "", dueDate: "", status: "pending", completedOn: "", notes: "" });
  useEffect(() => {
    if (!open) return;
    s.reset();
    setF(milestone
      ? { name: milestone.name, dueDate: milestone.dueDate, status: milestone.status, completedOn: milestone.completedOn ?? "", notes: milestone.notes ?? "" }
      : { name: "", dueDate: "", status: "pending", completedOn: "", notes: "" });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, milestone]);
  const set = (k: keyof typeof f) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const saved = await s.run(() => milestone
      ? updateMilestone(milestone.id, {
          name: f.name !== milestone.name ? f.name : undefined, dueDate: f.dueDate !== milestone.dueDate ? f.dueDate : undefined,
          status: f.status !== milestone.status ? f.status : undefined, completedOn: f.status === "done" && f.completedOn ? f.completedOn : undefined,
          notes: f.notes !== (milestone.notes ?? "") ? f.notes : undefined, expectedVersion: milestone.version
        })
      : addMilestone(orderId, { name: f.name, dueDate: f.dueDate, notes: f.notes || undefined }));
    if (!saved) return;
    announceDataChanged();
    onSaved();
    onClose();
  }

  return (
    <Modal open={open} onClose={onClose} locked={s.busy} testId="merch-milestone-form" title={milestone ? `Update “${milestone.name}”` : "Add a milestone"}
      footer={<Footer form="merch-milestone-form-el" onCancel={onClose} busy={s.busy} label="Save" testId="merch-milestone-save" />}>
      <form id="merch-milestone-form-el" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        <datalist id="merch-milestone-names-2">{COMMON_MILESTONES.map((m) => <option key={m} value={m} />)}</datalist>
        <Field label="Name">{(a) => <Input id={a.id} list="merch-milestone-names-2" value={f.name} onChange={set("name")} maxLength={120} required data-testid="merch-milestone-name" />}</Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Due date">{(a) => <Input id={a.id} type="date" value={f.dueDate} onChange={set("dueDate")} required data-testid="merch-milestone-due" />}</Field>
          {milestone && (
            <Field label="Status">
              {(a) => (
                <Select id={a.id} value={f.status} onChange={set("status")} data-testid="merch-milestone-status">
                  <option value="pending">Pending</option>
                  <option value="done">Done</option>
                  <option value="waived">Waived (not needed)</option>
                </Select>
              )}
            </Field>
          )}
        </div>
        {milestone && f.status === "done" && (
          <Field label="Completed on" hint="Defaults to today.">{(a) => <Input id={a.id} type="date" value={f.completedOn} onChange={set("completedOn")} />}</Field>
        )}
        <Field label="Notes (optional)">{(a) => <TextArea id={a.id} rows={2} value={f.notes} onChange={set("notes")} maxLength={500} />}</Field>
      </form>
    </Modal>
  );
}

// ── Conflicts ───────────────────────────────────────────────────────────────

export function ResolveConflictModal({ conflict, onClose, onResolved }: { conflict: MerchConflict | null; onClose: () => void; onResolved: () => void }) {
  const toast = useToast();
  const s = useSubmit();
  const [choice, setChoice] = useState<string>("");
  const [custom, setCustom] = useState("");
  const [note, setNote] = useState("");
  useEffect(() => {
    if (!conflict) return;
    s.reset();
    setChoice(""); setCustom(""); setNote("");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [conflict?.id]);
  if (!conflict) return null;
  const distinct = Array.from(new Map(conflict.values.map((v) => [v.value, v])).values());

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const chosenValue = choice === "__custom" ? custom : choice;
    const saved = await s.run(() => resolveConflict(conflict!.id, { expectedVersion: conflict!.version, chosenValue, note }));
    if (!saved) return;
    toast.push({ title: `${conflict!.label} resolved`, description: saved.resolvedValue, tone: "success" });
    announceDataChanged();
    onResolved();
    onClose();
  }

  return (
    <Modal open onClose={onClose} locked={s.busy} className="sm:max-w-2xl" testId="merch-resolve-form" title={`Resolve: ${conflict.label}`}
      description="The chosen value becomes the order's value. Your decision and note are recorded in the audit trail."
      footer={<Footer form="merch-resolve-form-el" onCancel={onClose} busy={s.busy} label="Resolve" testId="merch-resolve-save" disabled={!choice || note.trim().length < 3 || (choice === "__custom" && !custom.trim())} />}>
      <form id="merch-resolve-form-el" onSubmit={submit} className="space-y-3">
        <FormError error={s.error} />
        <fieldset>
          <legend className="field-label">Which value is correct?</legend>
          <div className="space-y-2" role="radiogroup">
            {distinct.map((v) => (
              <label key={v.value} className="surface-2 p-3 flex items-start gap-2 cursor-pointer">
                <input type="radio" name="merch-choice" className="mt-1 accent-[var(--accent)]" checked={choice === v.value} onChange={() => setChoice(v.value)} data-testid="merch-resolve-option" />
                <span className="min-w-0">
                  <span className="block text-body font-medium text-fg-primary">{v.display}</span>
                  <span className="block text-caption text-fg-tertiary">
                    {conflict.values.filter((x) => x.value === v.value).map((x) => (x.sourceKind === "order" ? "Order record (typed)" : `${x.documentTitle} v${x.versionNumber}${x.page ? `, p. ${x.page}` : ""}`)).join(" · ")}
                  </span>
                </span>
              </label>
            ))}
            <label className="surface-2 p-3 flex items-start gap-2 cursor-pointer">
              <input type="radio" name="merch-choice" className="mt-1 accent-[var(--accent)]" checked={choice === "__custom"} onChange={() => setChoice("__custom")} />
              <span className="flex-1 space-y-1">
                <span className="block text-body text-fg-primary">Another value</span>
                {choice === "__custom" && <Input aria-label="Correct value" value={custom} onChange={(e) => setCustom(e.target.value)} data-testid="merch-resolve-custom" />}
              </span>
            </label>
          </div>
          {s.fe("chosenValue") && <p className="field-error">{s.fe("chosenValue")}</p>}
        </fieldset>
        <Field label="Why is this the correct value?" hint="For example: confirmed by the buyer's email of 27 Oct." error={s.fe("note")}>
          {(a) => <TextArea id={a.id} rows={2} value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} required data-testid="merch-resolve-note" />}
        </Field>
      </form>
    </Modal>
  );
}
