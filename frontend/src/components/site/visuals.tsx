import Image from "next/image";
import type { ReactNode } from "react";
import { cn } from "@/lib/cn";
import { slugify } from "@/content/site/blocks";

/** A real product screenshot in a light browser frame, labelled as demo data. */
export function ScreenFrame({
  src,
  alt,
  caption = "BunonBrain product interface · simulated demo data",
  priority = false,
  className,
}: {
  src: string;
  alt: string;
  caption?: string;
  priority?: boolean;
  className?: string;
}) {
  return (
    <figure className={cn("relative", className)}>
      <div className="overflow-hidden rounded-xl border border-brand-navy/10 bg-white shadow-[0_30px_80px_-30px_rgba(16,38,61,0.45),0_0_0_1px_rgba(16,38,61,0.03)]">
        <div aria-hidden className="flex h-8 items-center gap-1.5 border-b border-brand-border bg-brand-offwhite px-3">
          <span className="size-2.5 rounded-full bg-brand-navy/15" />
          <span className="size-2.5 rounded-full bg-brand-navy/15" />
          <span className="size-2.5 rounded-full bg-brand-navy/15" />
          <span className="ml-3 h-4 w-48 max-w-[40%] rounded bg-brand-navy/[0.06]" />
        </div>
        <Image src={src} alt={alt} width={1440} height={800} priority={priority} sizes="(min-width: 1024px) 720px, 100vw" className="h-auto w-full" />
      </div>
      {caption && <figcaption className="mt-3 text-center font-plexmono text-[0.72rem] text-brand-muted">{caption}</figcaption>}
    </figure>
  );
}

/**
 * How BunonBrain sits in a plant: the data the factory already has, the
 * platform's modules, and the people who act on it. Pure SVG, scales with
 * its container, readable at phone width.
 */
export function PlatformDiagram({ className }: { className?: string }) {
  const sources = ["Output records & imports", "Machine, energy & RFID readings", "POs, tech packs, spec sheets", "Garment inspection photos", "Manuals & policies"];
  const modules = ["Production planning", "Line board & alerts", "Quality inspection", "Merchandising", "Ask & knowledge", "Agents & approvals"];
  const people = ["Factory Manager", "Production Supervisor", "QC Inspector", "Merchandiser", "HR Officer", "Worker"];
  const rowY = (i: number, n: number) => 40 + i * (300 / (n - 1 || 1));
  return (
    <figure className={className}>
      <svg viewBox="0 0 960 400" role="img" aria-labelledby="pd-title pd-desc" className="h-auto w-full">
        <title id="pd-title">How BunonBrain connects factory data, modules and people</title>
        <desc id="pd-desc">
          Five kinds of factory data flow into the BunonBrain platform, which runs six modules under role-based access and an audit log, and serves six roles on the floor and in the office.
        </desc>
        <defs>
          <linearGradient id="pd-core" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#16324F" />
            <stop offset="1" stopColor="#10263D" />
          </linearGradient>
        </defs>
        {/* flows */}
        {sources.map((_, i) => (
          <path key={`s${i}`} d={`M232 ${rowY(i, 5)} C 300 ${rowY(i, 5)}, 300 200, 352 200`} fill="none" stroke="#059669" strokeOpacity="0.35" strokeWidth="1.6" />
        ))}
        {people.map((_, i) => (
          <path key={`p${i}`} d={`M608 200 C 660 200, 660 ${rowY(i, 6)}, 728 ${rowY(i, 6)}`} fill="none" stroke="#16324F" strokeOpacity="0.22" strokeWidth="1.6" />
        ))}
        {/* sources */}
        {sources.map((s, i) => (
          <g key={s} transform={`translate(0 ${rowY(i, 5) - 18})`}>
            <rect width="232" height="36" rx="10" fill="#FFFFFF" stroke="#E2E8F0" />
            <circle cx="18" cy="18" r="4" fill="#059669" />
            <text x="32" y="23" fontSize="13" fill="#334155" fontFamily="var(--font-plex), sans-serif">{s}</text>
          </g>
        ))}
        {/* core */}
        <rect x="352" y="40" width="256" height="320" rx="20" fill="url(#pd-core)" />
        <text x="480" y="74" textAnchor="middle" fontSize="15" fontWeight="600" fill="#FFFFFF" fontFamily="var(--font-heading), sans-serif">BunonBrain platform</text>
        {modules.map((m, i) => (
          <g key={m} transform={`translate(372 ${92 + i * 36})`}>
            <rect width="216" height="28" rx="7" fill="#FFFFFF" fillOpacity="0.08" stroke="#FFFFFF" strokeOpacity="0.12" />
            <text x="12" y="19" fontSize="12.5" fill="#FFFFFF" fillOpacity="0.92" fontFamily="var(--font-plex), sans-serif">{m}</text>
          </g>
        ))}
        <text x="480" y="340" textAnchor="middle" fontSize="11" fill="#34D399" fontFamily="var(--font-plex-mono), monospace" letterSpacing="1">ROLE-BASED ACCESS · AUDIT LOG</text>
        {/* people */}
        {people.map((p, i) => (
          <g key={p} transform={`translate(728 ${rowY(i, 6) - 18})`}>
            <rect width="232" height="36" rx="10" fill="#F8FAFC" stroke="#E2E8F0" />
            <text x="16" y="23" fontSize="13" fill="#16324F" fontWeight="500" fontFamily="var(--font-plex), sans-serif">{p}</text>
          </g>
        ))}
      </svg>
      <figcaption className="sr-only">Factory data on the left, the BunonBrain platform in the middle, roles on the right.</figcaption>
    </figure>
  );
}

/** Numbered steps on a connecting rail. */
export function Steps({ items, tone = "light" }: { items: { title: string; body: ReactNode }[]; tone?: "light" | "dark" }) {
  return (
    <ol className={cn("grid gap-6", items.length >= 4 ? "md:grid-cols-2 lg:grid-cols-4" : "md:grid-cols-3")}>
      {items.map((s, i) => (
        <li key={s.title} className="relative">
          <div className="flex items-center gap-3">
            <span
              className={cn(
                "inline-flex size-9 shrink-0 items-center justify-center rounded-full font-plexmono text-[0.8rem] font-medium",
                tone === "light" ? "bg-brand-navy text-white" : "bg-white/10 text-[rgb(var(--brand-emerald-on-dark))] ring-1 ring-white/15",
              )}
            >
              {String(i + 1).padStart(2, "0")}
            </span>
            <span aria-hidden className={cn("h-px flex-1", tone === "light" ? "bg-brand-border" : "bg-white/15", i === items.length - 1 && "lg:hidden")} />
          </div>
          <h3 className={cn("mt-5 font-heading text-[1.2rem] font-semibold", tone === "light" ? "text-brand-text" : "text-white")}>{s.title}</h3>
          <p className={cn("mt-2 leading-relaxed", tone === "light" ? "text-brand-body" : "text-white/70")}>{s.body}</p>
        </li>
      ))}
    </ol>
  );
}

export function FaqList({ items }: { items: { q: string; a: ReactNode }[] }) {
  return (
    <div className="divide-y divide-brand-border rounded-2xl border border-brand-border bg-white">
      {items.map((f) => (
        <details key={f.q} id={slugify(f.q)} className="group scroll-mt-28 px-6 py-1 [&_summary::-webkit-details-marker]:hidden">
          <summary className="flex min-h-16 cursor-pointer list-none items-center justify-between gap-6 py-4 font-heading text-[1.08rem] font-medium text-brand-text">
            {f.q}
            <span aria-hidden className="relative size-5 shrink-0">
              <span className="absolute left-0 top-1/2 h-px w-5 bg-brand-navy" />
              <span className="absolute left-1/2 top-0 h-5 w-px bg-brand-navy transition-transform group-open:scale-y-0" />
            </span>
          </summary>
          <div className="pb-6 pr-10 leading-relaxed text-brand-body">{f.a}</div>
        </details>
      ))}
    </div>
  );
}
