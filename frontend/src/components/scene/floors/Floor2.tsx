"use client";

import { useMemo } from "react";
import { floorBase } from "@/lib/tour";
import { Batch, Inst, People, StaticMesh, geoBox, type InstItem, type PersonSpec } from "../geom";
import { C, FABRICS, pick, rng } from "../palette";

/** Floor 2: four sewing lines of ten stations, a feed table and a WIP pile. */
const Y0 = floorBase(2);
const LINE_ZS = [-3.5, -0.5, 2.5, 5.5];
const STATIONS = 10;
const stationX = (j: number) => -11.5 + j * 2.3;
const PILE_LINE = 2;
const PILE_STATION = 7;

function buildStatic() {
  const b = new Batch();
  for (const z of LINE_ZS) {
    for (let j = 0; j < STATIONS; j++) {
      const x = stationX(j);
      b.box(x, Y0 + 0.74, z, 1.15, 0.04, 0.66, C.table);
      b.block(x - 0.5, Y0, z, 0.05, 0.72, 0.56, C.steelDark);
      b.block(x + 0.5, Y0, z, 0.05, 0.72, 0.56, C.steelDark);
      b.block(x, Y0, z - 0.72, 0.42, 0.44, 0.38, C.steelDark); // stool
      b.block(x + 0.28, Y0 + 0.5, z + 0.05, 0.26, 0.2, 0.22, C.machineDark); // motor under the table
      b.block(x + 0.48, Y0 + 0.76, z + 0.2, 0.025, 0.5, 0.025, C.steel); // thread stand
      b.cyl(x + 0.48, Y0 + 1.2, z + 0.2, 0.05, 0.1, C.thread, "y", 8); // cone of thread
    }
    // Andon pole at the line end.
    b.block(10.6, Y0, z, 0.06, 2.3, 0.06, C.steelDark);
  }
  // Bundle feed table at the line heads.
  b.block(-14.4, Y0, 1.0, 1.5, 0.8, 7.5, C.tableDark);
  return b.build();
}

function buildGlow() {
  const b = new Batch();
  LINE_ZS.forEach((z, i) => {
    b.box(10.6, Y0 + 2.4, z, 0.22, 0.22, 0.22, i === PILE_LINE ? C.lampWarm : C.ok);
  });
  return b.build();
}

/** One sewing machine head: bed, pillar and arm, merged and instanced. */
function machineGeometry() {
  const b = new Batch();
  b.box(0, 0.06, 0, 0.5, 0.12, 0.2, C.machine);
  b.box(0.2, 0.25, 0, 0.1, 0.3, 0.13, C.machine);
  b.box(0.02, 0.38, 0, 0.42, 0.1, 0.12, C.machine);
  b.box(-0.17, 0.27, 0, 0.06, 0.14, 0.06, C.machineDark);
  return b.build();
}

const MACHINES: InstItem[] = LINE_ZS.flatMap((z) =>
  Array.from({ length: STATIONS }, (_, j) => ({ p: [stationX(j) + 0.1, Y0 + 0.76, z - 0.02] as [number, number, number] })),
);

const BUNDLES: InstItem[] = (() => {
  const r = rng(9);
  const items: InstItem[] = [];
  // One bundle waiting at each station.
  LINE_ZS.forEach((z) => {
    for (let j = 0; j < STATIONS; j++) {
      items.push({ p: [stationX(j) - 0.36, Y0 + 0.83, z + 0.12], s: [0.32, 0.1, 0.24], c: pick(r, FABRICS) });
    }
  });
  // Feed table stock.
  for (let k = 0; k < 18; k++) {
    items.push({
      p: [-14.4 + ((k % 3) - 1) * 0.42, Y0 + 0.86 + Math.floor(k / 9) * 0.12, -1.8 + (Math.floor(k / 3) % 3) * 2.4 + (k % 2) * 0.3],
      s: [0.38, 0.11, 0.3],
      c: pick(r, FABRICS),
    });
  }
  // The pile-up in front of op 14 on line 7: bundles stacked on the floor.
  const px = stationX(PILE_STATION);
  const pz = LINE_ZS[PILE_LINE] + 0.85;
  for (let layer = 0; layer < 5; layer++) {
    for (let k = 0; k < 4; k++) {
      items.push({
        p: [px - 0.28 + (k % 2) * 0.48 + (r() - 0.5) * 0.12, Y0 + 0.07 + layer * 0.15, pz - 0.18 + Math.floor(k / 2) * 0.34],
        s: [0.42, 0.13, 0.3],
        r: [0, (r() - 0.5) * 0.6, 0],
        c: pick(r, FABRICS),
      });
    }
  }
  return items;
})();

const PEOPLE: PersonSpec[] = [
  ...LINE_ZS.flatMap((z) => Array.from({ length: STATIONS }, (_, j) => ({ x: stationX(j), z: z - 0.72, y0: Y0, seated: true }))),
  { x: -13.2, z: 3.6, y0: Y0, face: -Math.PI / 2 },
  { x: 9.2, z: -2.0, y0: Y0, face: -2.2 },
  { x: 6.4, z: 4.2, y0: Y0, face: Math.PI / 2 },
];

export function Floor2() {
  const geoMachine = useMemo(machineGeometry, []);
  return (
    <group>
      <StaticMesh build={buildStatic} />
      <StaticMesh build={buildGlow} glow />
      <Inst items={MACHINES} geometry={geoMachine} color={C.machine} />
      <Inst items={BUNDLES} geometry={geoBox} />
      <People list={PEOPLE} seed={41} />
    </group>
  );
}
