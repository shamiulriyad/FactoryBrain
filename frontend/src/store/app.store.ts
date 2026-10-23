"use client";

import { create } from "zustand";
import { persist as storage } from "@/lib/persist";
import { defaultLocale, type Locale } from "@/i18n/registry";

export type Theme = "light" | "dark" | "system";

type AppState = {
  locale: Locale;
  theme: Theme;
  commandPaletteOpen: boolean;
  resetRequired: boolean;
  /** True once the user has reached Overview after onboarding. */
  onboardedAt: string | null;
  /** Whether the product tour has been completed (any finish flow). */
  tourCompleted: boolean;
  /** Whether the user explicitly dismissed the tour this run. */
  tourDismissed: boolean;
  /** Recent global-search queries (most-recent first, max 8). */
  recentSearches: string[];
  setLocale: (l: Locale) => void;
  setTheme: (t: Theme) => void;
  applyTheme: () => void;
  openCommandPalette: () => void;
  closeCommandPalette: () => void;
  toggleCommandPalette: () => void;
  markOnboarded: () => void;
  setTourCompleted: (v: boolean) => void;
  setTourDismissed: (v: boolean) => void;
  pushRecentSearch: (q: string) => void;
  clearRecentSearches: () => void;
  resetDemo: () => void;
};

const safeGet = <T,>(key: string, fallback: T): T => storage.get<T>(key, fallback);

export const useAppStore = create<AppState>((set, get) => ({
  locale: safeGet<Locale>("bunonbrain:locale", defaultLocale),
  theme: safeGet<Theme>("bunonbrain:theme", "dark"),
  commandPaletteOpen: false,
  resetRequired: false,
  onboardedAt: safeGet<string | null>("bunonbrain:onboardedAt", null),
  tourCompleted: safeGet<boolean>("bunonbrain:tourCompleted", false),
  tourDismissed: safeGet<boolean>("bunonbrain:tourDismissed", false),
  recentSearches: safeGet<string[]>("bunonbrain:recentSearches", []),
  setLocale: (l) => {
    storage.set("bunonbrain:locale", l);
    set({ locale: l });
  },
  setTheme: (t) => {
    storage.set("bunonbrain:theme", t);
    set({ theme: t });
    get().applyTheme();
  },
  applyTheme: () => {
    if (typeof document === "undefined") return;
    const stored = get().theme;
    let resolved: "light" | "dark" = stored === "system"
      ? (window.matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark")
      : stored;
    const root = document.documentElement;
    if (resolved === "light") {
      root.classList.add("light");
      root.classList.remove("dark");
    } else {
      root.classList.add("dark");
      root.classList.remove("light");
    }
  },
  openCommandPalette: () => set({ commandPaletteOpen: true }),
  closeCommandPalette: () => set({ commandPaletteOpen: false }),
  toggleCommandPalette: () => set((s) => ({ commandPaletteOpen: !s.commandPaletteOpen })),
  markOnboarded: () => {
    const stamp = new Date().toISOString();
    storage.set("bunonbrain:onboardedAt", stamp);
    set({ onboardedAt: stamp });
  },
  setTourCompleted: (v) => {
    storage.set("bunonbrain:tourCompleted", v);
    set({ tourCompleted: v });
  },
  setTourDismissed: (v) => {
    storage.set("bunonbrain:tourDismissed", v);
    set({ tourDismissed: v });
  },
  pushRecentSearch: (q) => {
    const trimmed = q.trim();
    if (!trimmed) return;
    const cur = get().recentSearches.filter((s) => s.toLowerCase() !== trimmed.toLowerCase());
    const next = [trimmed, ...cur].slice(0, 8);
    storage.set("bunonbrain:recentSearches", next);
    set({ recentSearches: next });
  },
  clearRecentSearches: () => {
    storage.set("bunonbrain:recentSearches", []);
    set({ recentSearches: [] });
  },
  resetDemo: () => {
    if (typeof window !== "undefined") {
      try {
        for (const k of Object.keys(window.localStorage)) {
          if (
            k.startsWith("bunonbrain:") ||
            k.startsWith("factoryBrain:") ||
            k.startsWith("thalamus:")
          ) {
            window.localStorage.removeItem(k);
          }
        }
      } catch {
        // ignore
      }
    }
    set({ resetRequired: true });
  }
}));
