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
/* People: a cylinder body and a sphere head. Never animated.          */
/* ------------------------------------------------------------------ */

export const geoBody = new THREE.CylinderGeometry(0.16, 0.21, 1, 7);
export const geoHead = new THREE.SphereGeometry(0.135, 8, 6);
export const geoBox = new THREE.BoxGeometry(1, 1, 1);
export const geoCylX = new THREE.CylinderGeometry(1, 1, 1, 10).rotateZ(Math.PI / 2);
export const geoCylZ = new THREE.CylinderGeometry(1, 1, 1, 10).rotateX(Math.PI / 2);

export interface PersonSpec {
  x: number;
  z: number;
  y0: number;
  seated?: boolean;
}

export function People({ list, seed }: { list: PersonSpec[]; seed: number }) {
  const { bodies, heads } = useMemo(() => {
    const r = rng(seed);
    const bodies: InstItem[] = [];
    const heads: InstItem[] = [];
    for (const p of list) {
      const h = p.seated ? 0.66 : 1.36 + r() * 0.12;
      const base = p.y0 + (p.seated ? 0.46 : 0);
      bodies.push({ p: [p.x, base + h / 2, p.z], s: [1, h, 1], c: pick(r, CLOTHING) });
      heads.push({ p: [p.x, base + h + 0.15, p.z], c: pick(r, SKIN) });
    }
    return { bodies, heads };
  }, [list, seed]);
  return (
    <>
      <Inst items={bodies} geometry={geoBody} />
      <Inst items={heads} geometry={geoHead} />
    </>
  );
}
