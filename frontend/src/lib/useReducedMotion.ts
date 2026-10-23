"use client";

import { useEffect, useState } from "react";

const QUERY = "(prefers-reduced-motion: reduce)";

/**
 * Returns true when the OS-level reduced-motion preference is set.
 *
 * Default to `false` during SSR + the first client render so React's
 * server and client output agree; sync to the real value in an effect
 * after hydration. Listens for `change` so toggling the OS setting
 * updates the UI live without a reload.
 */
export function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(false);

  useEffect(() => {
    if (typeof window === "undefined" || !window.matchMedia) return;
    const mq = window.matchMedia(QUERY);
    const update = () => setReduced(mq.matches);
    update();
    mq.addEventListener("change", update);
    return () => mq.removeEventListener("change", update);
  }, []);

  return reduced;
}