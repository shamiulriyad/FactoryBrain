"use client";

import { useRef } from "react";
import { useFrame } from "@react-three/fiber";
import type * as THREE from "three";
import { floorBase } from "@/lib/tour";
import { tourState } from "@/lib/tourStore";
import { Batch, Inst, People, StaticMesh, geoCylX, geoCylZ, writeInstance, type InstItem, type PersonSpec } from "../geom";
import { C, FABRICS, pick, rng } from "../palette";

/** Ground floor: fabric store racks and three inspection machines. */
const Y0 = floorBase(0);
const MACHINE_XS = [-13.5, -4.5, 4.5];
const MACHINE_Z = 1.4;
const SHELVES = [0.35, 1.25, 2.15];

function buildStatic() {
  const b = new Batch();
  // Roll racks along the back wall.
  for (let k = 0; k <= 14; k++) b.block(-16.8 + k * 2.4, Y0, -9.7, 0.08, 2.9, 1.5, C.steelDark);
  for (const y of SHELVES) b.box(0, Y0 + y - 0.04, -9.7, 33.8, 0.06, 1.5, C.steel);
  // Pallet stacks of rolls near the receiving door.
  b.block(-15, Y0, -2.5, 2.2, 0.15, 1.8, C.wood);
  b.block(-12.2, Y0, -2.5, 2.2, 0.15, 1.8, C.wood);
  // Inspection machines: frame, lit table frame, roll arms.
  for (const x of MACHINE_XS) {
    const z = MACHINE_Z;
    for (const sx of [-1.05, 1.05]) {
      b.block(x + sx, Y0, z - 0.6, 0.1, 1.75, 0.1, C.machineDark);
      b.block(x + sx, Y0, z + 0.6, 0.1, 0.95, 0.1, C.machineDark);
      b.box(x + sx, Y0 + 1.0, z, 0.08, 0.08, 1.3, C.machineDark);
    }
    b.box(x, Y0 + 1.06, z, 2.0, 0.1, 1.15, C.machine, 0, -0.42); // table housing
    b.block(x - 1.25, Y0, z + 0.2, 0.4, 1.05, 0.5, C.machineDark); // control box
    // Fabric web: from feed roll over the table to the take-up roll.
    b.box(x, Y0 + 1.42, z - 0.55, 1.7, 0.01, 0.35, "#4f5d78", 0, -0.9);
    b.box(x, Y0 + 0.9, z + 0.66, 1.7, 0.01, 0.4, "#4f5d78", 0, 1.1);
  }
  // Roll trolley.
  b.block(9.6, Y0 + 0.2, 4.0, 1.6, 0.06, 0.9, C.steel);
  b.block(9.6, Y0, 4.0, 1.5, 0.2, 0.8, C.steelDark);
  return b.build();
}

function buildGlow() {
  const b = new Batch();
  for (const x of MACHINE_XS) b.box(x, Y0 + 1.13, MACHINE_Z, 1.75, 0.02, 0.95, C.litTable, 0, -0.42);
  return b.build();
}

const ROLLS: InstItem[] = (() => {
  const r = rng(3);
  const items: InstItem[] = [];
  for (const y of SHELVES) {
    for (let x = -16.55; x < 16.6; x += 0.4) {
      if (r() < 0.12) continue; // a few gaps
      const rad = 0.15 + r() * 0.04;
      items.push({ p: [x, Y0 + y + rad, -9.65], s: [rad, rad, 1.35], c: pick(r, FABRICS) });
    }
  }
  // Pyramid stacks on the pallets.
  for (const px of [-15, -12.2]) {
    for (let row = 0; row < 3; row++) {
      for (let k = 0; k < 4 - row; k++) {
        items.push({
          p: [px, Y0 + 0.33 + row * 0.3, -3.05 + row * 0.18 + k * 0.36],
          s: [0.17, 0.17, 2.0],
          r: [0, Math.PI / 2, 0],
          c: pick(r, FABRICS),
        });
      }
    }
  }
  return items;
})();

/* Feed and take-up rolls rotate; index 2k is feed, 2k+1 take-up. */
const MACHINE_ROLLS: InstItem[] = MACHINE_XS.flatMap((x) => [
  { p: [x, Y0 + 1.62, MACHINE_Z - 0.78], s: [1.75, 0.2, 0.2], c: "#4f5d78" },
  { p: [x, Y0 + 0.62, MACHINE_Z + 0.82], s: [1.75, 0.24, 0.24], c: "#4f5d78" },
]) as InstItem[];

const PEOPLE: PersonSpec[] = [
  ...MACHINE_XS.map((x) => ({ x: x + 0.55, z: MACHINE_Z + 1.25, y0: Y0 })),
  { x: -13.6, z: -1.1, y0: Y0 },
  { x: 8.7, z: 4.9, y0: Y0 },
  { x: -6.2, z: -7.6, y0: Y0 },
];

export function FloorG() {
  const rollsRef = useRef<THREE.InstancedMesh>(null);
  const angle = useRef(0);

  useFrame((_, dt) => {
    const mesh = rollsRef.current;
    if (!mesh || tourState.reducedMotion || tourState.smooth > 2.5) return;
    angle.current += Math.min(dt, 0.1) * 1.4;
    MACHINE_ROLLS.forEach((it, i) => {
      writeInstance(mesh, i, { ...it, r: [i % 2 ? -angle.current : angle.current, 0, 0] });
    });
    mesh.instanceMatrix.needsUpdate = true;
  });

  return (
    <group>
      <StaticMesh build={buildStatic} />
      <StaticMesh build={buildGlow} glow />
      <Inst items={ROLLS} geometry={geoCylZ} />
      <Inst items={MACHINE_ROLLS} geometry={geoCylX} meshRef={rollsRef} animated />
      <People list={PEOPLE} seed={21} />
    </group>
  );
}
