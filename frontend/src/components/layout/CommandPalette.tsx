"use client";

// Ctrl/⌘ K palette: jump to a page or settings section the signed-in role
// can open, or send the typed text to Ask. It searches navigation only —
// there is no local copy of factory records to search.

import { useEffect, useMemo, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { AnimatePresence, motion } from "framer-motion";
import { CornerDownLeft, MessageSquareText, Search, Settings as SettingsIcon, X as XIcon } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { useAppStore } from "@/store/app.store";
import { useT } from "@/lib/useT";
import { hasRole, useAuthStore, type RequiredRole } from "@/lib/auth";
import { cn } from "@/lib/cn";
import { NAV_GROUPS } from "./nav";

type Item = { id: string; group: string; title: string; hint?: string; href: string; icon: LucideIcon; keywords: string };

const SETTINGS: Array<{ section: string; title: string; hint: string; role: RequiredRole; keywords: string }> = [
  { section: "advanced", title: "AI & models", hint: "Language model, API keys, embeddings, reindex", role: "Viewer", keywords: "llm openai gemini anthropic key embedding" },
  { section: "users", title: "Users & roles", hint: "Your access; Admins change roles", role: "Viewer", keywords: "user role admin operator viewer" },
  { section: "appearance", title: "Appearance", hint: "Theme and language", role: "Viewer", keywords: "theme dark light language bangla" },
  { section: "profile", title: "Business profile", hint: "Plant name (this browser)", role: "Viewer", keywords: "name city plant" },
  { section: "dataReset", title: "Reset this browser", hint: "Clear local preferences", role: "Viewer", keywords: "reset clear demo" }
];

export function CommandPalette() {
  const open = useAppStore((s) => s.commandPaletteOpen);
  const close = useAppStore((s) => s.closeCommandPalette);
  const toggle = useAppStore((s) => s.toggleCommandPalette);
  const router = useRouter();
  const user = useAuthStore((s) => s.user);
  const { t } = useT();
  const [q, setQ] = useState("");
  const [active, setActive] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

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

  useEffect(() => {
    if (!open) return;
    setQ("");
    setActive(0);
    setTimeout(() => inputRef.current?.focus(), 0);
  }, [open]);

  const all = useMemo<Item[]>(() => {
    const pages: Item[] = NAV_GROUPS.flatMap((g) =>
      g.items
        .filter((it) => hasRole(user, it.role))
        .map((it) => ({
          id: it.href,
          group: "Pages",
          title: t(it.labelKey),
          hint: it.demo ? "Demo data" : t(g.labelKey),
          href: it.href,
          icon: it.icon,
          keywords: `${t(it.labelKey)} ${it.keywords ?? ""}`.toLowerCase()
        }))
    );
    const settings: Item[] = SETTINGS.filter((s) => hasRole(user, s.role)).map((s) => ({
      id: `settings-${s.section}`,
      group: "Settings",
      title: s.title,
      hint: s.hint,
      href: `/app/settings?section=${s.section}`,
      icon: SettingsIcon,
      keywords: `${s.title} ${s.hint} ${s.keywords}`.toLowerCase()
    }));
    return [...pages, ...settings];
  }, [user, t]);

  const results = useMemo<Item[]>(() => {
    const query = q.trim().toLowerCase();
    const matches = query ? all.filter((i) => query.split(/\s+/).every((w) => i.keywords.includes(w))) : all;
    if (!query) return matches;
    return [
      ...matches,
      { id: "ask", group: "Ask", title: `Ask BunonBrain: “${q.trim()}”`, hint: "Search the knowledge base", href: `/app/ask?q=${encodeURIComponent(q.trim())}`, icon: MessageSquareText, keywords: "" }
    ];
  }, [q, all]);

  useEffect(() => {
    if (active >= results.length) setActive(0);
  }, [results.length, active]);

  useEffect(() => {
    listRef.current?.querySelector(`[data-index="${active}"]`)?.scrollIntoView({ block: "nearest" });
  }, [active]);

  function pick(item: Item) {
    router.push(item.href);
    close();
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setActive((n) => Math.min(results.length - 1, n + 1));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setActive((n) => Math.max(0, n - 1));
    } else if (e.key === "Enter") {
      e.preventDefault();
      const cur = results[active];
      if (cur) pick(cur);
    }
  }

  let lastGroup = "";
  return (
    <AnimatePresence>
      {open && (
        <motion.div
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.15 }}
          className="fixed inset-0 z-50 bg-[var(--bg-overlay)] flex items-start justify-center p-4 pt-16 md:pt-24"
          onClick={close}
        >
          <motion.div
            initial={{ opacity: 0, y: -8, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={{ opacity: 0, y: -8, scale: 0.98 }}
            transition={{ duration: 0.18 }}
            role="dialog"
            aria-modal="true"
            aria-label="Search pages"
            onClick={(e) => e.stopPropagation()}
            className="glass-strong w-full max-w-xl overflow-hidden"
          >
            <div className="flex items-center gap-2 px-4 h-12 border-b border-border-subtle">
              <Search size={16} className="text-fg-tertiary" aria-hidden />
              <input
                ref={inputRef}
                value={q}
                onChange={(e) => {
                  setQ(e.target.value);
                  setActive(0);
                }}
                onKeyDown={onKeyDown}
                placeholder="Go to a page, a setting, or ask a question…"
                aria-label="Search"
                aria-controls="cp-results"
                aria-activedescendant={results[active] ? `cp-${results[active].id}` : undefined}
                className="flex-1 bg-transparent outline-none text-body text-fg-primary placeholder:text-fg-tertiary"
              />
              <button type="button" onClick={close} aria-label="Close search" className="size-7 rounded-md inline-flex items-center justify-center text-fg-tertiary hover:text-fg-primary hover:bg-surface-2">
                <XIcon size={14} />
              </button>
            </div>
            <ul id="cp-results" ref={listRef} role="listbox" className="max-h-[60vh] overflow-y-auto py-2">
              {results.length === 0 && <li className="px-4 py-6 text-center text-caption text-fg-tertiary">No pages match.</li>}
              {results.map((item, i) => {
                const header = item.group !== lastGroup;
                lastGroup = item.group;
                const Icon = item.icon;
                return (
                  <li key={item.id} role="presentation">
                    {header && <p className="px-4 pt-2 pb-1 eyebrow">{item.group}</p>}
                    <button
                      type="button"
                      id={`cp-${item.id}`}
                      role="option"
                      aria-selected={i === active}
                      data-index={i}
                      onMouseEnter={() => setActive(i)}
                      onClick={() => pick(item)}
                      className={cn("w-full flex items-center gap-3 px-4 py-2 text-left", i === active ? "bg-[var(--accent-soft)]" : "hover:bg-surface-2")}
                    >
                      <Icon size={15} className={i === active ? "text-accent" : "text-fg-tertiary"} />
                      <span className="min-w-0 flex-1">
                        <span className="block text-body text-fg-primary truncate">{item.title}</span>
                        {item.hint && <span className="block text-caption text-fg-tertiary truncate">{item.hint}</span>}
                      </span>
                      {i === active && <CornerDownLeft size={13} className="text-fg-tertiary" aria-hidden />}
                    </button>
                  </li>
                );
              })}
            </ul>
            <div className="px-4 py-2 border-t border-border-subtle flex items-center gap-3 text-[11px] text-fg-tertiary">
              <span><span className="kbd">↑</span> <span className="kbd">↓</span> move</span>
              <span><span className="kbd">Enter</span> open</span>
              <span><span className="kbd">Esc</span> close</span>
            </div>
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}
