"use client";

import Link from "next/link";
import { useMemo, useState } from "react";
import { ArrowRight, Search } from "lucide-react";
import { cn } from "@/lib/cn";

export interface ResourceItem {
  title: string;
  href: string;
  type: string;
  summary: string;
}

/** Searchable, filterable list of resources (articles, guides, docs, FAQs). */
export function ResourceExplorer({ items, placeholder = "Search articles, guides and documentation" }: { items: ResourceItem[]; placeholder?: string }) {
  const [q, setQ] = useState("");
  const [type, setType] = useState<string>("All");
  const types = useMemo(() => ["All", ...Array.from(new Set(items.map((i) => i.type)))], [items]);
  const results = useMemo(() => {
    const terms = q.toLowerCase().split(/\s+/).filter(Boolean);
    return items.filter(
      (i) => (type === "All" || i.type === type) && terms.every((t) => `${i.title} ${i.summary} ${i.type}`.toLowerCase().includes(t)),
    );
  }, [items, q, type]);

  return (
    <div>
      <div className="flex flex-col gap-4 md:flex-row md:items-center">
        <label className="relative flex-1">
          <span className="sr-only">Search resources</span>
          <Search size={18} aria-hidden className="absolute left-4 top-1/2 -translate-y-1/2 text-brand-muted" />
          <input
            type="search"
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder={placeholder}
            className="min-h-12 w-full rounded-xl border border-brand-border bg-white pl-11 pr-4 text-brand-text placeholder:text-brand-muted/80 focus:border-brand-emerald focus:outline-none focus:ring-2 focus:ring-brand-emerald/25"
          />
        </label>
        <div role="group" aria-label="Filter by type" className="flex flex-wrap gap-2">
          {types.map((t) => (
            <button
              key={t}
              type="button"
              aria-pressed={type === t}
              onClick={() => setType(t)}
              className={cn(
                "inline-flex min-h-10 items-center rounded-full border px-4 text-[0.88rem] font-medium transition-colors",
                type === t ? "border-brand-navy bg-brand-navy text-white" : "border-brand-border bg-white text-brand-body hover:border-brand-navy/30",
              )}
            >
              {t}
            </button>
          ))}
        </div>
      </div>
      <p className="mt-6 text-[0.88rem] text-brand-muted" aria-live="polite">
        {results.length} {results.length === 1 ? "result" : "results"}
      </p>
      {results.length === 0 ? (
        <div className="mt-4 rounded-2xl border border-dashed border-brand-navy/20 bg-brand-offwhite p-10 text-center text-brand-body">
          Nothing matches “{q}”. Try another word, or{" "}
          <Link href="/support" className="text-brand-emerald-ink underline underline-offset-2">
            ask our team
          </Link>
          .
        </div>
      ) : (
        <ul className="mt-4 grid gap-4 md:grid-cols-2 lg:grid-cols-3">
          {results.map((r) => (
            <li key={r.href}>
              <Link
                href={r.href}
                className="group flex h-full flex-col rounded-2xl border border-brand-border bg-white p-6 transition-[border-color,box-shadow] hover:border-brand-navy/25 hover:shadow-[0_12px_32px_-16px_rgba(16,38,61,0.2)]"
              >
                <span className="font-plexmono text-[0.68rem] uppercase tracking-[0.14em] text-brand-emerald-ink">{r.type}</span>
                <span className="mt-2 font-heading text-[1.1rem] font-semibold leading-snug text-brand-text">{r.title}</span>
                <span className="mt-2 flex-1 text-[0.93rem] leading-relaxed text-brand-body">{r.summary}</span>
                <span className="mt-4 inline-flex items-center gap-1 text-[0.88rem] font-medium text-brand-emerald-ink">
                  Read <ArrowRight size={14} aria-hidden className="transition-transform group-hover:translate-x-0.5" />
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
