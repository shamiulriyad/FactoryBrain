"use client";

import { cn } from "@/lib/cn";

export function Skeleton({ className, style }: { className?: string; style?: React.CSSProperties }) {
  return (
    <div
      aria-hidden
      style={style}
      className={cn("rounded-md bg-surface-2 animate-pulse", className)}
    />
  );
}

export function SkeletonLines({ lines = 3 }: { lines?: number }) {
  return (
    <div className="space-y-2">
      {Array.from({ length: lines }).map((_, i) => (
        <Skeleton
          key={i}
          className="h-3"
          // last line shorter for realism
          {...(i === lines - 1 ? { style: { width: "70%" } } : {})}
        />
      ))}
    </div>
  );
}