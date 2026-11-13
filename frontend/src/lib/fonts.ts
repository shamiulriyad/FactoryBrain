import { Big_Shoulders_Display, IBM_Plex_Mono, IBM_Plex_Sans, Outfit } from "next/font/google";

/**
 * BunonBrain typefaces, shared by the marketing homepage and the app
 * sidebar so each font file is downloaded once.
 */
export const displayFont = Big_Shoulders_Display({
  subsets: ["latin"],
  weight: ["700", "800"],
  variable: "--font-display",
  // The homepage headline is the LCP element. "optional" paints it once: in
  // Big Shoulders if the font is already there (preloaded / cached),
  // otherwise in a condensed system fallback, with no late swap or shift.
  display: "optional",
  adjustFontFallback: false,
  fallback: ["Impact", "sans-serif-condensed", "Arial Narrow", "sans-serif"],
});

/**
 * The same face for the signed-in app, with a normal swap: the wordmark and
 * floor codes always end up in Big Shoulders (cached after the first visit),
 * instead of staying in a stand-in like the homepage's "optional" headline.
 */
export const displayFontApp = Big_Shoulders_Display({
  subsets: ["latin"],
  weight: ["700", "800"],
  variable: "--font-display",
  display: "swap",
});

export const plexSans = IBM_Plex_Sans({
  subsets: ["latin"],
  weight: ["400", "500", "600"],
  variable: "--font-plex",
  display: "swap",
});

export const plexMono = IBM_Plex_Mono({
  subsets: ["latin"],
  weight: ["400", "500"],
  variable: "--font-plex-mono",
  display: "swap",
  // Labels only; never the LCP element.
  preload: false,
});

/**
 * Outfit: the geometric sans the BunonBrain wordmark is drawn from. The logo
 * itself is outlined (components/brand/logoPaths.ts); this font is only
 * loaded on the brand page, for the type specimen.
 */
export const wordmarkFont = Outfit({
  subsets: ["latin"],
  weight: ["400", "600", "700"],
  variable: "--font-wordmark",
  display: "swap",
  preload: false,
});

/**
 * Corporate website headings: Outfit, the geometric face behind the logo.
 * Loaded (and preloaded) only by the public site layout.
 */
export const siteHeadingFont = Outfit({
  subsets: ["latin"],
  weight: ["500", "600", "700"],
  variable: "--font-heading",
  display: "swap",
});

/** Public corporate site: headings, body and labels. */
export const siteFontVariables = `${siteHeadingFont.variable} ${plexSans.variable} ${plexMono.variable}`;

/** Class names that define the three CSS font variables (marketing pages). */
export const fontVariables = `${displayFont.variable} ${plexSans.variable} ${plexMono.variable}`;

/** Same, for the signed-in app. */
export const appFontVariables = `${displayFontApp.variable} ${plexSans.variable} ${plexMono.variable}`;
