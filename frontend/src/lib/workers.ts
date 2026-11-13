// Worker management: types mirroring the API (BunonBrain.Application/Dtos/Workers)
// and the calls that aren't plain JSON (photo, CSV import / export).
//
// A worker record is separate from a login account. How much of a worker the
// API returns depends on the caller (`detail`): "basic" (directory fields),
// "employment" (plus employee number, joining date, grade, history) or "full"
// (plus personal contact details). Fields outside the caller's tier are
// simply absent; the UI never assumes they exist.

import { apiFetch } from "@/lib/auth";
import { ApiError, errorFromResponse } from "@/lib/api";
import type { FactoryRef } from "@/lib/access";

export const WORKER_STATUSES = ["Active", "Inactive", "Suspended", "Resigned", "Terminated"] as const;
export type WorkerStatus = (typeof WORKER_STATUSES)[number];

export const EMPLOYMENT_TYPES = ["Permanent", "Probation", "Contract", "Temporary", "Apprentice"] as const;
export const GENDERS = ["Female", "Male", "Other"] as const;
export const CARD_TYPES = ["RFID", "NFC"] as const;
export const CARD_DEACTIVATION_REASONS = ["Returned", "Lost", "Damaged", "Replaced"] as const;

export type OrgRef = { id: string; code: string; name: string };
export type OrgUnit = OrgRef & { factoryId: string; isActive: boolean; workerCount: number; createdAt: string; updatedAt: string };

export type WorkerDetailTier = "basic" | "employment" | "full";

export type Worker = {
  id: string;
  workerCode: string;
  factoryId: string;
  fullName: string;
  hasPhoto: boolean;
  status: WorkerStatus;
  isActive: boolean;
  designation?: string | null;
  department?: OrgRef | null;
  floor?: OrgRef | null;
  line?: { id: string; name: string } | null;
  lineId?: string | null;
  workstation?: string | null;
  // employment tier
  employmentType?: string | null;
  employeeCode?: string | null;
  joinedOn?: string | null;
  grade?: string | null;
  statusReason?: string | null;
  statusChangedAt?: string | null;
  hasAccount?: boolean | null;
  createdAt?: string | null;
  updatedAt?: string | null;
  // full tier
  phone?: string | null;
  email?: string | null;
  gender?: string | null;
  dateOfBirth?: string | null;
  presentAddress?: string | null;
  emergencyContactName?: string | null;
  emergencyContactPhone?: string | null;
  emergencyContactRelation?: string | null;
  userId?: string | null;
  detail: WorkerDetailTier;
};

export type WorkerStatusChange = {
  id: number;
  fromStatus?: string | null;
  toStatus: string;
  reason?: string | null;
  effectiveOn?: string | null;
  changedAt: string;
  changedByEmail?: string | null;
};

export type WorkerCard = {
  id: string;
  workerId: string;
  cardUid: string;
  cardType: string;
  label?: string | null;
  isActive: boolean;
  issuedAt: string;
  deactivatedAt?: string | null;
  deactivationReason?: string | null;
};

export type WorkerActivitySection = { key: string; title: string; available: boolean; note: string; items: unknown[] };

export type WorkerCapabilities = {
  edit: boolean;
  changeStatus: boolean;
  assignWorkstation: boolean;
  manageAccount: boolean;
  manageCards: boolean;
  allowedStatuses: WorkerStatus[];
};

export type WorkerProfile = {
  worker: Worker;
  factory: FactoryRef;
  machine?: string | null;
  account?: { userId: string; email?: string | null; isActive?: boolean | null; roles?: string[] | null } | null;
  cards?: WorkerCard[] | null;
  statusHistory?: WorkerStatusChange[] | null;
  activity: WorkerActivitySection[];
  can: WorkerCapabilities;
};

export type WorkerCount = { key: string; label: string; total: number; active: number };

export type WorkerStatistics = {
  total: number;
  active: number;
  inactive: number;
  suspended: number;
  resigned: number;
  terminated: number;
  withoutDepartment: number;
  withoutLine: number;
  byStatus: WorkerCount[];
  byFactory: WorkerCount[];
  byDepartment: WorkerCount[];
  byLine: WorkerCount[];
  byEmploymentType: WorkerCount[];
  byDesignation: WorkerCount[];
};

export type ImportIssue = { field: string; message: string };
export type ImportRow = {
  rowNumber: number;
  valid: boolean;
  fullName?: string | null;
  employeeCode?: string | null;
  phone?: string | null;
  joinedOn?: string | null;
  department?: string | null;
  designation?: string | null;
  employmentType?: string | null;
  floor?: string | null;
  line?: string | null;
  errors: ImportIssue[];
  warnings: ImportIssue[];
  workerCode?: string | null;
};
export type ImportResult = {
  dryRun: boolean;
  factoryId: string;
  totalRows: number;
  validRows: number;
  invalidRows: number;
  imported: number;
  columns: string[];
  ignoredColumns: string[];
  rows: ImportRow[];
};

/** The editable fields of a worker (POST / PUT body). */
export type WorkerInput = {
  fullName: string;
  phone: string;
  joinedOn: string;
  departmentId: string;
  designation: string;
  employmentType: string;
  employeeCode: string;
  gender: string;
  dateOfBirth: string;
  email: string;
  presentAddress: string;
  emergencyContactName: string;
  emergencyContactPhone: string;
  emergencyContactRelation: string;
  floorId: string;
  lineId: string;
  workstation: string;
  grade: string;
};

export const EMPTY_WORKER_INPUT: WorkerInput = {
  fullName: "", phone: "", joinedOn: "", departmentId: "", designation: "", employmentType: "Permanent", employeeCode: "",
  gender: "", dateOfBirth: "", email: "", presentAddress: "", emergencyContactName: "", emergencyContactPhone: "",
  emergencyContactRelation: "", floorId: "", lineId: "", workstation: "", grade: ""
};

export function inputFromWorker(w: Worker): WorkerInput {
  return {
    fullName: w.fullName,
    phone: w.phone ?? "",
    joinedOn: w.joinedOn ?? "",
    departmentId: w.department?.id ?? "",
    designation: w.designation ?? "",
    employmentType: w.employmentType ?? "Permanent",
    employeeCode: w.employeeCode ?? "",
    gender: w.gender ?? "",
    dateOfBirth: w.dateOfBirth ?? "",
    email: w.email ?? "",
    presentAddress: w.presentAddress ?? "",
    emergencyContactName: w.emergencyContactName ?? "",
    emergencyContactPhone: w.emergencyContactPhone ?? "",
    emergencyContactRelation: w.emergencyContactRelation ?? "",
    floorId: w.floor?.id ?? "",
    lineId: w.lineId ?? "",
    workstation: w.workstation ?? "",
    grade: w.grade ?? ""
  };
}

/** JSON body for POST /api/workers or PUT /api/workers/{id}: blanks become null. */
export function workerBody(input: WorkerInput, factoryId?: string): Record<string, unknown> {
  const v = (s: string) => (s.trim() === "" ? null : s.trim());
  const body: Record<string, unknown> = {
    fullName: input.fullName.trim(),
    phone: v(input.phone),
    joinedOn: v(input.joinedOn),
    departmentId: v(input.departmentId),
    designation: v(input.designation),
    employmentType: v(input.employmentType),
    employeeCode: v(input.employeeCode),
    gender: v(input.gender),
    dateOfBirth: v(input.dateOfBirth),
    email: v(input.email),
    presentAddress: v(input.presentAddress),
    emergencyContactName: v(input.emergencyContactName),
    emergencyContactPhone: v(input.emergencyContactPhone),
    emergencyContactRelation: v(input.emergencyContactRelation),
    floorId: v(input.floorId),
    lineId: v(input.lineId),
    grade: v(input.grade)
  };
  if (factoryId) {
    body.factoryId = factoryId;
    body.workstation = v(input.workstation);
  }
  return body;
}

/** Same checks the API makes, so most mistakes are caught before the request. Field → message. */
export function validateWorkerInput(input: WorkerInput, today = new Date()): Record<string, string> {
  const errors: Record<string, string> = {};
  const phoneOk = (p: string) => /^[0-9+()\- ]+$/.test(p) && (p.match(/[0-9]/g)?.length ?? 0) >= 6 && p.length <= 32;
  if (!input.fullName.trim()) errors.fullName = "Full name is required.";
  else if (input.fullName.trim().length > 120) errors.fullName = "At most 120 characters.";
  if (!input.phone.trim()) errors.phone = "Phone number is required.";
  else if (!phoneOk(input.phone.trim())) errors.phone = "Digits, spaces, +, - and parentheses (at least 6 digits).";
  if (!input.joinedOn) errors.joinedOn = "Joining date is required.";
  else {
    const joined = new Date(input.joinedOn + "T00:00:00");
    const limit = new Date(today);
    limit.setDate(limit.getDate() + 90);
    if (Number.isNaN(joined.getTime()) || joined.getFullYear() < 1950) errors.joinedOn = "Enter a real date.";
    else if (joined > limit) errors.joinedOn = "Can't be more than 90 days in the future.";
  }
  if (!input.departmentId) errors.departmentId = "Choose a department.";
  if (!input.designation.trim()) errors.designation = "Designation is required.";
  if (input.employeeCode.trim() && !/^[A-Za-z0-9][A-Za-z0-9\-_/]{0,31}$/.test(input.employeeCode.trim()))
    errors.employeeCode = "Letters, digits, '-', '_' and '/', up to 32 characters.";
  if (input.email.trim() && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(input.email.trim())) errors.email = "Not a valid email address.";
  if (input.emergencyContactPhone.trim() && !phoneOk(input.emergencyContactPhone.trim()))
    errors.emergencyContactPhone = "Digits, spaces, +, - and parentheses (at least 6 digits).";
  if (input.dateOfBirth) {
    const born = new Date(input.dateOfBirth + "T00:00:00");
    const ref = input.joinedOn ? new Date(input.joinedOn + "T00:00:00") : today;
    const minAge = new Date(born);
    minAge.setFullYear(minAge.getFullYear() + 14);
    if (Number.isNaN(born.getTime()) || born > today || born.getFullYear() < 1900) errors.dateOfBirth = "Enter a real past date.";
    else if (minAge > (ref < today ? ref : today)) errors.dateOfBirth = "A worker must be at least 14 years old on the joining date.";
  }
  if (input.presentAddress.length > 500) errors.presentAddress = "At most 500 characters.";
  return errors;
}

/** Status changes that need a reason (mirrors WorkerStatusTransitions on the server). */
export function statusReasonRequired(from: string, to: string): boolean {
  return to === "Suspended" || to === "Resigned" || to === "Terminated" || from === "Resigned" || from === "Terminated";
}

export const STATUS_TONE: Record<string, "success" | "neutral" | "warning" | "danger"> = {
  Active: "success",
  Inactive: "neutral",
  Suspended: "warning",
  Resigned: "neutral",
  Terminated: "danger"
};

/** "2024-03-01" → "1 Mar 2024", without a time-zone shift (these are calendar dates). */
export function formatDay(day: string | null | undefined, locale: "en" | "bn" = "en"): string {
  if (!day) return "—";
  const [y, m, d] = day.split("-").map(Number);
  if (!y || !m || !d) return day;
  return new Intl.DateTimeFormat(locale === "bn" ? "bn-BD" : "en-GB", { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" })
    .format(new Date(Date.UTC(y, m - 1, d)));
}

const UPLOAD_TIMEOUT_MS = 120_000;

async function send(url: string, init: RequestInit): Promise<Response> {
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), UPLOAD_TIMEOUT_MS);
  try {
    const res = await apiFetch(url, { cache: "no-store", ...init, signal: ctrl.signal });
    if (!res.ok) throw await errorFromResponse(res);
    return res;
  } catch (e) {
    if (e instanceof ApiError) throw e;
    if ((e as Error).name === "AbortError")
      throw new ApiError({ status: 0, kind: "timeout", message: "The request took too long. Check the connection and try again." });
    throw new ApiError({ status: 0, kind: "network", message: "Could not reach the server. Check your connection and try again." });
  } finally {
    clearTimeout(timer);
  }
}

/** PUT /api/workers/{id}/photo. */
export async function uploadWorkerPhoto(workerId: string, file: File): Promise<Worker> {
  const form = new FormData();
  form.append("photo", file, file.name);
  const res = await send(`/api/workers/${encodeURIComponent(workerId)}/photo`, { method: "PUT", body: form });
  return (await res.json()) as Worker;
}

/** The worker's photo as an object URL (revoke it when done). */
export async function fetchWorkerPhoto(workerId: string, signal?: AbortSignal): Promise<string> {
  const res = await apiFetch(`/api/workers/${encodeURIComponent(workerId)}/photo`, { cache: "no-store", signal });
  if (!res.ok) throw await errorFromResponse(res);
  return URL.createObjectURL(await res.blob());
}

/** POST /api/workers/import. `dryRun` validates only (the preview); false registers the valid rows. */
export async function importWorkers(file: File, factoryId: string, dryRun: boolean): Promise<ImportResult> {
  const form = new FormData();
  form.append("file", file, file.name);
  form.append("factoryId", factoryId);
  form.append("dryRun", dryRun ? "true" : "false");
  const res = await send("/api/workers/import", { method: "POST", body: form });
  return (await res.json()) as ImportResult;
}

/** Download a CSV from the API (export or template) and hand it to the browser as a file. */
export async function downloadCsv(url: string, fallbackName: string): Promise<number | null> {
  const res = await send(url, { method: "GET" });
  const blob = await res.blob();
  const disposition = res.headers.get("Content-Disposition") ?? "";
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)?.[1] ?? fallbackName;
  const href = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = href;
  a.download = decodeURIComponent(name);
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(href), 1000);
  const rows = Number(res.headers.get("X-Exported-Rows"));
  return Number.isFinite(rows) && res.headers.has("X-Exported-Rows") ? rows : null;
}
