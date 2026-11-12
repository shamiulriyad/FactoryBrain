/** Muted dusk palette for the 3D scene. */
export const C = {
  fog: "#5f5662",
  ground: "#2a2d2c",
  yard: "#4c4e52",
  road: "#24272b",
  roadLine: "#8c8a80",
  concrete: "#8a8984",
  concreteDark: "#64666a",
  slabEdge: "#55585d",
  backWall: "#4b4f55",
  wallTrim: "#3c4046",
  epoxy: "#3d6a59",
  epoxyLine: "#c9b56a",
  mullion: "#262c33",
  glass: "#7f97a8",
  steel: "#6f767d",
  steelDark: "#3f454c",
  table: "#a69b88",
  tableDark: "#7b7364",
  machine: "#c9cbc6",
  machineDark: "#5a6066",
  wood: "#8a6d4c",
  carton: "#b48c5c",
  cartonDark: "#9a754b",
  lamp: "#ffe3b3",
  lampWarm: "#f2c27a",
  litTable: "#f4efe2",
  thread: "#ff6b45",
  ok: "#8fc9a8",
  tree: "#3a5243",
  treeDark: "#2f4337",
  trunk: "#4a3d33",
  neighbour: "#3a3d44",
  neighbourDark: "#30333a",
  window: "#f2c27a",
  truckCab: "#7d8a92",
  truckBox: "#b9b6ad",
  solar: "#2c3a4f",
  hvac: "#9ea2a3",
};

export const FABRICS = [
  "#3f4f6b", "#6b4a4a", "#7d7461", "#4f6355", "#8c8478", "#2f3a4f",
  "#9a8466", "#5c4f6e", "#a59f92", "#6e5a45", "#41555e", "#7a6c7d",
];

export const CLOTHING = [
  "#4d5f73", "#7a5656", "#5e6b52", "#8a7b62", "#3e4a5c", "#6f5f74",
  "#957a5c", "#4f6b66", "#a08c76", "#5a5048",
];

export const SKIN = ["#8d6346", "#a87b5a", "#6e4a33", "#b98c69", "#7c553b"];

/** Deterministic PRNG so the scene looks the same on every load. */
export function rng(seed: number) {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6d2b79f5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export const pick = <T,>(r: () => number, list: readonly T[]) => list[Math.floor(r() * list.length)];
