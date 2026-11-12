"use client";

import { Children, useEffect, useRef, useState, type ReactNode } from "react";
import { Canvas, useFrame } from "@react-three/fiber";
import { PerformanceMonitor } from "@react-three/drei";
import { CAMERA_KEYS } from "@/lib/tour";
import { subscribeTour, tourState } from "@/lib/tourStore";
import { Building } from "./Building";
import { CameraRig } from "./CameraRig";
import { Detections } from "./Detections";
import { Site } from "./Site";
import { FloorG } from "./floors/FloorG";
import { Floor1 } from "./floors/Floor1";
import { Floor2 } from "./floors/Floor2";
import { Floor3 } from "./floors/Floor3";
import { Floor4 } from "./floors/Floor4";
import { C } from "./palette";

const MAX_DPR = 1.75;

/**
 * Mounts children one per frame so building the scene never blocks the
 * main thread in one long task. Calls onDone once everything is mounted.
 */
function Staged({ children, onDone }: { children: ReactNode; onDone: () => void }) {
  const items = Children.toArray(children);
  const [count, setCount] = useState(1);
  useEffect(() => {
    if (count >= items.length) {
      onDone();
      return;
    }
    let timer = 0;
    const raf = requestAnimationFrame(() => {
      timer = window.setTimeout(() => setCount((c) => c + 1), 0);
    });
    return () => {
      cancelAnimationFrame(raf);
      clearTimeout(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [count, items.length]);
  return <>{items.slice(0, count)}</>;
}

/** Fires onReady after the fully mounted scene has rendered two frames. */
function ReadySignal({ armed, onReady }: { armed: boolean; onReady: () => void }) {
  const frames = useRef(0);
  const done = useRef(false);
  useFrame(() => {
    if (!armed || done.current) return;
    frames.current += 1;
    if (frames.current >= 2) {
      done.current = true;
      onReady();
    }
  });
  return null;
}

export default function Scene({ onReady }: { onReady: () => void }) {
  const [active, setActive] = useState(true);
  const [mounted, setMounted] = useState(false);
  const [dpr, setDpr] = useState(() =>
    typeof window === "undefined" ? 1 : Math.min(window.devicePixelRatio || 1, MAX_DPR),
  );

  useEffect(() => {
    const update = () => setActive(tourState.canvasActive && document.visibilityState === "visible");
    update();
    const unsub = subscribeTour(update);
    document.addEventListener("visibilitychange", update);
    return () => {
      unsub();
      document.removeEventListener("visibilitychange", update);
    };
  }, []);

  const start = CAMERA_KEYS[0].desktop;

  return (
    <Canvas
      frameloop={active ? "always" : "never"}
      dpr={dpr}
      flat
      gl={{ antialias: true, alpha: true, powerPreference: "high-performance", stencil: false }}
      camera={{ fov: 42, near: 0.5, far: 700, position: start.pos }}
      onCreated={({ gl }) => gl.setClearColor(0x000000, 0)}
      style={{ position: "absolute", inset: 0 }}
    >
      <PerformanceMonitor
        onDecline={() => setDpr(1)}
        onIncline={() => setDpr(Math.min(window.devicePixelRatio || 1, MAX_DPR))}
      />
      <fog attach="fog" args={[C.fog, 80, 330]} />
      <hemisphereLight args={["#9aa0c0", "#3a332e", 1.05]} />
      {/* Warm, low dusk sun from the front-left. */}
      <directionalLight position={[-90, 22, 60]} color="#ffb98a" intensity={1.35} />
      {/* Cool rim light from behind on the right. */}
      <directionalLight position={[70, 45, -80]} color="#86a0e0" intensity={0.75} />
      <CameraRig />
      <Staged onDone={() => setMounted(true)}>
        <Building />
        <Site />
        <FloorG />
        <Floor1 />
        <Floor2 />
        <Floor3 />
        <Floor4 />
        <Detections />
      </Staged>
      <ReadySignal armed={mounted} onReady={onReady} />
    </Canvas>
  );
}
