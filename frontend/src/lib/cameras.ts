// Cameras, zones and devices: API contracts (BunonBrain.Application/Dtos/Cameras
// and Dtos/Devices), labels and the few calls that aren't plain JSON
// (multipart uploads, authenticated media). The API enforces every rule; the
// UI only avoids offering what it would refuse.

import { apiFetch } from "@/lib/auth";
import { ApiError, errorFromResponse } from "@/lib/api";
import type { OrgRef } from "@/lib/workers";
import type { BadgeTone } from "@/components/ui/Badge";

export type LineRef = { id: string; name: string };

// ─── Cameras ──────────────────────────────────────────────────────────────

export const CAMERA_TYPES = ["FixedCctv", "IpCamera", "UsbCamera", "RtspCamera", "Webcam", "RecordedVideo"] as const;
export const CAMERA_TYPE_LABEL: Record<string, string> = {
  FixedCctv: "Fixed CCTV",
  IpCamera: "IP camera",
  UsbCamera: "USB camera",
  RtspCamera: "RTSP camera",
  Webcam: "Webcam",
  RecordedVideo: "Recorded video (prototype)"
};

export const CAMERA_STATUSES = ["Active", "Inactive", "Maintenance"] as const;
export const CAMERA_STATUS_TONE: Record<string, BadgeTone> = { Active: "success", Inactive: "neutral", Maintenance: "warning" };

export const SOURCE_TYPES = ["None", "Rtsp", "LocalWebcam", "RecordedVideo", "HttpStream"] as const;
export const SOURCE_TYPE_LABEL: Record<string, string> = {
  None: "Not configured",
  Rtsp: "RTSP stream",
  LocalWebcam: "Local webcam",
  RecordedVideo: "Recorded video",
  HttpStream: "HTTP stream (future IP camera)"
};

export const ZONE_TYPES = ["Workstation", "ProductionLine", "Walkway", "Restricted", "Other"] as const;
export const ZONE_TYPE_LABEL: Record<string, string> = {
  Workstation: "Workstation",
  ProductionLine: "Production line",
  Walkway: "Walkway",
  Restricted: "Restricted area",
  Other: "Other"
};
export const ZONE_TYPE_COLOR: Record<string, string> = {
  Workstation: "#2F80ED",
  ProductionLine: "#27AE60",
  Walkway: "#F2994A",
  Restricted: "#EB5757",
  Other: "#9B51E0"
};

export type CameraWorkstationRef = { id: string; code: string; lineId: string; isActive: boolean };
export type CameraCoverage = { id: string; kind: "Line" | "Workstation"; line: LineRef; workstation?: CameraWorkstationRef | null; createdAt: string; createdByEmail: string };

export type CameraSource = {
  id: string;
  version: number;
  sourceType: string;
  streamUrl?: string | null;
  streamHost?: string | null;
  hasCredentials: boolean;
  webcamLabel?: string | null;
  hasVideo: boolean;
  videoFileName?: string | null;
  videoContentType?: string | null;
  videoSizeBytes?: number | null;
  notes?: string | null;
  /** Always NotChecked in this phase: the server never connects to camera URLs. */
  connectionCheck: string;
  createdAt: string;
  createdByEmail: string;
  retiredAt?: string | null;
};

export type ReferenceFrame = { source: string; width: number; height: number; contentType: string; frameSeconds?: number | null; updatedAt: string };

export type CameraHealth = {
  deviceId: string;
  deviceCode: string;
  isSimulated: boolean;
  status: string;
  health: string;
  connection: string;
  label: string;
  physicallyConnected: boolean;
  lastSeenAt?: string | null;
  lastErrorAt?: string | null;
  lastErrorMessage?: string | null;
};

export type Camera = {
  id: string;
  code: string;
  name: string;
  cameraType: string;
  factoryId: string;
  factoryCode: string;
  factoryName: string;
  department?: OrgRef | null;
  floor: OrgRef;
  line?: LineRef | null;
  location?: string | null;
  description?: string | null;
  status: string;
  statusReason?: string | null;
  statusChangedAt: string;
  installedOn?: string | null;
  source?: CameraSource | null;
  coverage: CameraCoverage[];
  activeZoneCount: number;
  referenceFrame?: ReferenceFrame | null;
  health?: CameraHealth | null;
  lastStatusUpdateAt: string;
  ai: { status: string; message: string; videoSourceReady: boolean; referenceFrameReady: boolean; activeZones: number };
  can: { manage: boolean; delete?: boolean | null; deleteBlockedReason?: string | null };
  createdAt: string;
  updatedAt: string;
};

export type CameraOverview = {
  total: number;
  byStatus: Record<string, number>;
  byType: Record<string, number>;
  bySource: Record<string, number>;
  withoutSource: number;
  withoutReferenceFrame: number;
  withZones: number;
  activeZones: number;
  withoutHealthDevice: number;
  byHealth: Record<string, number>;
  simulatedHealth: number;
};

export type ZonePoint = { x: number; y: number };

export type CameraZone = {
  id: string;
  cameraId: string;
  name: string;
  zoneType: string;
  workstation?: CameraWorkstationRef | null;
  line?: LineRef | null;
  points: ZonePoint[];
  area: number;
  color?: string | null;
  notes?: string | null;
  isActive: boolean;
  version: number;
  createdAt: string;
  createdByEmail: string;
  updatedAt: string;
  updatedByEmail: string;
};

export type ZoneRevision = {
  id: number;
  zoneId: string;
  version: number;
  action: string;
  name: string;
  zoneType: string;
  workstationId?: string | null;
  lineId?: string | null;
  points: ZonePoint[];
  isActive: boolean;
  drawnOnCurrentFrame: boolean;
  changedAt: string;
  changedByEmail: string;
};

export const AI_NOT_CONNECTED_NOTE =
  "Computer-vision processing is not part of this phase. No detection, tracking or recognition runs, and no AI results are shown anywhere on these pages.";

// ─── Devices ──────────────────────────────────────────────────────────────

export const DEVICE_CATEGORIES = ["RfidReader", "MachineSensor", "ProductionCounter", "Esp32Gateway", "IndustrialGateway", "Camera", "Other"] as const;
export const DEVICE_CATEGORY_LABEL: Record<string, string> = {
  RfidReader: "RFID reader",
  MachineSensor: "Machine sensor",
  ProductionCounter: "Production counter",
  Esp32Gateway: "ESP32 gateway",
  IndustrialGateway: "Industrial gateway",
  Camera: "Camera",
  Other: "Other"
};

export const DEVICE_STATUSES = ["Active", "Maintenance", "Disabled"] as const;
export const HEALTH_STATUSES = ["Online", "Offline", "Unknown", "Maintenance", "Disabled"] as const;
export const HEALTH_TONE: Record<string, BadgeTone> = {
  Online: "success",
  Offline: "danger",
  Unknown: "neutral",
  Maintenance: "warning",
  Disabled: "neutral"
};

export const EVENT_TYPE_LABEL: Record<string, string> = {
  ConnectionOnline: "Came online",
  ConnectionOffline: "Went offline",
  HeartbeatMissed: "Heartbeat missed",
  Error: "Error",
  StatusChanged: "Status changed",
  FirmwareChanged: "Firmware changed"
};
export const EVENT_SOURCE_LABEL: Record<string, string> = {
  Device: "Device (real)",
  Simulator: "Simulator",
  System: "Health monitor",
  Manual: "Person"
};

export type DeviceLink = { id: string; code: string; name?: string | null };

export type Device = {
  id: string;
  code: string;
  name: string;
  category: string;
  isSimulated: boolean;
  mode: "Simulated" | "Real";
  status: string;
  statusReason?: string | null;
  connectionStatus: string;
  healthStatus: string;
  healthLabel: string;
  physicallyConnected: boolean;
  heartbeatOverdue: boolean;
  factoryId: string;
  factoryCode: string;
  line?: LineRef | null;
  workstation?: DeviceLink | null;
  machine?: DeviceLink | null;
  camera?: DeviceLink | null;
  attendanceReader?: DeviceLink | null;
  manufacturer?: string | null;
  model?: string | null;
  serialNumber?: string | null;
  firmwareVersion?: string | null;
  metadata: Record<string, string>;
  installedOn?: string | null;
  heartbeatIntervalSeconds: number;
  offlineAfterSeconds: number;
  lastSeenAt?: string | null;
  lastHeartbeatAt?: string | null;
  lastConnectedAt?: string | null;
  lastDisconnectedAt?: string | null;
  connectionChangedAt?: string | null;
  lastErrorAt?: string | null;
  lastErrorCode?: string | null;
  lastErrorMessage?: string | null;
  createdAt: string;
  updatedAt: string;
};

export type DeviceEvent = {
  id: number;
  deviceId: string;
  deviceCode: string;
  eventType: string;
  severity: string;
  source: string;
  isSimulated: boolean;
  occurredAt: string;
  receivedAt: string;
  code?: string | null;
  message?: string | null;
  fromState?: string | null;
  toState?: string | null;
  recordedByEmail: string;
};

export type DeviceHeartbeat = {
  id: number;
  deviceId: string;
  occurredAt: string;
  receivedAt: string;
  source: string;
  isSimulated: boolean;
  messageId?: string | null;
  firmwareVersion?: string | null;
  metrics?: Record<string, number> | null;
  recordedByEmail: string;
};

export type DeviceMessageResult = { outcome: string; isSimulated: boolean; heartbeatId?: number | null; events: DeviceEvent[]; device: Device };
export type SimulatorBatchResult = { accepted: number; events: DeviceEvent[]; device: Device };

export type DeviceHealthOverview = {
  asOf: string;
  total: number;
  byHealth: Record<string, number>;
  byCategory: { category: string; total: number; online: number; offline: number; unknown: number; maintenance: number; disabled: number }[];
  real: number;
  simulated: number;
  physicallyConnected: number;
  simulatedOnline: number;
  heartbeatOverdue: number;
  errorsLast24Hours: number;
  needsAttention: Device[];
  recentEvents: DeviceEvent[];
};

export const SIMULATED_DEVICE_NOTE =
  "Simulated devices exist only in software. Their heartbeats, connection changes and errors come from the device simulator and are always marked SIMULATED; a simulated device is never shown as physically connected.";

// ─── Helpers ──────────────────────────────────────────────────────────────

export function seconds(s: number): string {
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.round(s / 6) / 10} min`.replace(".0 ", " ");
  return `${Math.round(s / 360) / 10} h`.replace(".0 ", " ");
}

export function bytes(n?: number | null): string {
  if (!n && n !== 0) return "—";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`;
  return `${(n / (1024 * 1024)).toFixed(1)} MB`;
}

const UPLOAD_TIMEOUT_MS = 300_000;

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
      throw new ApiError({ status: 0, kind: "timeout", message: "The upload took too long. Check the connection and try again." });
    throw new ApiError({ status: 0, kind: "network", message: "Could not reach the server. Check your connection and try again." });
  } finally {
    clearTimeout(timer);
  }
}

/** PUT /api/cameras/{id}/reference-frame — an image file, or a frame grabbed from the recorded video. */
export async function uploadReferenceFrame(cameraId: string, image: Blob, source: "Upload" | "VideoFrame", frameSeconds?: number): Promise<Camera> {
  const form = new FormData();
  form.append("image", image, image instanceof File ? image.name : "frame.png");
  form.append("source", source);
  if (frameSeconds !== undefined) form.append("frameSeconds", frameSeconds.toFixed(3));
  const res = await send(`/api/cameras/${encodeURIComponent(cameraId)}/reference-frame`, { method: "PUT", body: form });
  return (await res.json()) as Camera;
}

/** PUT /api/cameras/{id}/source/video — MP4 or WebM; becomes the camera's current source. */
export async function uploadCameraVideo(cameraId: string, file: File, notes?: string): Promise<Camera> {
  const form = new FormData();
  form.append("video", file, file.name);
  if (notes) form.append("notes", notes);
  const res = await send(`/api/cameras/${encodeURIComponent(cameraId)}/source/video`, { method: "PUT", body: form });
  return (await res.json()) as Camera;
}

/** Fetch an authenticated media file (reference frame, recorded video) as an object URL. Revoke it when done. */
export async function fetchMedia(path: string, signal?: AbortSignal): Promise<string> {
  const res = await apiFetch(path, { cache: "no-store", signal });
  if (!res.ok) throw await errorFromResponse(res);
  return URL.createObjectURL(await res.blob());
}
