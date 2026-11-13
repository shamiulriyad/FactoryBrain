"use client";

import { useRef } from "react";
import { useFrame } from "@react-three/fiber";
import type * as THREE from "three";
import { floorBase } from "@/lib/tour";
import { tourState } from "@/lib/tourStore";
import { Batch, Inst, People, StaticMesh, geoBox, type InstItem, type PersonSpec } from "../geom";
import { C, FABRICS, pick, rng } from "../palette";

/** Floor 1: three long cutting tables with lays, a CNC gantry, bundle carts. */
const Y0 = floorBase(1);
const TABLE_ZS = [-2.5, 1.5, 5.5];
const TABLE_CX = -2;
const TABLE_LEN = 24;
const TOP = 0.9;
const LAY_COLORS = ["#7d7461", "#4b5a73", "#9a8466"];
const GANTRY_TABLE = 1;

function buildStatic() {
  const b = new Batch();
  TABLE_ZS.forEach((z, t) => {
    b.box(TABLE_CX, Y0 + TOP - 0.03, z, TABLE_LEN, 0.06, 1.9, C.table);
    for (let k = 0; k <= 8; k++) b.block(TABLE_CX - TABLE_LEN / 2 + 0.2 + k * 2.95, Y0, z, 0.08, TOP - 0.06, 1.6, C.steelDark);
    // Fabric lay with a paper marker on top.
    b.box(TABLE_CX - 0.5, Y0 + TOP + 0.06, z, 21, 0.12, 1.62, LAY_COLORS[t]);
    b.box(TABLE_CX - 0.5, Y0 + TOP + 0.125, z, 21, 0.01, 1.6, "#d9d5ca");
    // Spreader parked at the table head.
    b.block(TABLE_CX + TABLE_LEN / 2 - 0.9, Y0 + TOP, z, 0.7, 0.55, 2.1, C.machineDark);
  });
  // Straight-knife cutting machines standing on the lays.
  for (const [x, t] of [[-7.4, 0], [-2.4, 2], [7.8, 1]] as const) {
    const z = TABLE_ZS[t] - 0.35;
    b.block(x, Y0 + TOP + 0.13, z, 0.34, 0.03, 0.26, C.machineDark); // base plate
    b.block(x, Y0 + TOP + 0.16, z, 0.05, 0.42, 0.1, C.steel); // blade guard
    b.block(x, Y0 + TOP + 0.58, z, 0.2, 0.22, 0.24, C.machineAccent); // motor
    b.box(x, Y0 + TOP + 0.88, z, 0.04, 0.12, 0.04, C.machineDark); // handle
  }
  // The shifted ply on table 3 (front), sticking out past the lay edge.
  b.box(-4.2, Y0 + TOP + 0.14, TABLE_ZS[2] + 0.13, 2.1, 0.03, 1.62, "#b49d78");
  // Bundle carts.
  for (const z of [-2.2, 1.0, 4.2]) {
    b.block(14.2, Y0 + 0.25, z, 1.5, 0.05, 1.0, C.steel);
    b.block(14.2, Y0 + 0.9, z, 1.5, 0.05, 1.0, C.steel);
    for (const [dx, dz] of [[-0.7, -0.45], [0.7, -0.45], [-0.7, 0.45], [0.7, 0.45]]) {
      b.block(14.2 + dx, Y0, z + dz, 0.05, 1.0, 0.05, C.steelDark);
    }
  }
  return b.build();
}

/** Gantry geometry centred on x = 0; the group slides along the table. */
function buildGantry() {
  const b = new Batch();
  const z = TABLE_ZS[GANTRY_TABLE];
  for (const s of [-1, 1]) {
    b.block(0, Y0 + TOP - 0.1, z + s * 1.08, 0.5, 1.05, 0.22, C.machine);
  }
  b.box(0, Y0 + TOP + 0.98, z, 0.55, 0.3, 2.45, C.machine);
  b.box(0.05, Y0 + TOP + 0.62, z + 0.2, 0.35, 0.45, 0.35, C.machineDark); // cutting head
  b.box(0.05, Y0 + TOP + 0.98, z, 0.57, 0.06, 2.47, "#c9a24a"); // warning stripe
  return b.build();
}

const BUNDLES: InstItem[] = (() => {
  const r = rng(5);
  const items: InstItem[] = [];
  for (const z of [-2.2, 1.0, 4.2]) {
    for (const shelf of [0.33, 0.98]) {
      for (let k = 0; k < 6; k++) {
        items.push({
          p: [13.7 + (k % 3) * 0.48, Y0 + shelf + 0.07 + Math.floor(k / 3) * 0.15, z - 0.2 + (k % 2) * 0.35],
          s: [0.44, 0.13, 0.32],
          c: pick(r, FABRICS),
        });
      }
    }
  }
  return items;
})();

/* Cutters stand at the table edge, facing the lay. */
const PEOPLE: PersonSpec[] = [
  { x: -8, z: TABLE_ZS[0] - 1.2, y0: Y0, face: 0 },
  { x: 1.5, z: TABLE_ZS[0] + 1.2, y0: Y0, face: Math.PI },
  { x: -10.5, z: TABLE_ZS[1] + 1.2, y0: Y0, face: Math.PI },
  { x: 7.8, z: TABLE_ZS[1] - 1.2, y0: Y0, face: 0 },
  { x: -7.4, z: TABLE_ZS[2] - 1.2, y0: Y0, face: 0 },
  { x: -2.4, z: TABLE_ZS[2] - 1.2, y0: Y0, face: -0.3 },
  { x: 2.4, z: TABLE_ZS[2] + 1.2, y0: Y0, face: Math.PI },
  { x: 12.8, z: 2.6, y0: Y0, face: Math.PI / 2 },
];

export function Floor1() {
  const gantry = useRef<THREE.Group>(null);
  const t = useRef(0);

  useFrame((_, dt) => {
    const g = gantry.current;
    if (!g || tourState.reducedMotion || tourState.smooth > 3.5) return;
    t.current += Math.min(dt, 0.1);
    // Slow pass along the table, easing at each end.
    g.position.x = TABLE_CX - 0.5 + Math.sin(t.current * 0.22) * 8.5;
  });

  return (
    <group>
      <StaticMesh build={buildStatic} />
      <group ref={gantry} position={[TABLE_CX - 0.5, 0, 0]}>
        <StaticMesh build={buildGantry} />
      </group>
      <Inst items={BUNDLES} geometry={geoBox} />
      <People list={PEOPLE} seed={31} />
    </group>
  );
}
