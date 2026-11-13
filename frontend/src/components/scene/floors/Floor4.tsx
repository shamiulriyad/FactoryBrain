"use client";

import { useLayoutEffect, useRef } from "react";
import { useFrame } from "@react-three/fiber";
import type * as THREE from "three";
import { floorBase } from "@/lib/tour";
import { tourState } from "@/lib/tourStore";
import { Batch, Inst, People, StaticMesh, geoBox, geoCylZ, writeInstance, type InstItem, type PersonSpec } from "../geom";
import { C, FABRICS, pick, rng } from "../palette";

/** Floor 4: pallets of cartons, packing tables, a roller conveyor, a pallet jack. */
const Y0 = floorBase(4);
const CONV = { x0: -15, x1: 11, z: 6.2, top: 0.78, width: 0.84 };
const ROLLER_PITCH = 0.3;
const ROLLERS = Math.floor((CONV.x1 - CONV.x0) / ROLLER_PITCH);
const MOVING = 9;
const CARTON: [number, number, number] = [0.58, 0.4, 0.38];
const PACK_XS = Array.from({ length: 7 }, (_, k) => -14 + k * 4.2);
const PACK_Z = -2.8;

/** Pallets: x, z, full layers. The PO 4471 pair sits mid-floor. */
const PALLETS: [number, number, number][] = [
  [3.95, 1.2, 4],
  [5.25, 1.2, 3],
  ...Array.from({ length: 12 }, (_, k) => [-15 + k * 2.6, -8.6, 2 + ((k * 7) % 3)] as [number, number, number]),
];

function buildStatic() {
  const b = new Batch();
  for (const [x, z] of PALLETS) {
    b.block(x, Y0, z, 1.2, 0.14, 1.2, C.wood);
  }
  // Packing tables with an open carton each.
  for (const x of PACK_XS) {
    b.box(x, Y0 + 0.84, PACK_Z, 2.0, 0.05, 0.9, C.table);
    b.block(x - 0.9, Y0, PACK_Z, 0.06, 0.82, 0.8, C.steelDark);
    b.block(x + 0.9, Y0, PACK_Z, 0.06, 0.82, 0.8, C.steelDark);
    b.block(x + 0.55, Y0 + 0.87, PACK_Z, 0.58, 0.36, 0.4, C.cartonDark);
  }
  // Conveyor frame and legs.
  const cx = (CONV.x0 + CONV.x1) / 2;
  const len = CONV.x1 - CONV.x0;
  for (const s of [-1, 1]) {
    b.box(cx, Y0 + CONV.top - 0.04, CONV.z + s * (CONV.width / 2 + 0.04), len, 0.16, 0.06, C.steel);
  }
  for (let x = CONV.x0 + 0.5; x < CONV.x1; x += 2.5) b.block(x, Y0, CONV.z, 0.08, CONV.top - 0.1, CONV.width, C.steelDark);
  // Pallet jack beside the PO 4471 pallets.
  b.box(7.6, Y0 + 0.06, 0.9, 1.15, 0.08, 0.16, "#b8893a");
  b.box(7.6, Y0 + 0.06, 1.5, 1.15, 0.08, 0.16, "#b8893a");
  b.block(8.35, Y0, 1.2, 0.35, 0.6, 0.75, "#9c7a3c");
  b.box(8.75, Y0 + 0.8, 1.2, 0.05, 0.9, 0.05, C.steelDark, 0, 0);
  b.box(8.75, Y0 + 1.25, 1.2, 0.05, 0.05, 0.35, C.steelDark);
  return b.build();
}

const CARTONS: InstItem[] = (() => {
  const r = rng(19);
  const items: InstItem[] = [];
  PALLETS.forEach(([x, z, layers], idx) => {
    for (let l = 0; l < layers; l++) {
      for (let i = 0; i < 2; i++) {
        for (let j = 0; j < 3; j++) {
          // The second PO pallet is short: its top layer is half done.
          if (idx === 1 && l === layers - 1 && j === 2) continue;
          items.push({
            p: [x - 0.3 + i * 0.6, Y0 + 0.14 + CARTON[1] / 2 + l * CARTON[1], z - 0.4 + j * 0.4],
            s: CARTON,
            c: r() > 0.25 ? C.carton : C.cartonDark,
          });
        }
      }
    }
  });
  return items;
})();

const FOLDED: InstItem[] = (() => {
  const r = rng(23);
  return PACK_XS.flatMap((x) =>
    Array.from({ length: 3 }, (_, k) => ({ p: [x - 0.45, Y0 + 0.9 + k * 0.05, PACK_Z] as [number, number, number], s: [0.45, 0.05, 0.36] as [number, number, number], c: pick(r, FABRICS) })),
  );
})();

const ROLLER_ITEMS: InstItem[] = Array.from({ length: ROLLERS }, (_, i) => ({
  p: [CONV.x0 + 0.15 + i * ROLLER_PITCH, Y0 + CONV.top - 0.04, CONV.z],
  s: [0.045, 0.045, CONV.width],
  c: "#9aa0a6",
}));

const MOVING_ITEMS: InstItem[] = Array.from({ length: MOVING }, () => ({ p: [0, 0, 0], s: [0.55, 0.4, 0.42], c: C.carton }));

const PEOPLE: PersonSpec[] = [
  ...PACK_XS.map((x) => ({ x, z: PACK_Z - 0.85, y0: Y0 })),
  { x: 9.6, z: 7.3, y0: Y0, face: Math.PI },
  { x: -6.0, z: -7.2, y0: Y0, face: Math.PI },
  { x: 2.6, z: 2.6, y0: Y0, face: Math.PI / 2 },
];

export function Floor4() {
  const rollers = useRef<THREE.InstancedMesh>(null);
  const moving = useRef<THREE.InstancedMesh>(null);
  const t = useRef(0);
  const item = useRef<InstItem>({ p: [0, 0, 0], s: [0.55, 0.4, 0.42] });

  const placeCartons = () => {
    const mesh = moving.current;
    if (!mesh) return;
    const len = CONV.x1 - CONV.x0;
    for (let i = 0; i < MOVING; i++) {
      const d = (((t.current * 0.55 + (i * len) / MOVING) % len) + len) % len;
      item.current.p = [CONV.x0 + d, Y0 + CONV.top + 0.2, CONV.z];
      writeInstance(mesh, i, item.current);
    }
    mesh.instanceMatrix.needsUpdate = true;
  };

  useLayoutEffect(placeCartons, []);

  useFrame((_, dt) => {
    if (tourState.reducedMotion || Math.abs(tourState.smooth - 5) > 1.5) return;
    t.current += Math.min(dt, 0.1);
    placeCartons();
    const mesh = rollers.current;
    if (!mesh) return;
    const a = -t.current * 6;
    for (let i = 0; i < ROLLERS; i++) writeInstance(mesh, i, { ...ROLLER_ITEMS[i], r: [0, 0, a] });
    mesh.instanceMatrix.needsUpdate = true;
  });

  return (
    <group>
      <StaticMesh build={buildStatic} />
      <Inst items={CARTONS} geometry={geoBox} />
      <Inst items={FOLDED} geometry={geoBox} />
      <Inst items={ROLLER_ITEMS} geometry={geoCylZ} meshRef={rollers} animated />
      <Inst items={MOVING_ITEMS} geometry={geoBox} meshRef={moving} animated />
      <People list={PEOPLE} seed={61} />
    </group>
  );
}
