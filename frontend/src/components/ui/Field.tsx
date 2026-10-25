"use client";

import { forwardRef, useId, type ReactNode } from "react";
import { cn } from "@/lib/cn";

type FieldProps = {
  label: ReactNode;
  hint?: ReactNode;
  error?: string | null;
  /** Render prop receives the generated id + aria wiring for the control. */
  children: (ids: { id: string; describedBy?: string; invalid: boolean }) => ReactNode;
  className?: string;
  id?: string;
};

/** Label + control + hint/error, with the aria attributes wired up. */
export function Field({ label, hint, error, children, className, id }: FieldProps) {
  const auto = useId();
  const controlId = id ?? `f-${auto}`;
  const hintId = hint ? `${controlId}-hint` : undefined;
  const errorId = error ? `${controlId}-error` : undefined;
  const describedBy = [hintId, errorId].filter(Boolean).join(" ") || undefined;
  return (
    <div className={className}>
      <label htmlFor={controlId} className="field-label">
        {label}
      </label>
      {children({ id: controlId, describedBy, invalid: Boolean(error) })}
      {hint && !error && (
        <p id={hintId} className="field-hint">
          {hint}
        </p>
      )}
      {error && (
        <p id={errorId} className="field-error" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

type SelectProps = React.SelectHTMLAttributes<HTMLSelectElement> & { className?: string };

export const Select = forwardRef<HTMLSelectElement, SelectProps>(function Select({ className, ...rest }, ref) {
  return <select ref={ref} className={cn("control", className)} {...rest} />;
});

type TextAreaProps = React.TextareaHTMLAttributes<HTMLTextAreaElement> & { className?: string };

export const TextArea = forwardRef<HTMLTextAreaElement, TextAreaProps>(function TextArea({ className, ...rest }, ref) {
  return <textarea ref={ref} className={cn("control", className)} {...rest} />;
});

/** Compact labelled select for filter bars. The label stays visible to screen readers. */
export function FilterSelect({
  label,
  value,
  onChange,
  children,
  className,
  testId
}: {
  label: string;
  value: string;
  onChange: (v: string) => void;
  children: ReactNode;
  className?: string;
  testId?: string;
}) {
  const id = useId();
  return (
    <div className={cn("flex items-center gap-2", className)}>
      <label htmlFor={id} className="text-caption text-fg-tertiary whitespace-nowrap">
        {label}
      </label>
      <select
        id={id}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        data-testid={testId}
        className="control !h-8 !w-auto !text-caption"
      >
        {children}
      </select>
    </div>
  );
}
