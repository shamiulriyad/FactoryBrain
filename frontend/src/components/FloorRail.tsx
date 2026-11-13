"use client";

import { useEffect, useRef, useState } from "react";
import { LAST_STEP, RAIL_STOPS } from "@/lib/tour";
import { scrollToStep, subscribeTour, tourState, type TourPhase } from "@/lib/tourStore";

function ariaLabel(stop: (typeof RAIL_STOPS)[number]) {
  if (stop.key === "ALL") return "All floors";
  if (stop.key === "OUT") return "Outside the building";
  return `Floor ${stop.key}: ${stop.name}`;
}

/** Elevator-style floor panel fixed on the right. */
export function FloorRail() {
  const [active, setActive] = useState(0);
  const [phase, setPhase] = useState<TourPhase>("hero");
  const altRef = useRef<HTMLSpanElement>(null);

  useEffect(
    () =>
      subscribeTour(() => {
        setActive(Math.max(0, Math.min(LAST_STEP, Math.round(tourState.target))));
        setPhase(tourState.phase);
        if (altRef.current) altRef.current.textContent = tourState.altitude.toFixed(1);
      }),
    [],
  );

  return (
    <nav
      aria-label="Floors"
      data-phase={phase}
      className="fixed right-3 top-[72px] z-30 transition-[opacity,visibility] duration-500 data-[phase=ended]:invisible data-[phase=ended]:opacity-0 max-md:data-[phase=hero]:invisible max-md:data-[phase=hero]:opacity-0 md:right-6 md:top-1/2 md:-translate-y-1/2"
    >
      <p
        aria-hidden="true"
        className="mb-2 whitespace-nowrap rounded-[3px] bg-fb-card px-1.5 py-1 text-right font-plexmono text-[0.62rem] tracking-[0.1em] text-fb-muted"
      >
        DRONE · ALT <span ref={altRef}>0.0</span> m
      </p>
      <ol className="ml-auto flex w-fit flex-col items-center gap-[3px] rounded-[26px] border border-fb-line bg-fb-card p-1 backdrop-blur-md md:gap-1.5 md:p-1.5">
        {RAIL_STOPS.map((stop) => {
          const isActive = active === stop.step;
          return (
            <li key={stop.key} className="group relative flex items-center">
              <span
                aria-hidden="true"
                className={`pointer-events-none absolute right-full mr-3 hidden whitespace-nowrap rounded-[3px] border border-fb-line bg-fb-card px-2 py-1 font-plexmono text-[0.7rem] transition-opacity md:block ${
                  isActive
                    ? "text-fb-lamp opacity-100"
                    : "text-fb-text opacity-0 group-focus-within:opacity-100 group-hover:opacity-100"
                }`}
              >
                {stop.name}
              </span>
              <button
                type="button"
                aria-label={ariaLabel(stop)}
                aria-current={isActive ? "step" : undefined}
                onClick={() => scrollToStep(stop.step)}
                className={`flex size-11 items-center justify-center rounded-full border font-plexmono text-[0.7rem] font-medium tracking-[0.04em] transition-[background-color,border-color,box-shadow,color] duration-300 ${
                  isActive
                    ? "border-fb-lamp bg-fb-lamp text-fb-ink shadow-[0_0_14px_rgba(4,120,84,0.35)]"
                    : "border-fb-text/25 bg-white text-fb-text hover:border-fb-text/60"
                }`}
              >
                {stop.key}
              </button>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}
