"use client";

// One fetch-state shape for every API-backed panel: first load, background
// refresh, error (kept next to the last good data), manual reload and
// optional polling that pauses while the tab is hidden.

import { useCallback, useEffect, useRef, useState } from "react";
import { apiJson, isAbort } from "@/lib/api";

export type Resource<T> = {
  data: T | null;
  error: Error | null;
  /** True until the first response (success or failure). */
  loading: boolean;
  /** True while any request is in flight (first load or refresh). */
  refreshing: boolean;
  /** When the last successful response arrived. */
  updatedAt: Date | null;
  reload: () => Promise<void>;
  /** Replace the cached data after a mutation returned the new state. */
  setData: (next: T | ((cur: T | null) => T | null)) => void;
};

export function useResource<T>(path: string | null, opts: { pollMs?: number } = {}): Resource<T> {
  const { pollMs } = opts;
  const [data, setDataState] = useState<T | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [loading, setLoading] = useState(path !== null);
  const [refreshing, setRefreshing] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);
  const inflight = useRef<AbortController | null>(null);

  const reload = useCallback(async () => {
    if (!path) return;
    inflight.current?.abort();
    const ctrl = new AbortController();
    inflight.current = ctrl;
    setRefreshing(true);
    try {
      const body = await apiJson<T>(path, { signal: ctrl.signal });
      setDataState(body);
      setError(null);
      setUpdatedAt(new Date());
    } catch (e) {
      if (isAbort(e)) return;
      setError(e as Error);
    } finally {
      if (inflight.current === ctrl) {
        inflight.current = null;
        setRefreshing(false);
        setLoading(false);
      }
    }
  }, [path]);

  useEffect(() => {
    setLoading(path !== null);
    void reload();
    return () => inflight.current?.abort();
  }, [reload, path]);

  useEffect(() => {
    if (!pollMs || !path) return;
    const id = setInterval(() => {
      if (typeof document !== "undefined" && document.visibilityState === "hidden") return;
      void reload();
    }, pollMs);
    return () => clearInterval(id);
  }, [pollMs, path, reload]);

  const setData = useCallback((next: T | ((cur: T | null) => T | null)) => {
    setDataState((cur) => (typeof next === "function" ? (next as (c: T | null) => T | null)(cur) : next));
  }, []);

  return { data, error, loading, refreshing, updatedAt, reload, setData };
}

/** Fire a window event that API-backed views listen to after a mutation elsewhere. */
export const DATA_CHANGED_EVENT = "bunonbrain:insights-refresh";

export function announceDataChanged() {
  if (typeof window !== "undefined") window.dispatchEvent(new CustomEvent(DATA_CHANGED_EVENT));
}

/** Re-run `fn` whenever another view announces a change. */
export function useOnDataChanged(fn: () => void) {
  const ref = useRef(fn);
  ref.current = fn;
  useEffect(() => {
    const handler = () => ref.current();
    window.addEventListener(DATA_CHANGED_EVENT, handler);
    return () => window.removeEventListener(DATA_CHANGED_EVENT, handler);
  }, []);
}
