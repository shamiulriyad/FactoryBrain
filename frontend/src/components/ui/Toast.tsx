"use client";

import { useEffect, useState, useCallback, type ReactNode, createContext, useContext } from "react";
import { AnimatePresence, motion, useReducedMotion } from "framer-motion";
import { AlertTriangle, Check, Info, X } from "lucide-react";
import { cn } from "@/lib/cn";

export type ToastTone = "success" | "error" | "info";

type ToastEntry = {
  id: string;
  title: string;
  description?: string;
  /** success (default), error or info — sets the icon and colour. */
  tone?: ToastTone;
  /** Optional undo — fires once if the user clicks within `ttlMs`. */
  onUndo?: () => void;
  ttlMs?: number;
};

type ToastContextValue = {
  push: (t: Omit<ToastEntry, "id">) => string;
  dismiss: (id: string) => void;
};

const ToastContext = createContext<ToastContextValue | null>(null);

const TONE: Record<ToastTone, { icon: ReactNode; cls: string }> = {
  success: {
    icon: <Check size={12} />,
    cls: "bg-[var(--risk-low-soft)] border-[var(--risk-low-border)] text-[var(--risk-low)]"
  },
  error: {
    icon: <AlertTriangle size={12} />,
    cls: "bg-[var(--risk-high-soft)] border-[var(--risk-high-border)] text-[var(--risk-high)]"
  },
  info: {
    icon: <Info size={12} />,
    cls: "bg-[var(--info-soft)] border-[var(--info-border)] text-[var(--info)]"
  }
};

export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastEntry[]>([]);
  const reduceMotion = useReducedMotion();
  const dismiss = useCallback((id: string) => setItems((arr) => arr.filter((t) => t.id !== id)), []);
  const push = useCallback((t: Omit<ToastEntry, "id">) => {
    const id = `toast-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`;
    const entry: ToastEntry = { id, ttlMs: t.tone === "error" ? 8000 : 5000, ...t };
    setItems((arr) => [...arr.slice(-3), entry]);
    return id;
  }, []);

  useEffect(() => {
    const timers = items.map((t) => window.setTimeout(() => dismiss(t.id), t.ttlMs ?? 5000));
    return () => timers.forEach(clearTimeout);
  }, [items, dismiss]);

  return (
    <ToastContext.Provider value={{ push, dismiss }}>
      {children}
      <div
        aria-live="polite"
        className="fixed bottom-20 md:bottom-4 right-4 left-4 sm:left-auto z-[60] flex flex-col gap-2 sm:max-w-sm pointer-events-none"
      >
        <AnimatePresence mode="popLayout" initial={false}>
          {items.map((t) => {
            const tone = TONE[t.tone ?? "success"];
            return (
              <motion.div
                key={t.id}
                layout={!reduceMotion}
                role={t.tone === "error" ? "alert" : "status"}
                data-testid="toast"
                data-tone={t.tone ?? "success"}
                initial={reduceMotion ? { opacity: 0 } : { opacity: 0, y: 12, scale: 0.96 }}
                animate={{ opacity: 1, y: 0, scale: 1 }}
                exit={reduceMotion ? { opacity: 0 } : { opacity: 0, y: 6, scale: 0.98 }}
                transition={{ duration: 0.2, ease: [0.16, 1, 0.3, 1] }}
                className="glass-strong p-3 flex items-start gap-2.5 pointer-events-auto"
              >
                <span className={cn("mt-0.5 size-5 rounded-full border inline-flex items-center justify-center shrink-0", tone.cls)}>
                  {tone.icon}
                </span>
                <div className="flex-1 min-w-0">
                  <p className="text-body text-fg-primary font-medium">{t.title}</p>
                  {t.description && <p className="mt-0.5 text-caption text-fg-secondary break-words">{t.description}</p>}
                </div>
                {t.onUndo && (
                  <button
                    type="button"
                    onClick={() => {
                      t.onUndo?.();
                      dismiss(t.id);
                    }}
                    className="text-caption text-accent hover:underline"
                  >
                    Undo
                  </button>
                )}
                <button
                  type="button"
                  onClick={() => dismiss(t.id)}
                  aria-label="Dismiss notification"
                  className="size-6 -mr-1 -mt-1 rounded inline-flex items-center justify-center text-fg-tertiary hover:text-fg-primary"
                >
                  <X size={12} />
                </button>
              </motion.div>
            );
          })}
        </AnimatePresence>
      </div>
    </ToastContext.Provider>
  );
}

export function useToast(): ToastContextValue {
  const ctx = useContext(ToastContext);
  if (!ctx) {
    return {
      push: () => "",
      dismiss: () => undefined
    };
  }
  return ctx;
}
