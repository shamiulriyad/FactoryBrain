"use client";

// /app/qc/[id] — one inspection: the stored image with the provider's
// localisation, the predictions exactly as the API returned them, and the
// inspector's review (accept / correct / reject each prediction, add missed
// defects, record the outcome). Everything shown comes from
// GET /api/qc/inspections/{id}; the page polls while analysis is running.

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { ArrowLeft, Check, Pencil, Plus, RefreshCw, Square, Trash2, X as XIcon } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { InspectionImage, overlaysFor } from "@/components/qc/InspectionImage";
import { useResource, announceDataChanged } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { canOperate, isAdmin, useAuthStore } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { cn } from "@/lib/cn";
import {
  OUTCOME_LABEL,
  OUTCOME_TONE,
  SEVERITY_TONE,
  STAGE_LABEL,
  STATUS_LABEL,
  STATUS_TONE,
  buildReviewRequest,
  canReview,
  findingLabel,
  formatBytes,
  formatConfidence,
  initialDraft,
  isInProgress,
  validateReviewDraft,
  type DraftDecision,
  type ReviewDraft
} from "@/lib/qcInspection";
import { retryInspection, reviewInspection } from "@/services/qc.api";
import { QC_OUTCOMES, QC_SEVERITIES, type AuditEntry, type QcCategory, type QcFinding, type QcInspectionDetail, type QcSeverity } from "@/services/contracts";

export default function InspectionDetailPage() {
  const params = useParams<{ id: string }>();
  const id = params?.id ?? "";
  const toast = useToast();
  const user = useAuthStore((s) => s.user);
  const operator = canOperate(user);
  const admin = isAdmin(user);

  const [poll, setPoll] = useState(true);
  const res = useResource<QcInspectionDetail>(id ? `/api/qc/inspections/${encodeURIComponent(id)}` : null, { pollMs: poll ? 3000 : undefined });
  const categories = useResource<QcCategory[]>("/api/qc/categories");
  const audit = useResource<AuditEntry[]>(admin && id ? `/api/audit?entityType=qc_inspection&entityId=${encodeURIComponent(id)}&limit=50` : null);
  const d = res.data;

  useEffect(() => {
    if (d) setPoll(isInProgress(d.status));
  }, [d]);

  const [reviewing, setReviewing] = useState(false);
  const [draft, setDraft] = useState<ReviewDraft | null>(null);
  const [drawing, setDrawing] = useState(false);
  const [highlight, setHighlight] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [submitErrors, setSubmitErrors] = useState<string[]>([]);

  // Completed inspections open straight into the review form for inspectors.
  useEffect(() => {
    if (d && operator && d.status === "completed" && !reviewing) {
      setReviewing(true);
      setDraft(initialDraft(d));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [d?.status, d?.version, operator]);

  const activeCategories = useMemo(() => (categories.data ?? []).filter((c) => c.isActive), [categories.data]);
  const overlays = useMemo(() => {
    if (!d) return [];
    const stored = overlaysFor(d.findings);
    const drafted = (draft?.additions ?? [])
      .filter((a) => a.box)
      .map((a) => ({ id: a.key, box: a.box, label: `${activeCategories.find((c) => c.code === a.categoryCode)?.name ?? "New defect"} (added)`, tone: "inspector" as const }));
    return [...stored, ...drafted];
  }, [d, draft, activeCategories]);

  if (!id) return null;
  if (res.loading) return <div className="page"><SkeletonRows rows={6} rowHeight="h-16" /></div>;
  if (res.error && !d) {
    const notFound = isApiError(res.error) && res.error.kind === "not_found";
    return (
      <div className="page page-narrow">
        <Callout tone="danger" title={notFound ? "Inspection not found" : "Could not load the inspection"}
          action={!notFound && <Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>
          {notFound ? "It may have been mistyped." : errorMessage(res.error)}
        </Callout>
        <Link href="/app/qc" className="mt-4 inline-flex text-caption text-accent hover:underline">← Inspection history</Link>
      </div>
    );
  }
  if (!d) return null;

  function startReview() {
    setDraft(initialDraft(d!));
    setReviewing(true);
    setSubmitErrors([]);
  }

  function setDecision(f: QcFinding, patch: Partial<DraftDecision> | null) {
    setDraft((cur) => {
      if (!cur) return cur;
      const decisions = { ...cur.decisions };
      if (patch === null) delete decisions[f.id];
      else decisions[f.id] = { ...(decisions[f.id] ?? { decision: "accepted" }), ...patch } as DraftDecision;
      return { ...cur, decisions };
    });
  }

  async function submitReview() {
    if (!draft || busy) return;
    const problems = validateReviewDraft(d!, draft);
    setSubmitErrors(problems);
    if (problems.length) return;
    setBusy(true);
    try {
      const updated = await reviewInspection(d!.id, buildReviewRequest(d!, draft));
      res.setData(updated);
      setReviewing(false);
      setDraft(null);
      setDrawing(false);
      announceDataChanged();
      void audit.reload();
      toast.push({ title: "Review saved", description: `${updated.reference} · ${OUTCOME_LABEL[updated.review!.outcome]}`, tone: "success" });
    } catch (e) {
      if (isApiError(e) && e.kind === "conflict") {
        setSubmitErrors(["Someone else changed this inspection while you were reviewing. The latest version has been loaded; check it and review again."]);
        await res.reload();
        setReviewing(false);
      } else if (isApiError(e) && Object.keys(e.fieldErrors).length) {
        setSubmitErrors(Object.values(e.fieldErrors).flat());
      } else {
        setSubmitErrors([errorMessage(e)]);
      }
    } finally {
      setBusy(false);
    }
  }

  async function retry() {
    if (busy) return;
    setBusy(true);
    try {
      res.setData(await retryInspection(d!.id));
      setPoll(true);
      announceDataChanged();
      toast.push({ title: "Analysis queued again", tone: "info" });
    } catch (e) {
      toast.push({ title: "Retry failed", description: errorMessage(e), tone: "error" });
    } finally {
      setBusy(false);
    }
  }

  const a = d.analysis;
  const modelFindings = d.findings.filter((f) => f.source === "model");
  const inspectorFindings = d.findings.filter((f) => f.source === "inspector");

  return (
    <div className="page" data-testid="qc-detail" data-status={d.status}>
      <Link href="/app/qc" className="inline-flex items-center gap-1 text-caption text-fg-secondary hover:text-fg-primary"><ArrowLeft size={12} /> Inspection history</Link>
      <PageHeader
        className="mt-2"
        eyebrow="Quality inspection"
        title={<span className="font-mono">{d.reference}</span>}
        description={`${d.productType} · ${STAGE_LABEL[d.stage]}${d.lineId ? ` · ${d.lineId}` : ""}${d.productionOrder ? ` · order ${d.productionOrder}` : ""}`}
        meta={
          <>
            <Badge tone={STATUS_TONE[d.status]} dot testId="qc-status">{STATUS_LABEL[d.status]}</Badge>
            {d.review && <Badge tone={OUTCOME_TONE[d.review.outcome]} testId="qc-outcome">{OUTCOME_LABEL[d.review.outcome]}</Badge>}
            <span className="text-caption text-fg-tertiary">Submitted {formatDateTime(d.createdAt)} by {d.createdByEmail}</span>
          </>
        }
        actions={
          operator && !reviewing && canReview(d.status) && (
            <Button variant="secondary" size="sm" onClick={startReview} data-testid="qc-start-review">
              <Pencil size={13} /> {d.status === "reviewed" ? "Correct review" : d.status === "failed" ? "Review manually" : "Review"}
            </Button>
          )
        }
      />

      <div className="mt-5 grid grid-cols-1 xl:grid-cols-[minmax(0,1.2fr)_minmax(0,1fr)] gap-4 items-start">
        <div className="space-y-4 min-w-0">
          <Panel
            title="Image"
            subtitle={`${d.image.width}×${d.image.height} · ${formatBytes(d.image.sizeBytes)}${d.image.originalFileName ? ` · ${d.image.originalFileName}` : ""}`}
            right={
              reviewing && (
                <Button size="sm" variant={drawing ? "primary" : "secondary"} onClick={() => setDrawing((v) => !v)} aria-pressed={drawing} data-testid="qc-draw">
                  <Square size={12} /> {drawing ? "Drag on the image…" : "Mark a missed defect"}
                </Button>
              )
            }
          >
            <InspectionImage
              url={d.image.url}
              alt={`Inspection ${d.reference}`}
              width={d.image.width}
              height={d.image.height}
              overlays={overlays}
              highlightedId={highlight}
              onSelect={(fid) => setHighlight(fid)}
              drawing={drawing}
              onDraw={(box) => {
                setDrawing(false);
                setDraft((cur) => cur && { ...cur, additions: [...cur.additions, { key: `add-${Date.now()}`, categoryCode: "", box }] });
              }}
            />
            <p className="mt-2 text-caption text-fg-tertiary">
              {d.findings.some((f) => f.box || f.polygon)
                ? "Red: model predictions · blue: inspector findings · dashed: rejected. Boxes are drawn where the provider located them."
                : a.localizationSupported === false
                  ? "The provider does not return locations, so no boxes are drawn."
                  : "No located defects to draw."}
            </p>
          </Panel>

          <Panel title="Analysis" testId="qc-analysis">
            {isInProgress(d.status) ? (
              <div className="flex items-center gap-2 text-body text-fg-secondary" role="status">
                <RefreshCw size={14} className="animate-spin" aria-hidden />
                {d.status === "pending" ? "Queued for the vision model…" : "The vision model is analysing the image…"} This page updates by itself.
              </div>
            ) : d.status === "failed" || a.failureCode ? (
              <Callout
                tone={a.failureCode === "provider_not_configured" ? "warning" : "danger"}
                title={a.failureCode === "provider_not_configured" ? "No AI analysis: no vision provider is configured" : "Analysis failed"}
                testId="qc-failure"
                action={operator && a.retryable && a.failureCode !== "provider_not_configured" && (
                  <Button size="sm" onClick={() => void retry()} disabled={busy} data-testid="qc-retry"><RefreshCw size={12} /> Retry</Button>
                )}
              >
                {a.failureMessage} <span className="font-mono">({a.failureCode})</span>
                {a.failureCode === "provider_not_configured" && " An inspector can still review this image manually."}
              </Callout>
            ) : (
              <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-caption">
                <dt className="text-fg-tertiary">Provider</dt><dd className="text-fg-primary" data-testid="qc-provider">{a.provider ?? "—"}</dd>
                <dt className="text-fg-tertiary">Model</dt><dd className="text-fg-primary font-mono break-all" data-testid="qc-model">{a.model ?? "—"}{a.modelVersion ? ` (${a.modelVersion})` : ""}</dd>
                <dt className="text-fg-tertiary">Confidence threshold</dt><dd className="text-fg-primary">{formatConfidence(a.confidenceThreshold)}</dd>
                <dt className="text-fg-tertiary">Localisation</dt><dd className="text-fg-primary">{a.localizationSupported ? "Boxes / masks" : "Labels only"}</dd>
                <dt className="text-fg-tertiary">Inference time</dt><dd className="text-fg-primary">{a.inferenceMs != null ? `${(a.inferenceMs / 1000).toFixed(1)} s` : "—"}</dd>
                <dt className="text-fg-tertiary">Analysed</dt><dd className="text-fg-primary">{a.analyzedAt ? formatDateTime(a.analyzedAt) : "—"}</dd>
              </dl>
            )}
          </Panel>
        </div>

        <div className="space-y-4 min-w-0">
          <Panel
            title={`Model predictions (${modelFindings.length})`}
            subtitle="Exactly as the provider returned them, after the confidence threshold. Confidence is the provider's own score."
            testId="qc-predictions"
          >
            {isInProgress(d.status) ? (
              <p className="text-caption text-fg-tertiary">Waiting for the analysis.</p>
            ) : modelFindings.length === 0 ? (
              <p className="text-caption text-fg-tertiary" data-testid="qc-no-predictions">
                {d.status === "failed" || a.failureCode ? "No predictions: the analysis did not run." : "The model reported no defects above the threshold."}
              </p>
            ) : (
              <ul className="space-y-2">
                {modelFindings.map((f) => (
                  <FindingRow key={f.id} f={f} highlighted={highlight === f.id} onHover={setHighlight}
                    reviewing={reviewing} draft={draft?.decisions[f.id]} categories={activeCategories} onDecision={(p) => setDecision(f, p)} />
                ))}
              </ul>
            )}
          </Panel>

          {(inspectorFindings.length > 0 || reviewing) && (
            <Panel title="Inspector findings" testId="qc-inspector-findings">
              {inspectorFindings.length > 0 && (
                <ul className="space-y-2 mb-3">
                  {inspectorFindings.map((f) => (
                    <FindingRow key={f.id} f={f} highlighted={highlight === f.id} onHover={setHighlight}
                      reviewing={reviewing} draft={draft?.decisions[f.id]} categories={activeCategories} onDecision={(p) => setDecision(f, p)} />
                  ))}
                </ul>
              )}
              {reviewing && draft && (
                <div className="space-y-2">
                  {draft.additions.map((add, i) => (
                    <div key={add.key} className="surface-2 p-3 grid grid-cols-1 sm:grid-cols-[1fr_120px_auto] gap-2 items-end" data-testid="qc-addition">
                      <Field label={`Missed defect ${i + 1}${add.box ? " (marked on image)" : ""}`}>
                        {(p) => (
                          <Select id={p.id} value={add.categoryCode} data-testid="qc-addition-category"
                            onChange={(e) => setDraft({ ...draft, additions: draft.additions.map((x) => (x.key === add.key ? { ...x, categoryCode: e.target.value } : x)) })}>
                            <option value="">Choose a category…</option>
                            {activeCategories.map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
                          </Select>
                        )}
                      </Field>
                      <Field label="Severity">
                        {(p) => (
                          <Select id={p.id} value={add.severity ?? ""}
                            onChange={(e) => setDraft({ ...draft, additions: draft.additions.map((x) => (x.key === add.key ? { ...x, severity: (e.target.value || undefined) as QcSeverity | undefined } : x)) })}>
                            <option value="">Default</option>
                            {QC_SEVERITIES.map((s) => <option key={s} value={s}>{s}</option>)}
                          </Select>
                        )}
                      </Field>
                      <Button size="sm" variant="ghost" aria-label={`Remove missed defect ${i + 1}`}
                        onClick={() => setDraft({ ...draft, additions: draft.additions.filter((x) => x.key !== add.key) })}>
                        <Trash2 size={13} />
                      </Button>
                    </div>
                  ))}
                  <Button size="sm" variant="secondary" data-testid="qc-add-defect"
                    onClick={() => setDraft({ ...draft, additions: [...draft.additions, { key: `add-${Date.now()}`, categoryCode: "" }] })}>
                    <Plus size={12} /> Add a missed defect
                  </Button>
                </div>
              )}
            </Panel>
          )}

          {reviewing && draft ? (
            <Panel title="Verified outcome" testId="qc-review-form">
              <fieldset>
                <legend className="field-label">Outcome for this piece</legend>
                <div className="flex flex-wrap gap-2" role="radiogroup" aria-label="Outcome">
                  {QC_OUTCOMES.map((o) => (
                    <button key={o} type="button" role="radio" aria-checked={draft.outcome === o} data-testid={`qc-outcome-${o}`}
                      onClick={() => setDraft({ ...draft, outcome: o })}
                      className={cn("h-8 px-3 rounded-[var(--radius-control)] border text-caption font-medium",
                        draft.outcome === o ? "border-[var(--accent-border)] bg-[var(--accent-soft)] text-fg-primary" : "border-border-subtle text-fg-secondary hover:text-fg-primary")}>
                      {OUTCOME_LABEL[o]}
                    </button>
                  ))}
                </div>
              </fieldset>
              <Field label="Review notes" className="mt-3" hint={`${draft.notes.length}/1000`}>
                {(p) => <TextArea id={p.id} rows={2} value={draft.notes} maxLength={1000} onChange={(e) => setDraft({ ...draft, notes: e.target.value })} data-testid="qc-review-notes" />}
              </Field>
              {!d.followUp && (
                <label className="mt-3 flex items-start gap-2 text-body text-fg-primary">
                  <input type="checkbox" className="mt-1 accent-[var(--accent)]" checked={draft.requestFollowUp} data-testid="qc-follow-up"
                    onChange={(e) => setDraft({ ...draft, requestFollowUp: e.target.checked })} />
                  <span>Request corrective action<span className="block text-caption text-fg-tertiary">Adds an item to the approval queue; its risk follows the most severe verified defect.</span></span>
                </label>
              )}
              {draft.requestFollowUp && (
                <Field label="What should be checked?" className="mt-2">
                  {(p) => <Input id={p.id} value={draft.followUpNote} maxLength={1000} onChange={(e) => setDraft({ ...draft, followUpNote: e.target.value })} />}
                </Field>
              )}
              {submitErrors.length > 0 && (
                <Callout tone="danger" className="mt-3" title="The review can't be saved yet" testId="qc-review-errors">
                  <ul className="list-disc pl-4">{submitErrors.map((e) => <li key={e}>{e}</li>)}</ul>
                </Callout>
              )}
              <div className="mt-4 flex flex-wrap gap-2">
                <Button variant="primary" onClick={() => void submitReview()} disabled={busy} data-testid="qc-submit-review">
                  <Check size={13} /> {busy ? "Saving…" : "Save review"}
                </Button>
                {d.status !== "completed" && (
                  <Button variant="ghost" onClick={() => { setReviewing(false); setDraft(null); setDrawing(false); setSubmitErrors([]); }} disabled={busy}>Cancel</Button>
                )}
              </div>
            </Panel>
          ) : d.review ? (
            <Panel title="Review" testId="qc-review">
              <p className="text-body text-fg-primary">
                <Badge tone={OUTCOME_TONE[d.review.outcome]}>{OUTCOME_LABEL[d.review.outcome]}</Badge>{" "}
                by {d.review.reviewedByEmail ?? "unknown"} · {formatDateTime(d.review.reviewedAt)}
              </p>
              {d.review.notes && <p className="mt-2 text-body text-fg-secondary whitespace-pre-wrap">{d.review.notes}</p>}
              <p className="mt-2 text-caption text-fg-tertiary">{d.findings.filter((f) => f.verified).length} verified defect(s).</p>
            </Panel>
          ) : !operator && canReview(d.status) ? (
            <Callout tone="info">Reviewing needs the Operator or Admin role.</Callout>
          ) : null}

          {d.followUp && (
            <Callout tone="info" title="Corrective action requested" testId="qc-follow-up-status"
              action={<Link className="text-caption text-accent hover:underline" href={`/app/approvals?focus=${encodeURIComponent(d.followUp.approvalId)}`}>Open approval</Link>}>
              Approval <span className="font-mono">{d.followUp.approvalId}</span> · {d.followUp.status.replace("_", " ")} · {d.followUp.riskTier} risk
            </Callout>
          )}

          {admin && (
            <Panel title="Audit trail" testId="qc-audit">
              {audit.loading ? <SkeletonRows rows={2} rowHeight="h-6" /> : audit.error ? (
                <p className="text-caption text-[var(--risk-high)]">{errorMessage(audit.error)}</p>
              ) : (
                <ol className="space-y-1.5 text-caption">
                  {(audit.data ?? []).map((e) => (
                    <li key={e.id} className="flex flex-wrap gap-x-2">
                      <span className="text-fg-tertiary">{formatDateTime(e.occurredAt)}</span>
                      <span className="font-mono text-fg-primary">{e.action}</span>
                      <span className="text-fg-secondary">{e.actorEmail}</span>
                      {e.reason && <span className="text-fg-tertiary basis-full">{e.reason}</span>}
                    </li>
                  ))}
                </ol>
              )}
            </Panel>
          )}
        </div>
      </div>
    </div>
  );
}

function FindingRow({
  f, highlighted, onHover, reviewing, draft, categories, onDecision
}: {
  f: QcFinding;
  highlighted: boolean;
  onHover: (id: string | null) => void;
  reviewing: boolean;
  draft?: DraftDecision;
  categories: QcCategory[];
  onDecision: (patch: Partial<DraftDecision> | null) => void;
}) {
  const decision = draft?.decision ?? (f.decision === "unreviewed" ? undefined : f.decision);
  return (
    <li
      className={cn("surface-2 p-3 transition-shadow", highlighted && "ring-2 ring-[var(--accent)]")}
      onMouseEnter={() => onHover(f.id)}
      onMouseLeave={() => onHover(null)}
      data-testid="qc-finding"
      data-finding-id={f.id}
      data-decision={f.decision}
    >
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="text-body font-medium text-fg-primary">
            {findingLabel(f)}
            {!f.categoryCode && f.source === "model" && <span className="ml-2 text-caption text-[var(--risk-medium)]">(not in the taxonomy)</span>}
          </p>
          <p className="text-caption text-fg-tertiary">
            {f.source === "model" ? `Confidence ${formatConfidence(f.confidence)}` : "Added by inspector"}
            {f.box ? " · located" : f.source === "model" ? " · no location" : ""}
            {f.description ? ` · ${f.description}` : ""}
          </p>
          {f.reviewerNote && <p className="mt-1 text-caption text-fg-secondary">Note: {f.reviewerNote}</p>}
        </div>
        <div className="flex items-center gap-1.5 shrink-0">
          <Badge tone={SEVERITY_TONE[f.effectiveSeverity]}>{f.effectiveSeverity}</Badge>
          {f.decision !== "unreviewed" && <Badge tone={f.verified ? "success" : "neutral"}>{f.decision}</Badge>}
        </div>
      </div>
      {reviewing && (
        <div className="mt-2 space-y-2">
          <div className="flex flex-wrap gap-1.5" role="radiogroup" aria-label={`Decision for ${findingLabel(f)}`}>
            {([
              ["accepted", "Accept", Check],
              ["corrected", "Correct", Pencil],
              ["rejected", "Reject", XIcon]
            ] as const).map(([value, label, Icon]) => (
              <button key={value} type="button" role="radio" aria-checked={decision === value} data-testid={`qc-decide-${value}`}
                onClick={() => onDecision({ decision: value })}
                className={cn("h-7 px-2.5 inline-flex items-center gap-1 rounded-md border text-caption",
                  decision === value ? "border-[var(--accent-border)] bg-[var(--accent-soft)] text-fg-primary" : "border-border-subtle text-fg-secondary hover:text-fg-primary")}>
                <Icon size={11} aria-hidden /> {label}
              </button>
            ))}
          </div>
          {decision === "corrected" && (
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-2">
              <Field label="Correct category">
                {(p) => (
                  <Select id={p.id} value={draft?.correctedCategoryCode ?? ""} data-testid="qc-correct-category"
                    onChange={(e) => onDecision({ correctedCategoryCode: e.target.value || undefined })}>
                    <option value="">Keep category</option>
                    {categories.map((c) => <option key={c.code} value={c.code}>{c.name}</option>)}
                  </Select>
                )}
              </Field>
              <Field label="Correct severity">
                {(p) => (
                  <Select id={p.id} value={draft?.correctedSeverity ?? ""} onChange={(e) => onDecision({ correctedSeverity: (e.target.value || undefined) as QcSeverity | undefined })}>
                    <option value="">Keep severity</option>
                    {QC_SEVERITIES.map((s) => <option key={s} value={s}>{s}</option>)}
                  </Select>
                )}
              </Field>
            </div>
          )}
          {decision && (
            <Input aria-label="Note (optional)" placeholder="Note (optional)" value={draft?.note ?? ""} maxLength={500}
              onChange={(e) => onDecision({ note: e.target.value })} />
          )}
        </div>
      )}
    </li>
  );
}
