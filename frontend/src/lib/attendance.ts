// Shifts, attendance, breaks and overtime: types mirroring the API
// (BunonBrain.Application/Dtos/Attendance) and small helpers.
//
// The API stores every time in UTC and works out work dates, lateness and
// overtime in the factory's time zone (Asia/Dhaka unless the factory's
// attendance policy says otherwise). Times shown here are the browser's
// local time; shift times ("08:00") are factory-local.

export const BREAK_TYPES = ["Lunch", "Tea", "Prayer", "Personal", "Other"] as const;
export type BreakType = (typeof BREAK_TYPES)[number];

export const DAY_NAMES = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"] as const;

export const SESSION_STATUSES = ["Open", "Completed", "MissingCheckOut", "MissingCheckIn"] as const;
export const SESSION_STATUS_LABEL: Record<string, string> = {
  Open: "Checked in",
  Completed: "Completed",
  MissingCheckOut: "Missing check-out",
  MissingCheckIn: "Missing check-in"
};
export const SESSION_STATUS_TONE: Record<string, "success" | "info" | "warning" | "danger" | "neutral"> = {
  Open: "info",
  Completed: "success",
  MissingCheckOut: "warning",
  MissingCheckIn: "warning"
};

export const OVERTIME_STATUSES = ["Requested", "PendingApproval", "Approved", "Rejected", "InProgress", "Completed", "Cancelled"] as const;
export const OVERTIME_STATUS_LABEL: Record<string, string> = {
  Requested: "Draft",
  PendingApproval: "Pending approval",
  Approved: "Approved",
  Rejected: "Rejected",
  InProgress: "In progress",
  Completed: "Completed",
  Cancelled: "Cancelled"
};
export const OVERTIME_STATUS_TONE: Record<string, "success" | "info" | "warning" | "danger" | "neutral" | "accent"> = {
  Requested: "neutral",
  PendingApproval: "warning",
  Approved: "accent",
  Rejected: "danger",
  InProgress: "info",
  Completed: "success",
  Cancelled: "neutral"
};

export const EVENT_SOURCES = ["Simulator", "Device", "Manual", "Correction", "System"] as const;
export const EVENT_TYPES = ["CheckIn", "CheckOut", "BreakStart", "BreakEnd"] as const;
export const EVENT_TYPE_LABEL: Record<string, string> = { CheckIn: "Check-in", CheckOut: "Check-out", BreakStart: "Break start", BreakEnd: "Break end" };

export type ShiftBreak = { id?: string | null; breakType: string; name: string; startLocal?: string | null; durationMinutes: number; isPaid: boolean };

export type Shift = {
  id: string;
  factoryId: string;
  code: string;
  name: string;
  startLocal: string;
  endLocal: string;
  isOvernight: boolean;
  scheduledMinutes: number;
  unpaidBreakMinutes: number;
  netScheduledMinutes: number;
  lateGraceMinutes: number;
  earlyLeaveGraceMinutes: number;
  workingDays: string[];
  isActive: boolean;
  description?: string | null;
  productionShiftCode?: string | null;
  breaks: ShiftBreak[];
  currentWorkers: number;
  createdAt: string;
  updatedAt: string;
};

export type ShiftRef = { id: string; code: string; name: string; startLocal: string; endLocal: string; isOvernight: boolean };

export type ShiftAssignment = {
  id: string;
  workerId: string;
  workerCode: string;
  workerName: string;
  factoryId: string;
  shift: ShiftRef;
  effectiveFrom: string;
  effectiveTo?: string | null;
  status: "Active" | "Ended" | "Superseded" | string;
  reason?: string | null;
  endReason?: string | null;
  previousAssignmentId?: string | null;
  assignedByEmail: string;
  endedByEmail?: string | null;
  createdAt: string;
};

export type AssignShiftOutcome = { workerId: string; workerCode: string; outcome: "Assigned" | "Skipped" | string; code?: string | null; message?: string | null; assignmentId?: string | null };
export type AssignShiftResult = { requested: number; assigned: number; skipped: number; results: AssignShiftOutcome[] };

export type SessionFigures = {
  attendanceMinutes: number;
  scheduledMinutes: number;
  scheduledBreakMinutes: number;
  netScheduledMinutes: number;
  breakMinutes: number;
  overBreakMinutes: number;
  netWorkingMinutes: number;
  lateMinutes: number;
  earlyDepartureMinutes: number;
  beyondShiftMinutes: number;
  approvedOvertimeMinutes: number;
};

export type AttendanceSession = {
  id: string;
  workerId: string;
  workerCode: string;
  workerName: string;
  factoryId: string;
  lineId?: string | null;
  workDate: string;
  shift?: ShiftRef | null;
  scheduledStart?: string | null;
  scheduledEnd?: string | null;
  checkInAt?: string | null;
  checkOutAt?: string | null;
  checkInSource?: string | null;
  checkOutSource?: string | null;
  status: string;
  needsReview: boolean;
  reviewReason?: string | null;
  hasSimulatedEvents: boolean;
  isCorrected: boolean;
  isLate: boolean;
  isEarlyDeparture: boolean;
  onBreak: boolean;
  figures: SessionFigures;
  createdAt: string;
};

export type AttendanceEvent = {
  id: number;
  workerId: string;
  workerCode: string;
  workerName: string;
  factoryId: string;
  eventType: string;
  occurredAt: string;
  receivedAt: string;
  source: string;
  isSimulated: boolean;
  readerCode?: string | null;
  cardUid?: string | null;
  outcome: "Accepted" | "Duplicate" | "Rejected" | string;
  outcomeCode?: string | null;
  outcomeMessage?: string | null;
  sessionId?: string | null;
  recordedByEmail: string;
  note?: string | null;
};

export type BreakSession = {
  id: string;
  workerId: string;
  workerCode: string;
  workerName: string;
  attendanceSessionId: string;
  breakType: string;
  startAt: string;
  endAt?: string | null;
  allowedMinutes: number;
  actualMinutes?: number | null;
  status: "Active" | "Completed" | "AutoClosed" | string;
  isOverdue: boolean;
  overMinutes: number;
  isSimulated: boolean;
  startSource: string;
  endSource?: string | null;
};

export type EventResult = { event: AttendanceEvent; session?: AttendanceSession | null; break?: BreakSession | null; replayed: boolean };

export type Correction = {
  id: string;
  workerId: string;
  workerCode: string;
  workerName: string;
  factoryId: string;
  sessionId?: string | null;
  workDate?: string | null;
  proposedCheckIn?: string | null;
  proposedCheckOut?: string | null;
  originalCheckIn?: string | null;
  originalCheckOut?: string | null;
  reason: string;
  status: "Pending" | "Approved" | "Rejected" | string;
  requestedByEmail: string;
  requestedAt: string;
  decidedByEmail?: string | null;
  decidedAt?: string | null;
  decisionNote?: string | null;
};

export type OvertimeHistory = { id: number; action: string; fromStatus?: string | null; toStatus: string; minutes?: number | null; note?: string | null; actorEmail: string; occurredAt: string };

export type Overtime = {
  id: string;
  workerId: string;
  workerCode: string;
  workerName: string;
  factoryId: string;
  lineId?: string | null;
  workDate: string;
  plannedStart: string;
  plannedEnd: string;
  requestedMinutes: number;
  reason: string;
  status: string;
  approvedMinutes?: number | null;
  approvedStart?: string | null;
  approvedEnd?: string | null;
  actualMinutes?: number | null;
  beyondShiftMinutes?: number | null;
  payableSuggestionMinutes?: number | null;
  payableMinutes?: number | null;
  payableNote?: string | null;
  payableConfirmedByEmail?: string | null;
  requestedByEmail: string;
  warnings: string[];
  history: OvertimeHistory[];
  can: { submit: boolean; approve: boolean; reject: boolean; cancel: boolean; confirmPayable: boolean };
  createdAt: string;
};

export type AttendanceStatistics = {
  date: string;
  factoryId?: string | null;
  scheduledWorkers: number;
  present: number;
  absent: number;
  late: number;
  checkedIn: number;
  missingPunches: number;
  onBreak: number;
  overdueBreaks: number;
  pendingOvertimeApprovals: number;
  completedOvertime: number;
  pendingCorrections: number;
  simulatedEvents: number;
  deviceEvents: number;
  manualEvents: number;
  overdueBreakList: BreakSession[];
};

export type ReportRow = {
  workerId: string;
  workerCode: string;
  workerName: string;
  lineId?: string | null;
  sessions: number;
  daysPresent: number;
  lateDays: number;
  lateMinutes: number;
  earlyDepartureDays: number;
  earlyDepartureMinutes: number;
  missingPunches: number;
  attendanceMinutes: number;
  breakMinutes: number;
  netWorkingMinutes: number;
  beyondShiftMinutes: number;
  approvedOvertimeMinutes: number;
  actualOvertimeMinutes: number;
  payableConfirmedMinutes: number;
  simulatedSessions: number;
};

export type AttendanceReport = { from: string; to: string; factoryId?: string | null; rows: ReportRow[]; totals: ReportRow };

export type AttendancePolicy = {
  factoryId: string;
  timeZoneId: string;
  duplicateWindowSeconds: number;
  missingCheckOutAfterMinutes: number;
  maxOpenSessionHours: number;
  maxBackdateDays: number;
  earlyCheckInWindowMinutes: number;
  overtimeRoundingMinutes: number;
  minOvertimeMinutes: number;
  maxDailyOvertimeMinutes: number;
  maxWeeklyOvertimeMinutes: number;
  isDefault: boolean;
  updatedAt?: string | null;
  updatedByEmail?: string | null;
};

export type Reader = { id: string; factoryId: string; code: string; name: string; direction: string; kind: string; isSimulated: boolean; location?: string | null; isActive: boolean };

export type WorkerAttendance = {
  workerId: string;
  workerCode: string;
  workerName: string;
  factoryId: string;
  lineId?: string | null;
  workerStatus: string;
  timeZoneId: string;
  currentShift?: ShiftAssignment | null;
  shiftHistory: ShiftAssignment[];
  openSession?: AttendanceSession | null;
  sessions: AttendanceSession[];
  breaks: BreakSession[];
  overtime: Overtime[];
  corrections: Correction[];
};

/** 0 → "0 min", 75 → "1 h 15 min", 480 → "8 h". */
export function minutes(n: number | null | undefined): string {
  if (n === null || n === undefined) return "—";
  const sign = n < 0 ? "−" : "";
  const v = Math.abs(Math.round(n));
  if (v < 60) return `${sign}${v} min`;
  const h = Math.floor(v / 60);
  const m = v % 60;
  return m ? `${sign}${h} h ${m} min` : `${sign}${h} h`;
}

/** Decimal hours for tables and exports: 75 → "1.25". */
export function hours(n: number | null | undefined): string {
  if (n === null || n === undefined) return "—";
  return (n / 60).toFixed(2);
}

/** "08:00 – 17:00" or "22:00 – 06:00 (+1)". */
export function shiftWindow(s: Pick<ShiftRef, "startLocal" | "endLocal" | "isOvernight">): string {
  return `${s.startLocal} – ${s.endLocal}${s.isOvernight ? " (+1 day)" : ""}`;
}

/** Today as yyyy-mm-dd in the browser's time zone. */
export function todayIso(offsetDays = 0): string {
  const d = new Date();
  d.setDate(d.getDate() + offsetDays);
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
}

/** A `datetime-local` value → ISO with offset, or undefined for "now". */
export function localToIso(value: string): string | undefined {
  if (!value) return undefined;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? undefined : d.toISOString();
}

/** An ISO time → `datetime-local` value (browser time). */
export function isoToLocal(iso: string | null | undefined): string {
  if (!iso) return "";
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return "";
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}

/** "13:05" in the browser's time zone. */
export function clock(iso: string | null | undefined): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "—" : d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });
}

/** A fresh idempotency key for one button press (retries reuse it). */
export function newKey(prefix: string): string {
  const rnd = typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  return `${prefix}-${rnd}`;
}

/** The note shown wherever overtime is displayed. */
export const OVERTIME_NOTE =
  "Time after a shift is not overtime by itself: only approved overtime counts, measured against actual attendance. Payable minutes are confirmed by HR and are never calculated into pay automatically.";

/** The note shown wherever simulated events can appear. */
export const SIMULATED_NOTE =
  "SIMULATED events come from the RFID simulator. They go through the same pipeline as real readers but are always stored and shown as simulated, never as real presence.";
