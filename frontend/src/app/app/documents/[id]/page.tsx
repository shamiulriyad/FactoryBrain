"use client";

import { useParams } from "next/navigation";
import { useEffect, useMemo, useState } from "react";
import { Archive, CheckCircle2, Download, Eye, FileUp, RefreshCw, RotateCcw, Save, Send, ShieldCheck, Sparkles, XCircle } from "lucide-react";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Panel } from "@/components/ui/Panel";
import { Modal } from "@/components/ui/Modal";
import { TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { useToast } from "@/components/ui/Toast";
import { ChecksPanel, DocForm, DocRiskBadge, DocStatusBadge, DocsPage, Loaded, fmtDate, period, type DocScope } from "@/components/documents/shared";
import { useResource } from "@/lib/useResource";
import { errorMessage } from "@/lib/api";
import { ACTION_TEXT, APPROVAL_TEXT, SOURCE_TEXT, docsApi, type DocDetail, type DocFile } from "@/lib/documents";

export default function Page() {
  return (
    <DocsPage title="Document" testId="document-detail" description="Check the values, complete what is missing, and move the document through review and approval.">
      {(scope) => <Detail scope={scope} />}
    </DocsPage>
  );
}

type Values = Record<string, unknown>;
type ActionId = "submit" | "start-review" | "verify" | "approve" | "reject" | "revise" | "archive";

function Detail({ scope }: { scope: DocScope }) {
  const params = useParams<{ id: string }>();
  const id = params?.id ?? "";
  const res = useResource<DocDetail>(id ? docsApi.detail(id) : null);
  return <Loaded res={res} rows={8}>{(d) => <Body key={`${d.document.id}:${d.document.currentVersion}:${d.document.status}`} d={d} scope={scope} setData={(n) => res.setData(n)} reload={() => void res.reload()} />}</Loaded>;
}

function Body({ d, scope, setData, reload }: { d: DocDetail; scope: DocScope; setData: (d: DocDetail) => void; reload: () => void }) {
  const toast = useToast();
  const doc = d.document;
  const [values, setValues] = useState<Values>(d.data);
  const [title, setTitle] = useState(doc.title);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [ask, setAsk] = useState<{ action: ActionId; label: string; required: boolean } | null>(null);
  const [aiKeys, setAiKeys] = useState<Set<string>>(new Set());
  const [aiFileId, setAiFileId] = useState<string | null>(null);
  const [viewVersion, setViewVersion] = useState<number | null>(null);
  const dirty = useMemo(() => JSON.stringify(values) !== JSON.stringify(d.data) || title !== doc.title, [values, title, d.data, doc.title]);
  const a = d.actions;

  async function run(label: string, fn: () => Promise<DocDetail | void>, ok?: string) {
    setBusy(label); setError(null);
    try {
      const next = await fn();
      if (next) setData(next);
      if (ok) toast.push({ title: ok });
    } catch (e) { setError(errorMessage(e)); } finally { setBusy(null); }
  }

  const save = () => run("Saving…", () => docsApi.save(doc.id, {
    data: values, title: title !== doc.title ? title : null, changeNote: note || null, expectedVersion: doc.currentVersion,
    acceptedAiFields: aiKeys.size ? [...aiKeys] : undefined, aiFileId
  }), "Saved as a new version");

  async function act(action: ActionId, n?: string) {
    if (dirty && action === "submit") { setError("Save your changes before submitting."); return; }
    await run("Working…", () => docsApi.act(doc.id, action, { note: n || null, expectedVersion: doc.currentVersion }),
      { submit: "Submitted", "start-review": "Review started", verify: "Verified", approve: "Approved", reject: "Rejected — the preparer is notified", revise: "Reopened for revision", archive: "Archived" }[action]);
  }

  return (
    <div className="space-y-4">
      <div className="surface p-4 space-y-3">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <p className="font-mono text-[12px] text-fg-tertiary" data-testid="doc-reference">{doc.reference} · v{doc.currentVersion}</p>
            {a.edit ? <Input value={title} onChange={(e) => setTitle(e.target.value)} className="mt-1 font-semibold max-w-xl" aria-label="Title" />
              : <h2 className="text-h3 font-semibold">{doc.title}</h2>}
            <p className="text-caption text-fg-secondary mt-1">{d.type.name} · {d.type.categoryName} · Period {period(doc.periodFrom, doc.periodTo)}{d.lineName ? ` · ${d.lineName}` : ""} · {SOURCE_TEXT[doc.source]}</p>
          </div>
          <div className="flex flex-wrap gap-1.5 items-center">
            <DocStatusBadge status={doc.status} />
            <DocRiskBadge risk={doc.risk} />
            <Badge tone="info">{APPROVAL_TEXT[doc.approvalLevel]}</Badge>
            {d.stockPosted && <Badge tone="success">Stock posted</Badge>}
          </div>
        </div>
        <div className="grid gap-2 text-caption sm:grid-cols-2 lg:grid-cols-4">
          <span>Prepared: {doc.createdByEmail}<br /><span className="text-fg-tertiary">{fmtDate(doc.createdAt)}</span></span>
          <span>Submitted: {d.submittedByEmail ?? "—"}<br /><span className="text-fg-tertiary">{fmtDate(doc.submittedAt)}</span></span>
          <span>Verified: {d.verifiedByEmail ?? "—"}<br /><span className="text-fg-tertiary">{fmtDate(d.verifiedAt)}</span></span>
          <span>{doc.status === "REJECTED" ? "Rejected" : "Approved"}: {doc.decidedByEmail ?? "—"}<br /><span className="text-fg-tertiary">{fmtDate(doc.decidedAt)}</span></span>
        </div>
        {doc.status === "REJECTED" && d.decisionNote && <Callout tone="danger" title="Rejected — correct and resubmit">{d.decisionNote}</Callout>}
        {doc.status === "APPROVED" && d.decisionNote && <Callout tone="success" title="Decision note">{d.decisionNote}</Callout>}
        <div className="flex flex-wrap gap-2" data-testid="doc-actions">
          {a.edit && <Button onClick={() => void save()} disabled={!dirty || !!busy} iconLeft={<Save size={14} />} data-testid="doc-save">Save</Button>}
          {a.regenerate && <Button variant="secondary" onClick={() => void run("Reading records…", () => docsApi.regenerate(doc.id), "Refreshed from records")} disabled={!!busy || dirty} iconLeft={<RefreshCw size={14} />}>Refresh from records</Button>}
          {(a.edit || doc.status === "DRAFT" || doc.status === "REJECTED") && (
            <Button variant="success" onClick={() => void act("submit")} disabled={!a.submit || !!busy || dirty} title={a.whyNotSubmit ?? undefined} iconLeft={<Send size={14} />} data-testid="doc-submit">Submit</Button>
          )}
          {a.startReview && <Button variant="secondary" onClick={() => void act("start-review")} disabled={!!busy} iconLeft={<Eye size={14} />}>Start review</Button>}
          {a.verify && <Button variant="secondary" onClick={() => setAsk({ action: "verify", label: "Verify", required: false })} disabled={!!busy} iconLeft={<ShieldCheck size={14} />} data-testid="doc-verify">Verify</Button>}
          {a.approve && <Button variant="success" onClick={() => setAsk({ action: "approve", label: "Approve", required: false })} disabled={!!busy} iconLeft={<CheckCircle2 size={14} />} data-testid="doc-approve">Approve</Button>}
          {a.reject && <Button variant="danger" onClick={() => setAsk({ action: "reject", label: "Reject", required: true })} disabled={!!busy} iconLeft={<XCircle size={14} />} data-testid="doc-reject">Reject</Button>}
          {a.revise && <Button variant="secondary" onClick={() => setAsk({ action: "revise", label: "Reopen for revision", required: true })} disabled={!!busy} iconLeft={<RotateCcw size={14} />}>Revise</Button>}
          {a.archive && <Button variant="ghost" onClick={() => setAsk({ action: "archive", label: "Archive", required: false })} disabled={!!busy} iconLeft={<Archive size={14} />}>Archive</Button>}
          <span className="ml-auto flex gap-1">
            {(["pdf", "xlsx", "docx"] as const).map((f) => (
              <Button key={f} size="sm" variant="ghost" iconLeft={<Download size={13} />} onClick={() => void run("Preparing…", () => docsApi.download(doc.id, f))} data-testid={`doc-download-${f}`}>{f.toUpperCase()}</Button>
            ))}
          </span>
        </div>
        {busy && <p className="text-caption text-fg-secondary" role="status">{busy}</p>}
        {error && <Callout tone="danger" title="That didn't work">{error}</Callout>}
        {!a.submit && a.whyNotSubmit && a.edit && <p className="text-caption text-fg-secondary">Submit is blocked: {a.whyNotSubmit}</p>}
        {a.whyNotApprove && ["SUBMITTED", "UNDER_REVIEW"].includes(doc.status) && <p className="text-caption text-fg-secondary">{a.whyNotApprove}</p>}
        {a.edit && dirty && (
          <Input value={note} onChange={(e) => setNote(e.target.value)} placeholder="What changed? (optional, kept with the version)" aria-label="Change note" className="max-w-xl" />
        )}
      </div>

      <ChecksPanel checks={d.checks} />

      <Panel title="Content" subtitle={d.type.generated ? "Figures are generated from records and can't be typed over; fix the source records and refresh. Add your remarks." : "Fields marked * are required. Grey fields are calculated or filled from records."}>
        <DocForm fields={d.type.fields} values={values} onChange={setValues} editable={a.edit} checks={d.checks} sources={d.sources} factoryId={scope.factoryId} aiKeys={aiKeys} />
      </Panel>

      <div className="grid gap-4 lg:grid-cols-2">
        <Files d={d} canUpload={a.upload} aiConfigured={scope.catalog.ai.configured} onChanged={reload}
          onApply={(file) => {
            const s = file.suggestions?.values ?? {};
            setValues({ ...values, ...s });
            setAiKeys(new Set(Object.keys(s).filter((k) => (file.suggestions?.methods[k] ?? "").startsWith("ai"))));
            setAiFileId(file.id);
            toast.push({ title: `${Object.keys(s).length} value(s) applied — check them, then Save` });
          }} />
        <AiPanel d={d} canUse={a.useAi} aiReason={scope.catalog.ai.reason} onDone={setData} canDraft={a.edit}
          onDraft={(k, text) => setValues({ ...values, [k]: text })} />
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel title="Versions" flush ariaLabel="Versions">
          <ul className="divide-y divide-border-subtle" data-testid="doc-versions">
            {d.versions.map((v) => (
              <li key={v.number} className="px-4 py-2 text-caption flex flex-wrap items-center gap-2">
                <span className="font-mono">v{v.number}</span>
                <span className="text-fg-secondary">{fmtDate(v.createdAt)} · {v.createdByEmail}</span>
                {v.changeNote && <span className="text-fg-tertiary">“{v.changeNote}”</span>}
                <span className="ml-auto flex gap-1">
                  {v.number !== doc.currentVersion && <Button size="sm" variant="ghost" onClick={() => setViewVersion(v.number)}>View</Button>}
                  <Button size="sm" variant="ghost" onClick={() => void run("Preparing…", () => docsApi.download(doc.id, "pdf", v.number))}>PDF</Button>
                </span>
              </li>
            ))}
          </ul>
        </Panel>
        <Panel title="History" flush ariaLabel="History">
          <ul className="divide-y divide-border-subtle max-h-[420px] overflow-y-auto" data-testid="doc-history">
            {d.history.map((h) => (
              <li key={h.id} className="px-4 py-2 text-caption">
                <span className="font-medium">{ACTION_TEXT[h.action] ?? h.action}</span>
                {h.versionNumber ? <span className="text-fg-tertiary"> · v{h.versionNumber}</span> : null}
                <span className="block text-fg-secondary">{h.actorEmail} · {fmtDate(h.occurredAt)}</span>
                {h.note && <span className="block text-fg-tertiary">{h.note}</span>}
              </li>
            ))}
          </ul>
        </Panel>
      </div>

      <ReasonModal ask={ask} onClose={() => setAsk(null)} onConfirm={(n) => { const x = ask!; setAsk(null); void act(x.action, n); }} />
      {viewVersion !== null && <VersionModal id={doc.id} n={viewVersion} d={d} factoryId={scope.factoryId} onClose={() => setViewVersion(null)} />}
    </div>
  );
}

function ReasonModal({ ask, onClose, onConfirm }: { ask: { action: ActionId; label: string; required: boolean } | null; onClose: () => void; onConfirm: (note: string) => void }) {
  const [text, setText] = useState("");
  useEffect(() => setText(""), [ask]);
  if (!ask) return null;
  return (
    <Modal open onClose={onClose} title={ask.label} description={ask.required ? "A reason is required; it is shown to the preparer and kept in the history." : "An optional note, kept in the history."}
      footer={<><Button variant="ghost" onClick={onClose}>Cancel</Button><Button variant={ask.action === "reject" ? "danger" : "primary"} disabled={ask.required && !text.trim()} onClick={() => onConfirm(text.trim())} data-testid="reason-confirm">{ask.label}</Button></>}>
      <TextArea rows={4} value={text} onChange={(e) => setText(e.target.value)} aria-label="Reason" data-testid="reason-text" />
    </Modal>
  );
}

function VersionModal({ id, n, d, factoryId, onClose }: { id: string; n: number; d: DocDetail; factoryId: string; onClose: () => void }) {
  const v = useResource<{ number: number; data: Values; changeNote?: string | null; createdAt: string; createdByEmail: string }>(`/api/documents/${id}/versions/${n}`);
  return (
    <Modal open onClose={onClose} title={`Version ${n}`} className="max-w-5xl" footer={<Button onClick={onClose}>Close</Button>}>
      <Loaded res={v}>{(x) => <DocForm fields={d.type.fields} values={x.data} onChange={() => undefined} editable={false} checks={null} factoryId={factoryId} />}</Loaded>
    </Modal>
  );
}

function Files({ d, canUpload, aiConfigured, onChanged, onApply }: { d: DocDetail; canUpload: boolean; aiConfigured: boolean; onChanged: () => void; onApply: (f: DocFile) => void }) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [useAi, setUseAi] = useState(aiConfigured);
  async function upload(f: File) {
    setBusy("Uploading…"); setError(null);
    try { await docsApi.upload(d.document.id, f); onChanged(); } catch (e) { setError(errorMessage(e)); } finally { setBusy(null); }
  }
  async function read(f: DocFile) {
    setBusy(useAi ? "Reading (rules first, AI only for what is missing)…" : "Reading with rules…"); setError(null);
    try { await docsApi.extract(d.document.id, f.id, useAi); onChanged(); } catch (e) { setError(errorMessage(e)); } finally { setBusy(null); }
  }
  return (
    <Panel title="Files" subtitle="Originals are stored privately and served only to people who can see this document.">
      <div className="space-y-3" data-testid="doc-files">
        {canUpload && (
          <div className="flex flex-wrap items-center gap-3 text-caption">
            <label className="inline-flex items-center gap-2 cursor-pointer"><FileUp size={14} />
              <input type="file" accept=".pdf,.docx,.xlsx,.csv,.txt,.jpg,.jpeg,.png,.webp" onChange={(e) => { const f = e.target.files?.[0]; if (f) void upload(f); e.target.value = ""; }} data-testid="doc-upload" />
            </label>
            <label className="inline-flex items-center gap-1.5"><input type="checkbox" checked={useAi} disabled={!aiConfigured} onChange={(e) => setUseAi(e.target.checked)} /><Sparkles size={13} /> Use AI when reading</label>
          </div>
        )}
        {busy && <p className="text-caption" role="status">{busy}</p>}
        {error && <Callout tone="danger">{error}</Callout>}
        {d.files.length === 0 && <p className="text-caption text-fg-tertiary">No files.</p>}
        {d.files.map((f) => (
          <div key={f.id} className="border border-border-subtle rounded-md p-2 text-caption space-y-1">
            <div className="flex flex-wrap items-center gap-2">
              <button type="button" className="text-accent hover:underline font-medium" onClick={() => void docsApi.openFile(d.document.id, f.id, f.fileName)}>{f.fileName}</button>
              <span className="text-fg-tertiary">{f.format.toUpperCase()} · {(f.sizeBytes / 1024).toFixed(0)} KB · {f.createdByEmail}</span>
              {f.extractionStatus === "COMPLETED" && <Badge tone="success">Read ({f.extractionMethod}{f.extractionModel ? ` · ${f.extractionModel}` : ""})</Badge>}
              {f.extractionStatus === "FAILED" && <Badge tone="danger">{f.failureCode}</Badge>}
              {canUpload && <Button size="sm" variant="secondary" className="ml-auto" disabled={!!busy} onClick={() => void read(f)}>{f.extractionStatus === "NONE" ? "Read values" : "Read again"}</Button>}
            </div>
            {f.failureMessage && <p className="text-[var(--risk-high)]">{f.failureMessage}</p>}
            {f.suggestions && (
              <div className="space-y-1">
                <p>{Object.keys(f.suggestions.values).length} value(s) found: {Object.keys(f.suggestions.values).map((k) => {
                  const fd = d.type.fields.find((x) => x.key === k);
                  const m = f.suggestions!.methods[k];
                  const c = f.suggestions!.confidence[k];
                  return `${fd?.label ?? k}${m?.startsWith("ai") ? " (AI" + (c !== undefined ? ` ${Math.round(c * 100)}%` : "") + ")" : ""}`;
                }).join(", ") || "none"}</p>
                {f.suggestions.rejected.length > 0 && <details><summary className="cursor-pointer text-fg-tertiary">{f.suggestions.rejected.length} value(s) not accepted</summary><ul className="list-disc pl-5">{f.suggestions.rejected.map((r, i) => <li key={i}>{r}</li>)}</ul></details>}
                {canUpload && Object.keys(f.suggestions.values).length > 0 && <Button size="sm" onClick={() => onApply(f)} data-testid="apply-suggestions">Apply to the form</Button>}
              </div>
            )}
          </div>
        ))}
      </div>
    </Panel>
  );
}

function AiPanel({ d, canUse, aiReason, onDone, canDraft, onDraft }: { d: DocDetail; canUse: boolean; aiReason?: string | null; onDone: (d: DocDetail) => void; canDraft: boolean; onDraft: (k: string, text: string) => void }) {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState<{ key: string; text: string } | null>(null);
  const textFields = d.type.fields.filter((f) => f.kind === "textarea" && !f.computed);
  async function summarize() {
    setBusy("Asking the AI…"); setError(null);
    try { onDone(await docsApi.summary(d.document.id)); } catch (e) { setError(errorMessage(e)); } finally { setBusy(null); }
  }
  async function draftField(k: string) {
    setBusy("Drafting…"); setError(null);
    try { const r = await docsApi.draftText(d.document.id, k); setDraft({ key: k, text: r.text }); } catch (e) { setError(errorMessage(e)); } finally { setBusy(null); }
  }
  return (
    <Panel title="AI assistance" subtitle="Advisory only: AI never approves or rejects. Only the needed fields are sent, never whole files unless you ask it to read one.">
      <div className="space-y-3 text-caption" data-testid="doc-ai">
        {!canUse && <p className="text-fg-tertiary">{aiReason ?? "AI is not available for you on this document."}</p>}
        {canUse && <Button size="sm" variant="secondary" iconLeft={<Sparkles size={13} />} disabled={!!busy} onClick={() => void summarize()} data-testid="ai-summary">Summarize and explain the findings</Button>}
        {d.aiSummary && (
          <div className="border border-border-subtle rounded-md p-2">
            <p className="text-[11px] text-fg-tertiary mb-1">AI-generated · {d.aiSummaryModel} · {fmtDate(d.aiSummaryAt)}</p>
            <p className="whitespace-pre-wrap">{d.aiSummary}</p>
          </div>
        )}
        {canUse && canDraft && textFields.length > 0 && (
          <div className="flex flex-wrap gap-1.5 items-center">
            <span>Draft text for:</span>
            {textFields.map((f) => <Button key={f.key} size="sm" variant="ghost" disabled={!!busy} onClick={() => void draftField(f.key)}>{f.label}</Button>)}
          </div>
        )}
        {draft && (
          <div className="border border-border-subtle rounded-md p-2 space-y-2">
            <p className="text-[11px] text-fg-tertiary">AI draft for “{textFields.find((f) => f.key === draft.key)?.label}” — edit it before saving. [needs input: …] marks facts the AI did not have.</p>
            <p className="whitespace-pre-wrap">{draft.text}</p>
            <div className="flex gap-2"><Button size="sm" onClick={() => { onDraft(draft.key, draft.text); setDraft(null); }}>Use this text</Button><Button size="sm" variant="ghost" onClick={() => setDraft(null)}>Discard</Button></div>
          </div>
        )}
        {busy && <p role="status">{busy}</p>}
        {error && <Callout tone="danger">{error}</Callout>}
      </div>
    </Panel>
  );
}
