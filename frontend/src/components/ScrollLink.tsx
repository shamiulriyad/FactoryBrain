"use client";

import type { AnchorHTMLAttributes, MouseEvent } from "react";
import { scrollToStep, stepScroll } from "@/lib/tourStore";

/**
 * An in-page link that, once the tour is measured, scrolls so the target
 * step sits exactly at its camera keyframe. Plain anchor without JS.
 */
export function ScrollLink({ step, onClick, ...rest }: AnchorHTMLAttributes<HTMLAnchorElement> & { step: number }) {
  const handle = (e: MouseEvent<HTMLAnchorElement>) => {
    onClick?.(e);
    if (e.defaultPrevented || stepScroll[step] === undefined) return;
    e.preventDefault();
    scrollToStep(step);
  };
  return <a {...rest} onClick={handle} />;
}
