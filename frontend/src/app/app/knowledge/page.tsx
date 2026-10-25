"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import Link from "next/link";
import { BookOpenText, Database, FilePlus2, RefreshCw, Search, Upload, Layers } from "lucide-react";
import { PageHeader } from "@/components/ui/PageHeader";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { Badge } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Field, FilterSelect, Select, TextArea } from "@/components/ui/Field";
import { Input } from "@/components/ui/Input";
import { Modal } from "@/components/ui/Modal";
import { Drawer } from "@/components/ui/Drawer";
import { EmptyState } from "@/components/ui/EmptyState";
import { StatCard } from "@/components/ui/StatCard";
import { SkeletonRows } from "@/components/ui/Status";
import { useToast } from "@/components/ui/Toast";
import { ReindexOutcomeView, ReindexPlanView, reindexFailure } from "@/components/rag/ReindexPlanView";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { useResource } from "@/lib/useResource";
import { isAdmin, useAuthStore } from "@/lib/auth";
import { formatDateTime, formatRelative } from "@/lib/format";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";
import {
  RAG_MAX_CONTENT_BYTES,
  RAG_SOURCES,
  type EmbeddingStatus,
  type RagDocument,
  type RagIngestRequest,
  type RagIngestResponse,
  type RagSource,
  type RagStatus,
  type ReindexOutcome,
  type ReindexPlan
} from "@/services/contracts";

const SOURCE_LABEL: Record<string, string> = { manual: "Manual", sop: "SOP", compliance: "Compliance", faq: "FAQ" };

type DocState = "current" | "reindex" | "pending";

/** Whether a document's stored vectors match the current embedding settings. */
function docState(d: RagDocument, emb: EmbeddingStatus | null): DocState {
  if (d.embeddingProvider === "pending") return "pending";
  if (!emb) return "current";
  return d.embeddingProvider === emb.provider && d.embeddingModel === emb.model && d.dims === emb.dimensions ? "current" : "reindex";
}

const STATE_BADGE: Record<DocState, { tone: "success" | "warning" | "danger"; label: string; title: string }> = {
  current: { tone: "success", label: "Embedded", title: "Stored vectors match the current embedding settings." },
  reindex: { tone: "warning", label: "Needs reindex", title: "Embedded with different settings; the next confirmed reindex re-embeds it." },
  pending: { tone: "danger", label: "No vectors", title: "Stored without vectors (embedder unavailable at ingest). Keyword search still finds it." }
};

export default function KnowledgePage() {
  const { locale } = useT();
  const admin = isAdmin(useAuthStore((s) => s.user));
  const [source, setSource] = useState<string>("");
  const [query, setQuery] = useState("");
  const [selected, setSelected] = useState<RagDocument | null>(null);
  const [ingestOpen, setIngestOpen] = useState(false);
  const [reindexOpen, setReindexOpen] = useState(false);

  const docs = useResource<RagDocument[]>(`/api/rag/documents${source ? `?source=${encodeURIComponent(source)}` : ""}`);
  const status = useResource<RagStatus>("/api/rag/status", { pollMs: 30_000 });
  const embedding = useResource<EmbeddingStatus>("/api/settings/embedding");

  const reloadAll = () => {
    void docs.reload();
    void status.reload();
    void embedding.reload();
  };

  const rows = useMemo(() => {
    const q = query.trim().toLowerCase();
    const list = docs.data ?? [];
    if (!q) return list;
    return list.filter((d) => [d.title, d.titleBn ?? "", d.id, ...d.tags].some((s) => s.toLowerCase().includes(q)));
  }, [docs.data, query]);

  const counts = useMemo(() => {
    const c = { current: 0, reindex: 0, pending: 0 };
    for (const d of docs.data ?? []) c[docState(d, embedding.data)]++;
    return c;
  }, [docs.data, embedding.data]);

  const s = status.data;
  const mismatch = s ? s.storedDims !== s.dims : false;

  return (
    <div className="page" data-testid="knowledge-page">
      <PageHeader
        eyebrow="Intelligence"
        title="Knowledge base"
        description="The API's own pgvector document index. Ask and agent runs no longer answer from it; they use the Python RAG service."
        actions={
          <>
            <Button variant="secondary" size="sm" onClick={reloadAll} disabled={docs.refreshing} aria-label="Refresh knowledge base">
              <RefreshCw size={13} className={cn(docs.refreshing && "animate-spin")} /> Refresh
            </Button>
            {admin && (
              <>
                <Button variant="secondary" size="sm" onClick={() => setReindexOpen(true)} data-testid="kb-review-reindex">
                  <Layers size={13} /> Review reindex
                </Button>
                <Button variant="primary" size="sm" onClick={() => setIngestOpen(true)} data-testid="kb-add-document">
                  <FilePlus2 size={13} /> Add document
                </Button>
              </>
            )}
          </>
        }
      />

      <Callout tone="warning" className="mt-5" title="Documents here are not used for answers" testId="kb-not-answer-source">
        Ask and agent runs answer only from the PDFs indexed in the Python RAG service (<span className="font-mono">rag/</span>). To add a document there, put the PDF in <span className="font-mono">rag/data/</span> and run <span className="font-mono">python ingest.py --pdf data/your.pdf</span>.
      </Callout>

      {!admin && (
        <Callout tone="info" className="mt-3" testId="kb-readonly">
          You can browse the index. Adding documents and reindexing need an Admin.
        </Callout>
      )}

      <section className="mt-5 grid grid-cols-2 lg:grid-cols-4 gap-3" aria-label="Index status">
        <StatCard
          label="Documents"
          value={docs.data ? (source ? `${docs.data.length}` : `${docs.data.length}`) : "—"}
          hint={source ? `${SOURCE_LABEL[source]} only` : `${counts.current} embedded · ${counts.reindex + counts.pending} need vectors`}
          loading={docs.loading}
          error={docs.error ? "Unavailable" : null}
          icon={<BookOpenText size={15} />}
          testId="kb-stat-docs"
        />
        <StatCard
          label="Active embedder"
          value={s ? <span className="text-[18px] leading-[26px] font-mono">{s.activeProvider}</span> : "—"}
          hint={s ? `${s.model} · ${s.dims} dims${s.configuredProvider !== s.activeProvider ? ` · configured: ${s.configuredProvider}` : ""}` : undefined}
          loading={status.loading}
          error={status.error ? "Unavailable" : null}
          tone={s?.degraded ? "warn" : "neutral"}
          icon={<Database size={15} />}
          testId="kb-stat-embedder"
        />
        <StatCard
          label="Stored vector size"
          value={s ? `${s.storedDims}` : "—"}
          hint={s ? (mismatch ? `Settings expect ${s.dims}: keyword-only ranking` : "Matches the embedder") : undefined}
          tone={mismatch ? "warn" : "neutral"}
          loading={status.loading}
          error={status.error ? "Unavailable" : null}
        />
        <StatCard
          label="Waiting for reindex"
          value={s ? `${s.pendingCount}` : "—"}
          hint={s?.lastProbeAt ? `Provider probe ${s.lastProbeOk ? "ok" : "failed"} ${formatRelative(s.lastProbeAt, locale)}` : "Provider not probed yet"}
          tone={(s?.pendingCount ?? 0) > 0 ? "warn" : "good"}
          loading={status.loading}
          error={status.error ? "Unavailable" : null}
          testId="kb-stat-pending"
        />
      </section>

      {s?.degraded && (
        <Callout tone="warning" className="mt-3" title="Embedding provider unavailable">
          The configured provider ({s.configuredProvider}) is not reachable, so search ranks by keywords only and new documents are stored without vectors until a reindex.
        </Callout>
      )}
      {mismatch && !s?.degraded && (
        <Callout tone="warning" className="mt-3" title="Stored vectors don't match the embedding settings">
          The vector column holds {s?.storedDims}-dimension vectors but the embedder produces {s?.dims}. Ranking falls back to keywords until an Admin reviews and confirms a reindex.
        </Callout>
      )}

      <Panel
        className="mt-5"
        flush
        title="Documents"
        subtitle={docs.data ? `${rows.length} shown · newest first` : undefined}
        right={
          <>
            <div className="relative">
              <Search size={14} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-fg-tertiary" aria-hidden />
              <input
                type="search"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="Search title, tag or id"
                aria-label="Search documents"
                className="control !h-8 !pl-8 !w-56 !text-caption"
                data-testid="kb-search"
              />
            </div>
            <FilterSelect label="Source" value={source} onChange={setSource} testId="kb-source-filter">
              <option value="">All sources</option>
              {RAG_SOURCES.map((s) => (
                <option key={s} value={s}>{SOURCE_LABEL[s]}</option>
              ))}
            </FilterSelect>
          </>
        }
      >
        {docs.error && (
          <div className="p-4">
            <Callout tone="danger" title="Could not load documents" action={<Button size="sm" onClick={() => void docs.reload()}>Retry</Button>}>
              {errorMessage(docs.error)}
            </Callout>
          </div>
        )}
        {docs.loading ? (
          <div className="p-4"><SkeletonRows rows={6} rowHeight="h-11" /></div>
        ) : rows.length === 0 && !docs.error ? (
          <EmptyState
            icon={<BookOpenText size={22} />}
            title={docs.data?.length ? "No documents match this search." : "No documents in the index yet."}
            body={docs.data?.length ? "Clear the search or pick another source." : admin ? "Add a manual or SOP to make it searchable in Ask." : "An Admin can add manuals and SOPs."}
          />
        ) : (
          <div className="overflow-x-auto">
            <table className="data-table" data-testid="kb-table">
              <thead>
                <tr>
                  <th>Title</th>
                  <th>Source</th>
                  <th className="num">Chunks</th>
                  <th>Embedding</th>
                  <th>Status</th>
                  <th>Added</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((d) => {
                  const st = STATE_BADGE[docState(d, embedding.data)];
                  return (
                    <tr
                      key={d.id}
                      data-testid="kb-row"
                      data-doc-id={d.id}
                      tabIndex={0}
                      className="cursor-pointer"
                      onClick={() => setSelected(d)}
                      onKeyDown={(e) => (e.key === "Enter" || e.key === " ") && (e.preventDefault(), setSelected(d))}
                    >
                      <td className="max-w-[340px]">
                        <span className="block text-fg-primary font-medium truncate">{d.title}</span>
                        {d.titleBn && <span className="block text-[12px] text-fg-tertiary truncate">{d.titleBn}</span>}
                        {d.tags.length > 0 && (
                          <span className="mt-1 flex flex-wrap gap-1">
                            {d.tags.slice(0, 4).map((t) => (
                              <span key={t} className="text-[11px] px-1.5 rounded bg-surface-2 border border-border-subtle text-fg-tertiary">{t}</span>
                            ))}
                            {d.tags.length > 4 && <span className="text-[11px] text-fg-tertiary">+{d.tags.length - 4}</span>}
                          </span>
                        )}
                      </td>
                      <td>
                        <Badge>{SOURCE_LABEL[d.source] ?? d.source}</Badge>
                        {d.isDemo && <Badge tone="accent" className="ml-1">Demo seed</Badge>}
                      </td>
                      <td className="num">{d.chunks}</td>
                      <td className="font-mono text-[12px] whitespace-nowrap">{d.embeddingProvider}/{d.embeddingModel}/{d.dims}</td>
                      <td><Badge tone={st.tone} dot title={st.title}>{st.label}</Badge></td>
                      <td className="whitespace-nowrap" title={formatDateTime(d.createdAt, locale)}>{formatRelative(d.createdAt, locale)}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        <p className="px-4 py-3 border-t border-border-subtle text-caption text-fg-tertiary">
          Removing or editing a document isn&apos;t supported by the API yet; re-ingest a corrected copy instead.
        </p>
      </Panel>

      <Drawer open={!!selected} onClose={() => setSelected(null)} title={selected ? "Document" : null} width="w-full sm:w-[30rem]">
        {selected && (
          <div className="p-5 space-y-4" data-testid="kb-detail">
            <div>
              <h3 className="text-title font-semibold text-fg-primary">{selected.title}</h3>
              {selected.titleBn && <p className="text-caption text-fg-tertiary">{selected.titleBn}</p>}
            </div>
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-caption">
              <dt className="text-fg-tertiary">Id</dt><dd className="font-mono text-fg-secondary break-all">{selected.id}</dd>
              <dt className="text-fg-tertiary">Source</dt><dd className="text-fg-secondary">{SOURCE_LABEL[selected.source] ?? selected.source}</dd>
              <dt className="text-fg-tertiary">Chunks</dt><dd className="text-fg-secondary">{selected.chunks}</dd>
              <dt className="text-fg-tertiary">Embedded with</dt><dd className="font-mono text-fg-secondary">{selected.embeddingProvider} / {selected.embeddingModel} / {selected.dims}</dd>
              <dt className="text-fg-tertiary">Status</dt><dd><Badge tone={STATE_BADGE[docState(selected, embedding.data)].tone} dot>{STATE_BADGE[docState(selected, embedding.data)].label}</Badge></dd>
              <dt className="text-fg-tertiary">Added</dt><dd className="text-fg-secondary">{formatDateTime(selected.createdAt, locale)}</dd>
              <dt className="text-fg-tertiary">Tags</dt><dd className="text-fg-secondary">{selected.tags.length ? selected.tags.join(", ") : "—"}</dd>
              <dt className="text-fg-tertiary">Origin</dt><dd className="text-fg-secondary">{selected.isDemo ? "Demo seed shipped with the app" : "Ingested through the API"}</dd>
            </dl>
            <p className="text-caption text-fg-secondary">{STATE_BADGE[docState(selected, embedding.data)].title}</p>
            <Link href={`/app/ask?q=${encodeURIComponent(selected.title)}`} className="inline-flex text-caption text-accent hover:underline">
              Ask about this document
            </Link>
          </div>
        )}
      </Drawer>

      {admin && (
        <>
          <IngestDialog open={ingestOpen} onClose={() => setIngestOpen(false)} onIngested={reloadAll} />
          <ReindexDialog open={reindexOpen} onClose={() => setReindexOpen(false)} onDone={reloadAll} />
        </>
      )}
    </div>
  );
}

// ── Ingest ──────────────────────────────────────────────────────────────────

type IngestDraft = { title: string; titleBn: string; source: RagSource; tags: string; url: string; content: string };
const EMPTY: IngestDraft = { title: "", titleBn: "", source: "manual", tags: "", url: "", content: "" };
const ACCEPTED_FILES = ".txt,.md,.markdown,.csv,text/plain,text/markdown";

function IngestDialog({ open, onClose, onIngested }: { open: boolean; onClose: () => void; onIngested: () => void }) {
  const toast = useToast();
  const [draft, setDraft] = useState<IngestDraft>(EMPTY);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [failure, setFailure] = useState<string | null>(null);
  const [result, setResult] = useState<RagIngestResponse | null>(null);
  const fileRef = useRef<HTMLInputElement>(null);

  const contentBytes = useMemo(() => new TextEncoder().encode(draft.content).length, [draft.content]);
  const tooLarge = contentBytes > RAG_MAX_CONTENT_BYTES;
  const tags = draft.tags.split(",").map((t) => t.trim()).filter(Boolean);

  function set<K extends keyof IngestDraft>(k: K, v: IngestDraft[K]) {
    setDraft((d) => ({ ...d, [k]: v }));
    setErrors((e) => ({ ...e, [k]: "" }));
  }

  function close() {
    if (busy) return;
    setDraft(EMPTY);
    setErrors({});
    setFailure(null);
    setResult(null);
    onClose();
  }

  async function loadFile(file: File) {
    if (file.size > RAG_MAX_CONTENT_BYTES) {
      setErrors((e) => ({ ...e, content: `That file is ${(file.size / 1024).toFixed(0)} KB; the API accepts at most 200 KB of text.` }));
      return;
    }
    const text = await file.text();
    setDraft((d) => ({ ...d, content: text, title: d.title || file.name.replace(/\.[^.]+$/, "") }));
    setErrors((e) => ({ ...e, content: "" }));
  }

  function validate(): boolean {
    const next: Record<string, string> = {};
    if (!draft.title.trim()) next.title = "A title is required.";
    else if (draft.title.trim().length > 280) next.title = "At most 280 characters.";
    if (draft.titleBn.trim().length > 280) next.titleBn = "At most 280 characters.";
    if (!draft.content.trim()) next.content = "Paste the document text or load a text file.";
    else if (tooLarge) next.content = "The text is larger than 200 KB.";
    if (tags.length > 32) next.tags = "At most 32 tags.";
    else if (tags.some((t) => t.length > 64)) next.tags = "Each tag can be at most 64 characters.";
    if (draft.url.trim()) {
      try {
        new URL(draft.url.trim());
      } catch {
        next.url = "Enter a full URL, e.g. https://intranet/manuals/needle.pdf";
      }
    }
    setErrors(next);
    return Object.keys(next).length === 0;
  }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (busy || !validate()) return;
    setBusy(true);
    setFailure(null);
    const body: RagIngestRequest = {
      title: draft.title.trim(),
      source: draft.source,
      content: draft.content,
      ...(draft.titleBn.trim() ? { titleBn: draft.titleBn.trim() } : {}),
      ...(tags.length ? { tags } : {}),
      ...(draft.url.trim() ? { url: draft.url.trim() } : {})
    };
    try {
      const res = await apiJson<RagIngestResponse>("/api/rag/ingest", { method: "POST", body, timeoutMs: 120_000 });
      setResult(res);
      toast.push({
        tone: res.embedded ? "success" : "info",
        title: res.embedded ? "Document ingested" : "Document stored without vectors",
        description: `${res.chunks} chunk(s) · ${res.provider}/${res.model}`
      });
      onIngested();
    } catch (err) {
      if (isApiError(err) && Object.keys(err.fieldErrors).length) {
        setErrors(Object.fromEntries(Object.entries(err.fieldErrors).map(([k, v]) => [k, v.join(" ")])));
      }
      setFailure(errorMessage(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open={open}
      onClose={close}
      locked={busy}
      title="Add a document"
      description="The API splits the text into chunks, embeds them with the active embedder and makes them searchable in Ask."
      className="sm:max-w-2xl"
      testId="kb-ingest-dialog"
      footer={
        result ? (
          <>
            <Button variant="ghost" onClick={() => { setResult(null); setDraft(EMPTY); }}>Add another</Button>
            <Button variant="primary" onClick={close}>Done</Button>
          </>
        ) : (
          <>
            <Button variant="ghost" onClick={close} disabled={busy}>Cancel</Button>
            <Button variant="primary" type="submit" form="kb-ingest-form" disabled={busy || tooLarge} data-testid="kb-ingest-submit">
              <Upload size={13} /> {busy ? "Ingesting…" : "Ingest document"}
            </Button>
          </>
        )
      }
    >
      {result ? (
        <div className="space-y-3" data-testid="kb-ingest-result">
          <Callout tone={result.embedded ? "success" : "warning"} title={result.embedded ? "Stored and embedded" : "Stored without vectors"}>
            <span className="font-mono">{result.documentId}</span>: {result.chunks} chunk(s) with {result.provider}/{result.model} ({result.dims} dims).
            {result.warning && <span className="block mt-1">{result.warning}</span>}
          </Callout>
          {!result.embedded && <p className="text-caption text-fg-secondary">Keyword search finds it now. Review a reindex once the embedder is available to add vectors.</p>}
        </div>
      ) : (
        <form id="kb-ingest-form" onSubmit={submit} noValidate className="space-y-4">
          {failure && <Callout tone="danger" title="Not ingested">{failure}</Callout>}
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="Title *" error={errors.title}>
              {(p) => <Input id={p.id} aria-describedby={p.describedBy} aria-invalid={p.invalid} value={draft.title} maxLength={280} onChange={(e) => set("title", e.target.value)} data-testid="kb-title" />}
            </Field>
            <Field label="Title (Bangla)" error={errors.titleBn}>
              {(p) => <Input id={p.id} aria-describedby={p.describedBy} aria-invalid={p.invalid} value={draft.titleBn} maxLength={280} onChange={(e) => set("titleBn", e.target.value)} />}
            </Field>
            <Field label="Source *" error={errors.source}>
              {(p) => (
                <Select id={p.id} aria-describedby={p.describedBy} value={draft.source} onChange={(e) => set("source", e.target.value as RagSource)} data-testid="kb-source">
                  {RAG_SOURCES.map((s) => <option key={s} value={s}>{SOURCE_LABEL[s]}</option>)}
                </Select>
              )}
            </Field>
            <Field label="Tags" hint="Comma-separated, e.g. needle, sewing, safety" error={errors.tags}>
              {(p) => <Input id={p.id} aria-describedby={p.describedBy} aria-invalid={p.invalid} value={draft.tags} onChange={(e) => set("tags", e.target.value)} />}
            </Field>
            <Field label="Source URL" hint="Optional link to the original document" error={errors.url} className="sm:col-span-2">
              {(p) => <Input id={p.id} aria-describedby={p.describedBy} aria-invalid={p.invalid} type="url" value={draft.url} onChange={(e) => set("url", e.target.value)} placeholder="https://" />}
            </Field>
          </div>
          <Field
            label="Content *"
            hint={`Plain text or Markdown · ${(contentBytes / 1024).toFixed(1)} KB of 200 KB`}
            error={errors.content || (tooLarge ? "The text is larger than 200 KB." : null)}
          >
            {(p) => (
              <TextArea
                id={p.id}
                aria-describedby={p.describedBy}
                aria-invalid={p.invalid || tooLarge}
                rows={10}
                value={draft.content}
                onChange={(e) => set("content", e.target.value)}
                className="font-mono text-[13px]"
                data-testid="kb-content"
              />
            )}
          </Field>
          <div className="flex flex-wrap items-center gap-2">
            <input
              ref={fileRef}
              type="file"
              accept={ACCEPTED_FILES}
              className="sr-only"
              aria-label="Load text from a file"
              onChange={(e) => {
                const f = e.target.files?.[0];
                if (f) void loadFile(f);
                e.target.value = "";
              }}
            />
            <Button type="button" variant="secondary" size="sm" onClick={() => fileRef.current?.click()}>
              <Upload size={13} /> Load from a text file
            </Button>
            <span className="text-caption text-fg-tertiary">.txt or .md, read in your browser and sent as text. PDF upload isn&apos;t supported by this API.</span>
          </div>
        </form>
      )}
    </Modal>
  );
}

// ── Reindex ─────────────────────────────────────────────────────────────────

function ReindexDialog({ open, onClose, onDone }: { open: boolean; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [plan, setPlan] = useState<ReindexPlan | null>(null);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [ack, setAck] = useState(false);
  const [outcome, setOutcome] = useState<ReindexOutcome | null>(null);

  async function loadPlan() {
    setLoading(true);
    setError(null);
    setAck(false);
    try {
      setPlan(await apiJson<ReindexPlan>("/api/rag/reindex/plan"));
    } catch (e) {
      setError(reindexFailure(e).message);
    } finally {
      setLoading(false);
    }
  }

  // Load a fresh plan every time the dialog opens.
  useEffect(() => {
    if (!open) return;
    setPlan(null);
    setOutcome(null);
    void loadPlan();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  async function confirm() {
    if (!plan || busy) return;
    setBusy(true);
    setError(null);
    try {
      const res = await apiJson<ReindexOutcome>("/api/rag/reindex", { method: "POST", body: { confirmPlanId: plan.planId }, timeoutMs: 15 * 60_000 });
      setOutcome(res);
      toast.push({ title: "Reindex complete", description: `${res.documents} document(s), ${res.chunks} chunk(s) re-embedded.` });
      onDone();
    } catch (e) {
      const f = reindexFailure(e);
      setError(f.message);
      if (f.plan) {
        setPlan(f.plan);
        setAck(false);
      }
    } finally {
      setBusy(false);
    }
  }

  const nothingToDo = plan && !plan.requiresReindex;

  return (
    <Modal
      open={open}
      onClose={onClose}
      locked={busy}
      title="Review reindex"
      description="Re-embeds stored documents with the current embedding settings. Nothing changes until you confirm this exact plan."
      className="sm:max-w-2xl"
      testId="kb-reindex-dialog"
      footer={
        outcome || nothingToDo ? (
          <Button variant="primary" onClick={onClose}>Close</Button>
        ) : (
          <>
            <Button variant="ghost" onClick={onClose} disabled={busy}>Cancel</Button>
            <Button variant="secondary" onClick={() => void loadPlan()} disabled={busy || loading}>Refresh plan</Button>
            <Button variant="danger" onClick={() => void confirm()} disabled={!plan || !ack || busy || loading} data-testid="kb-reindex-confirm">
              {busy ? "Re-embedding…" : "Confirm and re-embed"}
            </Button>
          </>
        )
      }
    >
      {error && <Callout tone="danger" className="mb-3" title="Nothing was changed">{error}</Callout>}
      {outcome ? (
        <Callout tone="success" title="Reindex complete"><ReindexOutcomeView outcome={outcome} /></Callout>
      ) : loading && !plan ? (
        <SkeletonRows rows={3} rowHeight="h-16" />
      ) : plan ? (
        nothingToDo ? (
          <Callout tone="success" title="Nothing to reindex" testId="kb-reindex-nothing">
            Every document already has vectors that match {plan.current.provider}/{plan.current.model}/{plan.current.dimensions}.
          </Callout>
        ) : (
          <div className="space-y-4">
            <ReindexPlanView plan={plan} />
            <label className="flex items-start gap-2 text-caption text-fg-secondary">
              <input type="checkbox" checked={ack} onChange={(e) => setAck(e.target.checked)} className="mt-0.5 accent-[var(--accent)]" data-testid="kb-reindex-ack" />
              <span>I reviewed this plan. Re-embed {plan.documents} document(s) ({plan.chunks} chunks){plan.cost.estimateUsd ? ` at an estimated ${plan.cost.isUpperBound ? "≤ " : ""}$${plan.cost.estimateUsd.toFixed(4)}` : ""}.</span>
            </label>
          </div>
        )
      ) : null}
    </Modal>
  );
}
