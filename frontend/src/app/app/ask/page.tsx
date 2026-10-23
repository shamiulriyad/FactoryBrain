"use client";

import { useState, useRef, useEffect, useMemo } from "react";
import { motion, AnimatePresence } from "framer-motion";
import {
  ArrowUp,
  Loader2,
  Database,
  FileText,
  Lightbulb,
  AlertTriangle,
  CheckCircle2,
  Copy,
  RefreshCw,
  BookmarkPlus,
  Sparkles,
  X,
  Clock,
  RotateCcw,
  UserCircle2
} from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";
import { useT } from "@/lib/useT";
import { askService } from "@/services/ask.service";
import { evidenceService } from "@/services/evidence.service";
import { riskService } from "@/services/risk.service";
import { insightService } from "@/services/insight.service";
import { activityService } from "@/services/activity.service";
import { agentService } from "@/services/agent.service";
import { useBusinessStore } from "@/store/business.store";
import { Panel } from "@/components/ui/Panel";
import { Button } from "@/components/ui/Button";
import { RiskPill } from "@/components/ui/StatusPill";
import { EvidenceBlock } from "@/components/evidence/EvidenceBlock";
import { Tooltip } from "@/components/ui/Tooltip";
import { motionTokens } from "@/lib/motion";
import { cn } from "@/lib/cn";
import { Skeleton } from "@/components/ui/Skeleton";
import { AgentAvatar, AGENT_PALETTE, AGENT_GLYPH, type AgentGlyph } from "@/components/agents/AgentAvatar";
import type { StreamChunk, AskAnswer, RiskTier } from "@/services/types";
import { useAskStore, type AskRecord } from "@/store/ask.store";

type Block = {
  type: "analyzed" | "finding" | "factors" | "evidence" | "recommendation" | "done";
  data: any;
};

export default function AskPage() {
  const { t, locale } = useT();
  const router = useRouter();
  const [query, setQuery] = useState("");
  const [streaming, setStreaming] = useState(false);
  const [blocks, setBlocks] = useState<Block[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [source, setSource] = useState<"demo" | "live">("demo");
  const [notice, setNotice] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [saved, setSaved] = useState(false);
  const [agentNote, setAgentNote] = useState<{ agentId: string; agentName: string } | null>(null);
  const scrollerRef = useRef<HTMLDivElement | null>(null);
  const currentIdRef = useRef<string | null>(null);
  const policy = riskService.read();
  const history = useAskStore((s) => s.history);
  const pushHistory = useAskStore((s) => s.push);
  const selectedAgentId = useBusinessStore((s) => s.selectedAgentId);
  const setSelectedAgent = useBusinessStore((s) => s.setSelectedAgent);
  const agents = useMemo(() => agentService.list(), []);

  const suggestions = askService.suggestions();
  const half = Math.ceil(suggestions.length / 2);
  const enSuggestions = suggestions.slice(0, half);
  const bnSuggestions = suggestions.slice(half);
  const selectedAgent = selectedAgentId ? agentService.get(selectedAgentId) ?? null : null;
  const selectedPalette = selectedAgent ? AGENT_PALETTE[selectedAgent.glyph as AgentGlyph] : null;
  const search = useSearchParams();

  // Prefill from Overview energy-duty / JUDGES deep links (?q=...).
  useEffect(() => {
    const q = search?.get("q");
    if (q && !query) setQuery(q);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search]);

  useEffect(() => {
    scrollerRef.current?.scrollTo({ top: scrollerRef.current.scrollHeight, behavior: "smooth" });
  }, [blocks.length]);

  async function submit(q?: string) {
    const finalQ = (q ?? query).trim();
    if (!finalQ || streaming) return;
    setQuery(finalQ);
    setBlocks([]);
    setError(null);
    setNotice(null);
    setStreaming(true);
    const id = `ask-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
    currentIdRef.current = id;
    try {
      const pack = askService.buildContextPack(finalQ, selectedAgentId);
      const res = await fetch("/api/ask", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ query: finalQ, agentId: selectedAgentId ?? undefined })
      });
      if (!res.ok) throw new Error(`ask failed ${res.status}`);
      const contentType = res.headers.get("content-type") ?? "";
      if (contentType.includes("event-stream")) {
        const reader = res.body!.getReader();
        const decoder = new TextDecoder();
        let buf = "";
        // eslint-disable-next-line no-constant-condition
        while (true) {
          const { value, done } = await reader.read();
          if (done) break;
          buf += decoder.decode(value, { stream: true });
          const events = buf.split("\n\n");
          buf = events.pop() ?? "";
          for (const ev of events) {
            if (ev.startsWith("event: end")) continue;
            if (ev.startsWith("event: notice")) {
              const dataLine = ev.split("\n").find((l) => l.startsWith("data: "));
              if (dataLine) {
                try {
                  const n = JSON.parse(dataLine.slice(6));
                  setNotice(n.warning ?? "Live model unavailable, showing demo answer.");
                } catch {/* ignore */}
              }
              continue;
            }
            if (ev.startsWith("event: agent")) {
              const dataLine = ev.split("\n").find((l) => l.startsWith("data: "));
              if (dataLine) {
                try {
                  const n = JSON.parse(dataLine.slice(6));
                  setAgentNote({ agentId: n.agentId, agentName: n.agentName });
                } catch {/* ignore */}
              }
              continue;
            }
            const dataLine = ev.split("\n").find((l) => l.startsWith("data: "));
            if (!dataLine) continue;
            try {
              const chunk: StreamChunk = JSON.parse(dataLine.slice(6));
              applyChunk(chunk);
            } catch {
              // ignore
            }
          }
        }
        // The mock SSE always means demo source.
        setSource("demo");
      } else {
        // JSON — live slice response shape (AskAnswer)
        const json = (await res.json()) as AskAnswer;
        setSource("live");
        setBlocks([
          { type: "analyzed", data: json.analyzed ?? [] },
          { type: "finding", data: { en: json.finding, bn: json.findingBn || json.finding } },
          ...(json.factors?.length ? [{ type: "factors" as const, data: json.factors }] : []),
          ...(json.evidence?.length ? [{ type: "evidence" as const, data: json.evidence }] : []),
          { type: "recommendation", data: json.recommendation },
          { type: "done", data: { confidence: json.confidence } }
        ]);
      }
    } catch (err) {
      setError(String(err));
    } finally {
      setStreaming(false);
      // Persist to history.
      setBlocks((latest) => {
        const record: AskRecord = {
          id,
          query: finalQ,
          locale,
          source,
          blocks: latest,
          isoDate: new Date().toISOString()
        };
        pushHistory(record);
        return latest;
      });
      currentIdRef.current = null;
    }
  }

  function applyChunk(chunk: StreamChunk) {
    if (chunk.type === "block") {
      const payload = chunk.payload;
      if (payload.kind === "analyzed") {
        setBlocks((arr) => [...arr, { type: "analyzed", data: payload.items }]);
      } else if (payload.kind === "finding") {
        setBlocks((arr) => [...arr, { type: "finding", data: { en: payload.en, bn: payload.bn } }]);
      }
    } else if (chunk.type === "factor") {
      setBlocks((arr) => [...arr, { type: "factors", data: chunk.payload }]);
    } else if (chunk.type === "evidence") {
      setBlocks((arr) => [...arr, { type: "evidence", data: chunk.payload }]);
    } else if (chunk.type === "recommendation") {
      setBlocks((arr) => [...arr, { type: "recommendation", data: chunk.payload }]);
    } else if (chunk.type === "done") {
      setBlocks((arr) => [...arr, { type: "done", data: chunk.payload }]);
    }
  }

  function clear() {
    setBlocks([]);
    setQuery("");
    setError(null);
    setNotice(null);
    setAgentNote(null);
  }

  function copyAnswer() {
    const text = blocks
      .filter((b) => b.type === "finding" || b.type === "recommendation")
      .map((b) => {
        if (b.type === "finding") return locale === "bn" ? b.data.bn : b.data.en;
        if (b.type === "recommendation") {
          return `[${b.data.riskTier}] ${locale === "bn" ? b.data.titleBn : b.data.title} — ${locale === "bn" ? b.data.actionBn : b.data.action}`;
        }
        return "";
      })
      .join("\n\n");
    if (!text) return;
    if (typeof navigator !== "undefined" && navigator.clipboard) {
      navigator.clipboard.writeText(text).then(() => {
        setCopied(true);
        setTimeout(() => setCopied(false), 1600);
      });
    }
  }

  function saveAsInsight() {
    if (!blocks.length || !query) return;
    const finding = blocks.find((b) => b.type === "finding");
    const rec = blocks.find((b) => b.type === "recommendation");
    if (!rec) return;
    const id = `ins-ask-${Date.now().toString(36)}`;
    insightService.upsertCustom?.({
      id,
      agentId: selectedAgentId ?? "manager-agent",
      agentLabel: selectedAgentId ? (agentService.get(selectedAgentId)?.name ?? "Manager Orchestrator") : "Manager Orchestrator",
      title: query,
      titleBn: query,
      finding: finding?.data?.en ?? "",
      findingBn: finding?.data?.bn ?? "",
      recommendation: rec.data,
      confidence: 0.7
    });
    activityService.push({
      actor: "user",
      actorLabel: "You",
      verb: "saved an answer as an insight",
      verbBn: "উত্তর অন্তর্দৃষ্টি হিসেবে সংরক্ষণ",
      target: query,
      targetBn: query,
      isoDate: new Date().toISOString()
    });
    setSaved(true);
    setTimeout(() => setSaved(false), 1800);
  }

  function loadFromHistory(rec: AskRecord) {
    setQuery(rec.query);
    setBlocks(rec.blocks);
    setSource(rec.source);
    setError(null);
    setNotice(null);
  }

  return (
    <div className="flex flex-col h-full" data-tour="ask">
      <div className="px-6 md:px-8 py-5 max-w-3xl mx-auto w-full flex items-start justify-between gap-4">
        <div>
          <h1 className="text-display font-semibold tracking-tight">{t("ask.title")}</h1>
          {selectedAgent && (
            <p
              className="mt-1 text-caption inline-flex items-center gap-1.5"
              style={{ color: selectedPalette?.strong }}
            >
              <UserCircle2 size={12} />
              Asking {locale === "bn" ? selectedAgent.nameBn : selectedAgent.name}
            </p>
          )}
        </div>
        <SourceChip source={source} />
      </div>

      {/* Agent selector pills */}
      <div className="px-6 md:px-8 max-w-3xl mx-auto w-full">
        <div className="flex items-center gap-2 overflow-x-auto pb-2 -mx-1 px-1">
          <AgentPill
            active={selectedAgentId === null}
            onClick={() => setSelectedAgent(null)}
            label="None"
            sublabel="Brain decides"
          />
          {agents.map((a) => {
            const palette = AGENT_PALETTE[a.glyph as AgentGlyph];
            return (
              <AgentPill
                key={a.id}
                active={selectedAgentId === a.id}
                onClick={() => setSelectedAgent(a.id)}
                label={locale === "bn" ? a.nameBn : a.name}
                sublabel={palette.label}
                agentId={a.id}
                color={palette.strong}
              />
            );
          })}
        </div>
      </div>

      <div ref={scrollerRef} className="flex-1 overflow-y-auto px-6 md:px-8 pb-40 max-w-3xl mx-auto w-full">
        {blocks.length === 0 && !streaming && (
          <div className="mt-6">
            <p className="text-caption text-fg-tertiary">{t("ask.suggestions")}</p>
            <div className="mt-3 flex flex-wrap gap-2">
              {enSuggestions.map((s) => (
                <SuggestionChip key={s} onClick={() => submit(s)}>{s}</SuggestionChip>
              ))}
            </div>
            <p className="mt-6 text-caption text-fg-tertiary">বাংলায়</p>
            <div className="mt-3 flex flex-wrap gap-2">
              {bnSuggestions.map((s) => (
                <SuggestionChip key={s} onClick={() => submit(s)}>{s}</SuggestionChip>
              ))}
            </div>

            {history.length > 0 && (
              <section className="mt-8">
                <p className="text-caption text-fg-tertiary inline-flex items-center gap-1.5">
                  <Clock size={12} /> {t("ask.history")}
                </p>
                <ul className="mt-2 space-y-1.5">
                  {history.slice(0, 6).map((r) => (
                    <li key={r.id}>
                      <button
                        type="button"
                        onClick={() => loadFromHistory(r)}
                        className="w-full text-left px-3 py-2 rounded-md border border-border-subtle bg-surface-2 hover:border-border-strong transition-colors"
                      >
                        <p className="text-body text-fg-primary line-clamp-1">{r.query}</p>
                        <p className="text-caption text-fg-tertiary inline-flex items-center gap-1.5 mt-0.5">
                          <span className="mono-pill">{r.source}</span>
                          {new Date(r.isoDate).toLocaleString(locale === "bn" ? "bn-BD" : "en-GB")}
                        </p>
                      </button>
                    </li>
                  ))}
                </ul>
              </section>
            )}
          </div>
        )}

        <div className="mt-4 space-y-4">
          {query && (
            <motion.div
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              className="flex justify-end"
            >
              <div className="max-w-[80%] rounded-md bg-surface-2 border border-border-subtle px-3 py-2 text-body text-fg-primary">
                {query}
              </div>
            </motion.div>
          )}

          {notice && (
            <div className="surface-2 p-2 text-caption text-fg-tertiary inline-flex items-center gap-1.5">
              <Sparkles size={12} /> {notice}
            </div>
          )}

          <AnimatePresence>
            {blocks.map((b, idx) => (
              <motion.div
                key={idx}
                initial={{ opacity: 0, y: 8 }}
                animate={{ opacity: 1, y: 0 }}
                transition={{ duration: motionTokens.duration.base / 1000, ease: motionTokens.ease.out }}
              >
                {renderBlock(b, locale)}
              </motion.div>
            ))}
          </AnimatePresence>

          {streaming && blocks.length > 0 && blocks[blocks.length - 1].type !== "done" && (
            <div className="surface-2 p-4 space-y-2 max-w-prose" aria-live="polite">
              <div className="flex items-center gap-2 text-caption text-fg-tertiary">
                <Loader2 size={12} className="animate-spin" /> {t("ask.streamingThinking")}
              </div>
              <Skeleton className="h-3 w-[80%]" />
              <Skeleton className="h-3 w-[68%]" />
              <Skeleton className="h-3 w-[55%]" />
            </div>
          )}

          {streaming && blocks.length === 0 && (
            <div className="surface-2 p-4 space-y-3 max-w-prose" aria-live="polite">
              <div className="flex items-center gap-2 text-caption text-fg-tertiary">
                <Loader2 size={12} className="animate-spin" /> {t("ask.streamingThinking")}
              </div>
              <div className="space-y-2">
                <Skeleton className="h-3 w-[88%]" />
                <Skeleton className="h-3 w-[72%]" />
                <Skeleton className="h-3 w-[60%]" />
                <Skeleton className="h-3 w-[40%]" />
              </div>
            </div>
          )}

          {error && (
            <div className="surface-2 p-3 text-caption text-risk-high">{error}</div>
          )}

          {agentNote && (
            <div
              className="surface-2 p-2.5 text-caption inline-flex items-center gap-2"
              style={{ borderLeft: `3px solid ${AGENT_PALETTE[AGENT_GLYPH[agentNote.agentId] as AgentGlyph]?.strong ?? "var(--accent)"}` }}
            >
              <Sparkles size={12} style={{ color: AGENT_PALETTE[AGENT_GLYPH[agentNote.agentId] as AgentGlyph]?.strong }} />
              Answer routed via <strong className="font-medium">{agentNote.agentName}</strong>
            </div>
          )}
        </div>

        {blocks.some((b) => b.type === "done") && (
          <div className="mt-4 flex items-center gap-2 flex-wrap">
            <Button variant="secondary" size="sm" onClick={copyAnswer}>
              {copied ? <CheckCircle2 size={12} className="text-risk-low" /> : <Copy size={12} />}
              {copied ? t("ask.copied") : t("ask.copy")}
            </Button>
            <Button
              variant="secondary"
              size="sm"
              onClick={() => submit(query)}
              disabled={streaming || !query}
            >
              <RefreshCw size={12} /> {t("ask.regenerate")}
            </Button>
            <Button variant="secondary" size="sm" onClick={saveAsInsight}>
              <BookmarkPlus size={12} /> {saved ? "Saved" : t("ask.saveToInsights")}
            </Button>
            <Button variant="ghost" size="sm" onClick={clear}>
              <RotateCcw size={12} /> {t("ask.newQuestion")}
            </Button>
          </div>
        )}
      </div>

      <div className="sticky bottom-0 bg-gradient-to-t from-canvas via-canvas/95 to-transparent pt-6 pb-4 px-6 md:px-8">
        <div className="max-w-3xl mx-auto">
          <form
            onSubmit={(e) => { e.preventDefault(); submit(); }}
            className="glass flex items-center gap-2 px-3 py-2"
          >
            <textarea
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && !e.shiftKey) {
                  e.preventDefault();
                  submit();
                }
              }}
              placeholder={locale === "bn" ? t("ask.placeholderBn") : t("ask.placeholder")}
              rows={1}
              className="flex-1 resize-none bg-transparent border-0 outline-none text-body text-fg-primary placeholder:text-fg-tertiary max-h-32"
            />
            <Button variant="primary" size="sm" type="submit" disabled={streaming || !query.trim()}>
              <ArrowUp size={14} />
            </Button>
          </form>
          <p className="mt-2 text-caption text-fg-tertiary text-center">{t("common.banglaHint")}</p>
        </div>
      </div>
    </div>
  );
}

function SourceChip({ source }: { source: "demo" | "live" }) {
  const isLive = source === "live";
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1.5 h-7 px-2 rounded-md border text-caption",
        isLive
          ? "bg-risk-low/10 border-risk-low/30 text-risk-low"
          : "bg-surface-2 border-border-subtle text-fg-tertiary"
      )}
      title={isLive ? "Connected to a live model" : "Deterministic demo answer"}
    >
      <span className={cn("size-1.5 rounded-full", isLive ? "bg-risk-low" : "bg-fg-tertiary")} />
      {isLive ? "Live" : "Demo"}
    </span>
  );
}

function renderBlock(block: Block, locale: "en" | "bn") {
  switch (block.type) {
    case "analyzed":
      return (
        <div className="surface-2 p-3">
          <div className="flex items-center gap-2 text-caption text-fg-tertiary">
            <Database size={12} /> Analyzed
          </div>
          <ul className="mt-2 flex flex-wrap gap-2">
            {block.data.map((it: any, i: number) => (
              <li key={i} className="mono-pill text-fg-secondary border border-border-subtle bg-surface px-2 py-1 rounded-sm">
                {it.domain} · {it.count} records{it.filter ? ` · ${Object.entries(it.filter).map(([k, v]) => `${k}:${v}`).join(", ")}` : ""}
              </li>
            ))}
          </ul>
        </div>
      );
    case "finding":
      return (
        <div className="glass p-4">
          <div className="flex items-center gap-2 text-caption text-fg-tertiary">
            <FileText size={12} /> Finding
          </div>
          <p className="mt-2 text-body text-fg-primary leading-6">
            {locale === "bn" ? block.data.bn : block.data.en}
          </p>
        </div>
      );
    case "factors":
      return (
        <div className="surface p-3">
          <div className="flex items-center gap-2 text-caption text-fg-tertiary">
            <Lightbulb size={12} /> Contributing factors
          </div>
          <ul className="mt-2 space-y-1.5">
            {block.data.map((f: any, i: number) => (
              <li key={i} className="flex items-center justify-between text-body">
                <span className="text-fg-secondary">{locale === "bn" ? f.labelBn : f.label}</span>
                <span className="mono-pill text-fg-primary">{locale === "bn" ? f.magnitudeBn : f.magnitude}</span>
              </li>
            ))}
          </ul>
        </div>
      );
    case "evidence":
      return <EvidenceBlock refs={block.data} />;
    case "recommendation":
      return (
        <div className="glass p-4">
          <div className="flex items-center justify-between gap-2">
            <div className="flex items-center gap-2 text-caption text-fg-tertiary">
              <AlertTriangle size={12} /> Recommended action
            </div>
            <RiskPill tier={block.data.riskTier} label={block.data.riskTier.toUpperCase()} />
          </div>
          <p className="mt-2 text-title text-fg-primary">{locale === "bn" ? block.data.titleBn : block.data.title}</p>
          <p className="mt-1.5 text-caption text-fg-secondary">{locale === "bn" ? block.data.actionBn : block.data.action}</p>
        </div>
      );
    case "done":
      return (
        <div className="flex items-center gap-2 text-caption text-fg-tertiary">
          <CheckCircle2 size={12} className="text-risk-low" /> Confidence {Math.round(block.data.confidence * 100)}%
        </div>
      );
  }
}

function SuggestionChip({ children, onClick }: { children: React.ReactNode; onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "h-8 px-3 rounded-md border border-border-subtle bg-surface-2 text-caption text-fg-secondary",
        "hover:text-fg-primary hover:border-border-strong transition-colors"
      )}
    >
      {children}
    </button>
  );
}

function AgentPill({
  active,
  onClick,
  label,
  sublabel,
  agentId,
  color
}: {
  active: boolean;
  onClick: () => void;
  label: string;
  sublabel: string;
  agentId?: string;
  color?: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-pressed={active}
      className={cn(
        "inline-flex items-center gap-2 h-9 pl-1.5 pr-3 rounded-full border text-caption transition-colors whitespace-nowrap",
        active
          ? "bg-[var(--btn-primary-bg)] text-[var(--btn-primary-fg)] border-transparent"
          : "bg-surface-2 border-border-subtle text-fg-secondary hover:text-fg-primary hover:border-border-strong"
      )}
      style={
        active && color
          ? { boxShadow: `0 0 0 2px ${color}55` }
          : undefined
      }
    >
      {agentId ? (
        <AgentAvatar agentId={agentId} size={22} framed={false} />
      ) : (
        <span
          className="inline-flex items-center justify-center size-7 rounded-full border border-border-subtle bg-surface"
          aria-hidden
        >
          <UserCircle2 size={14} className="text-fg-tertiary" />
        </span>
      )}
      <span className="flex flex-col items-start leading-tight">
        <span className="font-medium">{label}</span>
        <span className={cn("text-[10px]", active ? "opacity-80" : "text-fg-tertiary")}>{sublabel}</span>
      </span>
    </button>
  );
}