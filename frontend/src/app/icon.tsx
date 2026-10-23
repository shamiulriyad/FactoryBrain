// Dynamic Next.js favicon — renders the BunonBrain brand glyph at the
// canonical 32×32 size. Co-exists with /Thalamus_logo.png which we ship as
// a legacy asset until a BunonBrain logo is added.

import { ImageResponse } from "next/og";

export const runtime = "edge";
export const size = { width: 32, height: 32 };
export const contentType = "image/png";

export default function Icon() {
  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          background: "#0B0E14",
          borderRadius: 6,
          color: "#7CE0C6",
          fontSize: 22,
          fontWeight: 700,
          letterSpacing: -1
        }}
      >
        T
      </div>
    ),
    { ...size }
  );
}
