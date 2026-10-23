"use client";

import Image from "next/image";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";

type Props = {
  size?: number;
  className?: string;
  /** Show the rounded soft background — useful in headers. */
  framed?: boolean;
  /** Show the wordmark next to the logo. */
  withWordmark?: boolean;
  /** Force the raster PNG (true) or the inline SVG glyph (false). */
  usePng?: boolean;
  /** When true, run the SVG stroke draw-on once on mount. */
  reveal?: boolean;
};

/**
 * Brand mark for BunonBrain. Renders the supplied PNG when available,
 * otherwise falls back to an inline SVG glyph so the brand is always present.
 *
 * The PNG lives at `/Thalamus_logo.png`; we keep it under `public/` as a
 * legacy fallback so existing bookmarks still resolve, but the wordmark
 * is BunonBrain everywhere.
 */
export function BrandMark({
  size = 28,
  className,
  framed = false,
  withWordmark = false,
  usePng = false,
  reveal = false
}: Props) {
  const { t } = useT();
  return (
    <span className={cn("inline-flex items-center gap-2", className)}>
      {framed ? (
        <span
          className="inline-flex items-center justify-center rounded-md border border-[var(--accent-border)] bg-[var(--accent-soft)]"
          style={{ width: size, height: size }}
          aria-hidden
        >
          <BrandGlyph size={Math.round(size * 0.6)} reveal={reveal} />
        </span>
      ) : usePng ? (
        <Image
          src="/Thalamus_logo.png"
          alt={t("app.name") as string}
          width={size}
          height={size}
          priority
          className="rounded-sm object-contain"
          style={{ width: size, height: size }}
        />
      ) : (
        <BrandGlyph size={size} reveal={reveal} />
      )}
      {withWordmark && (
        <span className="text-body font-semibold tracking-tight text-fg-primary">{t("app.name")}</span>
      )}
    </span>
  );
}

/**
 * Inline SVG glyph. When `reveal` is true, the path strokes itself on once
 * via a CSS draw-on animation (handled in globals.css via `.brand-glyph-path`).
 * pathLength="1" normalises the stroke length so the animation works
 * without measuring the actual length.
 */
export function BrandGlyph({
  size = 28,
  className,
  reveal = false
}: {
  size?: number;
  className?: string;
  reveal?: boolean;
}) {
  return (
    <svg
      viewBox="0 0 32 32"
      width={size}
      height={size}
      className={cn("text-accent", className)}
      aria-hidden="true"
    >
      <defs>
        <linearGradient id="brand-grad" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0%" stopColor="currentColor" stopOpacity="0.9" />
          <stop offset="100%" stopColor="currentColor" stopOpacity="0.55" />
        </linearGradient>
      </defs>
      <rect x="2" y="2" width="28" height="28" rx="7" fill="url(#brand-grad)" />
      <path
        d="M9 22 V11 M9 11 H17 a4 4 0 0 1 0 8 H9"
        fill="none"
        stroke="var(--btn-primary-fg)"
        strokeWidth="2.2"
        strokeLinecap="round"
        strokeLinejoin="round"
        pathLength="1"
        className={reveal ? "brand-glyph-path" : undefined}
        style={reveal ? undefined : { strokeDasharray: "none", strokeDashoffset: 0 }}
      />
      <circle cx="24" cy="9" r="2" fill="var(--btn-primary-fg)" />
    </svg>
  );
}