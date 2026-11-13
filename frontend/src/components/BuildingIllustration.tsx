"use client";

import { useEffect, useState } from "react";
import { FLOOR_STATUS, LAST_STEP } from "@/lib/tour";
import { subscribeTour, tourState } from "@/lib/tourStore";

const PILL_COLOR = { OK: "rgb(var(--home-ok))", Watch: "rgb(var(--home-warn))", Act: "rgb(var(--home-thread))" } as const;

/**
 * Static elevation drawing used when WebGL is unavailable. The current
 * floor lights up as the reader scrolls; the last step shows every floor's
 * status. Decorative only: all content lives in the HTML cards.
 */
export function BuildingIllustration() {
  const [step, setStep] = useState(0);
  useEffect(
    () => subscribeTour(() => setStep(Math.max(0, Math.min(LAST_STEP, Math.round(tourState.target))))),
    [],
  );

  const floors = [0, 1, 2, 3, 4];
  const W = 300;
  const FH = 52;
  const top = 70;
  const groundY = top + FH * 5;
  const all = step === LAST_STEP;

  return (
    <svg
      viewBox="0 0 420 420"
      className="h-full w-full"
      preserveAspectRatio="xMidYMid meet"
      aria-hidden="true"
      focusable="false"
    >
      <rect x="20" y={groundY} width="380" height="16" rx="2" fill="#9fb08f" />
      <rect x="60" y={groundY - 2} width={W} height="4" fill="#c9c6bd" />
      {/* Roof: parapet, sign, tank. */}
      <rect x="60" y={top - 10} width={W} height="10" fill="#b8b5ad" />
      <rect x="150" y={top - 34} width="120" height="18" fill="#10263d" />
      <text x="210" y={top - 21} textAnchor="middle" fontSize="11" fontWeight="700" letterSpacing="2" style={{ fill: "#ffffff" }} fontFamily="var(--font-display), sans-serif">
        BUNONBRAIN
      </text>
      <rect x="78" y={top - 34} width="20" height="24" fill="#d4d7d8" />
      {floors.map((i) => {
        const y = groundY - (i + 1) * FH;
        const isOn = step === i + 1;
        const status = FLOOR_STATUS.find((s) => s.floor === (i === 0 ? "G" : String(i)))!;
        return (
          <g key={i}>
            <rect x="60" y={y} width={W} height={FH} fill={isOn ? "#e8f3ee" : "#cfdfea"} />
            <rect x="60" y={y + FH - 5} width={W} height="5" fill="#c4c1ba" />
            {Array.from({ length: 13 }, (_, k) => (
              <rect key={k} x={60 + k * 25} y={y} width="1.5" height={FH - 5} fill="#a7b0b8" />
            ))}
            <rect x="64" y={y + 4} width={W - 8} height="2" fill={isOn ? "#ffffff" : "#f4f1e8"} opacity={isOn ? 1 : 0.7} />
            {(isOn || all) && (
              <>
                <rect
                  x={i % 2 ? 110 : 230}
                  y={y + 18}
                  width="34"
                  height="22"
                  style={{ fill: "rgb(var(--home-thread) / 0.15)", stroke: "rgb(var(--home-thread))" }}
                  strokeWidth="1.5"
                />
                {all && (
                  <circle cx={340} cy={y + FH / 2 - 2} r="5" style={{ fill: PILL_COLOR[status.pill] }} />
                )}
              </>
            )}
            <text x="44" y={y + FH / 2 + 2} textAnchor="end" fontSize="13" style={{ fill: isOn ? "rgb(var(--home-lamp))" : "rgb(var(--home-muted))" }} fontFamily="var(--font-plex-mono), monospace">
              {i === 0 ? "G" : i}
            </text>
          </g>
        );
      })}
    </svg>
  );
}
