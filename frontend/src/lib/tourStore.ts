/**
 * Tiny mutable store shared by the scroll controller, the 3D scene (which
 * lives in a separate React renderer) and the floor rail. Per-frame values
 * are read directly; listeners fire at most once per animation frame.
 */

export type TourPhase = "hero" | "tour" | "ended";
export type SceneStatus = "idle" | "loading" | "ready" | "fallback";

export interface TourState {
  /** Progress from scroll position: k when tour section k is centred. */
  target: number;
  /** Damped progress the camera is actually at. */
  smooth: number;
  /** Camera height in metres. */
  altitude: number;
  phase: TourPhase;
  reducedMotion: boolean;
  scene: SceneStatus;
  /** False once the tour has scrolled away (canvas fully covered) or the tab is hidden. */
  canvasActive: boolean;
}

export const tourState: TourState = {
  target: 0,
  smooth: 0,
  altitude: 0,
  phase: "hero",
  reducedMotion: false,
  scene: "idle",
  canvasActive: true,
};

type Listener = () => void;
const listeners = new Set<Listener>();
let queued = false;

export function subscribeTour(fn: Listener): () => void {
  listeners.add(fn);
  return () => {
    listeners.delete(fn);
  };
}

/** Merge a partial update and notify listeners on the next frame. */
export function setTour(patch: Partial<TourState>): void {
  Object.assign(tourState, patch);
  if (queued) return;
  queued = true;
  const flush = () => {
    queued = false;
    listeners.forEach((l) => l());
  };
  if (typeof requestAnimationFrame === "function") requestAnimationFrame(flush);
  else flush();
}

/* Screen-space tags drawn over the canvas. The scene positions them by
   writing transforms straight to these elements every frame. */
const tagEls = new Map<string, HTMLElement>();

export function registerTag(id: string, el: HTMLElement | null): void {
  if (el) tagEls.set(id, el);
  else tagEls.delete(id);
}

export function getTag(id: string): HTMLElement | undefined {
  return tagEls.get(id);
}

/** Scroll offsets at which each step (0..6) is centred; filled by the controller. */
export const stepScroll: number[] = [];

export function scrollToStep(step: number): void {
  const top = stepScroll[step];
  if (top === undefined) return;
  window.scrollTo({ top, behavior: tourState.reducedMotion ? "auto" : "smooth" });
}
