"use client";

import Image from "next/image";
import { useCallback, useEffect, useState, type CSSProperties } from "react";
import { ChevronLeft, ChevronRight } from "lucide-react";
import type { HeroImage } from "@/content/site/heroMedia";
import { cn } from "@/lib/cn";

/**
 * Homepage hero photographs as a stack of cards. The front card is the photo
 * being shown; the next two peek out behind it (up and to the right) and can
 * be clicked to bring them forward. Previous / next buttons do the same from
 * the keyboard. Which photos are in the stack, and whether it advances on its
 * own, come from settings (heroMedia.ts and Administration → Website), never
 * from this component. Automatic rotation is skipped for reduced motion and
 * pauses while hovered, focused or off-screen.
 */

/** Where each depth in the stack sits. Depth 0 is the front card. */
const DEPTH: CSSProperties[] = [
  { transform: "translate(0, 0) scale(1)", zIndex: 30, opacity: 1 },
  { transform: "translate(22px, -22px) scale(0.955)", zIndex: 20, opacity: 1 },
  { transform: "translate(42px, -42px) scale(0.91)", zIndex: 10, opacity: 1 },
];
const HIDDEN: CSSProperties = { transform: "translate(42px, -42px) scale(0.91)", zIndex: 0, opacity: 0 };

export function HeroVisual({ images, rotate, intervalSeconds }: { images: HeroImage[]; rotate: boolean; intervalSeconds: number }) {
  const n = images.length;
  /** Index of the image at the front; the rest follow in order. */
  const [front, setFront] = useState(0);
  const [paused, setPaused] = useState(false);
  const [tick, setTick] = useState(0);
  const [reduced, setReduced] = useState(false);
  const canRotate = rotate && n > 1 && !reduced;

  useEffect(() => {
    const mq = window.matchMedia("(prefers-reduced-motion: reduce)");
    const sync = () => setReduced(mq.matches);
    sync();
    mq.addEventListener("change", sync);
    return () => mq.removeEventListener("change", sync);
  }, []);

  const go = useCallback((i: number) => setFront(((i % n) + n) % n), [n]);

  // One timer per photo: a click or arrow press restarts the countdown, so the
  // photo someone chose stays for a full interval before the stack moves on.
  useEffect(() => {
    if (!canRotate || paused) return;
    const id = window.setTimeout(() => {
      if (document.visibilityState === "visible") setFront((f) => (f + 1) % n);
      else setTick((t) => t + 1); // tab hidden: wait another interval
    }, intervalSeconds * 1000);
    return () => window.clearTimeout(id);
  }, [canRotate, paused, n, intervalSeconds, front, tick]);

  const current = images[front] ?? images[0];
  const depthOf = (i: number) => (i - front + n) % n;
  const stacked = n > 1;

  return (
    <div
      className={cn("relative", stacked && "pr-8 pt-8 sm:pr-11 sm:pt-11")}
      onMouseEnter={() => setPaused(true)}
      onMouseLeave={() => setPaused(false)}
      // Pause for keyboard users moving through the controls, not after a mouse click.
      onFocusCapture={(e) => (e.target as Element).matches?.(":focus-visible") && setPaused(true)}
      onBlurCapture={() => setPaused(false)}
    >
      <figure className="relative">
        <div className="relative aspect-[3/2]">
          {images.map((img, i) => {
            const depth = depthOf(i);
            const isFront = depth === 0;
            const style = { ...(depth < DEPTH.length ? DEPTH[depth] : HIDDEN), transformOrigin: "top right" };
            const card = (
              <>
                <Image
                  src={img.src}
                  alt={isFront ? img.alt : ""}
                  fill
                  priority={i === 0}
                  sizes="(min-width: 1280px) 640px, (min-width: 1024px) 48vw, 100vw"
                  style={{ objectPosition: img.focus ?? "50% 50%" }}
                  className="object-cover"
                />
                {!isFront && <span aria-hidden className="absolute inset-0 bg-white/25 transition-colors group-hover:bg-white/0" />}
              </>
            );
            const base = cn(
              "absolute inset-0 overflow-hidden rounded-[22px] bg-brand-offwhite ring-1 ring-brand-navy/10 transition-[transform,opacity,box-shadow] duration-700 ease-[cubic-bezier(0.22,1,0.36,1)] motion-reduce:transition-none md:rounded-[26px]",
              isFront ? "shadow-[0_30px_70px_-28px_rgba(16,38,61,0.5)]" : "shadow-[0_14px_36px_-20px_rgba(16,38,61,0.45)]",
            );
            // Back cards that peek out are buttons; the front card and hidden cards are not.
            return depth > 0 && depth < DEPTH.length ? (
              <button
                key={img.id}
                type="button"
                onClick={() => go(i)}
                aria-label={`Show photo: ${img.caption}`}
                className={cn(base, "group cursor-pointer focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand-emerald")}
                style={style}
              >
                {card}
              </button>
            ) : (
              <div key={img.id} className={cn(base, depth !== 0 && "pointer-events-none")} style={style} aria-hidden={!isFront}>
                {card}
                {isFront && (
                  <>
                    <div aria-hidden className="pointer-events-none absolute inset-x-0 bottom-0 h-2/5 bg-gradient-to-t from-brand-navy-deep/50 to-transparent" />
                    <span className="absolute bottom-3 left-3 inline-flex max-w-[calc(100%-9.5rem)] items-center gap-2 rounded-full bg-brand-navy-deep/80 px-3 py-1.5 text-[0.74rem] font-medium text-white backdrop-blur-sm sm:bottom-4 sm:left-4 sm:text-[0.78rem] md:bottom-5 md:left-5">
                      <span aria-hidden className="size-1.5 shrink-0 rounded-full bg-[rgb(var(--brand-emerald-on-dark))]" />
                      <span className="truncate">{img.caption}</span>
                    </span>
                  </>
                )}
              </div>
            );
          })}

          {stacked && (
            <div className="absolute bottom-3 right-3 z-40 flex items-center gap-1 rounded-full bg-white/90 p-1 shadow-[0_6px_20px_-8px_rgba(16,38,61,0.4)] backdrop-blur sm:bottom-4 sm:right-4 md:bottom-5 md:right-5">
              <button
                type="button"
                onClick={() => go(front - 1)}
                aria-label="Previous photo"
                className="inline-flex size-8 items-center justify-center rounded-full text-brand-navy hover:bg-brand-offwhite"
              >
                <ChevronLeft size={17} aria-hidden />
              </button>
              <span className="min-w-[2.6rem] text-center font-plexmono text-[0.72rem] text-brand-navy" aria-live="polite">
                {front + 1} / {n}
              </span>
              <button
                type="button"
                onClick={() => go(front + 1)}
                aria-label="Next photo"
                className="inline-flex size-8 items-center justify-center rounded-full text-brand-navy hover:bg-brand-offwhite"
              >
                <ChevronRight size={17} aria-hidden />
              </button>
            </div>
          )}
        </div>
        <figcaption className="mt-4 flex items-center justify-end gap-4 text-[0.74rem] text-brand-muted sm:justify-between">
          {stacked ? <span className="hidden sm:inline">Click a photo behind to bring it forward</span> : <span />}
          <span>
            Photo:{" "}
            <a href={current.credit.url} target="_blank" rel="noopener" className="underline-offset-2 hover:text-brand-text hover:underline">
              {current.credit.name}
            </a>{" "}
            /{" "}
            <a href={current.credit.sourceUrl} target="_blank" rel="noopener" className="underline-offset-2 hover:text-brand-text hover:underline">
              {current.credit.source}
            </a>
          </span>
        </figcaption>
      </figure>
    </div>
  );
}
