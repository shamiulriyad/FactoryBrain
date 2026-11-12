"use client";

import dynamic from "next/dynamic";
import { Component, useCallback, useEffect, useState, type ReactNode } from "react";
import { FLOOR_SECTIONS, LAST_STEP, PORTRAIT_ASPECT, cameraAt, statusForFloorIndex } from "@/lib/tour";
import {
  registerTag,
  setTour,
  stepScroll,
  subscribeTour,
  tourState,
  type SceneStatus,
  type TourPhase,
  type TourState,
} from "@/lib/tourStore";
import { BuildingIllustration } from "./BuildingIllustration";
import { StatusPill } from "./TourCard";

/* The 3D scene is its own chunk, fetched only after the hero has rendered. */
const Scene = dynamic(() => import("./scene/Scene"), { ssr: false, loading: () => null });

function webglAvailable(): boolean {
  try {
    if (new URLSearchParams(window.location.search).has("no3d")) return false;
    const canvas = document.createElement("canvas");
    const gl = canvas.getContext("webgl2") || canvas.getContext("webgl");
    if (!gl) return false;
    (gl as WebGLRenderingContext).getExtension("WEBGL_lose_context")?.loseContext();
    return true;
  } catch {
    return false;
  }
}

class SceneBoundary extends Component<{ onError: () => void; children: ReactNode }, { failed: boolean }> {
  state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  componentDidCatch() {
    this.props.onError();
  }
  render() {
    return this.state.failed ? null : this.props.children;
  }
}

/**
 * Maps scroll position to tour progress: p = k when tour section k is
 * centred in the viewport, linear in between. Also decides the phase and
 * whether the canvas is visible at all.
 */
function useScrollProgress() {
  useEffect(() => {
    const mq = window.matchMedia("(prefers-reduced-motion: reduce)");
    const syncMotion = () => setTour({ reducedMotion: mq.matches });
    syncMotion();
    mq.addEventListener("change", syncMotion);

    let stops: number[] = [0];
    let tourEnd = Infinity;
    let vh = window.innerHeight;

    const update = () => {
      const y = window.scrollY;
      let p = 0;
      if (stops.length > LAST_STEP) {
        if (y >= stops[LAST_STEP]) p = LAST_STEP;
        else {
          for (let i = 0; i < LAST_STEP; i++) {
            if (y < stops[i + 1]) {
              const span = stops[i + 1] - stops[i];
              p = i + (span > 0 ? Math.max(0, y - stops[i]) / span : 0);
              break;
            }
          }
        }
      }
      const phase: TourPhase = y + vh * 0.5 > tourEnd ? "ended" : p < 0.35 ? "hero" : "tour";
      const patch: Partial<TourState> = { target: p, phase, canvasActive: y < tourEnd - 1 };
      // Without a running camera, derive smooth progress and altitude here.
      if (tourState.scene !== "ready" && tourState.scene !== "loading") {
        const portrait = window.innerWidth / vh < PORTRAIT_ASPECT;
        patch.smooth = p;
        patch.altitude = Math.round((cameraAt(p, portrait).pos[1] + 0.5) * 10) / 10;
      }
      setTour(patch);
    };

    const measure = () => {
      vh = window.innerHeight;
      const sy = window.scrollY;
      const next = [0];
      for (let k = 1; k <= LAST_STEP; k++) {
        const el = document.getElementById(`tour-${k}`);
        if (!el) return;
        const r = el.getBoundingClientRect();
        next.push(Math.max(0, r.top + sy + r.height / 2 - vh / 2));
      }
      stops = next;
      stepScroll.splice(0, stepScroll.length, ...next);
      const last = document.getElementById(`tour-${LAST_STEP}`)!.getBoundingClientRect();
      tourEnd = last.bottom + sy;
      update();
    };

    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(document.body);
    window.addEventListener("scroll", update, { passive: true });
    window.addEventListener("resize", measure);
    return () => {
      mq.removeEventListener("change", syncMotion);
      ro.disconnect();
      window.removeEventListener("scroll", update);
      window.removeEventListener("resize", measure);
    };
  }, []);
}

/**
 * Starts loading the scene on the first sign of a visitor: scroll, wheel,
 * touch, key or mouse movement. The hero shows a poster of the same frame
 * until then, so first paint and the main thread stay free for the text.
 * If the page opens already scrolled (a #hash link, restored scroll), load now.
 */
function useLoadTrigger(enabled: boolean, onLoad: () => void) {
  useEffect(() => {
    if (!enabled) return;
    let fired = false;
    const events = ["scroll", "wheel", "pointerdown", "pointermove", "touchstart", "keydown"] as const;
    const fire = () => {
      if (fired) return;
      fired = true;
      cleanup();
      onLoad();
    };
    function cleanup() {
      events.forEach((e) => window.removeEventListener(e, fire));
    }
    if (window.scrollY > 0) {
      fire();
      return;
    }
    events.forEach((e) => window.addEventListener(e, fire, { passive: true, once: true }));
    return cleanup;
  }, [enabled, onLoad]);
}

export function TourBackdrop() {
  const [scene, setScene] = useState<SceneStatus>("idle");
  const [phase, setPhase] = useState<TourPhase>("hero");
  const [posterOk, setPosterOk] = useState(true);

  useScrollProgress();

  useEffect(() => subscribeTour(() => setPhase(tourState.phase)), []);

  const go = useCallback((status: SceneStatus) => {
    setScene(status);
    setTour({ scene: status });
  }, []);
  // The WebGL probe creates a context, which is not free; only pay for it
  // once the visitor actually starts the tour.
  const load = useCallback(() => go(webglAvailable() ? "loading" : "fallback"), [go]);
  const ready = useCallback(() => go("ready"), [go]);
  const fail = useCallback(() => go("fallback"), [go]);

  useLoadTrigger(scene === "idle", load);

  const showScene = scene === "loading" || scene === "ready";
  const posterVisible = scene !== "ready" && !(scene === "fallback" && phase !== "hero");

  return (
    <div aria-hidden="true" className="pointer-events-none fixed inset-0 z-0 overflow-hidden" data-scene={scene}>
      <div className="fb-sky absolute inset-0" />

      {posterOk && (
        <picture>
          <source media={`(max-aspect-ratio: ${Math.round(PORTRAIT_ASPECT * 10)}/10)`} srcSet="/home/poster-tall.jpg" />
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src="/home/poster-wide.jpg"
            alt=""
            decoding="async"
            onError={() => setPosterOk(false)}
            className={`absolute inset-0 h-full w-full object-cover transition-opacity duration-700 ${
              posterVisible ? "opacity-100" : "opacity-0"
            }`}
          />
        </picture>
      )}

      {showScene && (
        <div className={`absolute inset-0 transition-opacity duration-700 ${scene === "ready" ? "opacity-100" : "opacity-0"}`}>
          <SceneBoundary onError={fail}>
            <Scene onReady={ready} />
          </SceneBoundary>
        </div>
      )}

      {scene === "fallback" && (
        <div
          className={`absolute inset-x-4 top-[76px] h-[46svh] transition-opacity duration-500 md:inset-x-auto md:right-[7%] md:top-[12svh] md:h-[76svh] md:w-[48vw] ${
            phase === "hero" ? "opacity-0" : "opacity-100"
          }`}
        >
          <BuildingIllustration />
        </div>
      )}

      {/* Screen-space tags; the scene writes their position every frame. */}
      {showScene && (
        <div className="absolute inset-0">
          {FLOOR_SECTIONS.map((s, i) => (
            <div
              key={s.step}
              ref={(el) => registerTag(`det-${i}`, el)}
              className="absolute left-0 top-0 opacity-0 will-change-transform"
              data-example="true"
            >
              <span className="absolute -left-[3px] -top-[3px] block size-[7px] rounded-full bg-fb-thread" />
              <span className="absolute bottom-0 left-0 block h-10 w-px bg-fb-thread" />
              <span className="absolute bottom-10 left-0 block -translate-x-1/2 whitespace-nowrap rounded-[3px] border border-fb-thread/70 bg-fb-card px-2 py-1 font-plexmono text-[0.7rem] leading-tight text-fb-text">
                <span className="text-fb-thread">AI</span> · {s.detection.label}
              </span>
            </div>
          ))}
          {FLOOR_SECTIONS.map((s, i) => {
            const st = statusForFloorIndex(s.floorIndex);
            return (
              <div
                key={`chip-${s.step}`}
                ref={(el) => registerTag(`chip-${i}`, el)}
                className="absolute left-0 top-0 opacity-0 will-change-transform"
                data-example="true"
              >
                <span className="absolute left-0 top-0 flex -translate-y-1/2 items-center gap-2 whitespace-nowrap rounded-full border border-fb-line bg-fb-card py-1 pl-2.5 pr-1.5 font-plexmono text-[0.7rem] text-fb-text">
                  {st.floor} · {st.short}
                  <StatusPill pill={st.pill} />
                </span>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}
