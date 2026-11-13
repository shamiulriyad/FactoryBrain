// Workstation assignments: types mirroring the API
// (BunonBrain.Application/Dtos/Assignments) and small helpers.
//
// An assignment records where a worker is scheduled to work, from when to
// when. It is not proof that the worker was present or that a machine ran;
// the UI says so wherever occupancy is shown.

import type { OrgRef } from "@/lib/workers";

export const OCCUPANCY = ["Available", "Occupied", "CapacityReached", "Inactive"] as const;
export type Occupancy = (typeof OCCUPANCY)[number];

export const OCCUPANCY_LABEL: Record<Occupancy, string> = {
  Available: "Available",
  Occupied: "Occupied",
  CapacityReached: "Capacity reached",
  Inactive: "Inactive"
};

export const OCCUPANCY_TONE: Record<Occupancy, "success" | "info" | "warning" | "neutral"> = {
  Available: "success",
  Occupied: "info",
  CapacityReached: "warning",
  Inactive: "neutral"
};

export const ASSIGNMENT_STATUSES = ["Active", "Ended", "Transferred"] as const;
export type AssignmentStatus = (typeof ASSIGNMENT_STATUSES)[number];

export type MachineRef = { id: string; code: string; machineType?: string | null; isActive: boolean };
export type LineRef = { id: string; name: string };

export type AssignedWorker = {
  assignmentId: string;
  workerId: string;
  workerCode: string;
  fullName: string;
  startAt: string;
  machine?: MachineRef | null;
};

export type Workstation = {
  id: string;
  code: string;
  name?: string | null;
  factoryId: string;
  factoryCode: string;
  department: OrgRef;
  floor?: OrgRef | null;
  line: LineRef;
  capacity: number;
  position: number;
  isActive: boolean;
  assignedCount: number;
  occupancy: Occupancy;
  machines: MachineRef[];
  /** Absent when the caller may not see workers (e.g. IT / device administrators). */
  workers?: AssignedWorker[] | null;
  createdAt: string;
  updatedAt: string;
};

export type Machine = {
  id: string;
  factoryId: string;
  code: string;
  machineType?: string | null;
  brand?: string | null;
  serialNumber?: string | null;
  isActive: boolean;
  workstationId?: string | null;
  workstationCode?: string | null;
  createdAt: string;
  updatedAt: string;
};

export type Assignment = {
  id: string;
  workerId: string;
  workerCode: string;
  workerName: string;
  factoryId: string;
  department: OrgRef;
  floor?: OrgRef | null;
  line: LineRef;
  workstation: { id: string; code: string; name?: string | null };
  machine?: MachineRef | null;
  isPrimary: boolean;
  startAt: string;
  endAt?: string | null;
  status: AssignmentStatus;
  reason?: string | null;
  notes?: string | null;
  endReason?: string | null;
  previousAssignmentId?: string | null;
  nextAssignmentId?: string | null;
  assignedByEmail: string;
  endedByEmail?: string | null;
  createdAt: string;
};

export type LineOccupancy = {
  lineId: string;
  lineName: string;
  workstations: number;
  occupied: number;
  available: number;
  full: number;
  inactive: number;
  capacity: number;
  assigned: number;
};

export type AssignmentStatistics = {
  totalWorkstations: number;
  activeWorkstations: number;
  occupiedWorkstations: number;
  availableWorkstations: number;
  fullWorkstations: number;
  inactiveWorkstations: number;
  totalCapacity: number;
  activeAssignments: number;
  activeWorkers: number;
  unassignedActiveWorkers: number;
  byLine: LineOccupancy[];
  recentTransfers: Assignment[];
};

export type TransferResult = { closed: Assignment; opened: Assignment };

export const STATUS_TONE: Record<string, "success" | "info" | "neutral"> = {
  Active: "success",
  Transferred: "info",
  Ended: "neutral"
};

/** A `datetime-local` value (browser time) → ISO with offset, or null for "now". */
export function localToIso(value: string): string | null {
  if (!value) return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

/** Now, as a `datetime-local` value (for the input's max). */
export function nowLocal(): string {
  const d = new Date();
  d.setSeconds(0, 0);
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}

/** "3 h 20 min", "2 d 4 h" — how long an assignment lasted (or has lasted). */
export function duration(startIso: string, endIso?: string | null): string {
  const ms = (endIso ? new Date(endIso).getTime() : Date.now()) - new Date(startIso).getTime();
  if (!Number.isFinite(ms) || ms < 0) return "—";
  const min = Math.floor(ms / 60000);
  if (min < 60) return `${min} min`;
  const h = Math.floor(min / 60);
  if (h < 48) return `${h} h ${min % 60} min`;
  return `${Math.floor(h / 24)} d ${h % 24} h`;
}

/** The note shown wherever assignments are displayed. */
export const NOT_PRESENCE_NOTE =
  "Assignments show where workers are scheduled to work, as recorded by supervisors. They are not proof that a worker is present or that a machine is running.";
