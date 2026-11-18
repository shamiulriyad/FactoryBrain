"use client";

// The viewer's own webcam through getUserMedia: device choice, start / stop,
// permission and hardware errors explained, and the stream always released
// (stop, unmount, device unplugged). Needs a secure context (HTTPS or
// localhost); nothing is installed on the viewer's computer.

import { useCallback, useEffect, useRef, useState } from "react";

export type CameraErrorCode = "insecure" | "unsupported" | "denied" | "not_found" | "in_use" | "disconnected" | "failed";

export type CameraError = { code: CameraErrorCode; message: string };

export type BrowserCamera = {
  supported: boolean;
  secure: boolean;
  devices: { deviceId: string; label: string }[];
  deviceId: string | null;
  setDeviceId: (id: string | null) => void;
  stream: MediaStream | null;
  status: "idle" | "starting" | "live";
  error: CameraError | null;
  start: (deviceId?: string | null) => Promise<boolean>;
  stop: () => void;
  /** Settings of the live video track (actual size the camera delivers). */
  settings: MediaTrackSettings | null;
};

function explain(e: unknown): CameraError {
  const name = e instanceof Error ? e.name : "";
  switch (name) {
    case "NotAllowedError":
    case "SecurityError":
    // Chromium reports a blocked permission prompt (site setting, policy) as NotSupportedError.
    case "NotSupportedError":
      return { code: "denied", message: "Camera permission was refused. Allow the camera for this site in the browser's address bar (camera icon) or site settings, then press Start camera again." };
    case "NotFoundError":
    case "OverconstrainedError":
      return { code: "not_found", message: "No camera was found. Connect a webcam, or pick another camera in the list." };
    case "NotReadableError":
    case "AbortError":
      return { code: "in_use", message: "The camera could not be opened. Another app (Zoom, Teams, another tab) may be using it — close it and try again." };
    default:
      return { code: "failed", message: e instanceof Error && e.message ? `The browser couldn't open the camera: ${e.message}` : "The browser couldn't open the camera." };
  }
}

export function useBrowserCamera(): BrowserCamera {
  const [devices, setDevices] = useState<{ deviceId: string; label: string }[]>([]);
  const [deviceId, setDeviceId] = useState<string | null>(null);
  const [stream, setStream] = useState<MediaStream | null>(null);
  const [status, setStatus] = useState<"idle" | "starting" | "live">("idle");
  const [error, setError] = useState<CameraError | null>(null);
  const [settings, setSettings] = useState<MediaTrackSettings | null>(null);
  const streamRef = useRef<MediaStream | null>(null);

  const supported = typeof navigator !== "undefined" && !!navigator.mediaDevices?.getUserMedia;
  const secure = typeof window === "undefined" ? true : window.isSecureContext;

  const release = useCallback(() => {
    streamRef.current?.getTracks().forEach((t) => {
      t.onended = null;
      t.stop();
    });
    streamRef.current = null;
    setStream(null);
    setSettings(null);
    setStatus("idle");
  }, []);

  const refreshDevices = useCallback(async () => {
    if (!navigator.mediaDevices?.enumerateDevices) return;
    try {
      const all = await navigator.mediaDevices.enumerateDevices();
      const cams = all.filter((d) => d.kind === "videoinput").map((d, i) => ({ deviceId: d.deviceId, label: d.label || `Camera ${i + 1}` }));
      setDevices(cams);
    } catch {
      /* the list stays as it was */
    }
  }, []);

  useEffect(() => {
    if (!supported) return;
    void refreshDevices();
    const onChange = () => {
      void refreshDevices().then(async () => {
        // The camera in use was unplugged: release it and say so.
        const live = streamRef.current?.getVideoTracks()[0];
        if (live && live.readyState === "ended") {
          release();
          setError({ code: "disconnected", message: "The camera was disconnected." });
        }
      });
    };
    navigator.mediaDevices.addEventListener?.("devicechange", onChange);
    return () => navigator.mediaDevices.removeEventListener?.("devicechange", onChange);
  }, [supported, refreshDevices, release]);

  // Always release the camera when the component goes away.
  useEffect(() => () => {
    streamRef.current?.getTracks().forEach((t) => t.stop());
    streamRef.current = null;
  }, []);

  const start = useCallback(async (wanted?: string | null) => {
    setError(null);
    if (!secure) {
      setError({ code: "insecure", message: "Browsers only allow the camera on secure pages. Open the site over https:// (or on localhost)." });
      return false;
    }
    if (!supported) {
      setError({ code: "unsupported", message: "This browser can't open a camera. Use a current Chrome, Edge, Firefox or Safari." });
      return false;
    }
    release();
    setStatus("starting");
    const id = wanted === undefined ? deviceId : wanted;
    try {
      const s = await navigator.mediaDevices.getUserMedia({
        audio: false,
        video: id ? { deviceId: { exact: id }, width: { ideal: 1280 }, height: { ideal: 720 } } : { width: { ideal: 1280 }, height: { ideal: 720 } }
      });
      const track = s.getVideoTracks()[0];
      track.onended = () => {
        release();
        setError({ code: "disconnected", message: "The camera stopped (unplugged, or the browser revoked access)." });
      };
      streamRef.current = s;
      setStream(s);
      setSettings(track.getSettings());
      if (track.getSettings().deviceId) setDeviceId(track.getSettings().deviceId ?? null);
      setStatus("live");
      void refreshDevices();   // labels are only readable after permission
      return true;
    } catch (e) {
      release();
      setError(explain(e));
      return false;
    }
  }, [deviceId, release, refreshDevices, secure, supported]);

  const stop = useCallback(() => {
    release();
    setError(null);
  }, [release]);

  return { supported, secure, devices, deviceId, setDeviceId, stream, status, error, start, stop, settings };
}
