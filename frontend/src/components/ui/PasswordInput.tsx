"use client";

import { forwardRef, useState } from "react";
import { Eye, EyeOff } from "lucide-react";
import { Input } from "@/components/ui/Input";
import { cn } from "@/lib/cn";

type Props = Omit<React.InputHTMLAttributes<HTMLInputElement>, "type"> & { className?: string };

/**
 * A password field with a show / hide toggle, so people can check what they
 * typed. Hidden by default; the toggle is a real button (keyboard and screen
 * reader friendly) and never submits the form.
 */
export const PasswordInput = forwardRef<HTMLInputElement, Props>(function PasswordInput({ className, ...rest }, ref) {
  const [visible, setVisible] = useState(false);
  return (
    <span className="relative block w-full">
      <Input ref={ref} type={visible ? "text" : "password"} className={cn("pr-10", className)} {...rest} />
      <button
        type="button"
        onClick={() => setVisible((v) => !v)}
        aria-label={visible ? "Hide password" : "Show password"}
        aria-pressed={visible}
        title={visible ? "Hide password" : "Show password"}
        className="absolute inset-y-0 right-0 flex w-9 items-center justify-center rounded-r-md text-fg-tertiary hover:text-fg-primary focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--accent)]"
        data-testid="password-toggle"
      >
        {visible ? <EyeOff size={15} aria-hidden /> : <Eye size={15} aria-hidden />}
      </button>
    </span>
  );
});
