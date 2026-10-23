"use client";

import { motion, useReducedMotion } from "framer-motion";
import { motionTokens } from "@/lib/motion";

/**
 * Wraps page content so route changes get an enter/exit transition. Used
 * inside a pathname-keyed AnimatePresence (see template.tsx).
 *
 * Direction: short vertical translate + opacity fade. Reduced-motion path
 * collapses to a short opacity-only fade (Tier 2).
 */
export function PageTransition({ children }: { children: React.ReactNode }) {
  const reduceMotion = useReducedMotion();
  return (
    <motion.div
      initial={false}
      animate={{ opacity: 1 }}
      exit={{ opacity: 1 }}
      transition={{ duration: reduceMotion ? 0 : motionTokens.duration.page / 1000, ease: motionTokens.ease.out }}
      style={{ minHeight: "100%" }}
    >
      {children}
    </motion.div>
  );
}