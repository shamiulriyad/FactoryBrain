// Ask BunonBrain history — a per-user list of questions with the rendered answer blocks.
// Persisted so reload preserves the conversation arc.

"use client";

import { create } from "zustand";
import { persist as storage } from "@/lib/persist";

export type AskRecord = {
  id: string;
  query: string;
  /** Locale the user was in at the time of asking. */
  locale: "en" | "bn";
  /** Source of the answer. */
  source: "demo" | "live";
  /** Block kind set we persisted for this answer. */
  blocks: Array<{
    type: "analyzed" | "finding" | "factors" | "evidence" | "recommendation" | "done";
    data: any;
  }>;
  /** ISO timestamp. */
  isoDate: string;
};

type AskState = {
  history: AskRecord[];
  push: (rec: AskRecord) => void;
  remove: (id: string) => void;
  clear: () => void;
};

export const useAskStore = create<AskState>((set, get) => ({
  history: storage.get<AskRecord[]>("bunonbrain:askHistory", []),
  push: (rec) => {
    const cur = get().history.filter((r) => r.id !== rec.id);
    const next = [rec, ...cur].slice(0, 30);
    storage.set("bunonbrain:askHistory", next);
    set({ history: next });
  },
  remove: (id) => {
    const next = get().history.filter((r) => r.id !== id);
    storage.set("bunonbrain:askHistory", next);
    set({ history: next });
  },
  clear: () => {
    storage.set("bunonbrain:askHistory", []);
    set({ history: [] });
  }
}));