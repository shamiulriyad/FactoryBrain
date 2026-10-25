"use client";

// /app/vision — submit a garment image for inspection. The API stores it,
// queues it for the configured computer-vision provider and returns the
// inspection; the detail page follows the analysis. With no provider
// configured the page says so: the image can still be stored and inspected
// manually, but no prediction is shown that a model did not make.

import { useEffect, useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Camera, ImagePlus, ScanLine } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Skeleton } from "@/components/ui/Skeleton";
import { useToast } from "@/components/ui/Toast";
import { useResource, announceDataChanged } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import { SAFE_TOKEN, STAGE_LABEL, formatBytes, sniffImageType, validateImageFile } from "@/lib/qcInspection";
import { createInspection } from "@/services/qc.api";
import { QC_STAGES, type LineBoard, type QcCategory, type QcStage, type QcVisionStatus } from "@/services/contracts";

const PRODUCT_SUGGESTIONS = ["T-shirt", "Polo shirt", "Shirt", "Trousers", "Denim jeans", "Hoodie", "Jacket", "Dress", "Knit fabric", "Woven fabric"];

type Errors = Partial<Record<"image" | "productType" | "stage" | "lineId" | "productionOrder" | "notes" | "categoryCodes" | "form", string>>;

export default function VisionInspectPage() {
  const router = useRouter();
  const toast = useToast();
  const operator = canOperate(useAuthStore((s) => s.user));
  const status = useResource<QcVisionStatus>("/api/qc/vision/status");
  const categories = useResource<QcCategory[]>("/api/qc/categories");
  const lines = useResource<LineBoard>("/api/line-board");

  const [file, setFile] = useState<File | null>(null);
  const [preview, setPreview] = useState<string | null>(null);
  const [productType, setProductType] = useState("");
  const [stage, setStage] = useState<QcStage>("end_of_line");
  const [lineId, setLineId] = useState("");
  const [productionOrder, setProductionOrder] = useState("");
  const [notes, setNotes] = useState("");
  const [selected, setSelected] = useState<string[] | null>(null);
  const [errors, setErrors] = useState<Errors>({});
  const [busy, setBusy] = useState(false);
  const fileInput = useRef<HTMLInputElement>(null);
  // One key per submission attempt: a double click or a retried request returns the same inspection.
  const idempotencyKey = useRef<string | null>(null);

  const maxBytes = status.data?.maxImageBytes ?? 10 * 1024 * 1024;
  const active = useMemo(() => categories.data ?? [], [categories.data]);
  const chosen = selected ?? active.map((c) => c.code);

  useEffect(() => {
    if (!file) return setPreview(null);
    const u = URL.createObjectURL(file);
    setPreview(u);
    return () => URL.revokeObjectURL(u);
  }, [file]);

  function changed() {
    idempotencyKey.current = null;
  }

  async function pick(f: File | null) {
    changed();
    setErrors((e) => ({ ...e, image: undefined, form: undefined }));
    if (!f) return setFile(null);
    let problem = validateImageFile(f, maxBytes);
    if (!problem) {
      const head = new Uint8Array(await f.slice(0, 16).arrayBuffer());
      if (!sniffImageType(head)) problem = "This file is not a JPEG, PNG or WebP image (its contents don't match).";
    }
    if (problem) {
      setFile(null);
      if (fileInput.current) fileInput.current.value = "";
      setErrors((e) => ({ ...e, image: problem! }));
      return;
    }
    setFile(f);
  }

  function validate(): Errors {
    const e: Errors = {};
    const img = validateImageFile(file, maxBytes);
    if (img) e.image = img;
    if (!productType.trim()) e.productType = "Enter the product type.";
    else if (productType.trim().length > 64 || !SAFE_TOKEN.test(productType.trim())) e.productType = "Use up to 64 letters, digits, spaces and - _ . / #.";
    if (lineId.trim() && (lineId.trim().length > 32 || !SAFE_TOKEN.test(lineId.trim()))) e.lineId = "Use up to 32 letters, digits and - _ . / #.";
    if (productionOrder.trim() && (productionOrder.trim().length > 64 || !SAFE_TOKEN.test(productionOrder.trim())))
      e.productionOrder = "Use up to 64 letters, digits and - _ . / #.";
    if (notes.length > 1000) e.notes = "At most 1000 characters.";
    if (active.length > 0 && chosen.length === 0) e.categoryCodes = "Select at least one defect category.";
    return e;
  }

  async function submit(ev: React.FormEvent) {
    ev.preventDefault();
    if (busy) return;
    const e = validate();
    setErrors(e);
    if (Object.keys(e).length) return;
    setBusy(true);
    idempotencyKey.current ??= crypto.randomUUID();
    try {
      const { inspection, replayed } = await createInspection(
        { image: file!, productType, stage, lineId, productionOrder, notes, categoryCodes: selected === null ? [] : chosen },
        idempotencyKey.current
      );
      announceDataChanged();
      toast.push({
        title: replayed ? "Already submitted" : "Inspection submitted",
        description: `${inspection.reference} · ${inspection.status === "failed" ? "stored without AI analysis" : "analysis queued"}`,
        tone: "success"
      });
      router.push(`/app/qc/${inspection.id}`);
    } catch (err) {
      if (isApiError(err) && Object.keys(err.fieldErrors).length) {
        const fe: Errors = {};
        for (const [k, v] of Object.entries(err.fieldErrors)) fe[(k in { image: 1, productType: 1, stage: 1, lineId: 1, productionOrder: 1, notes: 1, categoryCodes: 1 } ? k : "form") as keyof Errors] = v[0];
        setErrors(fe);
      } else {
        setErrors({ form: errorMessage(err) });
      }
      setBusy(false);
    }
  }

  const s = status.data;

  return (
    <div className="page page-narrow" data-tour="vision">
      <PageHeader
        eyebrow="Quality control"
        title="Inspect a garment"
        description="Upload a photo of a garment or fabric. It is stored with its production context, analysed by the configured vision model, and then reviewed by an inspector."
        actions={<Link href="/app/qc" className="text-caption text-accent hover:underline">Inspection history →</Link>}
      />

      <div className="mt-5" data-testid="qc-vision-status">
        {status.loading ? (
          <Skeleton className="h-16" />
        ) : status.error ? (
          <Callout tone="danger" title="Could not check the vision provider" action={<Button size="sm" onClick={() => void status.reload()}>Retry</Button>}>
            {errorMessage(status.error)}
          </Callout>
        ) : s?.configured ? (
          <Callout tone="info" title={`AI analysis: ${s.provider ?? s.mode}${s.model ? ` · ${s.model}` : ""}`} testId="qc-provider-configured">
            Predictions below {Math.round(s.confidenceThreshold * 100)}% confidence are not stored. {s.confidenceNote} Every prediction is reviewed by an inspector before it counts.
          </Callout>
        ) : (
          <Callout tone="warning" title="AI analysis is not configured" testId="qc-provider-unconfigured">
            {s?.reason} Images you submit are stored and can be inspected manually; no predictions will be shown until an administrator configures a vision provider on the API server.
          </Callout>
        )}
      </div>

      {!operator && (
        <Callout tone="info" className="mt-4" testId="qc-submit-forbidden">
          Submitting inspections needs the Operator or Admin role. You can browse <Link className="underline" href="/app/qc">inspection history</Link>.
        </Callout>
      )}

      <form onSubmit={(e) => void submit(e)} noValidate className="mt-4 space-y-4" aria-label="New inspection">
        <Panel title="Image" icon={<Camera size={14} />} subtitle={`JPEG, PNG or WebP, up to ${formatBytes(maxBytes)}.`}>
          <div className="grid grid-cols-1 sm:grid-cols-[minmax(0,1fr)_200px] gap-4 items-start">
            <Field label="Garment photo" error={errors.image} hint="On a phone this opens the camera.">
              {(p) => (
                <input
                  ref={fileInput}
                  id={p.id}
                  aria-describedby={p.describedBy}
                  aria-invalid={p.invalid}
                  type="file"
                  accept="image/jpeg,image/png,image/webp"
                  capture="environment"
                  disabled={!operator || busy}
                  onChange={(e) => void pick(e.target.files?.[0] ?? null)}
                  data-testid="qc-file"
                  className="control !h-auto py-2 file:mr-3 file:rounded-md file:border-0 file:bg-surface-3 file:px-3 file:py-1 file:text-caption"
                />
              )}
            </Field>
            <div className="surface-2 aspect-[4/3] flex items-center justify-center overflow-hidden" aria-live="polite">
              {preview ? (
                // eslint-disable-next-line @next/next/no-img-element -- local preview of the picked file
                <img src={preview} alt="Selected garment" className="h-full w-full object-contain" />
              ) : (
                <ImagePlus size={22} className="text-fg-tertiary" aria-hidden />
              )}
            </div>
          </div>
          {file && <p className="mt-2 text-caption text-fg-tertiary" data-testid="qc-file-info">{file.name} · {formatBytes(file.size)}</p>}
        </Panel>

        <Panel title="Production context">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <Field label="Product type" error={errors.productType}>
              {(p) => (
                <>
                  <Input id={p.id} aria-describedby={p.describedBy} aria-invalid={p.invalid} list="qc-products" value={productType}
                    onChange={(e) => { setProductType(e.target.value); changed(); }} disabled={!operator || busy} maxLength={64} data-testid="qc-product" />
                  <datalist id="qc-products">{PRODUCT_SUGGESTIONS.map((x) => <option key={x} value={x} />)}</datalist>
                </>
              )}
            </Field>
            <Field label="Inspection stage" error={errors.stage}>
              {(p) => (
                <Select id={p.id} aria-describedby={p.describedBy} value={stage} onChange={(e) => { setStage(e.target.value as QcStage); changed(); }}
                  disabled={!operator || busy} data-testid="qc-stage">
                  {QC_STAGES.map((st) => <option key={st} value={st}>{STAGE_LABEL[st]}</option>)}
                </Select>
              )}
            </Field>
            <Field label="Production line (optional)" error={errors.lineId}>
              {(p) => (
                <>
                  <Input id={p.id} aria-describedby={p.describedBy} aria-invalid={p.invalid} list="qc-lines" value={lineId}
                    onChange={(e) => { setLineId(e.target.value); changed(); }} disabled={!operator || busy} maxLength={32} data-testid="qc-line" />
                  <datalist id="qc-lines">{(lines.data?.rows ?? []).map((r) => <option key={r.lineId} value={r.lineId}>{r.name}</option>)}</datalist>
                </>
              )}
            </Field>
            <Field label="Production order (optional)" error={errors.productionOrder}>
              {(p) => (
                <Input id={p.id} aria-describedby={p.describedBy} aria-invalid={p.invalid} value={productionOrder}
                  onChange={(e) => { setProductionOrder(e.target.value); changed(); }} disabled={!operator || busy} maxLength={64} data-testid="qc-order" />
              )}
            </Field>
          </div>
          <Field label="Notes (optional)" className="mt-4" error={errors.notes} hint={`${notes.length}/1000`}>
            {(p) => (
              <TextArea id={p.id} aria-describedby={p.describedBy} rows={2} value={notes} onChange={(e) => { setNotes(e.target.value); changed(); }}
                disabled={!operator || busy} />
            )}
          </Field>
        </Panel>

        <Panel
          title="Defect categories to look for"
          subtitle="The model is asked about these categories. Admins manage the list on the Quality page."
          right={
            active.length > 0 && (
              <div className="flex gap-2">
                <Button type="button" size="sm" variant="ghost" onClick={() => { setSelected(null); changed(); }} disabled={!operator || busy}>All</Button>
                <Button type="button" size="sm" variant="ghost" onClick={() => { setSelected([]); changed(); }} disabled={!operator || busy}>None</Button>
              </div>
            )
          }
        >
          {categories.loading ? (
            <Skeleton className="h-20" />
          ) : categories.error ? (
            <Callout tone="danger" title="Could not load the defect categories" action={<Button size="sm" onClick={() => void categories.reload()}>Retry</Button>}>
              {errorMessage(categories.error)}
            </Callout>
          ) : active.length === 0 ? (
            <Callout tone="warning">No active defect categories exist. An Admin must add one on the Quality page before inspections can be submitted.</Callout>
          ) : (
            <fieldset className="grid grid-cols-1 sm:grid-cols-2 gap-x-4 gap-y-2" aria-describedby={errors.categoryCodes ? "qc-cat-error" : undefined}>
              <legend className="sr-only">Defect categories</legend>
              {active.map((c) => (
                <label key={c.code} className="flex items-start gap-2 text-body text-fg-primary">
                  <input
                    type="checkbox"
                    className="mt-1 accent-[var(--accent)]"
                    checked={chosen.includes(c.code)}
                    disabled={!operator || busy}
                    onChange={(e) => {
                      changed();
                      setSelected(e.target.checked ? [...chosen, c.code] : chosen.filter((x) => x !== c.code));
                    }}
                  />
                  <span>
                    {c.name}
                    {c.description && <span className="block text-caption text-fg-tertiary">{c.description}</span>}
                  </span>
                </label>
              ))}
            </fieldset>
          )}
          {errors.categoryCodes && <p id="qc-cat-error" className="field-error mt-2" role="alert">{errors.categoryCodes}</p>}
        </Panel>

        {errors.form && <Callout tone="danger" title="The inspection was not submitted">{errors.form}</Callout>}

        <div className="flex flex-wrap items-center gap-3">
          <Button type="submit" variant="primary" disabled={!operator || busy || active.length === 0} data-testid="qc-submit">
            <ScanLine size={14} /> {busy ? "Uploading…" : s?.configured ? "Submit for analysis" : "Submit without AI analysis"}
          </Button>
          <span className="text-caption text-fg-tertiary">The image is stored on the server and only signed-in users can view it.</span>
        </div>
      </form>
    </div>
  );
}
