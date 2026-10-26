"use client";

// /app/merchandising/versions/[id] — one document version: the parsed text
// of the original next to the values read from it, each with its checked
// evidence, validation problems and (when reported) the extractor's own
// confidence. A merchandiser decides every value — accept, correct, reject or
// mark unresolved — chooses which confirmed values to write to the order, and
// confirms. Everything shown comes from GET /api/merchandising/versions/{id};
// the page polls while the document is being read.

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { ArrowLeft, Check, CheckCheck, Download, FileText, HelpCircle, Pencil, Plus, RefreshCw, Trash2, X as XIcon } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Field, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { BreakdownTable, VersionStatusBadge, useMerchConfig } from "@/components/merchandising/shared";
import { announceDataChanged, useResource } from "@/lib/useResource";
import { errorMessage, isApiError } from "@/lib/api";
import { canOperate, useAuthStore } from "@/lib/auth";
import { formatDateTime } from "@/lib/format";
import { cn } from "@/lib/cn";
import {
  BREAKDOWN,
  DECISION_LABEL,
  SOURCE_LABEL,
  acceptAllValid,
  buildConfirmRequest,
  describeCode,
  draftProblems,
  formatBytes,
  hasBlockingIssue,
  initialDraft,
  isInProgress,
  locate,
  parseBreakdown,
  type FieldDraft,
  type ReviewDraft
} from "@/lib/merchandising";
import { confirmVersion, downloadOriginal, merchPaths, retryVersion } from "@/services/merchandising.api";
import type { MerchExtractedField, MerchFieldValue, MerchVersionDetail } from "@/services/contracts";

export default function VersionReviewPage() {
  const params = useParams<{ id: string }>();
  const id = params?.id ?? "";
  const toast = useToast();
  const user = useAuthStore((s) => s.user);
  const operator = canOperate(user);
  const config = useMerchConfig();
  const [poll, setPoll] = useState(true);
  const res = useResource<MerchVersionDetail>(id ? merchPaths.version(id) : null, { pollMs: poll ? 2500 : undefined });
  const v = res.data;
  useEffect(() => { if (v) setPoll(isInProgress(v.processing.status)); }, [v]);

  const [draft, setDraft] = useState<ReviewDraft | null>(null);
  const [focus, setFocus] = useState<{ page?: number; snippet?: string } | null>(null);
  const [page, setPage] = useState(1);
  const [busy, setBusy] = useState(false);
  const [problems, setProblems] = useState<string[]>([]);

  // Reviewable versions open straight into the review for merchandisers.
  useEffect(() => {
    if (v && operator && v.reviewable && !draft) setDraft(initialDraft(v));
    if (v && !v.reviewable) setDraft(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [v?.id, v?.version, v?.reviewable, operator]);

  const presentKeys = useMemo(() => new Set(v?.fields.map((f) => f.key) ?? []), [v]);

  if (!id) return null;
  if (res.loading) return <div className="page"><SkeletonRows rows={6} rowHeight="h-16" /></div>;
  if (res.error && !v) {
    const notFound = isApiError(res.error) && res.error.kind === "not_found";
    return (
      <div className="page page-narrow">
        <Callout tone="danger" title={notFound ? "Document version not found" : "Could not load the document"} action={!notFound && <Button size="sm" onClick={() => void res.reload()}>Retry</Button>}>
          {notFound ? "It may have been mistyped." : errorMessage(res.error)}
        </Callout>
        <Link href="/app/merchandising?tab=documents" className="mt-4 inline-flex text-caption text-accent hover:underline">← Documents</Link>
      </div>
    );
  }
  if (!v) return null;
  const p = v.processing;
  const inProgress = isInProgress(p.status);
  const reviewing = operator && v.reviewable && draft !== null;

  function show(f: MerchExtractedField) {
    if (f.pageNumber) setPage(f.pageNumber);
    setFocus({ page: f.pageNumber, snippet: f.snippet });
  }

  function setField(fid: string, patch: Partial<FieldDraft>) {
    setDraft((d) => d && { ...d, fields: { ...d.fields, [fid]: { ...d.fields[fid], ...patch } } });
  }

  async function retry() {
    setBusy(true);
    try {
      res.setData(await retryVersion(v!.id));
      setPoll(true);
      announceDataChanged();
      toast.push({ title: "Queued again", tone: "info" });
    } catch (e) {
      toast.push({ title: "Retry not possible", description: errorMessage(e), tone: "error" });
    } finally {
      setBusy(false);
    }
  }

  async function confirm() {
    if (!draft || busy) return;
    const found = draftProblems(v!, draft);
    setProblems(found);
    if (found.length) return;
    setBusy(true);
    try {
      const updated = await confirmVersion(v!.id, buildConfirmRequest(v!, draft));
      res.setData(updated);
      setDraft(null);
      announceDataChanged();
      toast.push({ title: "Confirmed", description: `${updated.documentTitle} v${updated.versionNumber}`, tone: "success" });
    } catch (e) {
      if (isApiError(e) && e.kind === "conflict") {
        setProblems([errorMessage(e)]);
        await res.reload();
      } else if (isApiError(e) && Object.keys(e.fieldErrors).length) {
        setProblems(Object.values(e.fieldErrors).flat());
      } else {
        setProblems([errorMessage(e)]);
      }
    } finally {
      setBusy(false);
    }
  }

  const missingFor = v.missingRequiredFields.filter((k) => !draft?.additions.some((a) => a.fieldKey === k));
  const fieldLabel = (key: string) => config.data?.fields.find((f) => f.key === key)?.label ?? key;
  const addable = (config.data?.fields ?? []).filter((f) => !presentKeys.has(f.key));

  return (
    <div className="page" data-testid="merch-version" data-status={p.status}>
      <Link href={`/app/merchandising/orders/${v.orderId}`} className="inline-flex items-center gap-1 text-caption text-fg-secondary hover:text-fg-primary"><ArrowLeft size={12} /> {v.buyer} · PO {v.buyerOrderNumber}</Link>
      <PageHeader
        className="mt-2"
        eyebrow={v.documentType.replace("_", " ")}
        title={<span data-testid="merch-version-title">{v.documentTitle} · v{v.versionNumber}</span>}
        description={`${v.file.originalFileName ?? v.file.format} · ${formatBytes(v.file.sizeBytes)} · uploaded ${formatDateTime(v.createdAt)} by ${v.uploadedByEmail}${v.changeNote ? ` · “${v.changeNote}”` : ""}`}
        meta={<>
          <VersionStatusBadge status={p.status} testId="merch-version-status" />
          {!v.isCurrent && <Badge tone="neutral" testId="merch-version-superseded">Replaced by a newer version</Badge>}
        </>}
        actions={
          <Button variant="secondary" size="sm" iconLeft={<Download size={13} />} data-testid="merch-download"
            onClick={() => void downloadOriginal(v.file.url, v.file.originalFileName ?? `document.${v.file.format}`).catch((e) => toast.push({ title: "Download failed", description: errorMessage(e), tone: "error" }))}>
            Original file
          </Button>
        }
      />

      <div className="mt-4 space-y-3">
        {inProgress && (
          <Callout tone="info" title={p.status === "pending" ? "Queued for reading" : "Reading the document…"} testId="merch-processing">
            <span className="inline-flex items-center gap-2"><RefreshCw size={13} className="animate-spin" aria-hidden /> This page updates by itself.</span>
          </Callout>
        )}
        {p.status === "failed" && (
          <Callout tone={p.failureCode === "ocr_required" ? "warning" : "danger"} title={p.failureCode === "ocr_required" ? "The text can't be read automatically" : "Processing failed"} testId="merch-failure"
            action={operator && p.retryable && <Button size="sm" onClick={() => void retry()} disabled={busy} data-testid="merch-retry"><RefreshCw size={12} /> Retry</Button>}>
            {p.failureMessage} <span className="font-mono">({p.failureCode})</span>
            {v.reviewable && operator && " You can still enter the values by hand below, from the original file."}
          </Callout>
        )}
        {p.extractionCode && (
          <Callout tone="warning" title="Values were not extracted automatically" testId="merch-extraction-unavailable"
            action={operator && p.retryable && config.data?.extraction.configured && <Button size="sm" onClick={() => void retry()} disabled={busy}>Extract now</Button>}>
            {p.extractionMessage ?? describeCode(p.extractionCode)} The text was read; enter the values it states under “Add a value”.
          </Callout>
        )}
        {p.textTruncated && <Callout tone="warning">Only the first part of this document&apos;s text was read or sent for extraction; check the rest by hand.</Callout>}
        {v.conflicts.filter((c) => c.status === "open").length > 0 && (
          <Callout tone="danger" title="This document disagrees with another source" testId="merch-version-conflicts"
            action={<Link className="text-caption text-accent hover:underline" href={`/app/merchandising/orders/${v.orderId}#conflicts`}>Resolve on the order</Link>}>
            {v.conflicts.filter((c) => c.status === "open").map((c) => `${c.label}: ${c.values.map((x) => x.display).join(" vs ")}`).join(" · ")}
          </Callout>
        )}
      </div>

      <div className="mt-4 grid grid-cols-1 xl:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)] gap-4 items-start">
        <div className="space-y-4 min-w-0 xl:sticky xl:top-4">
          <SourcePanel v={v} page={page} setPage={setPage} focus={focus} />
          <Panel title="Processing" testId="merch-processing-details">
            <dl className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-caption">
              <dt className="text-fg-tertiary">Extractor</dt>
              <dd className="text-fg-primary" data-testid="merch-extractor">{p.provider ? `${p.provider}${p.model ? ` / ${p.model}` : ""}${p.modelVersion ? ` (${p.modelVersion})` : ""}` : "—"}</dd>
              <dt className="text-fg-tertiary">Pages · characters</dt><dd className="text-fg-primary">{p.pageCount ?? "—"} · {p.textLength?.toLocaleString("en-US") ?? "—"}</dd>
              <dt className="text-fg-tertiary">Read</dt><dd className="text-fg-primary">{p.processedAt ? formatDateTime(p.processedAt) : "—"}{p.extractionMs != null ? ` · ${(p.extractionMs / 1000).toFixed(1)} s` : ""}</dd>
              <dt className="text-fg-tertiary">Attempts</dt><dd className="text-fg-primary">{p.attempts}</dd>
              <dt className="text-fg-tertiary">SHA-256</dt><dd className="text-fg-primary font-mono break-all">{v.file.sha256.slice(0, 16)}…</dd>
            </dl>
            <p className="mt-2 text-caption text-fg-tertiary">{p.confidenceNote}</p>
          </Panel>
          {v.otherVersions.length > 0 && (
            <Panel title="Other versions" testId="merch-other-versions">
              <ul className="space-y-1.5 text-caption">
                {v.otherVersions.map((o) => (
                  <li key={o.id} className="flex flex-wrap items-center gap-2">
                    <Link className="text-accent hover:underline" href={`/app/merchandising/versions/${o.id}`}>v{o.versionNumber}</Link>
                    <VersionStatusBadge status={o.status} />
                    <span className="text-fg-tertiary">{o.originalFileName ?? o.format} · {formatDateTime(o.createdAt)} · {o.uploadedByEmail}{o.isCurrent ? " · current" : ""}</span>
                    {o.changeNote && <span className="basis-full text-fg-secondary">“{o.changeNote}”</span>}
                  </li>
                ))}
              </ul>
            </Panel>
          )}
        </div>

        <div className="space-y-4 min-w-0">
          <Panel
            title={`Extracted values (${v.fields.length})`}
            subtitle={reviewing ? "Decide every value. “Write to order” makes a confirmed value the order's value." : "As read from the document, with the decisions made at confirmation."}
            right={reviewing && v.fields.length > 0 && (
              <Button size="sm" variant="secondary" iconLeft={<CheckCheck size={12} />} onClick={() => setDraft((d) => d && acceptAllValid(v, d))} data-testid="merch-accept-valid">Accept all valid</Button>
            )}
            testId="merch-fields">
            {inProgress ? <p className="text-caption text-fg-tertiary">Waiting for the document to be read.</p> : v.fields.length === 0 ? (
              <p className="text-caption text-fg-tertiary" data-testid="merch-no-fields">No values were extracted from this version.</p>
            ) : (
              <ul className="space-y-2">
                {v.fields.map((f) => (
                  <FieldRow key={f.id} f={f} order={v.orderValues.find((o) => o.key === f.key)} draft={reviewing ? draft!.fields[f.id] : undefined}
                    onChange={(patch) => setField(f.id, patch)} onShow={() => show(f)} />
                ))}
              </ul>
            )}
            {missingFor.length > 0 && (
              <p className="mt-3 text-caption text-[var(--risk-medium)]" data-testid="merch-missing-fields">
                A purchase order should state: {missingFor.map(fieldLabel).join(", ")}. Not found in this version.
              </p>
            )}
          </Panel>

          {reviewing && (
            <Panel title="Add a value" subtitle="For a value the document states that wasn't extracted, or to record that it is missing (unresolved)." testId="merch-additions">
              <div className="space-y-2">
                {draft!.additions.map((a, i) => (
                  <div key={a.key} className="surface-2 p-3 space-y-2" data-testid="merch-addition">
                    <div className="grid grid-cols-1 sm:grid-cols-[180px_1fr_auto] gap-2 items-end">
                      <Field label="Field">
                        {(x) => (
                          <Select id={x.id} value={a.fieldKey} data-testid="merch-addition-field"
                            onChange={(e) => setDraft({ ...draft!, additions: draft!.additions.map((y) => (y.key === a.key ? { ...y, fieldKey: e.target.value } : y)) })}>
                            <option value="">Choose…</option>
                            {addable.map((f) => <option key={f.key} value={f.key}>{f.label}</option>)}
                          </Select>
                        )}
                      </Field>
                      {a.fieldKey === BREAKDOWN ? (
                        <Field label="Rows: colour, size, quantity (one per line)">
                          {(x) => <TextArea id={x.id} rows={3} value={a.value} disabled={a.decision === "unresolved"} data-testid="merch-addition-value"
                            onChange={(e) => setDraft({ ...draft!, additions: draft!.additions.map((y) => (y.key === a.key ? { ...y, value: e.target.value } : y)) })} />}
                        </Field>
                      ) : (
                        <Field label="Value as the document states it">
                          {(x) => <Input id={x.id} value={a.value} disabled={a.decision === "unresolved"} data-testid="merch-addition-value"
                            onChange={(e) => setDraft({ ...draft!, additions: draft!.additions.map((y) => (y.key === a.key ? { ...y, value: e.target.value } : y)) })} />}
                        </Field>
                      )}
                      <Button size="sm" variant="ghost" aria-label={`Remove added value ${i + 1}`} onClick={() => setDraft({ ...draft!, additions: draft!.additions.filter((y) => y.key !== a.key) })}><Trash2 size={13} /></Button>
                    </div>
                    <div className="flex flex-wrap gap-4 text-caption text-fg-secondary">
                      <label className="flex items-center gap-1.5">
                        <input type="checkbox" className="accent-[var(--accent)]" checked={a.decision === "unresolved"}
                          onChange={(e) => setDraft({ ...draft!, additions: draft!.additions.map((y) => (y.key === a.key ? { ...y, decision: e.target.checked ? "unresolved" : "accepted", apply: e.target.checked ? false : y.apply } : y)) })} />
                        Not stated / unresolved (ask the buyer)
                      </label>
                      {a.decision === "accepted" && (
                        <label className="flex items-center gap-1.5">
                          <input type="checkbox" className="accent-[var(--accent)]" checked={a.apply} data-testid="merch-addition-apply"
                            onChange={(e) => setDraft({ ...draft!, additions: draft!.additions.map((y) => (y.key === a.key ? { ...y, apply: e.target.checked } : y)) })} />
                          Write to order
                        </label>
                      )}
                    </div>
                  </div>
                ))}
                <Button size="sm" variant="secondary" iconLeft={<Plus size={12} />} data-testid="merch-add-value" disabled={addable.length === draft!.additions.length}
                  onClick={() => setDraft({ ...draft!, additions: [...draft!.additions, { key: `a-${Date.now()}`, fieldKey: "", value: "", decision: "accepted", apply: true }] })}>
                  Add a value
                </Button>
              </div>
            </Panel>
          )}

          {reviewing ? (
            <Panel title="Confirm" testId="merch-confirm-panel">
              <p className="text-caption text-fg-secondary">
                Confirming records your decisions and writes the ticked values to {v.orderReference}. The original file and the extracted values stay unchanged in the history.
              </p>
              <Field label="Review notes (optional)" className="mt-3" hint={`${draft!.notes.length}/1000`}>
                {(x) => <TextArea id={x.id} rows={2} maxLength={1000} value={draft!.notes} onChange={(e) => setDraft({ ...draft!, notes: e.target.value })} data-testid="merch-review-notes" />}
              </Field>
              {problems.length > 0 && (
                <Callout tone="danger" className="mt-3" title="Not confirmed yet" testId="merch-confirm-errors">
                  <ul className="list-disc pl-4">{problems.map((e) => <li key={e}>{e}</li>)}</ul>
                </Callout>
              )}
              <div className="mt-4">
                <Button variant="primary" onClick={() => void confirm()} disabled={busy} iconLeft={<Check size={13} />} data-testid="merch-confirm">
                  {busy ? "Confirming…" : "Confirm values"}
                </Button>
              </div>
            </Panel>
          ) : v.review ? (
            <Panel title="Confirmed" testId="merch-review">
              <p className="text-body text-fg-primary">By {v.review.confirmedByEmail ?? "unknown"} · {formatDateTime(v.review.confirmedAt)}</p>
              {v.review.notes && <p className="mt-1 text-body text-fg-secondary whitespace-pre-wrap">{v.review.notes}</p>}
            </Panel>
          ) : !operator && v.reviewable ? (
            <Callout tone="info" testId="merch-review-role">Confirming values needs the Operator or Admin role.</Callout>
          ) : !v.isCurrent ? (
            <Callout tone="info">This version was replaced before it was confirmed; review the current version instead.</Callout>
          ) : null}
        </div>
      </div>
    </div>
  );
}

// ── Source text with the evidence highlighted ───────────────────────────────

function SourcePanel({ v, page, setPage, focus }: { v: MerchVersionDetail; page: number; setPage: (n: number) => void; focus: { page?: number; snippet?: string } | null }) {
  const current = v.pages.find((p) => p.pageNumber === page) ?? v.pages[0];
  const parts = current && focus?.snippet && (!focus.page || focus.page === current.pageNumber) ? locate(current.text, focus.snippet) : null;
  useEffect(() => {
    if (parts) document.getElementById("merch-evidence-mark")?.scrollIntoView({ block: "center", behavior: "smooth" });
  }, [parts, focus]);
  return (
    <Panel title="Document text" subtitle="The text read from the original file. Choose “Show source” on a value to highlight where it came from." icon={<FileText size={14} />} testId="merch-source">
      {v.pages.length === 0 ? (
        <p className="text-caption text-fg-tertiary" data-testid="merch-no-text">No text was read from this file. Open the original file to review it.</p>
      ) : (
        <>
          {v.pages.length > 1 && (
            <div className="mb-2 flex flex-wrap gap-1" role="tablist" aria-label="Pages">
              {v.pages.map((p) => (
                <button key={p.pageNumber} type="button" role="tab" aria-selected={p.pageNumber === current.pageNumber} onClick={() => setPage(p.pageNumber)}
                  className={cn("h-7 min-w-7 px-2 rounded-md border text-caption", p.pageNumber === current.pageNumber ? "border-[var(--accent-border)] bg-[var(--accent-soft)] text-fg-primary" : "border-border-subtle text-fg-secondary")}>
                  {p.pageNumber}
                </button>
              ))}
            </div>
          )}
          <pre className="max-h-[60vh] overflow-auto whitespace-pre-wrap break-words rounded-md bg-surface-2 p-3 font-mono text-[12px] leading-5 text-fg-secondary" data-testid="merch-page-text" tabIndex={0}>
            {parts ? <>{parts[0]}<mark id="merch-evidence-mark" className="bg-[var(--accent-soft)] text-fg-primary ring-1 ring-[var(--accent)] rounded-sm" data-testid="merch-evidence-highlight">{parts[1]}</mark>{parts[2]}</> : current.text}
          </pre>
        </>
      )}
    </Panel>
  );
}

// ── One field ───────────────────────────────────────────────────────────────

function FieldRow({ f, order, draft, onChange, onShow }: {
  f: MerchExtractedField;
  order?: MerchFieldValue;
  draft?: FieldDraft;
  onChange: (patch: Partial<FieldDraft>) => void;
  onShow: () => void;
}) {
  const blocking = hasBlockingIssue(f);
  const decision = draft?.decision ?? (f.decision === "unreviewed" ? undefined : f.decision);
  const isBreakdown = f.key === BREAKDOWN;
  const rows = isBreakdown ? parseBreakdown(f.normalizedValue ?? f.rawValue) : null;
  const differsFromOrder = order?.value != null && f.normalizedValue != null && order.value !== f.normalizedValue;
  return (
    <li className="surface-2 p-3" data-testid="merch-field" data-key={f.key} data-decision={f.decision} data-evidence={f.evidenceStatus}>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="text-caption text-fg-tertiary">{f.label} · {SOURCE_LABEL[f.source]}</p>
          <p className="text-body font-medium text-fg-primary break-words" data-testid="merch-field-value">
            {isBreakdown ? f.display : (f.normalizedValue ?? f.rawValue ?? "—")}
            {!isBreakdown && f.rawValue && f.normalizedValue && f.rawValue !== f.normalizedValue && <span className="ml-2 text-caption font-normal text-fg-tertiary">read as “{f.rawValue}”</span>}
          </p>
          {f.decision === "corrected" && f.correctedValue && <p className="text-caption text-fg-primary">Corrected to <span className="font-medium">{isBreakdown ? "a new breakdown" : f.correctedValue}</span></p>}
          {differsFromOrder && <p className="text-caption text-[var(--risk-medium)]">The order currently says {order!.display}.</p>}
        </div>
        <div className="flex flex-wrap items-center gap-1.5 shrink-0">
          {f.confidence != null && <Badge tone={f.issues.some((i) => i.code === "low_confidence") ? "warning" : "neutral"} title="The extractor's own estimate, not a calibrated probability.">confidence {Math.round(f.confidence * 100)}%</Badge>}
          {f.evidenceStatus === "verified" ? (
            <Button size="sm" variant="ghost" onClick={onShow} data-testid="merch-show-source">Show source{f.pageNumber ? ` (p. ${f.pageNumber})` : ""}</Button>
          ) : (
            <Badge tone={f.evidenceStatus === "not_found" ? "danger" : "neutral"} title={f.evidenceStatus === "not_found" ? "The quoted text was not found in the document." : "No source text was given."}>
              {f.evidenceStatus === "not_found" ? "source not found" : "no source"}
            </Badge>
          )}
          {f.decision !== "unreviewed" && <Badge tone={f.decision === "accepted" || f.decision === "corrected" ? "success" : "neutral"}>{DECISION_LABEL[f.decision]}</Badge>}
        </div>
      </div>
      {f.snippet && <p className="mt-1 font-mono text-[11px] text-fg-secondary break-words" data-testid="merch-field-snippet">“{f.snippet}”</p>}
      {rows && <details className="mt-1"><summary className="cursor-pointer text-caption text-fg-secondary">Rows</summary><BreakdownTable rows={rows} /></details>}
      {f.issues.length > 0 && (
        <ul className="mt-1.5 space-y-0.5" data-testid="merch-field-issues">
          {f.issues.map((i) => (
            <li key={i.code} className={cn("text-caption", i.blocking ? "text-[var(--risk-high)]" : "text-[var(--risk-medium)]")} data-issue={i.code}>
              {i.blocking ? "✕ " : "! "}{i.message}
            </li>
          ))}
        </ul>
      )}
      {f.reviewerNote && <p className="mt-1 text-caption text-fg-secondary">Note: {f.reviewerNote}</p>}

      {draft && (
        <div className="mt-2 space-y-2">
          <div className="flex flex-wrap gap-1.5" role="radiogroup" aria-label={`Decision for ${f.label}`}>
            {([
              ["accepted", "Accept", Check],
              ["corrected", "Correct", Pencil],
              ["rejected", "Reject", XIcon],
              ["unresolved", "Unresolved", HelpCircle]
            ] as const).map(([value, label, Icon]) => {
              const disabled = value === "accepted" && (blocking || !f.normalizedValue);
              return (
                <button key={value} type="button" role="radio" aria-checked={decision === value} disabled={disabled} data-testid={`merch-decide-${value}`}
                  title={disabled ? "This value can't be accepted as read; correct it or mark it unresolved." : undefined}
                  onClick={() => onChange({ decision: value, apply: value === "accepted" || value === "corrected" ? draft.apply : false })}
                  className={cn("h-7 px-2.5 inline-flex items-center gap-1 rounded-md border text-caption disabled:opacity-40 disabled:cursor-not-allowed",
                    decision === value ? "border-[var(--accent-border)] bg-[var(--accent-soft)] text-fg-primary" : "border-border-subtle text-fg-secondary hover:text-fg-primary")}>
                  <Icon size={11} aria-hidden /> {label}
                </button>
              );
            })}
          </div>
          {decision === "corrected" && (isBreakdown ? (
            <TextArea aria-label={`Corrected ${f.label} (colour, size, quantity per line)`} rows={4} value={draft.correctedValue} onChange={(e) => onChange({ correctedValue: e.target.value })} data-testid="merch-correct-value" />
          ) : (
            <Input aria-label={`Corrected ${f.label}`} value={draft.correctedValue} onChange={(e) => onChange({ correctedValue: e.target.value })} data-testid="merch-correct-value" />
          ))}
          {decision && (
            <div className="flex flex-wrap items-center gap-3">
              <Input aria-label="Note (optional)" placeholder="Note (optional)" className="!h-8 !text-caption flex-1 min-w-[180px]" value={draft.note} maxLength={500} onChange={(e) => onChange({ note: e.target.value })} />
              {(decision === "accepted" || decision === "corrected") && (
                <label className="flex items-center gap-1.5 text-caption text-fg-secondary">
                  <input type="checkbox" className="accent-[var(--accent)]" checked={draft.apply} onChange={(e) => onChange({ apply: e.target.checked })} data-testid="merch-apply" />
                  Write to order
                </label>
              )}
            </div>
          )}
        </div>
      )}
    </li>
  );
}
