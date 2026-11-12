import { HERO } from "@/lib/tour";
import { ScrollLink } from "./ScrollLink";

/** First screen. Rendered on the server with no entrance animation, so the
 *  headline and CTAs are on the first frame (and are the LCP element). */
export function Hero() {
  return (
    <section id="top" aria-labelledby="hero-title" className="relative z-10 flex min-h-[100svh] flex-col">
      <div aria-hidden="true" className="fb-hero-scrim pointer-events-none absolute inset-0" />
      <div className="relative mx-auto flex w-full max-w-[1440px] flex-1 items-start px-4 pb-28 pt-24 md:items-center md:px-8 md:pt-16">
        <div className="max-w-[35rem]">
          <p className="whitespace-nowrap font-plexmono text-[0.72rem] uppercase tracking-[0.16em] text-fb-muted">
            AI operations for garment factories
          </p>
          <h1
            id="hero-title"
            className="mt-4 font-display text-[3.1rem] font-extrabold leading-[0.94] tracking-[0.005em] sm:text-[4rem] lg:text-[5.4rem]"
          >
            {HERO.headlineLead}
            <br />
            <span className="text-fb-lamp">{HERO.headlineAccent}</span>
            <br />
            {HERO.headlineTail}
          </h1>
          <p className="mt-6 max-w-[31rem] text-[1rem] leading-relaxed text-fb-text/90 md:text-[1.08rem]">{HERO.body}</p>
          {/* Layout is identical in fallback and web fonts (no shift on font load):
              the skip link always takes its own line on small screens. */}
          <div className="mt-8 flex flex-wrap items-center gap-3">
            <a href="#demo" className="fb-btn fb-btn-solid">
              {HERO.primaryCta}
            </a>
            <ScrollLink step={1} href="#tour-1" className="fb-btn fb-btn-ghost">
              {HERO.secondaryCta}
            </ScrollLink>
            <span className="basis-full sm:basis-auto">
              <a
                href="#how"
                className="inline-flex min-h-11 items-center px-2 text-[0.92rem] text-fb-text/85 underline decoration-fb-text/40 underline-offset-4 hover:text-fb-text hover:decoration-fb-text"
              >
                {HERO.skip}
              </a>
            </span>
          </div>
        </div>
      </div>
      <div aria-hidden="true" className="absolute inset-x-0 bottom-6 flex flex-col items-center gap-2">
        <span className="rounded-[3px] bg-fb-card px-2 py-1 font-plexmono text-[0.68rem] uppercase tracking-[0.18em] text-fb-text/85">
          {HERO.scrollCue}
        </span>
        <span className="fb-cue-line block h-8 w-px bg-fb-text/70" />
      </div>
    </section>
  );
}
