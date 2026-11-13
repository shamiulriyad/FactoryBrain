"use client";

import { useCallback, useEffect, useState } from "react";
import { CheckCircle2, AlertTriangle, XCircle, RefreshCw } from "lucide-react";
import { cn } from "@/lib/cn";

type State = "operational" | "degraded" | "unreachable";
interface Result {
  website: State;
  api: State;
  httpStatus: number | null;
  latencyMs: number;
  checkedAt: string;
}

const LABEL: Record<State, string> = { operational: "Operational", degraded: "Degraded", unreachable: "Unreachable" };
const ICON = { operational: CheckCircle2, degraded: AlertTriangle, unreachable: XCircle };
const COLOR: Record<State, string> = {
  operational: "text-[rgb(var(--brand-success-ink))]",
  degraded: "text-[rgb(var(--brand-warning-ink))]",
  unreachable: "text-brand-error",
};

/** Live check of the website and the application API. Refreshes every 60 s. */
export function StatusBoard() {
  const [result, setResult] = useState<Result | null>(null);
  const [failed, setFailed] = useState(false);
  const [loading, setLoading] = useState(false);

  const check = useCallback(async () => {
    setLoading(true);
    try {
      const res = await fetch("/api/site-status", { cache: "no-store" });
      setResult((await res.json()) as Result);
      setFailed(false);
    } catch {
      setFailed(true);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    check();
    const id = window.setInterval(check, 60_000);
    return () => window.clearInterval(id);
  }, [check]);

  const rows: { name: string; desc: string; state: State | null }[] = [
    { name: "Website", desc: "This site and its pages", state: failed ? "unreachable" : result ? result.website : null },
    { name: "Application & API", desc: "Sign-in, the BunonBrain app and its database", state: failed ? null : result ? result.api : null },
  ];
  const overall: State | null = failed ? "unreachable" : result ? (result.api === "operational" ? "operational" : result.api === "degraded" ? "degraded" : "unreachable") : null;

  return (
    <div>
      <div
        role="status"
        aria-live="polite"
        className={cn(
          "flex flex-wrap items-center justify-between gap-4 rounded-2xl border p-6",
          overall === "operational" ? "border-brand-emerald/30 bg-brand-emerald-light/50" : overall ? "border-brand-warning/40 bg-brand-warning/10" : "border-brand-border bg-brand-offwhite",
        )}
      >
        <p className="font-heading text-[1.3rem] font-semibold text-brand-text">
          {overall === null ? "Checking services…" : overall === "operational" ? "All systems operational" : "Some services are affected"}
        </p>
        <button
          type="button"
          onClick={check}
          disabled={loading}
          className="inline-flex min-h-11 items-center gap-2 rounded-lg border border-brand-border bg-white px-4 text-[0.9rem] font-medium text-brand-text disabled:opacity-60"
        >
          <RefreshCw size={15} aria-hidden className={loading ? "animate-spin" : undefined} /> Check again
        </button>
      </div>
      <ul className="mt-6 divide-y divide-brand-border rounded-2xl border border-brand-border">
        {rows.map((r) => {
          const Icon = r.state ? ICON[r.state] : null;
          return (
            <li key={r.name} className="flex items-center justify-between gap-4 px-6 py-5">
              <div>
                <p className="font-medium text-brand-text">{r.name}</p>
                <p className="text-[0.9rem] text-brand-muted">{r.desc}</p>
              </div>
              {r.state && Icon ? (
                <span className={cn("inline-flex items-center gap-2 font-medium", COLOR[r.state])}>
                  <Icon size={18} aria-hidden /> {LABEL[r.state]}
                </span>
              ) : (
                <span className="text-brand-muted">{failed ? "Unknown" : "Checking…"}</span>
              )}
            </li>
          );
        })}
      </ul>
      {result && (
        <p className="mt-4 font-plexmono text-[0.75rem] text-brand-muted">
          Last checked {new Date(result.checkedAt).toLocaleString()} · API response {result.latencyMs} ms
          {result.httpStatus ? ` · HTTP ${result.httpStatus}` : ""}
        </p>
      )}
    </div>
  );
}
