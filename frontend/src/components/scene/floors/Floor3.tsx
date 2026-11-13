"use client";

import { useLayoutEffect, useRef } from "react";
import { useFrame } from "@react-three/fiber";
import type * as THREE from "three";
import { floorBase } from "@/lib/tour";
import { tourState } from "@/lib/tourStore";
import { Batch, Inst, People, StaticMesh, geoBox, writeInstance, type InstItem, type PersonSpec } from "../geom";
import { C, CLOTHING, FABRICS, pick, rng } from "../palette";

/** Floor 3: moving overhead garment rail, ironing tables, QC light tables. */
const Y0 = floorBase(3);
const RAIL_Y = 3.25;
const RAIL = { x0: -14, x1: 12, zFront: -0.8, zBack: -3.2 };
const RUN = RAIL.x1 - RAIL.x0;
const TURN = RAIL.zFront - RAIL.zBack;
const LOOP = 2 * RUN + 2 * TURN;
const GARMENTS = 84;
const QC_XS = [-13.5, -9, -4.5, 4.5, 9, 13.5];
const QC_Z = 5.2;
const IRON_Z = -8.4;

function buildStatic() {
  const b = new Batch();
  // Rail loop and ceiling drops.
  b.box((RAIL.x0 + RAIL.x1) / 2, Y0 + RAIL_Y, RAIL.zFront, RUN, 0.06, 0.06, C.steel);
  b.box((RAIL.x0 + RAIL.x1) / 2, Y0 + RAIL_Y, RAIL.zBack, RUN, 0.06, 0.06, C.steel);
  b.box(RAIL.x0, Y0 + RAIL_Y, (RAIL.zFront + RAIL.zBack) / 2, 0.06, 0.06, TURN, C.steel);
  b.box(RAIL.x1, Y0 + RAIL_Y, (RAIL.zFront + RAIL.zBack) / 2, 0.06, 0.06, TURN, C.steel);
  for (let x = RAIL.x0 + 1; x < RAIL.x1; x += 4) {
    for (const z of [RAIL.zFront, RAIL.zBack]) b.block(x, Y0 + RAIL_Y, z, 0.04, 3.86 - RAIL_Y, 0.04, C.steelDark);
  }
  b.block(RAIL.x1 + 0.6, Y0, -2.0, 1.0, 1.4, 1.8, C.machineDark); // drive unit
  // Ironing tables with irons and a steam boiler.
  for (let k = 0; k < 8; k++) {
    const x = -14 + k * 3.6;
    b.box(x, Y0 + 0.9, IRON_Z, 1.35, 0.05, 0.44, "#c9c3b5");
    b.block(x, Y0, IRON_Z, 0.5, 0.88, 0.3, C.steelDark);
    b.box(x + 0.25, Y0 + 0.97, IRON_Z, 0.24, 0.1, 0.13, C.machineDark);
    b.block(x + 1.1, Y0, IRON_Z - 0.3, 0.5, 0.75, 0.5, C.hvac);
  }
  // QC light-table frames.
  for (const x of QC_XS) {
    b.block(x, Y0, QC_Z, 1.5, 0.92, 0.9, C.machineDark);
  }
  // Packing trolley for passed garments.
  b.block(0, Y0 + 0.15, 3.0, 1.2, 0.7, 0.8, "#566069");
  return b.build();
}

function buildGlow() {
  const b = new Batch();
  for (const x of QC_XS) b.box(x, Y0 + 0.94, QC_Z, 1.42, 0.03, 0.82, C.litTable);
  return b.build();
}

const TABLE_GARMENTS: InstItem[] = (() => {
  const r = rng(13);
  const items: InstItem[] = [];
  for (const x of QC_XS) {
    items.push({ p: [x, Y0 + 0.97, QC_Z], s: [0.62, 0.02, 0.5], r: [0, (r() - 0.5) * 0.3, 0], c: pick(r, FABRICS) });
    items.push({ p: [x + 0.55, Y0 + 0.98, QC_Z - 0.1], s: [0.3, 0.06, 0.3], c: pick(r, FABRICS) });
  }
  // Folded stack in the trolley.
  for (let k = 0; k < 6; k++) items.push({ p: [0, Y0 + 0.9 + k * 0.06, 3.0], s: [0.5, 0.05, 0.38], c: pick(r, FABRICS) });
  return items;
})();

const RAIL_GARMENTS: InstItem[] = (() => {
  const r = rng(17);
  return Array.from({ length: GARMENTS }, () => ({ p: [0, 0, 0] as [number, number, number], c: pick(r, [...FABRICS, ...CLOTHING]) }));
})();

/** Position along the rail loop, distance s in metres. */
function railPose(s: number, out: InstItem) {
  let d = ((s % LOOP) + LOOP) % LOOP;
  const y = Y0 + RAIL_Y - 0.48;
  if (d < RUN) {
    out.p = [RAIL.x0 + d, y, RAIL.zFront];
    out.r = [0, 0, 0];
  } else if ((d -= RUN) < TURN) {
    out.p = [RAIL.x1, y, RAIL.zFront - d];
    out.r = [0, Math.PI / 2, 0];
  } else if ((d -= TURN) < RUN) {
    out.p = [RAIL.x1 - d, y, RAIL.zBack];
    out.r = [0, 0, 0];
  } else {
    d -= RUN;
    out.p = [RAIL.x0, y, RAIL.zBack + d];
    out.r = [0, Math.PI / 2, 0];
  }
  out.s = [0.46, 0.7, 0.05];
}

const PEOPLE: PersonSpec[] = [
  ...QC_XS.map((x) => ({ x, z: QC_Z - 0.85, y0: Y0 })),
  ...Array.from({ length: 8 }, (_, k) => ({ x: -14 + k * 3.6, z: IRON_Z - 0.6, y0: Y0 })),
  { x: 1.2, z: 2.4, y0: Y0, face: -Math.PI / 2 },
];

export function Floor3() {
  const railRef = useRef<THREE.InstancedMesh>(null);
  const offset = useRef(0);
  const pose = useRef<InstItem>({ p: [0, 0, 0] });

  const place = () => {
    const mesh = railRef.current;
    if (!mesh) return;
    for (let i = 0; i < GARMENTS; i++) {
      railPose(offset.current + (i * LOOP) / GARMENTS, pose.current);
      writeInstance(mesh, i, pose.current);
    }
    mesh.instanceMatrix.needsUpdate = true;
  };

  useLayoutEffect(place, []);

  useFrame((_, dt) => {
    if (tourState.reducedMotion || Math.abs(tourState.smooth - 4) > 1.5) return;
    offset.current += Math.min(dt, 0.1) * 0.4;
    place();
  });

  return (
    <group>
      <StaticMesh build={buildStatic} />
      <StaticMesh build={buildGlow} glow />
      <Inst items={TABLE_GARMENTS} geometry={geoBox} />
      <Inst items={RAIL_GARMENTS} geometry={geoBox} meshRef={railRef} animated />
      <People list={PEOPLE} seed={51} />
    </group>
  );
}
