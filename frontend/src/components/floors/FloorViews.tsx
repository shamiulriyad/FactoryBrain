"use client";

// Shared pieces of the floor map and the floor pages. Everything shown comes
// from GET /api/factories/{id}/floor-status (see lib/useFloorStatus).

import Link from "next/link";
import { AlertTriangle, Cctv, Cog, Users } from "lucide-react";
import { StatusShape } from "@/components/sidebar/StatusMark";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { FilterSelect } from "@/components/ui/Field";
import { useMyFactories } from "@/components/workers/shared";
import { setFloorFactory, type FloorRollup, type FloorStatus } from "@/lib/useFloorStatus";
import { useT } from "@/lib/useT";
import { cn } from "@/lib/cn";

export const STATUS_TONE: Record<FloorStatus, BadgeTone> = { ok: "success", watch: "warning", act: "danger" };
export const SEVERITY_TONE: Record<string, BadgeTone> = { CRITICAL: "danger", HIGH: "danger", MEDIUM: "warning", LOW: "neutral" };

export function StatusBadge({ status }: { status: FloorStatus }) {
  const { t } = useT();
  return (
    <span className="inline-flex items-center gap-2 rounded-md border border-border-subtle px-2.5 py-1 text-caption text-fg-primary">
      <StatusShape status={status} />
      {t(`sidebar.status.${status}`)}
    </span>
  );
}

/** Factory picker for users who reach more than one factory. */
export function FloorFactoryPicker({ value }: { value: string }) {
  const { factories } = useMyFactories();
  if ((factories?.length ?? 0) < 2) return null;
  return (
    <FilterSelect label="Factory" value={value} onChange={(v) => setFloorFactory(v || null)}>
      {factories!.map((f) => <option key={f.id} value={f.id}>{f.code} · {f.name}</option>)}
    </FilterSelect>
  );
}

const MACHINE_ORDER = ["RUNNING", "IDLE", "STOPPED", "ERROR", "MAINTENANCE", "OFFLINE", "UNKNOWN"];
const MACHINE_TONE: Record<string, BadgeTone> = { RUNNING: "success", IDLE: "info", STOPPED: "warning", ERROR: "danger", MAINTENANCE: "accent", OFFLINE: "danger", UNKNOWN: "neutral" };

export function MachineStates({ states }: { states: Record<string, number> }) {
  const entries = MACHINE_ORDER.filter((s) => (states[s] ?? 0) > 0);
  if (entries.length === 0) return <span className="text-caption text-fg-tertiary">No machines</span>;
  return (
    <span className="flex flex-wrap gap-1">
      {entries.map((s) => <Badge key={s} tone={MACHINE_TONE[s]}>{states[s]} {s.toLowerCase()}</Badge>)}
    </span>
  );
}

/** One floor as a slab of the building on the floor map. */
export function FloorSlab({ floor }: { floor: FloorRollup }) {
  return (
    <Link
      href={`/app/floors/${floor.id}`}
      className={cn(
        "block rounded-lg border bg-[var(--bg-surface)] p-4 card-link",
        floor.status === "act" ? "border-[var(--risk-high)]" : floor.status === "watch" ? "border-[var(--risk-medium,#d69e2e)]" : "border-border-subtle",
        !floor.isActive && "opacity-60",
      )}
      data-testid={`floor-slab-${floor.code}`}
    >
      <div className="flex flex-wrap items-center gap-3">
        <span className="inline-flex size-9 items-center justify-center rounded-full border border-border-subtle text-body font-semibold tabular-nums">{floor.code}</span>
        <div className="min-w-0 flex-1">
          <p className="text-body font-medium text-fg-primary">{floor.name}{!floor.isActive && <span className="ml-2 text-caption text-fg-tertiary">inactive</span>}</p>
          <p className="text-caption text-fg-tertiary">{floor.reasons[0] ?? "Nothing needs attention"}</p>
        </div>
        <StatusBadge status={floor.status} />
      </div>
      <div className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-4 text-caption">
        <Fact icon={<Users size={13} />} label="Checked in" value={`${floor.checkedIn} / ${floor.workers}`} />
        <Fact icon={<Cog size={13} />} label="Machines" value={floor.machines} />
        <Fact icon={<AlertTriangle size={13} />} label="Open alerts" value={floor.activeAlerts} warn={floor.activeAlerts > 0} />
        <Fact icon={<Cctv size={13} />} label="Cameras" value={floor.cameras} />
      </div>
      {floor.lines.length > 0 && (
        <div className="mt-3 flex flex-wrap gap-1.5">
          {floor.lines.map((l) => (
            <span key={l.id} className={cn("rounded border px-2 py-0.5 text-caption",
              l.activeAlerts > 0 || l.machinesDown > 0 ? "border-[var(--risk-high)] text-fg-primary" : "border-border-subtle text-fg-secondary")}>
              {l.name}{l.machinesDown > 0 ? ` · ${l.machinesDown} down` : ""}
            </span>
          ))}
        </div>
      )}
    </Link>
  );
}

export function Fact({ icon, label, value, warn }: { icon: React.ReactNode; label: string; value: React.ReactNode; warn?: boolean }) {
  return (
    <div className="rounded border border-border-subtle px-2.5 py-1.5">
      <p className="flex items-center gap-1.5 text-fg-tertiary">{icon}{label}</p>
      <p className={cn("text-body font-semibold tabular-nums", warn && "text-[var(--risk-high)]")}>{value}</p>
    </div>
  );
}
