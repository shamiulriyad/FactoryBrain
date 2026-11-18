import Image from "next/image";
import { CheckCircle2, ClipboardCheck, Layers, ShieldCheck } from "lucide-react";

/**
 * Homepage hero visual: the real Floor board screen in an application window,
 * with a few cards lifted from the same product (sample factory data).
 * Purely presentational; nothing here is live.
 */
export function HeroProduct() {
  return (
    <figure className="relative">
      {/* Application window */}
      <div className="relative overflow-hidden rounded-2xl border border-brand-navy/10 bg-white shadow-[0_40px_100px_-40px_rgba(16,38,61,0.55),0_0_0_1px_rgba(16,38,61,0.04)]">
        <div className="flex items-center gap-2 border-b border-brand-border bg-brand-offwhite px-4 py-2.5">
          <span aria-hidden className="size-2.5 rounded-full bg-[#F87171]/80" />
          <span aria-hidden className="size-2.5 rounded-full bg-[#FBBF24]/80" />
          <span aria-hidden className="size-2.5 rounded-full bg-[#34D399]/80" />
          <span className="ml-3 inline-flex min-w-0 items-center gap-2 rounded-md border border-brand-border bg-white px-3 py-1 font-plexmono text-[0.7rem] text-brand-muted">
            <ShieldCheck size={12} aria-hidden className="shrink-0 text-brand-emerald" />
            <span className="truncate">BunonBrain · Floor board</span>
          </span>
        </div>
        <Image
          src="/site/screens/floor-board.jpg"
          alt="BunonBrain floor board for a garment factory: output against target, attendance, machines monitored, items waiting for approval, open alerts and line-by-line efficiency"
          width={1440}
          height={900}
          priority
          sizes="(min-width: 1024px) 720px, 100vw"
          className="block h-auto w-full"
        />
      </div>

      {/* Floating cards (sm and up) */}
      <div className="absolute -left-6 bottom-10 hidden w-[15.5rem] rounded-xl border border-brand-border bg-white/95 p-4 shadow-[0_20px_50px_-20px_rgba(16,38,61,0.45)] backdrop-blur sm:block xl:-left-12">
        <div className="flex items-center justify-between">
          <span className="inline-flex items-center gap-2 font-heading text-[0.92rem] font-semibold text-brand-text">
            <Layers size={15} aria-hidden className="text-brand-marigold" /> F2 · Sewing floor
          </span>
          <span className="inline-flex items-center gap-1.5 rounded-full bg-brand-emerald-light px-2 py-0.5 text-[0.7rem] font-semibold text-[rgb(var(--brand-success-ink))]">
            <span aria-hidden className="size-1.5 rounded-full bg-brand-emerald" /> OK
          </span>
        </div>
        <div className="mt-3 grid grid-cols-3 gap-2 text-center">
          {[
            ["4", "lines"],
            ["48", "machines"],
            ["54", "workers"],
          ].map(([v, l]) => (
            <div key={l} className="rounded-lg bg-brand-offwhite px-1 py-1.5">
              <p className="font-heading text-[1.05rem] font-semibold leading-none text-brand-text">{v}</p>
              <p className="mt-1 text-[0.68rem] text-brand-muted">{l}</p>
            </div>
          ))}
        </div>
      </div>

      <div className="absolute -right-2 -top-8 hidden w-[14rem] rounded-xl border border-brand-border bg-white/95 p-4 shadow-[0_20px_50px_-20px_rgba(16,38,61,0.45)] backdrop-blur md:block xl:-right-5">
        <p className="flex items-center gap-2 text-[0.75rem] font-medium uppercase tracking-[0.08em] text-brand-muted">
          <ClipboardCheck size={14} aria-hidden className="text-brand-marigold" /> Approvals
        </p>
        <p className="mt-2 font-heading text-[1.6rem] font-semibold leading-none text-brand-marigold-ink">44</p>
        <p className="mt-1 text-[0.82rem] text-brand-body">overtime requests waiting for a manager</p>
        <p className="mt-3 inline-flex items-center gap-1.5 text-[0.75rem] text-brand-muted">
          <CheckCircle2 size={13} aria-hidden className="text-brand-emerald" /> Every decision is audited
        </p>
      </div>

      <figcaption className="mt-4 text-right font-plexmono text-[0.7rem] text-brand-muted">Floor board · sample factory data</figcaption>
    </figure>
  );
}
