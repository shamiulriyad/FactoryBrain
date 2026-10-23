"use client";

import { motion, useReducedMotion } from "framer-motion";
import { cn } from "@/lib/cn";

type Props = {
  title: string;
  body?: string;
  className?: string;
  icon?: React.ReactNode;
};

export function EmptyState({ title, body, className, icon }: Props) {
  const reduceMotion = useReducedMotion();
  return (
    <motion.div
      initial={reduceMotion ? false : { opacity: 0, y: 6 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.24, ease: [0.22, 1, 0.36, 1] }}
      className={cn(
        "flex flex-col items-center justify-center text-center py-12 px-6 text-fg-tertiary",
        className
      )}
    >
      {icon && (
        <motion.div
          initial={reduceMotion ? false : { scale: 0.92, opacity: 0 }}
          animate={{ scale: 1, opacity: 0.7 }}
          transition={{ duration: 0.32, ease: [0.22, 1, 0.36, 1] }}
          className="mb-3"
        >
          {icon}
        </motion.div>
      )}
      <p className="text-body text-fg-secondary">{title}</p>
      {body && <p className="mt-1 text-caption max-w-md">{body}</p>}
    </motion.div>
  );
}