// Deterministic seeded PRNG so the demo dataset is reproducible.
// Mulberry32 — small, fast, sufficient for demo data.

export function makeRng(seed: number) {
  let s = seed >>> 0;
  return function rng() {
    s = (s + 0x6D2B79F5) >>> 0;
    let t = s;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function pick<T>(rng: () => number, arr: readonly T[]): T {
  return arr[Math.floor(rng() * arr.length)];
}

export function intBetween(rng: () => number, lo: number, hi: number) {
  return Math.floor(rng() * (hi - lo + 1)) + lo;
}

export function range<T>(n: number, fn: (i: number) => T): T[] {
  const out: T[] = [];
  for (let i = 0; i < n; i++) out.push(fn(i));
  return out;
}

export const baseSeed = 0x7A1AB12; // arbitrary placeholder