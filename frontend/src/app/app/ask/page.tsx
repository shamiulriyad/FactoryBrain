"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import {
  ArrowUp,
  BookOpenText,
  CheckCircle2,
  Copy,
  FileText,
  History,
  Languages,
  Loader2,
  MessageSquarePlus,
  MessageSquareText,
  Recycle,
  RefreshCw,
  ShieldCheck,
  Sparkles,
  Trash2,
} from "lucide-react";
import { useT } from "@/lib/useT";
import { Button } from "@/components/ui/Button";
import { Callout } from "@/components/ui/Callout";
import { Drawer } from "@/components/ui/Drawer";
import { Skeleton } from "@/components/ui/Skeleton";
import { cn } from "@/lib/cn";
import { apiJson, errorMessage, isApiError } from "@/lib/api";
import { useResource } from "@/lib/useResource";
import { askModeLabel, resolveAskMode, shouldShowConfidence, type AskMode } from "@/lib/askMode";
import type {
  AskConversationDetail,
  AskConversationSummary,
  AskMessage,
  AskRequest,
  RagServiceStatus,
} from "@/services/contracts";
import type { AskAnswer } from "@/services/types";

const MAX_QUERY = 2000;
/** While the RAG service is not ready: how often to re-check, and for how long. */
const RAG_WAKE_POLL_MS = 10_000;
const RAG_WAKE_MAX_MS = 3 * 60_000;

const SUGGESTIONS = [
  "What is the AQL inspection procedure?",
  "How is fabric inspection done?",
  "What is the 4-point system for fabric defects?",
  "Metal detection standard ki?",
];

/** A message as shown: saved ones from the API, plus the one being asked right now. */
type MessageView = AskMessage & {
  pending?: boolean;
  error?: { title: string; body: string };
  notice?: string | null;
};

type RagState = "checking" | "ready" | "waking" | "down";

export default function AskPage() {
  const { t, locale } = useT();
  const router = useRouter();
  const params = useSearchParams();
  const activeId = params?.get("c") ?? null;

  const [query, setQuery] = useState("");
  const [messages, setMessages] = useState<MessageView[]>([]);
  const [chatLoading, setChatLoading] = useState(false);
  const [chatMissing, setChatMissing] = useState(false);
  const [historyOpen, setHistoryOpen] = useState(false);
  const scrollerRef = useRef<HTMLDivElement | null>(null);
  const inputRef = useRef<HTMLTextAreaElement | null>(null);
  // Set when this page itself created the chat in the URL, so it isn't re-fetched.
  const createdHere = useRef<string | null>(null);

  const chats = useResource<AskConversationSummary[]>("/api/ask/conversations");

  // A hosted RAG service may be asleep (free hosting stops it after idle time); every
  // status read wakes it. Re-check every 10 s until it is ready, for at most 3 minutes.
  const [waitingForRag, setWaitingForRag] = useState(true);
  const rag = useResource<RagServiceStatus>("/api/ask/status", { pollMs: waitingForRag ? RAG_WAKE_POLL_MS : undefined });
  const ragUp = rag.data ? rag.data.reachable && rag.data.ready && rag.data.qdrant === "healthy" : null;
  useEffect(() => {
    if (ragUp === true) { setWaitingForRag(false); return; }
    if (!waitingForRag) return;
    const stop = setTimeout(() => setWaitingForRag(false), RAG_WAKE_MAX_MS);
    return () => clearTimeout(stop);
  }, [ragUp, waitingForRag]);
  const ragState: RagState = ragUp === true ? "ready" : ragUp === false ? (waitingForRag ? "waking" : "down") : rag.error && !waitingForRag ? "down" : "checking";

  // Prefill from the command palette (?q=…).
  useEffect(() => {
    const q = params?.get("q");
    if (q) setQuery(q.slice(0, MAX_QUERY));
  }, [params]);

  // Open the chat named in the URL (?c=…): survives reload and the browser's back button.
  useEffect(() => {
    setChatMissing(false);
    if (!activeId) { setMessages([]); return; }
    if (createdHere.current === activeId) { createdHere.current = null; return; }
    let cancelled = false;
    setChatLoading(true);
    apiJson<AskConversationDetail>(`/api/ask/conversations/${encodeURIComponent(activeId)}`)
      .then((d) => { if (!cancelled) setMessages(d.messages); })
      .catch(() => { if (!cancelled) { setMessages([]); setChatMissing(true); } })
      .finally(() => { if (!cancelled) setChatLoading(false); });
    return () => { cancelled = true; };
  }, [activeId]);

  useEffect(() => {
    scrollerRef.current?.scrollTo({ top: scrollerRef.current.scrollHeight, behavior: "smooth" });
  }, [messages]);

  const busy = messages.some((m) => m.pending);

  const ask = useCallback(async (question: string, opts: { fresh?: boolean } = {}) => {
    const q = question.trim().slice(0, MAX_QUERY);
    if (!q || busy) return;
    setQuery("");
    const tempId = `pending-${Date.now()}`;
    setMessages((cur) => [...cur.filter((m) => !m.error), {
      id: tempId, question: q, answer: "", mode: "live", confidence: 0, citations: [], cached: false,
      createdAt: new Date().toISOString(), pending: true,
    }]);
    const body: AskRequest = { query: q, ...(activeId ? { conversationId: activeId } : {}), ...(opts.fresh ? { fresh: true } : {}) };
    try {
      const json = await apiJson<AskAnswer>("/api/ask", { method: "POST", body, timeoutMs: 120_000 });
      const answered: MessageView = {
        id: json.messageId ?? tempId,
        question: q,
        answer: json.finding,
        mode: resolveAskMode(json),
        confidence: json.confidence,
        citations: json.citations ?? [],
        cached: Boolean(json.cached),
        cachedQuestion: json.cachedQuestion ?? null,
        createdAt: json.createdAt,
        notice: json.warning ?? null,
      };
      setMessages((cur) => cur.map((m) => (m.id === tempId ? answered : m)));
      if (json.conversationId && json.conversationId !== activeId) {
        createdHere.current = json.conversationId;
        router.replace(`/app/ask?c=${json.conversationId}`, { scroll: false });
      }
      void chats.reload();
    } catch (e) {
      const error =
        isApiError(e) && e.kind === "rate_limited" ? { title: "Too many questions", body: e.message }
        : isApiError(e) && e.kind === "validation" ? { title: "Question not accepted", body: e.message }
        : isApiError(e) && (e.kind === "unavailable" || e.kind === "server")
          ? { title: "The RAG service is unavailable", body: `${e.message} If it was asleep it is waking up now; try again in a minute.` }
        : { title: "No answer", body: errorMessage(e) };
      setMessages((cur) => cur.map((m) => (m.id === tempId ? { ...m, pending: false, error } : m)));
    }
  }, [activeId, busy, chats, router]);

  function newChat() {
    setHistoryOpen(false);
    setMessages([]);
    router.push("/app/ask");
    setTimeout(() => inputRef.current?.focus(), 0);
  }

  function openChat(id: string) {
    setHistoryOpen(false);
    if (id !== activeId) router.push(`/app/ask?c=${id}`);
  }

  async function deleteChat(c: AskConversationSummary) {
    if (!window.confirm(`Delete the chat "${c.title}"? This can't be undone.`)) return;
    try {
      await apiJson<void>(`/api/ask/conversations/${c.id}`, { method: "DELETE" });
      if (c.id === activeId) newChat();
      void chats.reload();
    } catch (e) {
      window.alert(`The chat could not be deleted: ${errorMessage(e)}`);
    }
  }

  const history = (
    <HistoryList
      chats={chats.data}
      loading={chats.loading && !chats.data}
      error={chats.error ? errorMessage(chats.error) : null}
      activeId={activeId}
      locale={locale}
      onNew={newChat}
      onOpen={openChat}
      onDelete={(c) => void deleteChat(c)}
    />
  );

  return (
    // Fill the screen below the 3.5rem top bar (the page wrapper has no fixed height, so
    // h-full can't be used): the composer stays at the bottom and only the chat scrolls.
    <div className="flex h-[calc(100dvh-3.5rem)] min-h-0 overflow-hidden" data-tour="ask">
      <aside className="hidden md:flex w-72 shrink-0 flex-col border-r border-border-subtle bg-[var(--bg-surface)]" aria-label="Chat history">
        {history}
      </aside>
      <Drawer open={historyOpen} onClose={() => setHistoryOpen(false)} title="Chat history" width="w-[20rem]">
        {history}
      </Drawer>

      <section className="flex-1 min-w-0 min-h-0 flex flex-col">
        {/* Header */}
        <header className="border-b border-border-subtle bg-[var(--bg-surface)]">
          <div className="max-w-3xl mx-auto w-full px-4 md:px-6 py-3 flex items-center justify-between gap-3">
            <div className="min-w-0 flex items-center gap-3">
              <span className="hidden sm:flex size-9 shrink-0 items-center justify-center rounded-xl bg-[var(--accent-soft)] text-[var(--accent)]" aria-hidden>
                <MessageSquareText size={18} />
              </span>
              <div className="min-w-0">
                <h1 className="text-[18px] leading-[26px] font-semibold tracking-tight">{t("ask.title")}</h1>
                <p className="text-caption text-fg-tertiary truncate" data-testid="ask-rag-status">
                  Answers from your factory documents
                  {rag.data ? <> · <span className="text-fg-secondary">{modelLabel(rag.data)}</span></> : null}
                </p>
              </div>
            </div>
            <div className="flex items-center gap-2 shrink-0">
              <RagPill state={ragState} />
              <Button variant="secondary" size="sm" className="md:hidden" onClick={() => setHistoryOpen(true)} aria-label="Open chat history">
                <History size={14} /> History
              </Button>
              <Button variant="secondary" size="sm" onClick={newChat} aria-label="Start a new chat">
                <MessageSquarePlus size={14} /> <span className="hidden sm:inline">New chat</span>
              </Button>
            </div>
          </div>
        </header>

        {/* Conversation */}
        <div ref={scrollerRef} className="flex-1 overflow-y-auto" aria-live="polite">
          <div className="max-w-3xl mx-auto w-full px-4 md:px-6 py-5 pb-8 space-y-5">
            {ragState === "waking" && (
              <Callout tone="warning" title="The RAG service is starting…" testId="ask-rag-waking">
                It was asleep (free hosting stops it after 15 idle minutes) and needs about a minute to wake. This page checks
                every 10 seconds; you can type your question meanwhile.
              </Callout>
            )}
            {ragState === "down" && (
              <Callout tone="danger" title="The RAG service is not available" testId="ask-rag-down"
                action={<button type="button" className="underline" onClick={() => { setWaitingForRag(true); void rag.reload(); }}>Check again</button>}>
                {rag.data?.reachable ? `The service is running but not ready (Qdrant: ${rag.data.qdrant}).` : "The API cannot reach the Python RAG service."}{" "}
                Check that the RAG service and Qdrant are running.
              </Callout>
            )}

            {chatMissing && (
              <Callout tone="warning" title="Chat not found" action={<button type="button" className="underline" onClick={newChat}>New chat</button>}>
                It may have been deleted. Start a new chat or pick one from the history.
              </Callout>
            )}

            {chatLoading ? (
              <div className="space-y-3" role="status" aria-label="Loading the chat">
                <Skeleton className="h-10 w-1/2 ml-auto" />
                <Skeleton className="h-24 w-full" />
              </div>
            ) : messages.length === 0 && !chatMissing ? (
              <Welcome onPick={(q) => void ask(q)} disabled={busy} />
            ) : (
              messages.map((m) => (
                <Turn key={m.id} m={m} locale={locale} onRetry={() => void ask(m.question)} onFresh={() => void ask(m.question, { fresh: true })} busy={busy} />
              ))
            )}
          </div>
        </div>

        {/* Composer */}
        <div className="border-t border-border-subtle bg-[var(--bg-surface)] px-4 md:px-6 pt-3 pb-4">
          <div className="max-w-3xl mx-auto">
            <form
              onSubmit={(e) => { e.preventDefault(); void ask(query); }}
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
                  if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); void ask(query); }
                }}
                placeholder={activeId ? "Ask a follow-up question…" : locale === "bn" ? t("ask.placeholderBn") : t("ask.placeholder")}
                data-testid="ask-input"
                rows={1}
                className="flex-1 resize-none bg-transparent border-0 outline-none text-body text-fg-primary placeholder:text-fg-tertiary max-h-40 py-2"
              />
              <Button variant="primary" size="md" type="submit" disabled={busy || !query.trim()} aria-label={t("ask.sendLabel")}>
                {busy ? <Loader2 size={15} className="animate-spin" /> : <ArrowUp size={15} />}
              </Button>
            </form>
            <p className="mt-1.5 text-caption text-fg-tertiary flex justify-between gap-3">
              <span>English, বাংলা or Banglish · Enter to send, Shift + Enter for a new line</span>
              {query.length > MAX_QUERY * 0.8 && <span>{query.length}/{MAX_QUERY}</span>}
            </p>
          </div>
        </div>
      </section>
    </div>
  );
}

function modelLabel(s: RagServiceStatus): string {
  const provider = s.llmProvider === "openai" ? "OpenAI" : s.llmProvider === "google" ? "Gemini" : s.llmProvider;
  return provider ? `${provider} ${s.llmModel}` : s.llmModel;
}

function RagPill({ state }: { state: RagState }) {
  const look = {
    ready: { label: "Ready", cls: "bg-[var(--risk-low-soft)] border-[var(--risk-low-border)] text-[var(--risk-low)]" },
    waking: { label: "Waking up", cls: "bg-[var(--risk-medium-soft)] border-[var(--risk-medium-border)] text-[var(--risk-medium)]" },
    down: { label: "Unavailable", cls: "bg-[var(--risk-high-soft)] border-[var(--risk-high-border)] text-[var(--risk-high)]" },
    checking: { label: "Checking…", cls: "bg-surface-2 border-border-subtle text-fg-secondary" },
  }[state];
  return (
    <span className={cn("hidden sm:inline-flex items-center gap-1.5 h-7 px-2.5 rounded-full border text-caption font-medium", look.cls)} data-testid="ask-rag-pill" data-state={state}>
      <span className={cn("size-1.5 rounded-full bg-current", state === "waking" && "animate-pulse")} aria-hidden />
      {look.label}
    </span>
  );
}

function HistoryList({
  chats, loading, error, activeId, locale, onNew, onOpen, onDelete,
}: {
  chats: AskConversationSummary[] | null;
  loading: boolean;
  error: string | null;
  activeId: string | null;
  locale: "en" | "bn";
  onNew: () => void;
  onOpen: (id: string) => void;
  onDelete: (c: AskConversationSummary) => void;
}) {
  return (
    <div className="flex flex-col min-h-0 h-full">
      <div className="p-3 border-b border-border-subtle">
        <Button variant="primary" size="sm" className="w-full justify-center" onClick={onNew}>
          <MessageSquarePlus size={14} /> New chat
        </Button>
      </div>
      <p className="px-4 pt-3 pb-1 eyebrow flex items-center gap-1.5"><History size={12} /> Your chats</p>
      <div className="flex-1 overflow-y-auto px-2 pb-3" data-testid="ask-history">
        {loading ? (
          <div className="space-y-2 p-2">{[0, 1, 2].map((i) => <Skeleton key={i} className="h-11 w-full" />)}</div>
        ) : error ? (
          <p className="px-2 py-3 text-caption text-[var(--risk-high)]">Could not load your chats: {error}</p>
        ) : !chats?.length ? (
          <p className="px-2 py-3 text-caption text-fg-tertiary">
            No chats yet. Your questions and answers are saved here, so you can open them again later on any device.
          </p>
        ) : (
          <ul className="space-y-0.5">
            {chats.map((c) => (
              <li key={c.id} className="group relative">
                <button
                  type="button"
                  onClick={() => onOpen(c.id)}
                  aria-current={c.id === activeId ? "page" : undefined}
                  className={cn(
                    "w-full text-left rounded-[var(--radius-control)] px-3 py-2 pr-9 transition-colors",
                    c.id === activeId ? "bg-[var(--accent-soft)] text-fg-primary" : "hover:bg-surface-2 text-fg-secondary",
                  )}
                >
                  <span className="block text-body line-clamp-1">{c.title}</span>
                  <span className="block text-[11px] text-fg-tertiary mt-0.5">
                    {relativeTime(c.updatedAt, locale)} · {c.messageCount} {c.messageCount === 1 ? "question" : "questions"}
                  </span>
                </button>
                <button
                  type="button"
                  onClick={() => onDelete(c)}
                  aria-label={`Delete the chat "${c.title}"`}
                  className="absolute right-1.5 top-1/2 -translate-y-1/2 p-1.5 rounded-md text-fg-tertiary opacity-0 group-hover:opacity-100 focus:opacity-100 hover:text-[var(--risk-high)] hover:bg-surface-2"
                >
                  <Trash2 size={13} />
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

function Welcome({ onPick, disabled }: { onPick: (q: string) => void; disabled: boolean }) {
  return (
    <div className="pt-4 md:pt-10">
      <div className="text-center">
        <span className="inline-flex size-12 items-center justify-center rounded-2xl bg-[var(--accent-soft)] text-[var(--accent)]" aria-hidden>
          <Sparkles size={22} />
        </span>
        <h2 className="mt-3 text-[20px] leading-7 font-semibold text-fg-primary">Ask about your factory documents</h2>
        <p className="mt-1 text-body text-fg-secondary">Quality manuals, SOPs and other PDFs your team has added. Every answer shows the pages it comes from.</p>
      </div>

      <p className="mt-8 eyebrow">Try a question</p>
      <div className="mt-2 grid gap-2 sm:grid-cols-2">
        {SUGGESTIONS.map((s) => (
          <button
            key={s}
            type="button"
            disabled={disabled}
            onClick={() => onPick(s)}
            className="text-left rounded-[var(--radius-card)] border border-border-subtle bg-[var(--bg-surface)] px-3.5 py-3 text-body text-fg-primary hover:border-[var(--accent)] hover:bg-[var(--accent-soft)] transition-colors disabled:opacity-60"
          >
            {s}
          </button>
        ))}
      </div>

      <ul className="mt-8 grid gap-3 sm:grid-cols-2 text-caption text-fg-secondary">
        <Feature icon={<BookOpenText size={14} />} title="Only from your documents">Answers use the indexed PDFs, with page references. If they don&apos;t cover it, it says so.</Feature>
        <Feature icon={<Languages size={14} />} title="Your language">Ask in English, বাংলা or Banglish; the answer comes in the same language.</Feature>
        <Feature icon={<Recycle size={14} />} title="Repeated questions are instant">The same question asked again reuses the earlier answer, without AI cost. &quot;Ask fresh&quot; gets a new one.</Feature>
        <Feature icon={<ShieldCheck size={14} />} title="Saved to your account">Your chats appear under &quot;Your chats&quot;; only you can see them.</Feature>
      </ul>
    </div>
  );
}

function Feature({ icon, title, children }: { icon: React.ReactNode; title: string; children: React.ReactNode }) {
  return (
    <li className="flex gap-2.5 rounded-[var(--radius-card)] bg-surface-2 px-3 py-2.5">
      <span className="mt-0.5 text-[var(--accent)]" aria-hidden>{icon}</span>
      <span><span className="block font-medium text-fg-primary">{title}</span>{children}</span>
    </li>
  );
}

function Turn({ m, locale, onRetry, onFresh, busy }: {
  m: MessageView; locale: "en" | "bn"; onRetry: () => void; onFresh: () => void; busy: boolean;
}) {
  const [copied, setCopied] = useState(false);
  const mode = m.mode as AskMode;
  const sources = m.citations ?? [];

  function copy() {
    const text = [m.answer, sources.length ? "Sources:\n" + sources.map((c) => `- ${c.title}`).join("\n") : ""].filter(Boolean).join("\n\n");
    if (!navigator.clipboard) return;
    navigator.clipboard.writeText(text).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1600); }, () => undefined);
  }

  return (
    <div className="space-y-2.5" data-testid="ask-turn">
      <div className="flex justify-end">
        <div className="max-w-[85%] rounded-2xl rounded-br-md bg-[var(--accent-soft)] border border-[var(--accent-border)] px-4 py-2.5 text-body text-fg-primary whitespace-pre-wrap">
          {m.question}
        </div>
      </div>

      {m.pending ? (
        <div className="surface p-4 space-y-3" role="status">
          <div className="flex items-center gap-2 text-caption text-fg-tertiary">
            <Loader2 size={13} className="animate-spin" /> Searching your documents and preparing an answer…
          </div>
          <Skeleton className="h-3 w-[88%]" />
          <Skeleton className="h-3 w-[72%]" />
          <Skeleton className="h-3 w-[56%]" />
        </div>
      ) : m.error ? (
        <Callout tone="danger" title={m.error.title} action={<Button size="sm" onClick={onRetry} disabled={busy}>Retry</Button>}>
          {m.error.body}
        </Callout>
      ) : (
        <article className="surface p-4" data-testid="ask-finding">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p className="eyebrow flex items-center gap-1.5"><Sparkles size={12} /> BunonBrain</p>
            <div className="flex items-center gap-1.5">
              {m.cached && (
                <span
                  className="inline-flex items-center gap-1 h-6 px-2 rounded-full border text-[11px] font-medium bg-[var(--accent-soft)] border-[var(--accent-border)] text-[var(--accent)]"
                  title={m.cachedQuestion ? `Same as the earlier question: "${m.cachedQuestion}"` : "Reused from an earlier, equivalent question"}
                  data-testid="ask-cached"
                >
                  <Recycle size={11} /> Reused answer · no AI cost
                </span>
              )}
              <SourceChip source={mode} />
            </div>
          </div>

          <p className="mt-2 text-body text-fg-primary leading-6 whitespace-pre-wrap">{m.answer}</p>

          {m.cached && m.cachedQuestion && (
            <p className="mt-2 text-caption text-fg-tertiary">Answered before as: &ldquo;{m.cachedQuestion}&rdquo;</p>
          )}

          {sources.length > 0 && (
            <details className="mt-3 rounded-[var(--radius-control)] bg-surface-2 px-3 py-2" data-testid="ask-citations">
              <summary className="cursor-pointer select-none text-caption font-medium text-fg-secondary flex items-center gap-1.5">
                <FileText size={12} /> Sources ({sources.length})
                {shouldShowConfidence(mode) && <span className="font-normal text-fg-tertiary">· best match {Math.round(m.confidence * 100)}%</span>}
              </summary>
              <ol className="mt-2 space-y-2.5">
                {sources.map((c, i) => (
                  <li key={c.id} className="flex gap-2.5">
                    <span className="mt-0.5 size-5 shrink-0 rounded-full bg-[var(--bg-surface)] border border-border-subtle text-[11px] font-semibold flex items-center justify-center text-fg-secondary">{i + 1}</span>
                    <div className="min-w-0">
                      <p className="text-caption font-medium text-fg-primary">{c.title}</p>
                      <p className="mt-0.5 text-caption text-fg-secondary leading-5">{c.snippet}</p>
                      <p className="mt-0.5 text-[11px] text-fg-tertiary">match {Math.round((c.confidence ?? 0) * 100)}%</p>
                    </div>
                  </li>
                ))}
              </ol>
            </details>
          )}

          {m.notice && <Callout tone="warning" className="mt-3" testId="ask-notice" title="Note">{m.notice}</Callout>}

          <div className="mt-3 flex flex-wrap items-center gap-2 text-caption text-fg-tertiary">
            <Button variant="ghost" size="sm" onClick={copy}>
              {copied ? <CheckCircle2 size={13} className="text-[var(--risk-low)]" /> : <Copy size={13} />}
              {copied ? "Copied" : "Copy"}
            </Button>
            {m.cached && (
              <Button variant="ghost" size="sm" onClick={onFresh} disabled={busy} title="Ask the AI again instead of reusing the earlier answer">
                <RefreshCw size={13} /> Ask fresh
              </Button>
            )}
            <span className="ml-auto">{new Date(m.createdAt).toLocaleString(locale === "bn" ? "bn-BD" : "en-GB", { dateStyle: "medium", timeStyle: "short" })}</span>
          </div>
        </article>
      )}
    </div>
  );
}

function SourceChip({ source }: { source: AskMode }) {
  const { label, title, isLive } = askModeLabel(source);
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 h-6 px-2 rounded-full border text-[11px] font-medium",
        isLive
          ? "bg-[var(--risk-low-soft)] border-[var(--risk-low-border)] text-[var(--risk-low)]"
          : source === "no_source"
          ? "bg-[var(--risk-medium-soft)] border-[var(--risk-medium-border)] text-[var(--risk-medium)]"
          : "bg-surface-2 border-border-subtle text-fg-secondary",
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

function relativeTime(iso: string, locale: "en" | "bn"): string {
  const then = new Date(iso).getTime();
  const minutes = Math.round((Date.now() - then) / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.round(hours / 24);
  if (days < 7) return `${days} d ago`;
  return new Date(iso).toLocaleDateString(locale === "bn" ? "bn-BD" : "en-GB", { day: "numeric", month: "short" });
}
