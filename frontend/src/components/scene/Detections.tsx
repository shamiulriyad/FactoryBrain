"use client";

import { useEffect, useMemo, useRef } from "react";
import { useFrame } from "@react-three/fiber";
import * as THREE from "three";
import { BUILDING, FLOOR_SECTIONS, PORTRAIT_ASPECT, floorBase } from "@/lib/tour";
import { getTag, tourState } from "@/lib/tourStore";

const THREE_COLOR = new THREE.Color("#ff6b45");
const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);

/**
 * AI detections: a pulsing outline box with a faint fill on each floor's
 * target, drawn over everything. Also positions the HTML tags (one per
 * detection, plus one status chip per floor for the final x-ray view).
 */
export function Detections() {
  const parts = useMemo(
    () =>
      FLOOR_SECTIONS.map((s) => {
        const box = new THREE.BoxGeometry(...s.detection.size);
        const edges = new THREE.EdgesGeometry(box);
        const line = new THREE.LineBasicMaterial({ color: THREE_COLOR, transparent: true, depthTest: false, depthWrite: false, toneMapped: false });
        const fill = new THREE.MeshBasicMaterial({ color: THREE_COLOR, transparent: true, opacity: 0, depthTest: false, depthWrite: false, toneMapped: false });
        return { box, edges, line, fill, center: s.detection.center, size: s.detection.size };
      }),
    [],
  );
  const groups = useRef<(THREE.Group | null)[]>([]);
  const v = useMemo(() => new THREE.Vector3(), []);

  useEffect(
    () => () =>
      parts.forEach((p) => {
        p.box.dispose();
        p.edges.dispose();
        p.line.dispose();
        p.fill.dispose();
      }),
    [parts],
  );

  useFrame((state) => {
    const p = tourState.smooth;
    const t = state.clock.elapsedTime;
    const still = tourState.reducedMotion;
    const xray = clamp01((p - 5.2) / 0.8);
    const wave = still ? 1 : 0.5 + 0.5 * Math.sin(t * 3.4);
    const { width, height } = state.size;
    const camera = state.camera;
    // Chips sit on the facade on narrow screens so they never run off the edge.
    const chipX = width / height < PORTRAIT_ASPECT ? -7 : BUILDING.halfWidth + 0.4;

    const place = (el: HTMLElement | undefined, point: THREE.Vector3, opacity: number) => {
      if (!el) return;
      if (opacity < 0.02) {
        el.style.opacity = "0";
        return;
      }
      v.copy(point).project(camera);
      if (v.z > 1) {
        el.style.opacity = "0";
        return;
      }
      const x = Math.round((v.x * 0.5 + 0.5) * width);
      const y = Math.round((-v.y * 0.5 + 0.5) * height);
      el.style.transform = `translate3d(${x}px, ${y}px, 0)`;
      el.style.opacity = opacity.toFixed(3);
    };

    parts.forEach((part, i) => {
      const step = i + 1;
      const near = clamp01(1 - Math.abs(p - step) * 1.6);
      const w = Math.max(near, xray);
      const g = groups.current[i];
      if (g) {
        g.visible = w > 0.01;
        const s = still ? 1 : 1 + 0.035 * wave;
        g.scale.setScalar(s);
      }
      part.line.opacity = w * (0.65 + 0.35 * wave);
      part.fill.opacity = w * (0.08 + 0.08 * wave);

      // Detection tag: above the box, only while that floor is in view.
      const tagOpacity = near * (1 - xray);
      v.set(part.center[0], part.center[1] + part.size[1] / 2, part.center[2]);
      place(getTag(`det-${i}`), v, tagOpacity);

      // Status chip at the floor's front-right corner, in the x-ray view.
      v.set(chipX, floorBase(i) + 2.1, BUILDING.halfDepth + 0.2);
      place(getTag(`chip-${i}`), v, xray);
    });
  });

  return (
    <group>
      {parts.map((part, i) => (
        <group key={i} ref={(g) => {
            groups.current[i] = g;
          }} position={part.center} visible={false}>
          <lineSegments geometry={part.edges} material={part.line} renderOrder={1000} />
          <mesh geometry={part.box} material={part.fill} renderOrder={999} />
        </group>
      ))}
    </group>
  );
}
