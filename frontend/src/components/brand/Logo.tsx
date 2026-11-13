import type { CSSProperties } from "react";
import {
  LOCKUP,
  LOGO_COLORS,
  SYMBOL_BODY,
  SYMBOL_HOOK,
  SYMBOL_VIEWBOX,
  SYMBOL_WAVES,
  SYMBOL_WAVE_SMALL,
  WORDMARK,
} from "./logoPaths";

/**
 * BunonBrain logo family. Pure SVG, no hooks, so it works in server and
 * client components alike.
 *
 * tone:
 *  - "color"  : navy symbol and "Bunon", emerald waves and "Brain" (light backgrounds)
 *  - "onDark" : white symbol and "Bunon", light emerald waves and "Brain" (navy backgrounds)
 *  - "mono"   : everything in currentColor (one-colour print, embossing, overlays)
 *  - "auto"   : follows the app theme via --logo-ink / --logo-accent (globals.css)
 */
export type LogoTone = "color" | "onDark" | "mono" | "auto";

function palette(tone: LogoTone) {
  if (tone === "mono") return { ink: "currentColor", accent: "currentColor" };
  if (tone === "auto") return { ink: `var(--logo-ink, ${LOGO_COLORS.navy})`, accent: `var(--logo-accent, ${LOGO_COLORS.emerald})` };
  if (tone === "onDark") return { ink: LOGO_COLORS.white, accent: LOGO_COLORS.emeraldOnDark };
  return { ink: LOGO_COLORS.navy, accent: LOGO_COLORS.emerald };
}

/** Accessible name when `label` is given, otherwise decorative. */
function a11y(label?: string) {
  return label ? { role: "img", "aria-label": label } : { "aria-hidden": true, focusable: "false" as const };
}

/** The symbol's strokes, in 48-unit symbol space. */
export function SymbolStrokes({
  ink,
  accent,
  small = false,
  reveal = false,
}: {
  ink: string;
  accent: string;
  small?: boolean;
  reveal?: boolean;
}) {
  const draw = reveal ? "brand-glyph-path" : undefined;
  return (
    <g fill="none" strokeLinecap="round" strokeLinejoin="round">
      <path d={SYMBOL_HOOK} stroke={ink} strokeWidth={3} pathLength={1} className={draw} />
      <path d={SYMBOL_BODY} stroke={ink} strokeWidth={small ? 4.6 : 4} pathLength={1} className={draw} />
      {small ? (
        <path d={SYMBOL_WAVE_SMALL} stroke={accent} strokeWidth={3.4} />
      ) : (
        SYMBOL_WAVES.map((d) => <path key={d} d={d} stroke={accent} strokeWidth={2.4} />)
      )}
    </g>
  );
}

/** Standalone symbol. Below 34 px it switches to the one-wave small cut. */
export function LogoSymbol({
  size = 32,
  tone = "color",
  small,
  reveal = false,
  label,
  className,
  style,
}: {
  size?: number;
  tone?: LogoTone;
  /** Force the small cut on or off; defaults to size < 34. */
  small?: boolean;
  /** Draw the strokes on once (onboarding welcome). */
  reveal?: boolean;
  label?: string;
  className?: string;
  style?: CSSProperties;
}) {
  const { ink, accent } = palette(tone);
  return (
    <svg
      viewBox={SYMBOL_VIEWBOX}
      width={size}
      height={size}
      className={className}
      style={{ flexShrink: 0, ...style }}
      {...a11y(label)}
    >
      <SymbolStrokes ink={ink} accent={accent} small={small ?? size < 34} reveal={reveal} />
    </svg>
  );
}

/** "Bunon" + "Brain" outlines, translated so the baseline sits at y = 0. */
export function WordmarkPaths({ first, second }: { first: string; second: string }) {
  return (
    <>
      <path d={WORDMARK.bunon} fill={first} />
      <path d={WORDMARK.brain} fill={second} />
    </>
  );
}

/** Primary horizontal logo: symbol + BunonBrain wordmark. `height` is the symbol height. */
export function Logo({
  height = 32,
  tone = "color",
  label,
  className,
  style,
}: {
  height?: number;
  tone?: LogoTone;
  label?: string;
  className?: string;
  style?: CSSProperties;
}) {
  const { ink, accent } = palette(tone);
  const width = (height * LOCKUP.width) / LOCKUP.height;
  return (
    <svg
      viewBox={LOCKUP.viewBox}
      width={Math.round(width * 10) / 10}
      height={height}
      className={className}
      style={{ flexShrink: 0, ...style }}
      {...a11y(label)}
    >
      <SymbolStrokes ink={ink} accent={accent} small={height < 26} />
      <g transform={`translate(${LOCKUP.wordmarkX} ${LOCKUP.baselineY}) scale(${LOCKUP.wordmarkScale})`}>
        <WordmarkPaths first={ink} second={accent} />
      </g>
    </svg>
  );
}

/** Wordmark alone (no symbol). `height` is the cap height in px. */
export function Wordmark({
  height = 20,
  tone = "color",
  label,
  className,
}: {
  height?: number;
  tone?: LogoTone;
  label?: string;
  className?: string;
}) {
  const { ink, accent } = palette(tone);
  const top = WORDMARK.capHeight + 2;
  const scale = height / WORDMARK.capHeight;
  return (
    <svg
      viewBox={`0 ${-top} ${WORDMARK.width} ${top + 1}`}
      width={Math.round(WORDMARK.width * scale)}
      height={Math.round((top + 1) * scale)}
      className={className}
      {...a11y(label)}
    >
      <WordmarkPaths first={ink} second={accent} />
    </svg>
  );
}

/**
 * App icon / favicon tile: the symbol in white on a navy rounded square, with
 * light-emerald waves. Used where the mark needs its own ground.
 */
export function LogoAppIcon({
  size = 40,
  radius = 11,
  label,
  className,
  reveal = false,
}: {
  size?: number;
  /** Corner radius in 48-unit tile space. */
  radius?: number;
  label?: string;
  className?: string;
  reveal?: boolean;
}) {
  return (
    <svg viewBox="0 0 48 48" width={size} height={size} className={className} style={{ flexShrink: 0 }} {...a11y(label)}>
      <rect width="48" height="48" rx={radius} fill={LOGO_COLORS.navy} />
      <g transform="translate(24 24.3) scale(0.74) translate(-24.25 -22.7)">
        <SymbolStrokes ink={LOGO_COLORS.white} accent={LOGO_COLORS.emeraldOnDark} small={size < 72} reveal={reveal} />
      </g>
    </svg>
  );
}
