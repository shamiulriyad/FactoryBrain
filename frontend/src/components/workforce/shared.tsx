"use client";

import Link from "next/link";
import { AlertTriangle, Clock, Sparkles, UserCheck } from "lucide-react";
import { Badge, type BadgeTone } from "@/components/ui/Badge";
import { getWorkforceLine, type WorkforceLineId } from "@/data/workforce/lines";
import type {
  AttendanceStatus,
  GrievanceCategory,
  GrievanceStatus,
  LeaveRequestStatus,
  LeaveType,
  MachineSkill,
  ReferralStatus,
  SkillProficiency,
  WorkerRole,
  WorkerStatus
} from "@/data/workforce/types";
import type { Locale } from "@/lib/format";

export type WorkforceTab =
  | "overview"
  | "workers"
  | "attendance"
  | "leave"
  | "performance"
  | "skills"
  | "grievances"
  | "wellbeing";

export const WORKFORCE_TABS: readonly WorkforceTab[] = [
  "overview",
  "workers",
  "attendance",
  "leave",
  "performance",
  "skills",
  "grievances",
  "wellbeing"
] as const;

export function lineLabel(lineId: WorkforceLineId | string, locale: Locale): string {
  const line = getWorkforceLine(lineId);
  if (!line) return lineId;
  return locale === "bn" ? line.nameBn : line.nameEn;
}

export function lineShortCode(lineId: WorkforceLineId | string): string {
  return getWorkforceLine(lineId)?.code ?? lineId.toUpperCase();
}

export function roleLabel(t: (k: string) => string, role: WorkerRole): string {
  return t(`workforce.roles.${role}`);
}

export function skillLabel(t: (k: string) => string, skill: MachineSkill): string {
  return t(`workforce.skills.${skill}`);
}

export function proficiencyLabel(t: (k: string) => string, p: SkillProficiency | number): string {
  const clamped = Math.max(0, Math.min(4, Math.round(p)));
  return t(`workforce.proficiency.p${clamped}`);
}

export function workerStatusLabel(t: (k: string) => string, status: WorkerStatus): string {
  return t(`workforce.statuses.${status}`);
}

export function workerStatusTone(status: WorkerStatus): BadgeTone {
  if (status === "active") return "success";
  if (status === "on_leave") return "warning";
  return "neutral";
}

export function attendanceStatusLabel(t: (k: string) => string, status: AttendanceStatus): string {
  return t(`workforce.attendanceStatuses.${status}`);
}

export function attendanceStatusTone(status: AttendanceStatus): BadgeTone {
  switch (status) {
    case "present":
      return "success";
    case "late":
      return "warning";
    case "absent":
      return "danger";
    case "leave":
      return "info";
    case "half_day":
      return "accent";
    case "holiday":
    default:
      return "neutral";
  }
}

export function leaveTypeLabel(t: (k: string) => string, type: LeaveType): string {
  return t(`workforce.leaveTypes.${type}`);
}

export function leaveStatusLabel(t: (k: string) => string, status: LeaveRequestStatus): string {
  return t(`workforce.leaveStatuses.${status}`);
}

export function leaveStatusTone(status: LeaveRequestStatus): BadgeTone {
  if (status === "approved") return "success";
  if (status === "rejected") return "danger";
  return "warning";
}

export function grievanceCategoryLabel(t: (k: string) => string, cat: GrievanceCategory): string {
  return t(`workforce.grievanceCategories.${cat}`);
}

export function grievanceStatusLabel(t: (k: string) => string, status: GrievanceStatus): string {
  return t(`workforce.grievanceStatuses.${status}`);
}

export function grievanceStatusTone(status: GrievanceStatus): BadgeTone {
  switch (status) {
    case "resolved":
      return "success";
    case "investigating":
      return "info";
    case "acknowledged":
      return "warning";
    case "new":
    default:
      return "danger";
  }
}

export function referralStatusLabel(t: (k: string) => string, status: ReferralStatus): string {
  return t(`workforce.referralStatuses.${status}`);
}

export function referralStatusTone(status: ReferralStatus): BadgeTone {
  if (status === "closed") return "success";
  if (status === "scheduled") return "info";
  return "warning";
}

/** Shown wherever a local edit overrides the initial seed. */
export function LocalEditChip({ label, testId }: { label: string; testId?: string }) {
  return (
    <Badge tone="accent" testId={testId ?? "local-edit-badge"}>
      <UserCheck size={11} className="mr-1 inline" aria-hidden />
      {label}
    </Badge>
  );
}

/** Standard label for deterministic rule-based recommendations (no LLM). */
export function RuleBasedChip({ label, testId }: { label: string; testId?: string }) {
  return (
    <Badge tone="info" testId={testId ?? "rule-based-badge"}>
      <Sparkles size={11} className="mr-1 inline" aria-hidden />
      {label}
    </Badge>
  );
}

/**
 * Hours-only workload signal (>60 h/week for 3 consecutive weeks).
 * Clickable chip that opens the worker's profile.
 */
export function WorkloadAlertChip({
  workerId,
  label,
  testId
}: {
  workerId: string;
  label: string;
  testId?: string;
}) {
  return (
    <Link
      href={`/app/workforce/workers/${encodeURIComponent(workerId)}?sub=performance`}
      data-testid={testId ?? `workload-alert-chip-${workerId}`}
      className="inline-flex items-center gap-1 rounded-sm border border-[var(--risk-medium-border)] bg-[var(--risk-medium-soft)] px-2 py-0.5 text-[11px] font-medium text-[var(--risk-medium)] hover:underline"
      title="Derived from weekly hours (>60 h/week for 3 weeks) — click to open worker profile"
    >
      <AlertTriangle size={11} aria-hidden />
      {label}
    </Link>
  );
}

/** Small inline note reminding the user that statutory numbers are illustrative demo values. */
export function IllustrativePolicyNote({ text, className }: { text: string; className?: string }) {
  return (
    <p className={className ?? "text-caption text-fg-tertiary flex items-center gap-1.5"} data-testid="illustrative-policy-note">
      <Clock size={12} className="shrink-0 text-fg-tertiary" aria-hidden />
      <span>{text}</span>
    </p>
  );
}
