"use client";

import { useContext, useRef } from "react";
import { LayoutRouterContext } from "next/dist/shared/lib/app-router-context.shared-runtime";

/**
 * Freezes the router context so the exiting page keeps rendering its OWN
 * content during an exit animation, instead of flashing the next route's
 * data mid-swap. Required by the App Router + AnimatePresence exit pattern.
 */
export function FrozenRouter({ children }: { children: React.ReactNode }) {
  const context = useContext(LayoutRouterContext ?? ({} as any));
  const frozen = useRef(context).current;
  return (
    <LayoutRouterContext.Provider value={frozen}>
      {children}
    </LayoutRouterContext.Provider>
  );
}