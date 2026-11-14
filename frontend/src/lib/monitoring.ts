// Central monitoring: types mirroring the API's DTOs, labels and the
// Asia/Dhaka formatting every monitoring page uses. Timestamps from the API
// are UTC; people in the factory read them in Dhaka time.

import type { BadgeTone } from "@/components/ui/Badge";

export type MonRef = { id: string; code: string; name?: string | null };
export type MonLine = { id: string; name: string };
export type MonZone = { id: string; name: string; zoneType: string };
export type SourceType = "SIMULATED" | "REAL";

export type MonitoringEvent = {
  id: string;
  eventType: string;
  eventLabel: string;
  category: "VISION" | "SYSTEM";
  sourceType: SourceType;
  isSimulated: boolean;
  producer: string;
  severity: "INFO" | "WARNING" | "CRITICAL";
  summary: string;
  factoryId: string;
  factoryCode: string;
  departmentName?: string | null;
  floorName?: string | null;
  line?: MonLine | null;
  workstation?: MonRef | null;
  camera?: MonRef | null;
  device?: MonRef | null;
  zone?: MonZone | null;
  trackId?: string | null;
  verifiedWorker?: MonRef | null;
  identityMethod?: string | null;
  confidence?: number | null;
  payload: Record<string, unknown>;
  modelName?: string | null;
  modelVersion?: string | null;
  simulationRunId?: string | null;
  occurredAt: string;
  receivedAt: string;
  recordedByEmail: string;
  reviewCount: number;
  latestReview?: string | null;
};

export type EventReview = { id: number; decision: string; note?: string | null; reviewedByEmail: string; reviewedByRole?: string | null; reviewedAt: string };
export type AlertLink = { id: string; code: string; title: string; status: string; severity: string };
export type MonitoringEventDetail = { event: MonitoringEvent; reviews: EventReview[]; alerts: AlertLink[]; canReview: boolean };

export type AlertCan = { acknowledge: boolean; escalate: boolean; resolve: boolean; dismiss: boolean };

export type MonitoringAlert = {
  id: string;
  code: string;
  alertType: string;
  category: "VISION" | "SYSTEM";
  severity: "LOW" | "MEDIUM" | "HIGH" | "CRITICAL";
  title: string;
  description: string;
  status: "OPEN" | "ACKNOWLEDGED" | "ESCALATED" | "RESOLVED" | "DISMISSED";
  sourceType: SourceType;
  isSimulated: boolean;
  factoryId: string;
  factoryCode: string;
  departmentName?: string | null;
  floorName?: string | null;
  line?: MonLine | null;
  workstation?: MonRef | null;
  camera?: MonRef | null;
  device?: MonRef | null;
  zone?: MonZone | null;
  eventId: string;
  lastEventId?: string | null;
  occurrenceCount: number;
  firstOccurredAt: string;
  lastOccurredAt: string;
  escalationLevel: number;
  escalatedToRole?: string | null;
  acknowledgedAt?: string | null;
  acknowledgedByEmail?: string | null;
  escalatedAt?: string | null;
  escalatedByEmail?: string | null;
  resolvedAt?: string | null;
  resolvedByEmail?: string | null;
  resolutionCode?: string | null;
  resolutionNote?: string | null;
  dismissedAt?: string | null;
  dismissedByEmail?: string | null;
  dismissReason?: string | null;
  evidence: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  version: number;
  can: AlertCan;
};

export type AlertAction = {
  id: number;
  action: string;
  fromStatus?: string | null;
  toStatus: string;
  note?: string | null;
  escalatedToRole?: string | null;
  resolutionCode?: string | null;
  eventId?: string | null;
  actorEmail: string;
  actorRole?: string | null;
  at: string;
};

export type MonitoringAlertDetail = { alert: MonitoringAlert; history: AlertAction[]; event?: MonitoringEvent | null; relatedEvents: MonitoringEvent[] };

export type LineStatus = {
  lineId: string;
  lineName: string;
  factoryId?: string | null;
  factoryCode?: string | null;
  cameras: number;
  camerasOnline: number;
  camerasOffline: number;
  activeAlerts: number;
  highestSeverity?: string | null;
  lastEventAt?: string | null;
  eventsLastHour: number;
  status: "NORMAL" | "ATTENTION" | "ALERT" | "NO_COVERAGE";
};

export type FactoryStatus = {
  id: string;
  code: string;
  name: string;
  cameras: number;
  camerasOnline: number;
  camerasOffline: number;
  devices: number;
  devicesOffline: number;
  activeAlerts: number;
  highestSeverity?: string | null;
  status: "NORMAL" | "ATTENTION" | "ALERT";
};

export type MonitoringOverview = {
  asOf: string;
  canSeeVision: boolean;
  cameras: { total: number; online: number; offline: number; unknown: number; notActive: number; simulatedHealth: number };
  devices: { total: number; online: number; offline: number; unknown: number; maintenance: number; disabled: number; simulated: number; real: number };
  alerts: { active: number; unacknowledged: number; escalated: number; critical: number; high: number; simulated: number; real: number; inFilter: number };
  events: { lastHour: number; last24Hours: number; inRange: number; simulatedInRange: number; realInRange: number };
  recentEvents: MonitoringEvent[];
  activeAlerts: MonitoringAlert[];
  lines: LineStatus[];
  factories: FactoryStatus[];
  aiStatus: string;
};

export type CameraFeed = {
  sourceType: string;
  kind: "RECORDED_VIDEO" | "LOCAL_WEBCAM" | "LIVE_STREAM" | "NONE";
  playable: boolean;
  liveStreamAvailable: boolean;
  webcamLabel?: string | null;
  videoFileName?: string | null;
  connectionCheck: string;
  note: string;
};

export type CameraConnection = {
  status: "ONLINE" | "OFFLINE" | "UNKNOWN" | "INACTIVE" | "MAINTENANCE";
  label: string;
  isSimulated: boolean;
  physicallyConnected: boolean;
  lastSeenAt?: string | null;
  healthDeviceCode?: string | null;
  lastError?: string | null;
};

export type MonitoringCamera = {
  id: string;
  code: string;
  name: string;
  cameraType: string;
  adminStatus: string;
  factoryId: string;
  factoryCode: string;
  floorName?: string | null;
  line?: MonLine | null;
  coveredWorkstations: string[];
  feed: CameraFeed;
  connection: CameraConnection;
  activeZones: number;
  activeAlerts: number;
  highestSeverity?: string | null;
  lastEventAt?: string | null;
  hasReferenceFrame: boolean;
};

export type MonitoringCameraDetail = {
  camera: MonitoringCamera;
  zones: MonZone[];
  recentEvents: MonitoringEvent[];
  activeAlerts: MonitoringAlert[];
  aiStatus: string;
  aiMessage: string;
};

export type SimulationRun = {
  id: string;
  factoryId: string;
  factoryCode: string;
  status: "RUNNING" | "STOPPED" | "COMPLETED";
  cameras: MonRef[];
  eventTypes: string[];
  eventsPerMinute: number;
  durationMinutes: number;
  eventCount: number;
  startedAt: string;
  startedByEmail: string;
  endsAt: string;
  stoppedAt?: string | null;
  stoppedByEmail?: string | null;
  stopReason?: string | null;
};

export type SimulatorStatus = {
  running?: SimulationRun | null;
  recent: SimulationRun[];
  eventTypes: string[];
  maxEventsPerMinute: number;
  maxDurationMinutes: number;
  maxCameras: number;
  note: string;
};

export type SimulatorTriggerResult = { outcome: string; event?: MonitoringEvent | null; alert?: MonitoringAlert | null; note?: string | null };

// ─── Labels and tones ──────────────────────────────────────────────────────

export const MON = "/app/monitoring";

export const EVENT_TYPES = [
  "PERSON_DETECTED", "TRACK_UPDATED", "PERSON_LOST", "ZONE_ENTERED", "ZONE_EXITED", "ACTIVITY_CLASSIFIED", "UNCERTAIN_DETECTION",
  "CAMERA_STATUS_CHANGED", "DEVICE_STATUS_CHANGED", "MODEL_STATUS_CHANGED"
] as const;

export const EVENT_TYPE_LABEL: Record<string, string> = {
  PERSON_DETECTED: "Person detected",
  TRACK_UPDATED: "Track updated",
  PERSON_LOST: "Person lost",
  ZONE_ENTERED: "Zone entered",
  ZONE_EXITED: "Zone exited",
  ACTIVITY_CLASSIFIED: "Activity classified",
  UNCERTAIN_DETECTION: "Uncertain detection",
  CAMERA_STATUS_CHANGED: "Camera status",
  DEVICE_STATUS_CHANGED: "Device status",
  MODEL_STATUS_CHANGED: "Model status"
};

export const ALERT_STATUSES = ["OPEN", "ACKNOWLEDGED", "ESCALATED", "RESOLVED", "DISMISSED"] as const;
export const ALERT_STATUS_LABEL: Record<string, string> = {
  OPEN: "Open",
  ACKNOWLEDGED: "Acknowledged",
  ESCALATED: "Escalated",
  RESOLVED: "Resolved",
  DISMISSED: "Dismissed",
  ACTIVE: "Active (open, acknowledged, escalated)"
};
export const ALERT_STATUS_TONE: Record<string, BadgeTone> = {
  OPEN: "danger",
  ACKNOWLEDGED: "warning",
  ESCALATED: "warning",
  RESOLVED: "success",
  DISMISSED: "neutral"
};

export const SEVERITIES = ["CRITICAL", "HIGH", "MEDIUM", "LOW"] as const;
export const SEVERITY_TONE: Record<string, BadgeTone> = { CRITICAL: "danger", HIGH: "danger", MEDIUM: "warning", LOW: "neutral" };
export const SEVERITY_RANK: Record<string, number> = { CRITICAL: 4, HIGH: 3, MEDIUM: 2, LOW: 1 };

export const ALERT_TYPE_LABEL: Record<string, string> = {
  CAMERA_OFFLINE: "Camera offline",
  DEVICE_OFFLINE: "Device offline",
  DEVICE_ERROR: "Device error",
  MODEL_UNAVAILABLE: "Model unavailable",
  UNCERTAIN_DETECTION_REVIEW: "Uncertain detection: review",
  RESTRICTED_ZONE_ENTRY: "Restricted zone entered"
};

export const RESOLUTION_LABEL: Record<string, string> = {
  ISSUE_FIXED: "Issue fixed",
  FALSE_ALARM: "False alarm",
  NO_ACTION_NEEDED: "No action needed",
  OTHER: "Other"
};

export const ESCALATION_TARGETS: { id: string; label: string }[] = [
  { id: "ProductionSupervisor", label: "Production Supervisor" },
  { id: "FactoryManager", label: "Factory Manager" },
  { id: "FactoryAdmin", label: "Factory Admin" },
  { id: "DeviceAdmin", label: "IT / Device Administrator" }
];

export const REVIEW_DECISION_LABEL: Record<string, string> = {
  CONFIRMED: "Confirmed",
  FALSE_POSITIVE: "False positive",
  STILL_UNCERTAIN: "Still uncertain",
  NEEDS_FOLLOW_UP: "Needs follow-up"
};

export const PRODUCER_LABEL: Record<string, string> = {
  API: "Ingestion API",
  SIMULATOR: "Monitoring simulator",
  DEVICE_PIPELINE: "Device pipeline",
  CAMERA_SERVICE: "Camera settings",
  CV_SERVICE: "Computer-vision service (YOLO + ByteTrack)"
};

export const CONNECTION_TONE: Record<string, BadgeTone> = {
  ONLINE: "success",
  OFFLINE: "danger",
  UNKNOWN: "neutral",
  INACTIVE: "neutral",
  MAINTENANCE: "warning"
};

export const LINE_STATUS_LABEL: Record<string, string> = {
  NORMAL: "Normal",
  ATTENTION: "Needs attention",
  ALERT: "Alert",
  NO_COVERAGE: "No camera coverage"
};

export const FEED_KIND_LABEL: Record<string, string> = {
  RECORDED_VIDEO: "Recorded video",
  LOCAL_WEBCAM: "Local webcam",
  LIVE_STREAM: "Live stream (not connected)",
  NONE: "No source"
};

export const AI_NOT_CONNECTED =
  "Computer vision is not connected in this phase. No detection, tracking or recognition runs on any camera. Vision events come from the monitoring simulator (labelled SIMULATED) or from producers using the ingestion API.";

export const SIMULATED_NOTE = "SIMULATED: generated by the monitoring simulator for demonstration. Not a camera AI result and never a real person's identity.";

// ─── Time (Asia/Dhaka) ─────────────────────────────────────────────────────

export const DHAKA = "Asia/Dhaka";

const dateTime = new Intl.DateTimeFormat("en-GB", { timeZone: DHAKA, day: "numeric", month: "short", hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
const timeOnly = new Intl.DateTimeFormat("en-GB", { timeZone: DHAKA, hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
const dateOnly = new Intl.DateTimeFormat("en-CA", { timeZone: DHAKA, year: "numeric", month: "2-digit", day: "2-digit" });

/** "14 Nov, 19:28:05" in Dhaka time. */
export function dhaka(iso: string | null | undefined): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "—" : dateTime.format(d);
}

/** "19:28:05" in Dhaka time. */
export function dhakaTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? "—" : timeOnly.format(d);
}

/** Today's date in Dhaka, "2026-11-14". */
export function dhakaToday(now: Date = new Date()): string {
  return dateOnly.format(now);
}

/** A Dhaka wall-clock value from a datetime-local input ("2026-11-14T08:00") as a UTC ISO string. Dhaka is UTC+6 all year. */
export function dhakaInputToIso(local: string): string | null {
  if (!local) return null;
  const d = new Date(`${local.length === 16 ? `${local}:00` : local}+06:00`);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

/** "5 s ago", "3 min ago", "2 h ago". */
export function ago(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return "never";
  const s = Math.max(0, Math.round((now - new Date(iso).getTime()) / 1000));
  if (s < 60) return `${s} s ago`;
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  return `${Math.floor(s / 86400)} d ago`;
}

/** A query string from the non-empty values. */
export function qs(params: Record<string, string | number | boolean | null | undefined>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== null && v !== undefined && v !== "") p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : "";
}

export function payloadString(e: { payload?: Record<string, unknown> | null }, key: string): string | null {
  const v = e.payload?.[key];
  return typeof v === "string" ? v : null;
}
