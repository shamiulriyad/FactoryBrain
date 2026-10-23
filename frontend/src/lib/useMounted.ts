"use client";

import { useEffect, useState } from "react";

/**
 * Returns true after the first client render. Use it to gate any UI that
 * depends on persisted state so the server and the first client render
 * produce the same markup.
 */
export function useMounted(): boolean {
  const [m, setM] = useState(false);
  useEffect(() => setM(true), []);
  return m;
}
