"use client";

import * as THREE from "three";
import { Batch, Inst, StaticMesh, type InstItem } from "./geom";
import { C, rng } from "./palette";

const GROUND_Y = -0.5;

/** Distant neighbour buildings: x, z, width, height, depth. */
const BLOCKS: [number, number, number, number, number][] = [
  [-70, 80, 26, 14, 18], [-40, 86, 18, 22, 14], [-8, 92, 30, 12, 16], [30, 84, 22, 18, 20],
  [64, 78, 28, 26, 16], [100, 70, 20, 12, 22], [-105, 64, 22, 16, 18], [-92, -10, 18, 20, 30],
  [-80, -64, 30, 28, 24], [-30, -78, 26, 16, 22], [20, -84, 34, 24, 18], [70, -60, 24, 30, 26],
  [96, 6, 20, 18, 34], [-140, 30, 30, 22, 20], [150, 30, 26, 14, 24], [0, -130, 50, 26, 30],
];

function buildSite() {
  const b = new Batch();
  // Ground, yard apron, pavement and road.
  b.box(0, GROUND_Y - 0.06, 0, 900, 0.1, 900, C.ground);
  b.box(0, GROUND_Y + 0.01, 4.5, 76, 0.04, 61, C.yard);
  b.box(0, GROUND_Y + 0.01, 41.5, 900, 0.04, 4, "#b3b0a8");
  b.box(0, GROUND_Y + 0.0, 48.5, 900, 0.04, 9, C.road);
  for (let x = -240; x <= 240; x += 8) b.box(x, GROUND_Y + 0.03, 48.5, 3.2, 0.01, 0.16, C.roadLine);
  // Yard markings for the loading bay.
  for (let k = 0; k < 4; k++) b.box(29, GROUND_Y + 0.035, -8 + k * 4.5, 13, 0.01, 0.1, C.epoxyLine);

  // Boundary wall with a gate opening at the front right.
  const wallH = 2.2;
  const wall = (x: number, z: number, w: number, d: number) => {
    b.block(x, GROUND_Y, z, w, wallH, d, "#e3ded3");
    b.block(x, GROUND_Y + wallH, z, w + 0.1, 0.12, d + 0.1, "#c4bfb3");
  };
  wall(-11, 35, 54, 0.3); // front, left of gate
  wall(32, 35, 12, 0.3); // front, right of gate
  wall(0, -26, 76, 0.3);
  wall(-38, 4.5, 0.3, 61);
  wall(38, 4.5, 0.3, 61);
  for (const x of [16, 26]) b.block(x, GROUND_Y, 35, 0.6, 2.8, 0.6, "#c4bfb3"); // gate posts

  // Loading dock on the right side of the building, with canopy.
  b.block(20.4, GROUND_Y, -3.2, 3.6, 1.6, 11, C.concreteDark);
  b.box(21.2, 4.6, -3.2, 5.4, 0.2, 12, C.steel);
  for (const z of [-8.6, 2.2]) b.block(23.6, GROUND_Y, z, 0.18, 5.1, 0.18, C.steelDark);

  // Two trucks backed onto the dock, cab facing +x.
  for (const z of [-6.0, -0.6]) {
    b.block(28.1, GROUND_Y + 0.55, z, 8.4, 0.35, 1.9, "#3a3e44"); // chassis
    b.block(27.4, GROUND_Y + 0.9, z, 7.2, 2.8, 2.5, C.truckBox);
    b.block(32.6, GROUND_Y + 0.9, z, 2.1, 2.3, 2.4, C.truckCab);
    b.box(33.66, GROUND_Y + 2.45, z, 0.05, 0.9, 2.1, "#6f8ea6"); // windscreen
    for (const x of [24.6, 25.8, 29.6, 32.6]) {
      for (const s of [-1, 1]) b.cyl(x, GROUND_Y + 0.5, z + s * 1.05, 0.5, 0.36, "#1d1f22", "z", 10);
    }
  }

  // Street lights along the pavement.
  for (let x = -90; x <= 90; x += 30) {
    b.block(x, GROUND_Y, 43, 0.14, 7, 0.14, C.steelDark);
    b.box(x, GROUND_Y + 7, 43.9, 0.12, 0.12, 1.8, C.steelDark);
  }

  // Distant neighbour buildings.
  const r = rng(7);
  for (const [x, z, w, h, d] of BLOCKS) {
    b.block(x, GROUND_Y, z, w, h, d, r() > 0.5 ? C.neighbour : C.neighbourDark);
    b.block(x, GROUND_Y + h, z, w + 0.4, 0.5, d + 0.4, "#a9adb0");
  }
  return b.build();
}

function buildWindows() {
  const b = new Batch();
  const r = rng(11);
  for (const [x, z, w, h, d] of BLOCKS) {
    // Face that looks towards the factory.
    const faceZ = z > 0 ? z - d / 2 - 0.05 : z + d / 2 + 0.05;
    // Daytime: regular rows of glass, not a few lit windows.
    for (let wy = GROUND_Y + 2; wy < GROUND_Y + h - 1.5; wy += 3) {
      for (let wx = x - w / 2 + 1.5; wx < x + w / 2 - 1; wx += 2.4) {
        b.box(wx, wy, faceZ, 1.3, 1.1, 0.05, r() > 0.15 ? C.window : "#c7d6e0");
      }
    }
  }
  for (let x = -90; x <= 90; x += 30) b.box(x, GROUND_Y + 6.9, 44.6, 0.5, 0.08, 0.3, "#d8dcdf");
  return b.build();
}

const TREE_SPOTS: [number, number, number][] = [
  [-60, 40, 1.1], [-48, 39, 0.9], [-30, 40, 1.2], [42, 40, 1.1],
  [58, 39, 0.9], [74, 40, 1.2], [-33, 30, 1.0], [-33, -18, 1.1], [33, -20, 0.9], [-24, -22, 0.8],
  [-46, 10, 1.2], [-47, -12, 1.0], [46, 18, 1.1], [47, -8, 0.9], [-20, 30.5, 0.7], [-28, 31, 0.85],
];

const TRUNKS: InstItem[] = TREE_SPOTS.map(([x, z, s]) => ({ p: [x, GROUND_Y + 1.2 * s, z], s: [0.22 * s, 2.4 * s, 0.22 * s], c: C.trunk }));
const CANOPIES: InstItem[] = TREE_SPOTS.map(([x, z, s], i) => ({
  p: [x, GROUND_Y + 3.6 * s, z],
  s: [2.2 * s, 2.6 * s, 2.2 * s],
  r: [0, i * 0.7, 0],
  c: i % 2 ? C.tree : C.treeDark,
}));

const geoTrunk = new THREE.CylinderGeometry(1, 1, 1, 6);
const geoCanopy = new THREE.IcosahedronGeometry(1, 0);

export function Site() {
  return (
    <group>
      <StaticMesh build={buildSite} />
      <StaticMesh build={buildWindows} glow />
      <Inst items={TRUNKS} geometry={geoTrunk} />
      <Inst items={CANOPIES} geometry={geoCanopy} />
    </group>
  );
}
