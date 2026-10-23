"use client";

import { AnimatePresence } from "framer-motion";
import { usePathname } from "next/navigation";
import { PageTransition } from "@/components/layout/PageTransition";
import { FrozenRouter } from "@/components/layout/FrozenRouter";

/**
 * Onboarding template — remounts on every nav so each step gets a fresh
 * enter animation. `mode="wait"` makes AnimatePresence run the exit
 * before mounting the new page; FrozenRouter keeps the outgoing tree
 * rendering its own content during the exit.
 */
export default function OnboardingTemplate({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  return (
    <AnimatePresence mode="wait" initial={false}>
      <PageTransition key={pathname}>
        <FrozenRouter>{children}</FrozenRouter>
      </PageTransition>
    </AnimatePresence>
  );
}