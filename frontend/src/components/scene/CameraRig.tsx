"use client";

import { useMemo, useRef } from "react";
import { useFrame } from "@react-three/fiber";
import * as THREE from "three";
import { FOV, LAST_STEP, PORTRAIT_ASPECT, cameraAt, type CameraPose } from "@/lib/tour";
import { setTour, tourState } from "@/lib/tourStore";

/**
 * Drives the camera from tourState.target (scroll progress). Damps towards
 * it with frame-rate-independent smoothing, adds a very small hover sway,
 * and publishes the damped progress and altitude back to the store.
 */
export function CameraRig() {
  const pose = useMemo<CameraPose>(() => ({ pos: [0, 0, 0], target: [0, 0, 0] }), []);
  const look = useMemo(() => new THREE.Vector3(), []);
  const smooth = useRef<number | null>(null);
  const lastAlt = useRef(-1);

  useFrame((state, delta) => {
    const cam = state.camera as THREE.PerspectiveCamera;
    const dt = Math.min(delta, 0.1);
    const goal = tourState.target;
    const reduced = tourState.reducedMotion;

    let p: number;
    if (reduced) {
      // No flight: jump-cut to the nearest keyframe.
      p = Math.max(0, Math.min(LAST_STEP, Math.round(goal)));
    } else if (smooth.current === null) {
      p = goal;
    } else {
      p = smooth.current + (goal - smooth.current) * (1 - Math.exp(-dt * 5));
      if (Math.abs(goal - p) < 1e-4) p = goal;
    }
    smooth.current = p;

    const portrait = state.size.width / state.size.height < PORTRAIT_ASPECT;
    const fov = portrait ? FOV.portrait : FOV.desktop;
    if (cam.fov !== fov) {
      cam.fov = fov;
      cam.updateProjectionMatrix();
    }

    cameraAt(p, portrait, pose);
    let [x, y, z] = pose.pos;
    if (!reduced) {
      const t = state.clock.elapsedTime;
      x += Math.sin(t * 0.53) * 0.06;
      y += Math.sin(t * 0.81 + 1.3) * 0.045;
      z += Math.sin(t * 0.37 + 0.4) * 0.05;
    }
    cam.position.set(x, y, z);
    look.set(pose.target[0], pose.target[1], pose.target[2]);
    cam.lookAt(look);

    // Height above the yard (ground sits at y = -0.5).
    const alt = Math.round((pose.pos[1] + 0.5) * 10) / 10;
    if (tourState.smooth !== p || alt !== lastAlt.current) {
      lastAlt.current = alt;
      setTour({ smooth: p, altitude: alt });
    }
  });

  return null;
}
