"use client";

import { useEffect, useMemo, useRef } from "react";
import { useFrame } from "@react-three/fiber";
import * as THREE from "three";
import { mergeGeometries } from "three/examples/jsm/utils/BufferGeometryUtils.js";
import { BUILDING, floorBase } from "@/lib/tour";
import { tourState } from "@/lib/tourStore";
import { Batch, Inst, StaticMesh, geoBox, type InstItem } from "./geom";
import { C } from "./palette";

const H = BUILDING.floorHeight;
const HW = BUILDING.halfWidth;
const HD = BUILDING.halfDepth;
const N = BUILDING.floors;
const ROOF = N * H;
const CLEAR = H - 0.34; // floor-to-ceiling clear height

function buildStructure() {
  const b = new Batch();
  // Plinth and ground floor slab.
  b.box(0, -0.25, 0, HW * 2 + 1.2, 0.5, HD * 2 + 1.2, C.concreteDark);
  for (let i = 1; i <= N; i++) {
    const y = i * H;
    b.box(0, y - 0.17, 0, HW * 2 + 0.6, 0.34, HD * 2 + 0.6, C.concrete);
    // Spandrel bands on the front and sides.
    b.box(0, y - 0.2, HD + 0.32, HW * 2 + 0.7, 0.5, 0.06, C.slabEdge);
    b.box(-HW - 0.32, y - 0.2, 0, 0.06, 0.5, HD * 2 + 0.7, C.slabEdge);
    b.box(HW + 0.32, y - 0.2, 0, 0.06, 0.5, HD * 2 + 0.7, C.slabEdge);
  }
  for (let i = 0; i < N; i++) {
    const y0 = floorBase(i);
    // Green epoxy floor with yellow walkway lines.
    b.box(0, y0 + 0.015, 0, HW * 2 - 0.6, 0.03, HD * 2 - 0.6, C.epoxy);
    b.box(0, y0 + 0.033, 8.9, HW * 2 - 2, 0.006, 0.09, C.epoxyLine);
    b.box(0, y0 + 0.033, 9.9, HW * 2 - 2, 0.006, 0.09, C.epoxyLine);
    b.box(0, y0 + 0.033, -4.6, HW * 2 - 2, 0.006, 0.09, C.epoxyLine);
    // Columns: front, sides, interior row.
    for (const x of BUILDING.columnXs) {
      const cx = Math.max(-HW + 0.3, Math.min(HW - 0.3, x));
      b.block(cx, y0, HD - 0.3, 0.6, CLEAR, 0.6, C.concrete);
    }
    for (const sx of [-HW + 0.3, HW - 0.3]) b.block(sx, y0, 0, 0.6, CLEAR, 0.6, C.concrete);
    for (const x of BUILDING.interiorColumnXs) b.block(x, y0, BUILDING.interiorColumnZ, 0.55, CLEAR, 0.55, C.concrete);
    // Mullions: front every 2.25 m, sides every 2.2 m, plus sill and head rails.
    for (let k = 0; k <= 16; k++) b.block(-HW + k * 2.25, y0, HD + 0.08, 0.07, CLEAR, 0.08, C.mullion);
    for (let k = 0; k <= 10; k++) {
      const z = -HD + k * 2.2;
      b.block(-HW - 0.08, y0, z, 0.08, CLEAR, 0.07, C.mullion);
      b.block(HW + 0.08, y0, z, 0.08, CLEAR, 0.07, C.mullion);
    }
    for (const yy of [y0 + 0.9, y0 + CLEAR - 0.6]) {
      b.box(0, yy, HD + 0.08, HW * 2, 0.06, 0.08, C.mullion);
      b.box(-HW - 0.08, yy, 0, 0.08, 0.06, HD * 2, C.mullion);
      b.box(HW + 0.08, yy, 0, 0.08, 0.06, HD * 2, C.mullion);
    }
  }
  // Solid back wall, full height.
  b.box(0, ROOF / 2, -HD - 0.05, HW * 2 + 0.6, ROOF, 0.4, C.backWall);
  for (let i = 1; i < N; i++) b.box(0, i * H - 0.2, -HD - 0.27, HW * 2 + 0.6, 0.3, 0.06, C.wallTrim);
  // Roof finish and parapet.
  b.box(0, ROOF + 0.02, 0, HW * 2, 0.04, HD * 2, "#5b5d5f");
  b.block(0, ROOF, HD + 0.18, HW * 2 + 0.6, 1.0, 0.25, C.concreteDark);
  b.block(0, ROOF, -HD - 0.18, HW * 2 + 0.6, 1.0, 0.25, C.concreteDark);
  b.block(-HW - 0.18, ROOF, 0, 0.25, 1.0, HD * 2 + 0.6, C.concreteDark);
  b.block(HW + 0.18, ROOF, 0, 0.25, 1.0, HD * 2 + 0.6, C.concreteDark);
  // Stair and lift core.
  b.block(-13.5, ROOF, -7.5, 4.6, 3.2, 4.2, C.concrete);
  b.block(-13.5, ROOF + 3.2, -7.5, 4.9, 0.2, 4.5, C.concreteDark);
  b.block(-13.5, ROOF, -5.38, 1.1, 2.1, 0.05, C.steelDark);
  // HVAC units with fan discs.
  for (let k = 0; k < 4; k++) {
    for (const z of [-8.2, -4.6]) {
      const x = 5.5 + k * 2.6;
      b.block(x, ROOF, z, 1.9, 1.05, 1.5, C.hvac);
      b.cyl(x - 0.4, ROOF + 1.08, z, 0.38, 0.06, C.steelDark, "y", 10);
      b.cyl(x + 0.45, ROOF + 1.08, z, 0.38, 0.06, C.steelDark, "y", 10);
    }
  }
  b.box(9.4, ROOF + 0.35, -6.4, 10.5, 0.25, 0.3, C.steel); // duct run
  // Water tank on legs.
  for (const [dx, dz] of [[-0.9, -0.9], [0.9, -0.9], [-0.9, 0.9], [0.9, 0.9]]) {
    b.block(-14 + dx, ROOF, 6.5 + dz, 0.16, 1.5, 0.16, C.steelDark);
  }
  b.block(-14, ROOF + 1.5, 6.5, 2.4, 0.1, 2.4, C.steelDark);
  b.cyl(-14, ROOF + 2.65, 6.5, 1.15, 2.2, "#7c8183", "y", 12);
  b.cyl(-14, ROOF + 3.8, 6.5, 1.15, 0.1, "#6a6f71", "y", 12, 0.3);
  // Roof sign frame (the letters are a separate textured plane).
  for (const x of [-3.5, 3.5]) b.block(x, ROOF + 1.0, HD - 0.3, 0.14, 1.4, 0.14, C.steelDark);
  b.box(0, ROOF + 2.95, HD - 0.36, 12.6, 1.9, 0.1, "#191d22");
  return b.build();
}

function buildLights() {
  const b = new Batch();
  for (let i = 0; i < N; i++) {
    const y = floorBase(i) + CLEAR - 0.05;
    for (const z of [-8, -3.2, 1.2, 5.2, 9.2]) {
      for (let k = 0; k < 7; k++) b.box(-15 + k * 5, y, z, 3.4, 0.06, 0.16, C.lamp);
    }
  }
  return b.build();
}

/** One merged glass geometry per floor: front plus both sides. */
function buildGlass(i: number) {
  const y = floorBase(i) + CLEAR / 2;
  const front = new THREE.PlaneGeometry(HW * 2, CLEAR).translate(0, y, HD + 0.06);
  const left = new THREE.PlaneGeometry(HD * 2, CLEAR).rotateY(-Math.PI / 2).translate(-HW - 0.06, y, 0);
  const right = new THREE.PlaneGeometry(HD * 2, CLEAR).rotateY(Math.PI / 2).translate(HW + 0.06, y, 0);
  return mergeGeometries([front, left, right])!;
}

const SOLAR: InstItem[] = (() => {
  const items: InstItem[] = [];
  for (let ix = 0; ix < 14; ix++) {
    for (let iz = 0; iz < 4; iz++) {
      const x = -9.6 + ix * 1.12;
      const z = -9.4 + iz * 2.3;
      if (x > 4.4) continue;
      items.push({ p: [x, ROOF + 0.55, z], s: [1.0, 0.05, 1.75], r: [-0.32, 0, 0], c: C.solar });
    }
  }
  return items;
})();

function makeSignTexture(): THREE.CanvasTexture {
  const canvas = document.createElement("canvas");
  canvas.width = 1024;
  canvas.height = 160;
  const ctx = canvas.getContext("2d")!;
  const host = document.querySelector(".fb-home");
  const family = host ? getComputedStyle(host).getPropertyValue("--font-display").trim() : "";
  ctx.fillStyle = "#191d22";
  ctx.fillRect(0, 0, canvas.width, canvas.height);
  ctx.fillStyle = "#f2c27a";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.font = `800 118px ${family || "Impact, 'Arial Narrow', sans-serif"}`;
  if ("letterSpacing" in ctx) (ctx as CanvasRenderingContext2D & { letterSpacing: string }).letterSpacing = "10px";
  ctx.fillText("FACTORYBRAIN", canvas.width / 2, canvas.height / 2 + 6);
  const tex = new THREE.CanvasTexture(canvas);
  tex.colorSpace = THREE.SRGBColorSpace;
  tex.anisotropy = 4;
  return tex;
}

export function Building() {
  const glassGeos = useMemo(() => Array.from({ length: N }, (_, i) => buildGlass(i)), []);
  const glassMats = useMemo(
    () =>
      Array.from(
        { length: N },
        () =>
          new THREE.MeshLambertMaterial({
            color: C.glass,
            emissive: "#1a2330",
            transparent: true,
            opacity: 0.6,
            depthWrite: false,
          }),
      ),
    [],
  );
  const signTex = useMemo(makeSignTexture, []);
  const signMat = useRef<THREE.MeshBasicMaterial>(null);

  useEffect(
    () => () => {
      glassGeos.forEach((g) => g.dispose());
      glassMats.forEach((m) => m.dispose());
      signTex.dispose();
    },
    [glassGeos, glassMats, signTex],
  );

  useFrame(() => {
    const p = tourState.smooth;
    const xray = Math.min(1, Math.max(0, p - 5));
    for (let i = 0; i < N; i++) {
      const near = Math.max(0, 1 - Math.abs(p - (i + 1)) * 1.4);
      const base = 0.58 + (0.06 - 0.58) * near;
      glassMats[i].opacity = base + (0.22 - base) * xray;
    }
  });

  return (
    <group>
      <StaticMesh build={buildStructure} />
      <StaticMesh build={buildLights} glow />
      <Inst items={SOLAR} geometry={geoBox} />
      {glassGeos.map((g, i) => (
        <mesh key={i} geometry={g} material={glassMats[i]} renderOrder={2} />
      ))}
      <mesh position={[0, ROOF + 2.95, HD - 0.3]}>
        <planeGeometry args={[12.2, 1.9]} />
        <meshBasicMaterial ref={signMat} map={signTex} toneMapped={false} />
      </mesh>
      {Array.from({ length: N }, (_, i) => (
        <pointLight
          key={i}
          position={[0, floorBase(i) + 3.1, 1.5]}
          color="#ffd6a3"
          intensity={4.2}
          distance={34}
          decay={1}
        />
      ))}
    </group>
  );
}
