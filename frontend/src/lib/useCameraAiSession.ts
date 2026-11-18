"use client";

// Drives one camera AI session: starts it on the server, then samples the
// live <video> at the chosen interval (default 1 frame per second), sends a
// JPEG of the CURRENT frame, waits for the answer, and only then takes the
// next one. There is never more than one request in flight and frames are
// never queued: the latest frame wins. 30 fps preview frames stay in the
// browser; only the sampled frames leave it.

import { useCallback, useEffect, useRef, useState } from "react";
import { isApiError } from "@/lib/api";
import {
  problemCode,
  sendCameraAiFrame,
  startCameraAiSession,
  stopCameraAiSession,
  stopCameraAiSessionOnUnload,
  type CameraAiAnalysis,
  type CameraAiError,
  type CameraAiEvent,
  type CameraAiSession
} from "@/lib/cameraAi";

export type AiPhase = "idle" | "starting" | "running" | "waiting" | "stopping" | "ended" | "error";

export type AiSessionState = {
  phase: AiPhase;
  session: CameraAiSession | null;
  analysis: CameraAiAnalysis | null;
  /** The last per-frame AI failure (OpenAI time-out, invalid answer…). */
  frameError: CameraAiError | null;
  /** Why the session can't run or stopped (server refused, ended, network). */
  problem: { code: string | null; message: string } | null;
  /** Frames this page sent / skipped (skipped = busy or too soon; never queued). */
  sent: number;
  skipped: number;
  /** Browser-measured time from sending a frame to its answer. */
  roundTripMs: number | null;
  /** Bytes of the last JPEG sent. */
  frameBytes: number | null;
  events: CameraAiEvent[];
  /** Effective analysed frames per second over the last answers. */
  effectiveFps: number | null;
};

const INITIAL: AiSessionState = {
  phase: "idle", session: null, analysis: null, frameError: null, problem: null, sent: 0, skipped: 0, roundTripMs: null, frameBytes: null, events: [],
  effectiveFps: null
};

export type FrameOptions = { intervalMs: number; maxWidth: number; quality: number; maxBytes: number };

const sleep = (ms: number, signal: AbortSignal) =>
  new Promise<void>((resolve) => {
    if (signal.aborted) return resolve();
    const t = setTimeout(resolve, ms);
    signal.addEventListener("abort", () => { clearTimeout(t); resolve(); }, { once: true });
  });

/** JPEG of what the video shows right now, scaled to at most maxWidth; quality lowered once if it is too big. */
async function capture(video: HTMLVideoElement, canvas: HTMLCanvasElement, o: FrameOptions): Promise<Blob | null> {
  const w = video.videoWidth, h = video.videoHeight;
  if (!w || !h || video.readyState < 2) return null;
  const scale = Math.min(1, o.maxWidth / w);
  canvas.width = Math.round(w * scale);
  canvas.height = Math.round(h * scale);
  const ctx = canvas.getContext("2d");
  if (!ctx) return null;
  ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
  const encode = (q: number) => new Promise<Blob | null>((res) => canvas.toBlob(res, "image/jpeg", q));
  let blob = await encode(o.quality);
  if (blob && blob.size > o.maxBytes) blob = await encode(Math.max(0.4, o.quality - 0.25));
  return blob && blob.size <= o.maxBytes ? blob : null;
}

export function useCameraAiSession(cameraId: string, videoRef: React.RefObject<HTMLVideoElement | null>) {
  const [state, setState] = useState<AiSessionState>(INITIAL);
  const loop = useRef<AbortController | null>(null);
  const sessionId = useRef<string | null>(null);
  const canvas = useRef<HTMLCanvasElement | null>(null);
  const answeredAt = useRef<number[]>([]);

  const patch = useCallback((p: Partial<AiSessionState> | ((s: AiSessionState) => Partial<AiSessionState>)) =>
    setState((s) => ({ ...s, ...(typeof p === "function" ? p(s) : p) })), []);

  const addEvents = useCallback((events: CameraAiEvent[]) => {
    if (events.length === 0) return;
    patch((s) => {
      const seen = new Set(s.events.map((e) => e.id ?? `${e.eventType}-${e.occurredAt}`));
      const fresh = events.filter((e) => !seen.has(e.id ?? `${e.eventType}-${e.occurredAt}`));
      return { events: [...fresh.reverse(), ...s.events].slice(0, 30) };
    });
  }, [patch]);

  const run = useCallback(async (id: string, opts: () => FrameOptions, signal: AbortSignal) => {
    let backoff = 0;
    while (!signal.aborted) {
      const o = opts();
      const started = performance.now();
      const video = videoRef.current;
      canvas.current ??= document.createElement("canvas");
      const blob = video ? await capture(video, canvas.current, o) : null;
      if (signal.aborted) break;
      let wait = o.intervalMs;
      if (!blob) {
        // The video isn't ready yet (or a frame couldn't be encoded small enough): try again shortly.
        await sleep(250, signal);
        continue;
      }
      try {
        patch((s) => ({ sent: s.sent + 1, frameBytes: blob.size }));
        const r = await sendCameraAiFrame(id, blob, signal);
        const now = performance.now();
        answeredAt.current = [...answeredAt.current, now].slice(-6);
        const a = answeredAt.current;
        const fps = a.length >= 2 ? ((a.length - 1) * 1000) / (a[a.length - 1] - a[0]) : null;
        backoff = 0;
        // A failed frame keeps the last good analysis on screen (marked by its time), next to the error.
        patch((s) => ({
          phase: "running", session: r.session, roundTripMs: Math.round(now - started), effectiveFps: fps,
          analysis: r.analysis ?? s.analysis, frameError: r.outcome === "failed" ? r.error ?? null : null, problem: null
        }));
        addEvents(r.events);
      } catch (e) {
        if (signal.aborted) break;
        const code = problemCode(e);
        if (isApiError(e) && (code === "analysis_in_progress" || code === "frame_too_soon" || code === "ai_busy")) {
          // Skip this frame; the next one is taken fresh after the server's hint.
          patch((s) => ({ skipped: s.skipped + 1 }));
          wait = Math.max(wait, (e.retryAfterSeconds ?? 1) * 1000);
        } else if (isApiError(e) && (code === "session_not_found" || code === "session_expired" || code === "daily_limit_reached" || e.status === 401 || e.status === 403)) {
          patch({ phase: "ended", problem: { code, message: e.message } });
          break;
        } else {
          // Network or server trouble: keep the camera running and retry with back-off (the server reports the camera offline if this lasts).
          backoff = Math.min(15_000, backoff ? backoff * 2 : 2_000);
          patch({ phase: "waiting", problem: { code: code ?? (isApiError(e) ? e.kind : "network"), message: isApiError(e) ? e.message : "Connection lost; retrying." } });
          wait = backoff;
        }
      }
      const elapsed = performance.now() - started;
      await sleep(Math.max(0, wait - elapsed), signal);
    }
  }, [addEvents, patch, videoRef]);

  const start = useCallback(async (opts: () => FrameOptions) => {
    loop.current?.abort();
    answeredAt.current = [];
    setState({ ...INITIAL, phase: "starting" });
    try {
      const s = await startCameraAiSession(cameraId, opts().intervalMs);
      sessionId.current = s.id;
      const ctrl = new AbortController();
      loop.current = ctrl;
      patch({ phase: "running", session: s });
      void run(s.id, opts, ctrl.signal);
      return true;
    } catch (e) {
      patch({ phase: "error", problem: { code: problemCode(e), message: isApiError(e) ? e.message : "Could not start the AI session." } });
      return false;
    }
  }, [cameraId, patch, run]);

  const stop = useCallback(async () => {
    loop.current?.abort();
    loop.current = null;
    const id = sessionId.current;
    sessionId.current = null;
    if (!id) {
      patch({ phase: "idle" });
      return;
    }
    patch({ phase: "stopping" });
    try {
      const s = await stopCameraAiSession(id);
      patch({ phase: "idle", session: s });
    } catch {
      patch({ phase: "idle" });   // the server's idle timeout ends it anyway
    }
  }, [patch]);

  // Closing the tab or navigating away ends the session (and never leaves a loop running).
  useEffect(() => {
    const onHide = () => { if (sessionId.current) stopCameraAiSessionOnUnload(sessionId.current); };
    window.addEventListener("pagehide", onHide);
    return () => {
      window.removeEventListener("pagehide", onHide);
      loop.current?.abort();
      if (sessionId.current) stopCameraAiSessionOnUnload(sessionId.current);
      sessionId.current = null;
    };
  }, []);

  return { state, start, stop, addEvents };
}
