// Vision + manual-docs store.
// Pattern mirrors ask.store.ts / sensors.store.ts: Zustand state, persisted
// under a `bunonbrain:*` key, with a small mutator set and a reset hook for
// the demo-reset affordance. Seed data is sourced from src/data/vision.ts
// (Simulated — no real model inference, no Supabase).

"use client";

import { create } from "zustand";
import { persist as storage } from "@/lib/persist";
import { buildManualDocs, buildVisionResults } from "@/data/vision";
import {
  ManualDocListSchema,
  VisionResultListSchema,
  type ManualDocT,
  type VisionResultT
} from "@/services/sensors.schemas";

const VISION_KEY = "bunonbrain:vision";
const MANUALS_KEY = "bunonbrain:manualDocs";

/** Caps chosen to mirror sensors.store.ts + ask.store.ts. */
const MAX_RESULTS = 60;
const MAX_DOCS = 100;

function readValidatedResults(): VisionResultT[] {
  const raw = storage.get<unknown>(VISION_KEY, []);
  const parsed = VisionResultListSchema.safeParse(raw);
  if (parsed.success) return parsed.data;
  // Drop corrupted state — keep the app booting.
  return [];
}

function readValidatedDocs(): ManualDocT[] {
  const raw = storage.get<unknown>(MANUALS_KEY, []);
  const parsed = ManualDocListSchema.safeParse(raw);
  if (parsed.success) return parsed.data;
  return [];
}

type VisionState = {
  results: VisionResultT[];
  docs: ManualDocT[];

  // Vision results --------------------------------------------------------
  pushResult: (r: VisionResultT) => void;
  pushManyResults: (rs: VisionResultT[]) => void;
  removeResult: (id: string) => void;

  // Manual docs -----------------------------------------------------------
  pushDoc: (d: ManualDocT) => void;
  removeDoc: (id: string) => void;

  // Demo affordance -------------------------------------------------------
  reset: () => void;
};

export const useVisionStore = create<VisionState>((set, get) => ({
  results: readValidatedResults(),
  docs: readValidatedDocs(),

  pushResult: (r) => {
    const cur = get().results.filter((x) => x.id !== r.id);
    const next = [r, ...cur].slice(0, MAX_RESULTS);
    storage.set(VISION_KEY, next);
    set({ results: next });
  },
  pushManyResults: (rs) => {
    if (!rs.length) return;
    const seen = new Set(get().results.map((x) => x.id));
    const fresh = rs.filter((r) => !seen.has(r.id));
    if (!fresh.length) return;
    const next = [...fresh.reverse(), ...get().results].slice(0, MAX_RESULTS);
    storage.set(VISION_KEY, next);
    set({ results: next });
  },
  removeResult: (id) => {
    const next = get().results.filter((r) => r.id !== id);
    storage.set(VISION_KEY, next);
    set({ results: next });
  },

  pushDoc: (d) => {
    const cur = get().docs.filter((x) => x.id !== d.id);
    const next = [d, ...cur].slice(0, MAX_DOCS);
    storage.set(MANUALS_KEY, next);
    set({ docs: next });
  },
  removeDoc: (id) => {
    const next = get().docs.filter((d) => d.id !== id);
    storage.set(MANUALS_KEY, next);
    set({ docs: next });
  },

  reset: () => {
    // Small initial seed set; both sides labelled Simulated.
    const freshResults = buildVisionResults(0xC0FFEE_71, 6);
    const freshDocs = buildManualDocs();
    storage.set(VISION_KEY, freshResults);
    storage.set(MANUALS_KEY, freshDocs);
    set({ results: freshResults, docs: freshDocs });
  }
}));

/** First-run helper: seeds a tiny initial set when no persisted state exists. */
export function ensureVisionSeeded(): void {
  const s = useVisionStore.getState();
  if (s.results.length === 0 || s.docs.length === 0) {
    s.reset();
  }
}
