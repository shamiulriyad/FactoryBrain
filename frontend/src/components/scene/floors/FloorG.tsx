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
  // Inspection machines. Fabric runs from the feed roll (low, back) over the
  // top roller and down the lit, inclined panel that faces the inspector
  // (+z side), then onto the take-up roll at the front.
  for (const x of MACHINE_XS) {
    const z = MACHINE_Z;
    for (const sx of [-1.12, 1.12]) {
      b.block(x + sx, Y0, z - 0.55, 0.12, 2.0, 0.12, C.machineDark); // back post
      b.block(x + sx, Y0, z + 0.5, 0.12, 0.95, 0.12, C.machineDark); // front post
      b.box(x + sx, Y0 + 0.06, z, 0.14, 0.12, 1.4, C.machineDark); // foot
      b.box(x + sx, Y0 + 1.42, z - 0.05, 0.1, 0.1, 1.3, C.machineDark, 0, 0.72); // side rail along the panel
    }
    b.box(x, Y0 + 2.0, z - 0.55, 2.36, 0.1, 0.12, C.machineDark); // top beam
    b.box(x, Y0 + 1.4, z + 0.02, 2.12, 0.08, 1.2, C.machine, 0, 0.9); // panel housing
    b.box(x, Y0 + 0.2, z + 0.5, 2.36, 0.08, 0.1, C.machineAccent); // kick rail
    // Control console on the right, at the inspector's hand.
    b.block(x + 1.45, Y0, z + 0.45, 0.42, 1.0, 0.36, C.machineDark);
    b.box(x + 1.45, Y0 + 1.06, z + 0.48, 0.44, 0.1, 0.4, C.machine, 0, 0.5);
    // Fabric web from the feed roll up the back to the top roller.
    b.box(x, Y0 + 1.25, z - 0.5, 1.8, 1.45, 0.012, "#5b6f94");
    b.cyl(x, Y0 + 1.9, z - 0.38, 0.07, 1.95, C.steel, "x", 10);
  }
  // Roll trolley.
  b.block(9.6, Y0 + 0.2, 4.0, 1.6, 0.06, 0.9, C.steel);
  b.block(9.6, Y0, 4.0, 1.5, 0.2, 0.8, C.steelDark);
  return b.build();
}

function buildGlow() {
  const b = new Batch();
  for (const x of MACHINE_XS) b.box(x, Y0 + 1.435, MACHINE_Z + 0.06, 1.95, 0.02, 1.08, C.litTable, 0, 0.9);
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
  { p: [x, Y0 + 0.5, MACHINE_Z - 0.62], s: [1.85, 0.26, 0.26], c: "#5b6f94" },
  { p: [x, Y0 + 0.72, MACHINE_Z + 0.62], s: [1.85, 0.2, 0.2], c: "#5b6f94" },
]) as InstItem[];

const PEOPLE: PersonSpec[] = [
  // Inspector in front of each lit panel, a helper loading rolls at the back.
  ...MACHINE_XS.map((x) => ({ x: x + 0.75, z: MACHINE_Z + 1.15, y0: Y0, face: Math.PI - 0.25 })),
  ...MACHINE_XS.map((x) => ({ x: x - 0.7, z: MACHINE_Z - 1.25, y0: Y0, face: 0.2 })),
  { x: -13.6, z: -1.1, y0: Y0, face: Math.PI },
  { x: 8.7, z: 4.9, y0: Y0, face: -0.8 },
  { x: -6.2, z: -7.6, y0: Y0, face: Math.PI },
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
