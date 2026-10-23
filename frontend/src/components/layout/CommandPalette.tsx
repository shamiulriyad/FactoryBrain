"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { motion, AnimatePresence } from "framer-motion";
import {
  CornerDownLeft,
  FileText,
  Layers,
  Settings as SettingsIcon,
  Search,
  Sparkles,
  X as XIcon,
  Clock
} from "lucide-react";
import { useAppStore } from "@/store/app.store";
import { useT } from "@/lib/useT";
import { Input } from "@/components/ui/Input";
import { cn } from "@/lib/cn";
import { searchService, type SearchItem, type SearchKind } from "@/services/search.service";

export function CommandPalette() {
  const open = useAppStore((s) => s.commandPaletteOpen);
  const close = useAppStore((s) => s.closeCommandPalette);
  const toggle = useAppStore((s) => s.toggleCommandPalette);
  const router = useRouter();
  const [q, setQ] = useState("");
  const [active, setActive] = useState(0);
  const { t, locale } = useT();
  const recent = useAppStore((s) => s.recentSearches);
  const pushRecent = useAppStore((s) => s.pushRecentSearch);
  const clearRecent = useAppStore((s) => s.clearRecentSearches);
  const listRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        toggle();
      } else if (e.key === "Escape" && open) {
        close();
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [open, toggle, close]);

  // Reset query + active row on open.
  useEffect(() => {
    if (open) {
      setQ("");
      setActive(0);
    }
  }, [open]);

  const flat = useMemo<SearchItem[]>(() => {
    if (!q.trim()) return [];
    void open; // open is intentionally part of deps so the palette re-derives on open.
    return searchService.search(q, locale);
  }, [q, locale, open]);

  const grouped = useMemo(() => searchService.group(flat), [flat]);

  // Flat index across grouped results — used for keyboard nav.
  const flatForNav = useMemo(() => grouped.flatMap((g) => g.items), [grouped]);

  // Keep `active` within range.
  useEffect(() => {
    if (active >= flatForNav.length) setActive(0);
  }, [flatForNav.length, active]);

  function pick(item: SearchItem) {
    if (q.trim()) pushRecent(q.trim());
    router.push(item.href);
    close();
    setQ("");
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setActive((n) => Math.min(flatForNav.length - 1, n + 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActive((n) => Math.max(0, n - 1));
    } else if (e.key === "Enter") {
      e.preventDefault();
      const cur = flatForNav[active];
      if (cur) pick(cur);
    }
  }

  // Scroll active row into view.
  useEffect(() => {
    if (!listRef.current) return;
    const el = listRef.current.querySelector(`[data-row-index="${active}"]`) as HTMLElement | null;
    if (el) el.scrollIntoView({ block: "nearest" });
  }, [active]);

  // locale is intentional re-trigger signal; suggestions() closes over translations.
  const suggestions = useMemo(() => { void locale; return searchService.suggestions(locale); }, [locale]);
  const showEmptyQueryState = !q.trim();
  const showNoResults = !!q.trim() && flatForNav.length === 0;

  return (
    <AnimatePresence>
      {open && (
        <motion.div
          key="cp"
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.18 }}
          className="fixed inset-0 z-50 bg-[var(--bg-overlay)] flex items-start justify-center p-4 pt-16 md:pt-24"
          onClick={close}
        >
          <motion.div
            initial={{ opacity: 0, y: -8, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: -8, scale: 0.98 }}
            transition={{ duration: 0.2, ease: [0.22, 1, 0.36, 1] }}
            className="glass w-full max-w-xl shadow-glass rounded-md overflow-hidden"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="p-3 border-b border-border-subtle">
              <div className="relative flex items-center gap-2">
                <Search size={14} className="absolute left-3 text-fg-tertiary pointer-events-none" />
                <Input
                  autoFocus
                  placeholder={t("common.searchHint")}
                  value={q}
                  onChange={(e) => {
                    setQ(e.target.value);
                    setActive(0);
                  }}
                  onKeyDown={onKeyDown}
                  className="pl-8"
                  aria-label={t("common.search")}
                />
                <kbd className="hidden md:inline mono-pill border border-border-subtle bg-surface-2 px-1.5 py-0.5 rounded-sm text-fg-tertiary">
                  ⏎
                </kbd>
              </div>
              <p className="mt-2 text-caption text-fg-tertiary">{t("common.banglaHint")}</p>
            </div>

            <div ref={listRef} className="max-h-[60vh] overflow-y-auto">
              {showEmptyQueryState && (
                <EmptyQueryState
                  recent={recent}
                  suggestions={suggestions}
                  onPick={(value) => {
                    setQ(value);
                    setActive(0);
                  }}
                  onClearRecent={clearRecent}
                  t={t}
                />
              )}

              {showNoResults && <NoResultsState t={t} />}

              {!showEmptyQueryState && !showNoResults && (
                <ResultsList
                  grouped={grouped}
                  active={active}
                  onPick={pick}
                  onHover={setActive}
                  t={t}
                  locale={locale}
                  q={q}
                />
              )}
            </div>

            <div className="border-t border-border-subtle px-3 py-2 flex items-center justify-between text-caption text-fg-tertiary">
              <span className="inline-flex items-center gap-2">
                <kbd className="mono-pill border border-border-subtle bg-surface-2 px-1 rounded-sm">↑</kbd>
                <kbd className="mono-pill border border-border-subtle bg-surface-2 px-1 rounded-sm">↓</kbd>
                navigate
              </span>
              <span className="inline-flex items-center gap-2">
                <kbd className="mono-pill border border-border-subtle bg-surface-2 px-1 rounded-sm">
                  <CornerDownLeft size={10} className="inline" />
                </kbd>
                open
              </span>
              <span className="inline-flex items-center gap-2">
                <kbd className="mono-pill border border-border-subtle bg-surface-2 px-1 rounded-sm">esc</kbd>
                close
              </span>
            </div>
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}

function EmptyQueryState({
  recent,
  suggestions,
  onPick,
  onClearRecent,
  t
}: {
  recent: string[];
  suggestions: string[];
  onPick: (q: string) => void;
  onClearRecent: () => void;
  t: (k: string) => string;
}) {
  return (
    <div className="p-3 space-y-4">
      {recent.length > 0 && (
        <section>
          <div className="flex items-center justify-between mb-2">
            <p className="text-caption text-fg-tertiary inline-flex items-center gap-1.5">
              <Clock size={12} /> {t("common.recent")}
            </p>
            <button
              type="button"
              onClick={onClearRecent}
              className="text-caption text-fg-tertiary hover:text-fg-primary inline-flex items-center gap-1"
            >
              <XIcon size={10} /> {t("common.cancel")}
            </button>
          </div>
          <div className="flex flex-wrap gap-1.5">
            {recent.map((r) => (
              <button
                key={r}
                type="button"
                onClick={() => onPick(r)}
                className="h-8 px-3 rounded-md border border-border-subtle bg-surface-2 text-caption text-fg-secondary hover:text-fg-primary hover:border-border-strong transition-colors"
              >
                {r}
              </button>
            ))}
          </div>
        </section>
      )}
      <section>
        <p className="text-caption text-fg-tertiary mb-2 inline-flex items-center gap-1.5">
          <Sparkles size={12} /> {t("common.suggestedQueries")}
        </p>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-1.5">
          {suggestions.map((s) => (
            <button
              key={s}
              type="button"
              onClick={() => onPick(s)}
              className="flex items-center gap-2 px-3 py-2 rounded-md border border-border-subtle bg-surface-2 text-caption text-fg-primary hover:border-border-strong transition-colors text-left"
            >
              <Search size={12} className="text-fg-tertiary shrink-0" />
              <span className="truncate">{s}</span>
            </button>
          ))}
        </div>
      </section>
    </div>
  );
}

function NoResultsState({ t }: { t: (k: string) => string }) {
  return (
    <div className="p-6 text-center">
      <p className="text-body text-fg-primary">{t("common.noResults")}</p>
      <p className="mt-1 text-caption text-fg-tertiary max-w-sm mx-auto">
        {t("common.noResultsHint")}
      </p>
    </div>
  );
}

function ResultsList({
  grouped,
  active,
  onPick,
  t,
  locale,
  q,
  onHover
}: {
  grouped: Array<{ kind: SearchKind; items: SearchItem[] }>;
  active: number;
  onPick: (it: SearchItem) => void;
  t: (k: string) => string;
  locale: "en" | "bn";
  q: string;
  onHover: (idx: number) => void;
}) {
  let runningIdx = 0;
  return (
    <div className="py-1">
      {grouped.map((g) => (
        <section key={g.kind} className="py-1">
          <p className="px-4 py-1.5 text-caption text-fg-tertiary flex items-center gap-1.5 sticky top-0 bg-[var(--bg-surface)]/95 backdrop-blur z-[1]">
            <KindIcon kind={g.kind} />
            {t(`search.types.${g.kind}`)}
            <span className="ml-auto mono-pill bg-surface-2 px-1.5 rounded-sm">
              {g.items.length}
            </span>
          </p>
          <ul>
            {g.items.map((it) => {
              const idx = runningIdx++;
              const isActive = idx === active;
              return (
                <li key={it.id}>
                  <button
                    type="button"
                    data-row-index={idx}
                    onMouseEnter={() => onHover(idx)}
                    onClick={() => onPick(it)}
                    className={cn(
                      "flex w-full items-start gap-3 px-4 py-2.5 text-left transition-colors",
                      isActive
                        ? "bg-[var(--accent-soft)] border-l-2 border-[var(--accent)]"
                        : "border-l-2 border-transparent hover:bg-surface-2"
                    )}
                  >
                    <span className="size-6 rounded-md bg-surface-2 border border-border-subtle flex items-center justify-center text-fg-tertiary shrink-0">
                      <KindIcon kind={it.kind} small />
                    </span>
                    <div className="flex-1 min-w-0">
                      <div className="text-body text-fg-primary truncate">
                        <HighlightedText
                          text={locale === "bn" ? it.titleBn : it.title}
                          q={q}
                        />
                      </div>
                      <div className="text-caption text-fg-tertiary truncate">
                        <HighlightedText
                          text={locale === "bn" ? it.hintBn : it.hint}
                          q={q}
                        />
                      </div>
                    </div>
                    <CornerDownLeft
                      size={12}
                      className={cn(
                        "self-center shrink-0",
                        isActive ? "text-accent" : "text-fg-tertiary opacity-0"
                      )}
                    />
                  </button>
                </li>
              );
            })}
          </ul>
        </section>
      ))}
    </div>
  );
}

function HighlightedText({ text, q }: { text: string; q: string }) {
  if (!q.trim()) return <>{text}</>;
  const lower = text.toLowerCase();
  const needle = q.toLowerCase();
  const idx = lower.indexOf(needle);
  if (idx === -1) return <>{text}</>;
  return (
    <>
      {text.slice(0, idx)}
      <mark className="bg-[var(--accent-soft)] text-accent rounded-sm px-0.5">
        {text.slice(idx, idx + needle.length)}
      </mark>
      {text.slice(idx + needle.length)}
    </>
  );
}

function KindIcon({ kind, small }: { kind: SearchKind; small?: boolean }) {
  const size = small ? 12 : 12;
  switch (kind) {
    case "page":
      return <Layers size={size} />;
    case "agent":
      return <Sparkles size={size} />;
    case "insight":
    case "approval":
      return <Sparkles size={size} />;
    case "activity":
      return <Clock size={size} />;
    case "order":
    case "buyer":
    case "supplier":
    case "compliance":
    case "process":
    case "target":
    case "risk":
      return <FileText size={size} />;
    case "integration":
      return <Layers size={size} />;
    case "setting":
      return <SettingsIcon size={size} />;
    default:
      return <Search size={size} />;
  }
}
