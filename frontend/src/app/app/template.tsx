"use client";

import { AnimatePresence } from "framer-motion";
import { usePathname } from "next/navigation";
import { PageTransition } from "@/components/layout/PageTransition";
import { FrozenRouter } from "@/components/layout/FrozenRouter";

/**
 * App template — remounts on every nav so each main-route change gets a
 * fresh enter animation. Sidebar/Topbar persist because they live in
 * layout.tsx, not here. See the page-transition-animation skill for why
 * we need this pattern instead of mounting inside the page.
 */
export default function AppTemplate({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  return (
    <AnimatePresence mode="wait" initial={false}>
      <PageTransition key={pathname}>
        <FrozenRouter>{children}</FrozenRouter>
      </PageTransition>
    </AnimatePresence>
  );
}