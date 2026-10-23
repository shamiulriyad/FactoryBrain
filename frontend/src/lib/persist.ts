// localStorage helper that is SSR-safe and falls back to in-memory when window is absent.
// Stores expose a single source of truth (initialised on module load in a server-safe way)
// and rehydrate via a useEffect in components that need persisted client state.

const memoryStore: Record<string, string> = {};

function read(key: string): string | null {
  if (typeof window === "undefined") {
    return Object.prototype.hasOwnProperty.call(memoryStore, key) ? memoryStore[key] : null;
  }
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

export const persist = {
  /** Returns `fallback` when key is missing or the JSON is invalid. SSR-safe. */
  get<T>(key: string, fallback: T): T {
    const raw = read(key);
    if (!raw) return fallback;
    try {
      return JSON.parse(raw) as T;
    } catch {
      return fallback;
    }
  },
  set<T>(key: string, value: T) {
    const json = JSON.stringify(value);
    if (typeof window === "undefined") {
      memoryStore[key] = json;
      return;
    }
    try {
      window.localStorage.setItem(key, json);
    } catch {
      // quota or disabled — swallow
    }
  },
  remove(key: string) {
    if (typeof window === "undefined") {
      delete memoryStore[key];
      return;
    }
    try {
      window.localStorage.removeItem(key);
    } catch {
      // swallow
    }
  }
};

/**
 * Returns true after the first client render. Implemented in a separate
 * client-only module: `./useMounted.ts`. Re-export it here for the
 * convenience of existing imports — the `"use client"` directive on the
 * `useMounted` module keeps it out of any server bundle that pulls
 * `persist.ts` indirectly.
 */
export { useMounted } from "./useMounted";
