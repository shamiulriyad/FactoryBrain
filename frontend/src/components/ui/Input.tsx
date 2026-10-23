"use client";

import { forwardRef } from "react";
import { cn } from "@/lib/cn";

type Props = React.InputHTMLAttributes<HTMLInputElement> & { className?: string };

export const Input = forwardRef<HTMLInputElement, Props>(function Input({ className, ...rest }, ref) {
  return (
    <input
      ref={ref}
      className={cn(
        "h-9 w-full rounded-md bg-surface-2 border border-border-subtle px-3 text-body text-fg-primary placeholder:text-fg-tertiary",
        "transition-[border-color,box-shadow] duration-150",
        "hover:border-[var(--border-strong)]",
        "focus:outline-none focus:border-[var(--accent)] focus:shadow-[0_0_0_3px_var(--accent-soft)]",
        className
      )}
      {...rest}
    />
  );
});