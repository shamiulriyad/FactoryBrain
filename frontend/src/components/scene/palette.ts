/** Daylight palette for the 3D scene: clear sky, light concrete, bright interiors. */
export const C = {
  fog: "#dfe9f2",
  ground: "#9fb08f",
  yard: "#c9c6bd",
  road: "#6b7076",
  roadLine: "#f4f1e8",
  concrete: "#d9d6cf",
  concreteDark: "#b8b5ad",
  slabEdge: "#c4c1ba",
  backWall: "#e4e1da",
  wallTrim: "#c9c6bf",
  epoxy: "#b7c9bf",
  epoxyLine: "#e0b73f",
  mullion: "#a7b0b8",
  glass: "#bcd6e6",
  steel: "#a9b1b8",
  steelDark: "#6e7780",
  table: "#d8cdb6",
  tableDark: "#b5a88e",
  machine: "#f2f3f0",
  machineDark: "#4f6476",
  machineAccent: "#1f7a5c",
  wood: "#b48a5a",
  carton: "#c99a62",
  cartonDark: "#ad8150",
  lamp: "#ffffff",
  lampWarm: "#f2b84b",
  litTable: "#fbfaf4",
  thread: "#ff6b45",
  ok: "#3fbf7f",
  tree: "#5f8f4e",
  treeDark: "#4b7a3f",
  trunk: "#6b5440",
  neighbour: "#d7d2c8",
  neighbourDark: "#bfc4c8",
  window: "#8fb2c9",
  truckCab: "#e9ecef",
  truckBox: "#f4f4f1",
  solar: "#2f4a6b",
  hvac: "#c3c7c8",
  hair: "#1d1a17",
  trousers: "#3b4250",
};

export const FABRICS = [
  "#4f6fa8", "#b0575a", "#c9b48a", "#5f8f6d", "#d9d2c3", "#33476b",
  "#d79a52", "#7d64a3", "#e4e0d6", "#9b6b47", "#4f8a9a", "#a2809f",
];

/** Work clothes: bright enough to read against the floor from the drone. */
export const CLOTHING = [
  "#2f74c0", "#d24d57", "#e3a72f", "#1f9e7a", "#8a5cc2", "#e2729c",
  "#f1f1ee", "#3a8fb7", "#c9682c", "#5b6fd6", "#2a9d8f", "#b5443b",
];

export const SKIN = ["#a8754f", "#c08a62", "#8a5a3b", "#d0a07a", "#97664a"];

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
