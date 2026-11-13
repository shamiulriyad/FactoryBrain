import { ImageResponse } from "next/og";
import { LOCKUP, LOGO_COLORS, SYMBOL_BODY, SYMBOL_HOOK, SYMBOL_WAVES, TAGLINE, WORDMARK } from "@/components/brand/logoPaths";

/** Default social share image for every public page. Edge runtime: the Node
 *  renderer can't load its bundled font from a path containing spaces. */
export const runtime = "edge";
export const alt = "BunonBrain: Bangladesh RMG Workforce OS";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

function logoSvg() {
  const strokes = `<g fill="none" stroke-linecap="round" stroke-linejoin="round"><path d="${SYMBOL_HOOK}" stroke="#fff" stroke-width="3"/><path d="${SYMBOL_BODY}" stroke="#fff" stroke-width="4"/>${SYMBOL_WAVES.map((d) => `<path d="${d}" stroke="${LOGO_COLORS.emeraldOnDark}" stroke-width="2.4"/>`).join("")}</g>`;
  const words = `<g transform="translate(${LOCKUP.wordmarkX} ${LOCKUP.baselineY}) scale(${LOCKUP.wordmarkScale})"><path d="${WORDMARK.bunon}" fill="#fff"/><path d="${WORDMARK.brain}" fill="${LOGO_COLORS.emeraldOnDark}"/></g>`;
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${LOCKUP.viewBox}" width="${LOCKUP.width * 3}" height="${LOCKUP.height * 3}">${strokes}${words}</svg>`;
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
}

export default function OgImage() {
  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          flexDirection: "column",
          justifyContent: "space-between",
          padding: "72px 80px",
          background: `linear-gradient(135deg, ${LOGO_COLORS.navy}, ${LOGO_COLORS.navyDeep})`,
          color: "#fff",
        }}
      >
        {/* eslint-disable-next-line @next/next/no-img-element */}
        <img src={logoSvg()} width={LOCKUP.width * 3} height={LOCKUP.height * 3} alt="" />
        <div style={{ display: "flex", flexDirection: "column" }}>
          <div style={{ fontSize: 60, fontWeight: 700, lineHeight: 1.1, letterSpacing: -1.5, maxWidth: 940 }}>
            One operating system for the whole garment floor.
          </div>
          <div style={{ marginTop: 28, fontSize: 28, color: LOGO_COLORS.emeraldOnDark }}>{TAGLINE}</div>
        </div>
      </div>
    ),
    { ...size },
  );
}
