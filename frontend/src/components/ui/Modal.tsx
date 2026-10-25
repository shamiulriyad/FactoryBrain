"use client";

import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { X } from "lucide-react";
import { cn } from "@/lib/cn";
import { motion, AnimatePresence, useReducedMotion } from "framer-motion";
import { Button } from "./Button";

type Props = {
  open: boolean;
  onClose: () => void;
  title?: ReactNode;
  /** Short text under the title. */
  description?: ReactNode;
  children: ReactNode;
  className?: string;
  /** Optional footer (actions, etc.). */
  footer?: ReactNode;
  /** While true, Escape / backdrop clicks don't close (e.g. a request is in flight). */
  locked?: boolean;
  testId?: string;
};

const FOCUSABLE = 'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])';

/** Accessible modal — focus trap, Escape, click-outside, focus restored on close. */
export function Modal({ open, onClose, title, description, children, footer, className, locked, testId }: Props) {
  const ref = useRef<HTMLDivElement>(null);
  const reduceMotion = useReducedMotion();
  const titleId = useId();
  const descId = useId();

  useEffect(() => {
    if (!open) return;
    const previous = document.activeElement as HTMLElement | null;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && !locked) {
        e.stopPropagation();
        onClose();
        return;
      }
      if (e.key !== "Tab" || !ref.current) return;
      const nodes = Array.from(ref.current.querySelectorAll<HTMLElement>(FOCUSABLE));
      if (nodes.length === 0) return;
      const first = nodes[0];
      const last = nodes[nodes.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    };
    window.addEventListener("keydown", onKey, true);
    const t = setTimeout(() => {
      const target = ref.current?.querySelector<HTMLElement>("[data-autofocus]") ?? ref.current;
      target?.focus();
    }, 0);
    return () => {
      clearTimeout(t);
      window.removeEventListener("keydown", onKey, true);
      previous?.focus?.();
    };
  }, [open, onClose, locked]);

  return (
    <AnimatePresence>
      {open && (
        <motion.div
          initial={{ opacity: 0 }}
          animate={{ opacity: 1 }}
          exit={{ opacity: 0 }}
          transition={{ duration: 0.16 }}
          className="fixed inset-0 z-50 bg-[var(--bg-overlay)] flex items-end sm:items-center justify-center p-0 sm:p-4"
          onClick={() => !locked && onClose()}
        >
          <motion.div
            initial={reduceMotion ? { opacity: 0 } : { opacity: 0, y: 12, scale: 0.98 }}
            animate={{ opacity: 1, y: 0, scale: 1 }}
            exit={reduceMotion ? { opacity: 0 } : { opacity: 0, y: 6, scale: 0.98 }}
            transition={{ duration: 0.2, ease: [0.16, 1, 0.3, 1] }}
            role="dialog"
            aria-modal="true"
            aria-labelledby={title ? titleId : undefined}
            aria-describedby={description ? descId : undefined}
            tabIndex={-1}
            ref={ref}
            data-testid={testId}
            onClick={(e) => e.stopPropagation()}
            className={cn(
              "glass-strong w-full sm:max-w-lg max-h-[90dvh] flex flex-col outline-none rounded-b-none sm:rounded-[var(--radius-card)]",
              className
            )}
          >
            {title && (
              <header className="flex items-start justify-between gap-3 px-5 pt-4 pb-3 border-b border-border-subtle">
                <div className="min-w-0">
                  <h2 id={titleId} className="text-title font-semibold text-fg-primary">
                    {title}
                  </h2>
                  {description && (
                    <p id={descId} className="mt-1 text-caption text-fg-secondary">
                      {description}
                    </p>
                  )}
                </div>
                <button
                  type="button"
                  onClick={onClose}
                  disabled={locked}
                  aria-label="Close"
                  className="size-8 -mr-2 rounded-md hover:bg-surface-2 flex items-center justify-center text-fg-tertiary hover:text-fg-primary transition-colors disabled:opacity-40"
                >
                  <X size={16} />
                </button>
              </header>
            )}
            <div className="p-5 overflow-y-auto">{children}</div>
            {footer && (
              <footer className="flex flex-wrap items-center justify-end gap-2 px-5 py-3 border-t border-border-subtle">{footer}</footer>
            )}
          </motion.div>
        </motion.div>
      )}
    </AnimatePresence>
  );
}

type ConfirmProps = {
  open: boolean;
  title: ReactNode;
  description?: ReactNode;
  children?: ReactNode;
  confirmLabel: ReactNode;
  cancelLabel?: ReactNode;
  tone?: "primary" | "danger" | "success";
  /** While the confirmed action runs: buttons disabled, dialog can't be dismissed. */
  busy?: boolean;
  /** Extra condition for enabling confirm (e.g. a required reason). */
  confirmDisabled?: boolean;
  onConfirm: () => void;
  onCancel: () => void;
  testId?: string;
  /** Test id for the confirm button (defaults to `${testId}-confirm`). */
  confirmTestId?: string;
};

/** Two-button confirmation for final or costly actions. */
export function ConfirmDialog({
  open,
  title,
  description,
  children,
  confirmLabel,
  cancelLabel = "Cancel",
  tone = "primary",
  busy,
  confirmDisabled,
  onConfirm,
  onCancel,
  testId,
  confirmTestId
}: ConfirmProps) {
  return (
    <Modal
      open={open}
      onClose={onCancel}
      title={title}
      description={description}
      locked={busy}
      testId={testId}
      footer={
        <>
          <Button variant="ghost" onClick={onCancel} disabled={busy} data-autofocus={tone === "danger" ? true : undefined}>
            {cancelLabel}
          </Button>
          <Button
            variant={tone}
            onClick={onConfirm}
            disabled={busy || confirmDisabled}
            data-testid={confirmTestId ?? (testId ? `${testId}-confirm` : undefined)}
            data-autofocus={tone === "danger" ? undefined : true}
          >
            {busy ? "Working…" : confirmLabel}
          </Button>
        </>
      }
    >
      {children}
    </Modal>
  );
}

/** Small helper for components that open a confirm for one of several targets. */
export function useConfirmTarget<T>() {
  const [target, setTarget] = useState<T | null>(null);
  return { target, open: (t: T) => setTarget(t), close: () => setTarget(null) };
}
