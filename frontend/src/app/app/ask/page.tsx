"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import {
  AlertTriangle,
  ArrowUp,
  CheckCircle2,
  Clock,
  Copy,
  FileText,
  Lightbulb,
  Loader2,
  RefreshCw,
  RotateCcw,
  Search,
  Trash2
} from "lucide-react";
import { useT } from "@/lib/useT";
import { Button } from "@/components/ui/Button";
import { Badge, RISK_TONE } from "@/components/ui/Badge";
import { Callout } from "@/components/ui/Callout";
import { Skeleton } from "@/components/ui/Skeleton";
import { EvidenceBlock } from "@/components/evidence/EvidenceBlock";
import { cn } from "@/lib/cn";
import { useAskStore, type AskRecord } from "@/store/ask.store";
import { useAuthStore } from "@/lib/auth";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { useResource } from "@/lib/useResource";
import { askModeLabel, resolveAskMode, shouldShowConfidence, type AskMode } from "@/lib/askMode";
import type { AskRagHit, AskRequest, RagServiceStatus } from "@/services/contracts";
import type { AskAnswer } from "@/services/types";

type Answer = AskAnswer & { ragHits?: AskRagHit[] };
type Block = AskRecord["blocks"][number];

const MAX_QUERY = 2000;

/** Turn an API answer into the blocks the page (and the history) renders. */
function toBlocks(json: Answer, mode: AskMode): Block[] {
  return [
    { type: "analyzed", data: json.analyzed ?? [] },
    { type: "finding", data: { en: json.finding, bn: json.findingBn || json.finding } },
    ...(json.factors?.length ? [{ type: "factors" as const, data: json.factors }] : []),
    ...(json.citations?.length ? [{ type: "citations" as const, data: json.citations }] : []),
    ...(json.evidence?.length ? [{ type: "evidence" as const, data: json.evidence }] : []),
    ...(json.recommendation ? [{ type: "recommendation" as const, data: json.recommendation }] : []),
    { type: "done", data: { confidence: shouldShowConfidence(mode) ? json.confidence : null, ragHits: json.ragHits ?? [] } }
  ];
}

export default function AskPage() {
  const { t, locale } = useT();
  const search = useSearchParams();
  const user = useAuthStore((s) => s.user);
  const [query, setQuery] = useState("");
  const [asked, setAsked] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [blocks, setBlocks] = useState<Block[]>([]);
  const [error, setError] = useState<{ title: string; body: string } | null>(null);
  const [source, setSource] = useState<AskMode>("demo");
  const [notice, setNotice] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const scrollerRef = useRef<HTMLDivElement | null>(null);
  const inputRef = useRef<HTMLTextAreaElement | null>(null);
  const rag = useResource<RagServiceStatus>("/api/ask/status");
  const ragUp = rag.data ? rag.data.reachable && rag.data.ready && rag.data.qdrant === "healthy" : null;

  // History is kept on this device only, and only the signed-in user's own.
  const allHistory = useAskStore((s) => s.history);
  const pushHistory = useAskStore((s) => s.push);
  const removeHistory = useAskStore((s) => s.remove);
  const history = useMemo(() => allHistory.filter((r) => r.userId === user?.id), [allHistory, user?.id]);

  useEffect(() => {
    const q = search?.get("q");
    if (q) setQuery(q.slice(0, MAX_QUERY));
  }, [search]);

  useEffect(() => {
    scrollerRef.current?.scrollTo({ top: scrollerRef.current.scrollHeight, behavior: "smooth" });
  }, [blocks.length, loading]);

  async function submit(q?: string) {
    const finalQ = (q ?? query).trim();
    if (!finalQ || loading) return;
    setQuery(finalQ);
    setAsked(finalQ);
    setBlocks([]);
    setError(null);
    setNotice(null);
    setLoading(true);
    const body: AskRequest = { query: finalQ };
    try {
      const json = await apiJson<Answer>("/api/ask", { method: "POST", body, timeoutMs: 90_000 });
      // Only the API's explicit `mode` decides whether this is shown as live.
      const mode = resolveAskMode(json);
      const next = toBlocks(json, mode);
      setSource(mode);
      setBlocks(next);
      if (json.warning) setNotice(json.warning);
      pushHistory({
        id: json.taskId || `ask-${Date.now().toString(36)}`,
        userId: user?.id,
        query: finalQ,
        locale,
        source: mode,
        blocks: next,
        isoDate: new Date().toISOString()
      });
    } catch (e) {
      if (isApiError(e) && e.kind === "rate_limited") setError({ title: "Too many questions", body: e.message });
      else if (isApiError(e) && e.kind === "validation") setError({ title: "Question not accepted", body: e.message });
      else if (isApiError(e) && (e.kind === "unavailable" || e.kind === "server")) setError({ title: "The RAG service is unavailable", body: `${e.message} Your question was not answered.` });
      else setError({ title: "No answer", body: errorMessage(e) });
    } finally {
      setLoading(false);
    }
  }

  function clear() {
    setBlocks([]);
    setQuery("");
    setAsked(null);
    setError(null);
    setNotice(null);
    inputRef.current?.focus();
  }

  function copyAnswer() {
    const text = blocks
      .map((b) => {
        if (b.type === "finding") return locale === "bn" ? b.data.bn : b.data.en;
        if (b.type === "recommendation") return `[${b.data.riskTier}] ${locale === "bn" ? b.data.titleBn : b.data.title} — ${locale === "bn" ? b.data.actionBn : b.data.action}`;
        if (b.type === "citations") return "Sources:\n" + b.data.map((c: { title: string }) => `- ${c.title}`).join("\n");
        return "";
      })
      .filter(Boolean)
      .join("\n\n");
    if (!text || !navigator.clipboard) return;
    navigator.clipboard.writeText(text).then(
      () => {
        setCopied(true);
        setTimeout(() => setCopied(false), 1600);
      },
      () => undefined
    );
  }

  function loadFromHistory(rec: AskRecord) {
    setQuery(rec.query);
    setAsked(rec.query);
    setBlocks(rec.blocks);
    setSource(rec.source);
    setError(null);
    setNotice(null);
  }

  const done = blocks.some((b) => b.type === "done");

  return (
    <div className="flex flex-col h-full" data-tour="ask">
      <div className="border-b border-border-subtle bg-[var(--bg-surface)]">
        <div className="max-w-3xl mx-auto w-full px-4 md:px-6 py-4 flex flex-wrap items-center justify-between gap-3">
          <div className="min-w-0">
            <h1 className="text-[20px] leading-[28px] font-semibold tracking-tight">{t("ask.title")}</h1>
            <p className="text-caption text-fg-tertiary" data-testid="ask-rag-status">
              {rag.data
                ? <>Answered by the RAG service: <span className="text-fg-secondary">{rag.data.llmModel}</span> over the PDFs in <span className="font-mono text-fg-secondary">{rag.data.collection}</span> ({rag.data.embeddingModel}).</>
                : rag.error ? "Could not read the RAG service status." : "Checking the RAG service…"}
            </p>
          </div>
          {(asked || done) && <SourceChip source={source} />}
        </div>
      </div>

      <div ref={scrollerRef} className="flex-1 overflow-y-auto">
        <div className="max-w-3xl mx-auto w-full px-4 md:px-6 py-5 pb-8">
          {!asked && !loading && (
            <div>
              {ragUp === false && (
                <Callout tone="danger" className="mb-4" title="The RAG service is not available" testId="ask-rag-down">
                  {rag.data?.reachable ? `The service is running but not ready (Qdrant: ${rag.data.qdrant}).` : "The API cannot reach the Python RAG service."} Start Qdrant and run <span className="font-mono">uvicorn main:app --port 8000</span> in <span className="font-mono">rag/</span>.
                </Callout>
              )}
              <Callout tone="info">
                Answers come only from the PDFs indexed in the RAG service; add documents with <span className="font-mono">python ingest.py --pdf data/your.pdf</span> in <span className="font-mono">rag/</span>. Each question is answered on its own, and the sources under an answer are the passages it cites.
              </Callout>

              {history.length > 0 && (
                <section className="mt-8">
                  <div className="flex items-center justify-between">
                    <p className="eyebrow inline-flex items-center gap-1.5"><Clock size={12} /> Recent questions on this device</p>
                  </div>
                  <ul className="mt-2 space-y-1.5">
                    {history.slice(0, 8).map((r) => (
                      <li key={r.id} className="group flex items-stretch gap-1">
                        <button
                          type="button"
                          onClick={() => loadFromHistory(r)}
                          className="flex-1 min-w-0 text-left px-3 py-2 rounded-[var(--radius-control)] border border-border-subtle bg-[var(--bg-surface)] hover:border-border-strong transition-colors"
                        >
                          <p className="text-body text-fg-primary line-clamp-1">{r.query}</p>
                          <p className="text-caption text-fg-tertiary mt-0.5">
                            {askModeLabel(r.source).label} · {new Date(r.isoDate).toLocaleString(locale === "bn" ? "bn-BD" : "en-GB")}
                          </p>
                        </button>
                        <button
                          type="button"
                          onClick={() => removeHistory(r.id)}
                          aria-label={`Remove "${r.query}" from history`}
                          className="px-2 rounded-[var(--radius-control)] text-fg-tertiary hover:text-[var(--risk-high)] hover:bg-surface-2"
                        >
                          <Trash2 size={14} />
                        </button>
                      </li>
                    ))}
                  </ul>
                </section>
              )}
            </div>
          )}

          {asked && (
            <div className="flex justify-end">
              <div className="max-w-[85%] rounded-2xl rounded-br-md bg-[var(--accent-soft)] border border-[var(--accent-border)] px-4 py-2.5 text-body text-fg-primary whitespace-pre-wrap">
                {asked}
              </div>
            </div>
          )}

          <div className="mt-4 space-y-3">
            {notice && (
              <Callout tone="warning" testId="ask-notice" title="Note">{notice}</Callout>
            )}

            {blocks.map((b, idx) => (
              <div key={idx}>{renderBlock(b, locale, t)}</div>
            ))}

            {loading && (
              <div className="surface p-4 space-y-3" role="status" aria-live="polite">
                <div className="flex items-center gap-2 text-caption text-fg-tertiary">
                  <Loader2 size={13} className="animate-spin" /> Searching the knowledge base and preparing an answer…
                </div>
                <Skeleton className="h-3 w-[88%]" />
                <Skeleton className="h-3 w-[72%]" />
                <Skeleton className="h-3 w-[56%]" />
              </div>
            )}

            {error && (
              <Callout tone="danger" title={error.title} action={asked ? <Button size="sm" onClick={() => void submit(asked)}>Retry</Button> : undefined}>
                {error.body}
              </Callout>
            )}
          </div>

          {done && (
            <div className="mt-4 flex items-center gap-2 flex-wrap">
              <Button variant="secondary" size="sm" onClick={copyAnswer}>
                {copied ? <CheckCircle2 size={13} className="text-[var(--risk-low)]" /> : <Copy size={13} />}
                {copied ? t("ask.copied") : t("ask.copy")}
              </Button>
              <Button variant="secondary" size="sm" onClick={() => void submit(asked ?? query)} disabled={loading}>
                <RefreshCw size={13} /> Ask again
              </Button>
              <Button variant="ghost" size="sm" onClick={clear}>
                <RotateCcw size={13} /> {t("ask.newQuestion")}
              </Button>
            </div>
          )}
        </div>
      </div>

      <div className="border-t border-border-subtle bg-[var(--bg-surface)] px-4 md:px-6 pt-3 pb-4">
        <div className="max-w-3xl mx-auto">
          <form
            onSubmit={(e) => {
              e.preventDefault();
              void submit();
            }}
            className="flex items-end gap-2 rounded-[var(--radius-card)] border border-border-strong bg-[var(--bg-surface)] pl-3 pr-2 py-2 focus-within:border-[var(--accent)] focus-within:shadow-[0_0_0_3px_var(--accent-soft)]"
          >
            <label htmlFor="ask-input" className="sr-only">Your question</label>
            <textarea
              id="ask-input"
              ref={inputRef}
              value={query}
              maxLength={MAX_QUERY}
              onChange={(e) => setQuery(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && !e.shiftKey) {
                  e.preventDefault();
                  void submit();
                }
              }}
              placeholder={locale === "bn" ? t("ask.placeholderBn") : t("ask.placeholder")}
              data-testid="ask-input"
              rows={1}
              className="flex-1 resize-none bg-transparent border-0 outline-none text-body text-fg-primary placeholder:text-fg-tertiary max-h-40 py-2"
            />
            <Button variant="primary" size="md" type="submit" disabled={loading || !query.trim()} aria-label={t("ask.sendLabel")}>
              {loading ? <Loader2 size={15} className="animate-spin" /> : <ArrowUp size={15} />}
            </Button>
          </form>
          <p className="mt-1.5 text-caption text-fg-tertiary flex justify-between gap-3">
            <span>{t("common.banglaHint")}</span>
            {query.length > MAX_QUERY * 0.8 && <span>{query.length}/{MAX_QUERY}</span>}
          </p>
        </div>
      </div>
    </div>
  );
}

function SourceChip({ source }: { source: AskMode }) {
  const { label, title, isLive } = askModeLabel(source);
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 h-7 px-2.5 rounded-full border text-caption font-medium",
        isLive
          ? "bg-[var(--risk-low-soft)] border-[var(--risk-low-border)] text-[var(--risk-low)]"
          : source === "no_source"
          ? "bg-[var(--risk-medium-soft)] border-[var(--risk-medium-border)] text-[var(--risk-medium)]"
          : "bg-surface-2 border-border-subtle text-fg-secondary"
      )}
      title={title}
      data-testid="ask-source-chip"
      data-mode={source}
    >
      <span className={cn("size-1.5 rounded-full", isLive ? "bg-[var(--risk-low)]" : "bg-current")} aria-hidden />
      {label}
    </span>
  );
}

function renderBlock(block: Block, locale: "en" | "bn", t: (k: string, p?: Record<string, string | number>) => string) {
  switch (block.type) {
    case "analyzed":
      if (!block.data?.length) return null;
      return (
        <div className="flex flex-wrap items-center gap-1.5 text-caption text-fg-tertiary">
          <Search size={12} /> Searched:
          {block.data.map((it: { domain: string; count: number }, i: number) => (
            <span key={i} className="badge">{it.domain} · {it.count}</span>
          ))}
        </div>
      );
    case "finding":
      return (
        <div className="surface p-4" data-testid="ask-finding">
          <p className="eyebrow flex items-center gap-1.5"><FileText size={12} /> {t("ask.finding")}</p>
          <p className="mt-2 text-body text-fg-primary leading-6 whitespace-pre-wrap">{locale === "bn" ? block.data.bn : block.data.en}</p>
        </div>
      );
    case "factors":
      return (
        <div className="surface p-4">
          <p className="eyebrow flex items-center gap-1.5"><Lightbulb size={12} /> {t("ask.factors")}</p>
          <ul className="mt-2 space-y-1.5">
            {block.data.map((f: { label: string; labelBn: string; magnitude: string; magnitudeBn: string }, i: number) => (
              <li key={i} className="flex items-center justify-between gap-3 text-body">
                <span className="text-fg-secondary">{locale === "bn" ? f.labelBn : f.label}</span>
                <span className="text-fg-primary font-medium">{locale === "bn" ? f.magnitudeBn : f.magnitude}</span>
              </li>
            ))}
          </ul>
        </div>
      );
    case "evidence":
      return <EvidenceBlock refs={block.data} />;
    case "citations":
      return (
        <div className="surface p-4" data-testid="ask-citations">
          <p className="eyebrow flex items-center gap-1.5"><FileText size={12} /> Sources ({block.data.length})</p>
          <ol className="mt-3 space-y-3">
            {block.data.map((c: { id: string; title: string; snippet: string; confidence: number; source: string; url: string }, i: number) => (
              <li key={c.id} className="flex gap-3">
                <span className="mt-0.5 size-5 shrink-0 rounded-full bg-surface-2 border border-border-subtle text-[11px] font-semibold flex items-center justify-center text-fg-secondary">{i + 1}</span>
                <div className="min-w-0">
                  <p className="text-body text-fg-primary font-medium">
                    {c.url ? <a href={c.url} target="_blank" rel="noreferrer noopener" className="hover:underline">{c.title}</a> : c.title}
                  </p>
                  <p className="mt-0.5 text-caption text-fg-secondary leading-5">{c.snippet}</p>
                  <p className="mt-1 text-[11px] text-fg-tertiary">{c.source} · match {Math.round((c.confidence ?? 0) * 100)}%</p>
                </div>
              </li>
            ))}
          </ol>
        </div>
      );
    case "recommendation":
      return (
        <div className="surface p-4 border-l-[3px] border-l-[var(--accent)]">
          <div className="flex items-center justify-between gap-2">
            <p className="eyebrow flex items-center gap-1.5"><AlertTriangle size={12} /> {t("ask.recommendedAction")}</p>
            <Badge tone={RISK_TONE[block.data.riskTier as "low" | "medium" | "high"] ?? "neutral"}>{String(block.data.riskTier).toUpperCase()}</Badge>
          </div>
          <p className="mt-2 text-body font-semibold text-fg-primary">{locale === "bn" ? block.data.titleBn : block.data.title}</p>
          <p className="mt-1 text-caption text-fg-secondary">{locale === "bn" ? block.data.actionBn : block.data.action}</p>
        </div>
      );
    case "done": {
      const hits: AskRagHit[] = block.data.ragHits ?? [];
      return (
        <div className="space-y-2">
          <p className="flex items-center gap-2 text-caption text-fg-tertiary">
            <CheckCircle2 size={13} className="text-[var(--risk-low)]" />
            {typeof block.data.confidence === "number" ? `Best source match ${Math.round(block.data.confidence * 100)}% (similarity of the top cited passage)` : "No document passage was cited"}
          </p>
          {hits.length > 0 && (
            <details className="surface-2 px-3 py-2" data-testid="ask-retrieval">
              <summary className="cursor-pointer text-caption text-fg-secondary select-none">Retrieval details · {hits.length} passages</summary>
              <div className="mt-2 overflow-x-auto">
                <table className="data-table">
                  <thead><tr><th>Passage</th><th className="num">Hybrid</th><th className="num">Vector</th><th className="num">BM25</th></tr></thead>
                  <tbody>
                    {hits.map((h) => (
                      <tr key={h.id}>
                        <td className="max-w-[320px]">
                          <span className="block text-fg-primary truncate">{h.title}</span>
                          <span className="block text-[11px] text-fg-tertiary">{h.department} · {h.category}</span>
                        </td>
                        <td className="num">{h.hybridScore.toFixed(3)}</td>
                        <td className="num">{h.denseScore.toFixed(3)}</td>
                        <td className="num">{h.bm25Score.toFixed(3)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </details>
          )}
        </div>
      );
    }
  }
}
