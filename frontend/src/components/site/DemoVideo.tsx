"use client";

import { useState } from "react";
import { Play } from "lucide-react";
import { ButtonLink } from "@/components/site/ui";

/**
 * The homepage demo video. Shows YouTube's thumbnail with a play button and
 * loads the player (youtube-nocookie.com) only after a click, so the page
 * stays fast and nothing is sent to YouTube until the visitor asks for it.
 * Without a video it shows a placeholder that offers a live demo instead.
 */
export function DemoVideo({ videoId, title }: { videoId?: string | null; title?: string | null }) {
  const [playing, setPlaying] = useState(false);
  const label = title || "BunonBrain product demo";

  if (!videoId) {
    return (
      <div className="site-cta relative flex aspect-video w-full flex-col items-center justify-center overflow-hidden rounded-2xl px-6 text-center text-white">
        <div aria-hidden className="site-weave pointer-events-none absolute inset-0 opacity-60" />
        <span aria-hidden className="relative inline-flex size-16 items-center justify-center rounded-full bg-white/10 ring-1 ring-white/20">
          <Play size={26} className="ml-1 text-brand-marigold-on-dark" fill="currentColor" />
        </span>
        <p className="relative mt-5 font-heading text-[1.35rem] font-semibold">The demo video is on its way.</p>
        <p className="relative mt-2 max-w-[30rem] text-[0.98rem] text-white/75">Until then, we&apos;ll walk you through BunonBrain live on a running garment plant.</p>
        <div className="relative mt-6">
          <ButtonLink href="/demo" arrow>
            Request a live demo
          </ButtonLink>
        </div>
      </div>
    );
  }

  if (playing) {
    return (
      <div className="relative aspect-video w-full overflow-hidden rounded-2xl bg-black">
        <iframe
          className="absolute inset-0 size-full"
          src={`https://www.youtube-nocookie.com/embed/${videoId}?autoplay=1&rel=0&modestbranding=1&playsinline=1`}
          title={label}
          allow="accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share"
          referrerPolicy="strict-origin-when-cross-origin"
          allowFullScreen
        />
      </div>
    );
  }

  return (
    <button
      type="button"
      onClick={() => setPlaying(true)}
      aria-label={`Play video: ${label}`}
      className="group relative block aspect-video w-full overflow-hidden rounded-2xl bg-brand-navy-deep focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-4 focus-visible:outline-brand-emerald"
    >
      {/* YouTube's own thumbnail; next/image isn't configured for remote hosts. */}
      {/* eslint-disable-next-line @next/next/no-img-element */}
      <img
        src={`https://i.ytimg.com/vi/${videoId}/hqdefault.jpg`}
        alt=""
        loading="lazy"
        className="absolute inset-0 size-full object-cover transition-transform duration-500 group-hover:scale-[1.02]"
      />
      <span aria-hidden className="absolute inset-0 bg-gradient-to-t from-brand-navy-deep/80 via-brand-navy-deep/20 to-transparent" />
      <span aria-hidden className="absolute left-1/2 top-1/2 inline-flex size-20 -translate-x-1/2 -translate-y-1/2 items-center justify-center rounded-full bg-white/95 shadow-[0_12px_40px_-8px_rgba(16,38,61,0.6)] transition-transform group-hover:scale-105">
        <Play size={30} className="ml-1 text-brand-marigold" fill="currentColor" />
      </span>
      <span className="absolute inset-x-0 bottom-0 p-5 text-left font-heading text-[1.05rem] font-semibold text-white sm:p-6 sm:text-[1.2rem]">{label}</span>
    </button>
  );
}
