"use client";

import { useLayoutEffect, useMemo, useRef, type Ref } from "react";
import * as THREE from "three";
import { mergeGeometries } from "three/examples/jsm/utils/BufferGeometryUtils.js";
import type { Vec3 } from "@/lib/tour";
import { CLOTHING, SKIN, pick, rng } from "./palette";

/* ------------------------------------------------------------------ */
/* Shared materials                                                    */
/* ------------------------------------------------------------------ */

/** Flat-shaded, vertex-coloured material for merged static geometry. */
export const matStatic = new THREE.MeshLambertMaterial({ vertexColors: true, flatShading: true });
/** Unlit vertex-coloured material for light strips, lit tables, windows. */
export const matGlow = new THREE.MeshBasicMaterial({ vertexColors: true, toneMapped: false });
/** Flat-shaded material for instanced meshes (colour comes from instanceColor). */
export const matInst = new THREE.MeshLambertMaterial({ flatShading: true });

/* ------------------------------------------------------------------ */
/* Merged static geometry                                              */
/* ------------------------------------------------------------------ */

const tmpColor = new THREE.Color();

function paint(g: THREE.BufferGeometry, color: string) {
  g.deleteAttribute("uv");
  const n = g.attributes.position.count;
  const arr = new Float32Array(n * 3);
  tmpColor.set(color);
  for (let i = 0; i < n; i++) {
    arr[i * 3] = tmpColor.r;
    arr[i * 3 + 1] = tmpColor.g;
    arr[i * 3 + 2] = tmpColor.b;
  }
  g.setAttribute("color", new THREE.BufferAttribute(arr, 3));
  return g;
}

/**
 * Collects many coloured primitives and merges them into one geometry,
 * so a whole floor's furniture is a single draw call.
 */
export class Batch {
  private parts: THREE.BufferGeometry[] = [];

  /** Box by centre and size, optionally rotated about y then x. */
  box(x: number, y: number, z: number, w: number, h: number, d: number, color: string, ry = 0, rx = 0) {
    const g = new THREE.BoxGeometry(w, h, d);
    if (rx) g.rotateX(rx);
    if (ry) g.rotateY(ry);
    g.translate(x, y, z);
    this.parts.push(paint(g, color));
    return this;
  }

  /** Box standing on yBottom. */
  block(x: number, yBottom: number, z: number, w: number, h: number, d: number, color: string, ry = 0) {
    return this.box(x, yBottom + h / 2, z, w, h, d, color, ry);
  }

  /** Cylinder by centre; axis is the long direction. */
  cyl(
    x: number, y: number, z: number, r: number, len: number, color: string,
    axis: "x" | "y" | "z" = "y", seg = 8, rTop = r,
  ) {
    const g = new THREE.CylinderGeometry(rTop, r, len, seg);
    if (axis === "x") g.rotateZ(Math.PI / 2);
    if (axis === "z") g.rotateX(Math.PI / 2);
    g.translate(x, y, z);
    this.parts.push(paint(g, color));
    return this;
  }

  /** Arbitrary geometry (already positioned). */
  add(g: THREE.BufferGeometry, color: string) {
    this.parts.push(paint(g, color));
    return this;
  }

  build(): THREE.BufferGeometry {
    if (this.parts.length === 0) return new THREE.BufferGeometry();
    const merged = mergeGeometries(this.parts, false) ?? new THREE.BufferGeometry();
    this.parts.forEach((p) => p.dispose());
    this.parts = [];
    merged.computeBoundingSphere();
    return merged;
  }
}

export function StaticMesh({ build, glow = false }: { build: () => THREE.BufferGeometry; glow?: boolean }) {
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const geometry = useMemo(build, []);
  useLayoutEffect(() => () => geometry.dispose(), [geometry]);
  return <mesh geometry={geometry} material={glow ? matGlow : matStatic} matrixAutoUpdate={false} />;
}

/* ------------------------------------------------------------------ */
/* Instancing                                                          */
/* ------------------------------------------------------------------ */

export interface InstItem {
  p: Vec3;
  s?: Vec3;
  r?: Vec3;
  c?: string;
}

const tmpObj = new THREE.Object3D();

export function writeInstance(mesh: THREE.InstancedMesh, i: number, item: InstItem) {
  tmpObj.position.set(item.p[0], item.p[1], item.p[2]);
  if (item.r) tmpObj.rotation.set(item.r[0], item.r[1], item.r[2]);
  else tmpObj.rotation.set(0, 0, 0);
  if (item.s) tmpObj.scale.set(item.s[0], item.s[1], item.s[2]);
  else tmpObj.scale.set(1, 1, 1);
  tmpObj.updateMatrix();
  mesh.setMatrixAt(i, tmpObj.matrix);
}

export function Inst({
  items,
  geometry,
  material = matInst,
  color,
  meshRef,
  animated = false,
}: {
  items: InstItem[];
  geometry: THREE.BufferGeometry;
  material?: THREE.Material;
  /** Fallback colour when an item has none. */
  color?: string;
  meshRef?: Ref<THREE.InstancedMesh>;
  animated?: boolean;
}) {
  const local = useRef<THREE.InstancedMesh | null>(null);
  useLayoutEffect(() => {
    const mesh = local.current;
    if (!mesh) return;
    items.forEach((it, i) => {
      writeInstance(mesh, i, it);
      const c = it.c ?? color;
      if (c) mesh.setColorAt(i, tmpColor.set(c));
    });
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
    if (!animated) mesh.computeBoundingSphere();
  }, [items, color, animated]);

  return (
    <instancedMesh
      ref={(m: THREE.InstancedMesh | null) => {
        local.current = m;
        if (typeof meshRef === "function") meshRef(m);
        else if (meshRef) (meshRef as { current: THREE.InstancedMesh | null }).current = m;
      }}
      args={[geometry, material, items.length]}
      frustumCulled={!animated}
    />
  );
}

/* ------------------------------------------------------------------ */
/* People: merged low-poly figures (legs, body, arms), plus head and   */
/* hair. Forward is +z before rotation. Never animated.                */
/* ------------------------------------------------------------------ */

export const geoBox = new THREE.BoxGeometry(1, 1, 1);
export const geoCylX = new THREE.CylinderGeometry(1, 1, 1, 10).rotateZ(Math.PI / 2);
export const geoCylZ = new THREE.CylinderGeometry(1, 1, 1, 10).rotateX(Math.PI / 2);

/* Vertex colours multiply the instance colour: white parts take the clothing
   colour, grey parts become a darker shade of it (trousers, shoes). */
const SHIRT = "#ffffff";
const LEGS = "#4a4f58";
const SHOES = "#2a2a2a";

/** Standing figure, feet at y = 0, shoulders at about 1.42 m. */
function standingBody() {
  const b = new Batch();
  for (const sx of [-0.1, 0.1]) {
    b.block(sx, 0, 0.03, 0.15, 0.08, 0.26, SHOES);
    b.block(sx, 0.08, 0, 0.14, 0.76, 0.16, LEGS);
  }
  b.block(0, 0.82, 0, 0.4, 0.16, 0.22, SHIRT); // hips
  b.block(0, 0.98, 0, 0.42, 0.46, 0.24, SHIRT); // chest
  b.box(0, 1.46, 0, 0.12, 0.08, 0.12, SHIRT); // neck
  // Arms slightly forward, as if working at a table.
  for (const sx of [-0.27, 0.27]) b.box(sx, 1.17, 0.08, 0.1, 0.56, 0.11, SHIRT, 0, -0.32);
  return b.build();
}

/** Seated figure, seat at y = 0.46, hands reaching forward to a table. */
function seatedBody() {
  const b = new Batch();
  for (const sx of [-0.1, 0.1]) {
    b.box(sx, 0.48, 0.2, 0.14, 0.14, 0.44, LEGS); // thighs
    b.block(sx, 0.08, 0.38, 0.13, 0.42, 0.14, LEGS); // shins
    b.block(sx, 0, 0.42, 0.14, 0.08, 0.24, SHOES);
  }
  b.block(0, 0.46, -0.02, 0.4, 0.14, 0.26, SHIRT); // hips
  b.box(0, 0.83, -0.04, 0.42, 0.46, 0.24, SHIRT, 0, 0.12); // chest, leaning in
  b.box(0, 1.1, 0.02, 0.12, 0.08, 0.12, SHIRT); // neck
  for (const sx of [-0.27, 0.27]) b.box(sx, 0.86, 0.22, 0.1, 0.11, 0.48, SHIRT, 0, 0.15); // forearms down to the table
  return b.build();
}

let bodies: { standing: THREE.BufferGeometry; seated: THREE.BufferGeometry } | null = null;
function bodyGeometries() {
  if (!bodies) bodies = { standing: standingBody(), seated: seatedBody() };
  return bodies;
}

export const geoHead = new THREE.SphereGeometry(0.12, 12, 9);
/** Hair: the top and back of the head. */
export const geoHair = new THREE.SphereGeometry(0.128, 12, 7, 0, Math.PI * 2, 0, Math.PI * 0.55).translate(0, 0.012, -0.012);

const matPeople = new THREE.MeshLambertMaterial({ vertexColors: true, flatShading: true });

export interface PersonSpec {
  x: number;
  z: number;
  y0: number;
  seated?: boolean;
  /** Heading in radians about y; 0 faces +z (towards the glass and the drone). */
  face?: number;
}

const HAIR_COLOURS = ["#1d1a17", "#2b211a", "#14110f", "#3a2a1e"];

export function People({ list, seed }: { list: PersonSpec[]; seed: number }) {
  const { standing, seated, heads, hair } = useMemo(() => {
    const r = rng(seed);
    const standing: InstItem[] = [];
    const seated: InstItem[] = [];
    const heads: InstItem[] = [];
    const hair: InstItem[] = [];
    for (const p of list) {
      const face = p.face ?? 0;
      const k = 0.94 + r() * 0.12; // height variation
      const cloth = pick(r, CLOTHING);
      const sin = Math.sin(face);
      const cos = Math.cos(face);
      // Head offset in the figure's own frame, rotated into the world.
      const local = p.seated ? { y: 1.28, z: 0.06 } : { y: 1.62, z: 0.02 };
      const hx = p.x + sin * local.z * k;
      const hz = p.z + cos * local.z * k;
      const hy = p.y0 + local.y * k;
      const item: InstItem = { p: [p.x, p.y0, p.z], s: [k, k, k], r: [0, face, 0], c: cloth };
      (p.seated ? seated : standing).push(item);
      heads.push({ p: [hx, hy, hz], s: [k, k * 1.08, k], r: [0, face, 0], c: pick(r, SKIN) });
      hair.push({ p: [hx, hy, hz], s: [k, k * 1.08, k], r: [0, face, 0], c: pick(r, HAIR_COLOURS) });
    }
    return { standing, seated, heads, hair };
  }, [list, seed]);
  const geo = bodyGeometries();
  return (
    <>
      {standing.length > 0 && <Inst items={standing} geometry={geo.standing} material={matPeople} />}
      {seated.length > 0 && <Inst items={seated} geometry={geo.seated} material={matPeople} />}
      <Inst items={heads} geometry={geoHead} />
      <Inst items={hair} geometry={geoHair} />
    </>
  );
}
