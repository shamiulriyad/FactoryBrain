"use client";

// Machine health for /app Overview: one cell per machine in the sensor
// snapshot (GET /api/sensors/snapshot), worst status first. Values are the
// snapshot's own vibration, temperature and duty cycle.

import Link from "next/link";
import { ArrowUpRight, Cpu } from "lucide-react";
import { Panel } from "@/components/ui/Panel";
import { Badge } from "@/components/ui/Badge";
import { Skeleton } from "@/components/ui/Skeleton";
import { cn } from "@/lib/cn";
import { errorMessage } from "@/lib/api";
import type { Resource } from "@/lib/useResource";
import type { SensorSnapshot } from "@/services/contracts";

const STATUS: Record<string, { label: string; dot: string; cell: string; rank: number }> = {
  down: { label: "Down", dot: "bg-[var(--risk-high)]", cell: "border-[var(--risk-high-border)] bg-[var(--risk-high-soft)]", rank: 0 },
  at_risk: { label: "At risk", dot: "bg-[var(--risk-medium)]", cell: "border-[var(--risk-medium-border)] bg-[var(--risk-medium-soft)]", rank: 1 },
  healthy: { label: "Healthy", dot: "bg-[var(--risk-low)]", cell: "border-border-subtle bg-surface-2", rank: 2 }
};
const UNKNOWN = { label: "Unknown", dot: "bg-fg-tertiary", cell: "border-border-subtle bg-surface-2", rank: 3 };
const statusOf = (s: string) => STATUS[s] ?? UNKNOWN;

export function MachineHealthPanel({ snapshot }: { snapshot: Resource<SensorSnapshot> }) {
  const machines = (snapshot.data?.machines ?? []).slice().sort((a, b) => statusOf(a.status).rank - statusOf(b.status).rank || a.id.localeCompare(b.id));
  const count = (s: string) => machines.filter((m) => m.status === s).length;

  return (
    <Panel
      title="Machine health"
      subtitle={snapshot.data ? `${machines.length} machines · sensor tick #${snapshot.data.tick}` : "From the sensor snapshot"}
      icon={<Cpu size={14} />}
      right={
        <>
          {snapshot.data?.simulated && <span title={snapshot.data.source}><Badge tone="accent" dot>Simulated</Badge></span>}
          <Link href="/app/sensors" className="text-caption text-accent hover:underline inline-flex items-center gap-1">Sensors <ArrowUpRight size={12} /></Link>
        </>
      }
      className="h-full"
    >
      {snapshot.loading ? (
        <div className="grid grid-cols-3 gap-2">{Array.from({ length: 9 }).map((_, i) => <Skeleton key={i} className="h-14" />)}</div>
      ) : machines.length === 0 ? (
        <p className="text-caption text-fg-tertiary">{snapshot.error ? errorMessage(snapshot.error) : "No machines are reporting yet."}</p>
      ) : (
        <div data-testid="machine-health">
          <div className="mb-3 flex flex-wrap gap-x-4 gap-y-1 text-caption text-fg-secondary">
            {(["down", "at_risk", "healthy"] as const).map((s) => (
              <span key={s} className="inline-flex items-center gap-1.5">
                <span className={cn("size-2 rounded-full", STATUS[s].dot)} aria-hidden />
                <span className="font-semibold tabular-nums text-fg-primary">{count(s)}</span> {STATUS[s].label.toLowerCase()}
              </span>
            ))}
          </div>
          <ul className="grid grid-cols-2 sm:grid-cols-3 gap-2" aria-label="Machines">
            {machines.map((m) => {
              const st = statusOf(m.status);
              return (
                <li
                  key={m.id}
                  className={cn("rounded-md border px-2.5 py-2", st.cell)}
                  title={`${m.id}: ${st.label} · vibration ${m.vibration.toFixed(2)} mm/s · ${m.temperature.toFixed(1)} °C · duty ${Math.round(m.dutyCycle * 100)}%`}
                >
                  <div className="flex items-center justify-between gap-1">
                    <span className="font-mono text-[12px] font-semibold text-fg-primary">{m.id}</span>
                    <span className="inline-flex items-center gap-1 text-[11px] font-medium text-fg-secondary">
                      <span className={cn("size-1.5 rounded-full", st.dot)} aria-hidden />
                      {st.label}
                    </span>
                  </div>
                  <p className="mt-1 text-[11px] leading-4 text-fg-tertiary tabular-nums">
                    {m.vibration.toFixed(1)} mm/s · {m.temperature.toFixed(0)} °C · {Math.round(m.dutyCycle * 100)}%
                  </p>
                </li>
              );
            })}
          </ul>
          <p className="mt-3 text-caption text-fg-tertiary">Vibration RMS · temperature · duty cycle, as reported in this tick.</p>
        </div>
      )}
    </Panel>
  );
}
