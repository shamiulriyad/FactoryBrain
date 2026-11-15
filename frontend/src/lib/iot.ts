// Machine monitoring, production counters and IoT devices: types mirroring
// the /api/iot responses and the labels every machine view uses. Machine
// data is never worker data: running time is the machine's, counts are
// machine output. Simulated and real figures are never added together.

import type { BadgeTone } from "@/components/ui/Badge";
import type { MonitoringAlert, MonLine, MonRef } from "@/lib/monitoring";

export type MachineState = "RUNNING" | "STOPPED" | "IDLE" | "MAINTENANCE" | "ERROR" | "OFFLINE" | "UNKNOWN";
export type DataSource = "REAL" | "SIMULATED";

export type MachineDevice = {
  id: string; code: string; name: string; category: string; deviceType: string; deviceTypeLabel: string; protocol: string; isSimulated: boolean;
  health: string; healthLabel: string; lastHeartbeatAt?: string | null; lastSeenAt?: string | null; firmwareVersion?: string | null; isCounter: boolean; decidesState: boolean;
};

export type Machine = {
  id: string; code: string; machineType?: string | null; machineKind: string; factoryId: string; factoryCode: string;
  workstation?: MonRef | null; line?: MonLine | null; departmentName?: string | null; floorName?: string | null; isActive: boolean; monitored: boolean;
  state: MachineState; since?: string | null; reason?: string | null; sourceType?: string | null; isSimulated: boolean;
  powerWatts?: number | null; currentAmps?: number | null; temperatureC?: number | null; vibrationMmS?: number | null; readingsAt?: string | null;
  lastTelemetryAt?: string | null; lastHeartbeatAt?: string | null; lastCountAt?: string | null; errorCode?: string | null;
  inMaintenance: boolean; maintenanceReason?: string | null; activeConditions: string[]; devices: MachineDevice[];
};

export type MachineCounts = { total: number; running: number; stopped: number; idle: number; offline: number; error: number; maintenance: number; unknown: number; unmonitored: number };
export type GroupCount = { key: string; label: string; pieces: number; piecesPerHour?: number | null };
export type BucketCount = { start: string; label: string; pieces: number };
export type RecentDeviceEvent = { id: number; deviceId: string; deviceCode: string; eventType: string; severity: string; message?: string | null; isSimulated: boolean; occurredAt: string };

export type BridgeStatus = {
  enabled: boolean; connected: boolean; broker?: string | null; tls: boolean; clientId: string; topicPrefix: string; subscriptions: string[];
  connectedSince?: string | null; lastMessageAt?: string | null; received: number; accepted: number; duplicates: number; rejected: number;
  lastError?: string | null; lastErrorAt?: string | null; reconnects: number; note: string;
};

export type MachineOverview = {
  asOf: string; source: DataSource; counts: MachineCounts; machines: Machine[]; piecesToday: number; productionByLine: GroupCount[]; productionByShift: GroupCount[];
  downtimeSecondsToday: number; recentDeviceEvents: RecentDeviceEvent[]; alerts: MonitoringAlert[]; bridge: BridgeStatus; canSeeProduction: boolean; note: string;
};

export type MachineMetrics = {
  machineId: string; code: string; line?: MonLine | null; workstation?: MonRef | null; pieces: number; runningSeconds: number; idleSeconds: number; downtimeSeconds: number;
  stoppedSeconds: number; errorSeconds: number; offlineSeconds: number; unknownSeconds: number; maintenanceSeconds: number; piecesPerRunningHour?: number | null; runningShare?: number | null;
};

export type ProductionReport = {
  from: string; to: string; source: DataSource; timeZone: string; totalPieces: number; countedPieces: number; correctionPieces: number; piecesPerHour?: number | null;
  byLine: GroupCount[]; byWorkstation: GroupCount[]; byMachine: GroupCount[]; byShift: GroupCount[]; hourly: BucketCount[]; daily: BucketCount[];
  machines: MachineMetrics[]; runningSeconds: number; idleSeconds: number; downtimeSeconds: number; note: string;
};

export type StateTransition = {
  id: number; machineId: string; machineCode: string; fromState?: string | null; toState: MachineState; reason: string; trigger: string; sourceType: string;
  deviceCode?: string | null; occurredAt: string; durationSeconds?: number | null;
};

export type Telemetry = {
  id: number; deviceId: string; deviceCode: string; reportedState?: string | null; powerWatts?: number | null; currentAmps?: number | null; temperatureC?: number | null;
  vibrationMmS?: number | null; errorCode?: string | null; metrics?: Record<string, number> | null; sourceType: string; transport: string; outcome: string;
  messageId?: string | null; occurredAt: string; receivedAt: string;
};

export type CounterEvent = {
  id: number; deviceId: string; deviceCode: string; machine?: MonRef | null; workstation?: MonRef | null; line?: MonLine | null; counterMode: string; rawValue: number;
  pulses: number; pieces: number; status: string; note?: string | null; shiftCode?: string | null; productionDate: string; sourceType: string; transport: string;
  messageId?: string | null; occurredAt: string; receivedAt: string;
};

export type MachineProfile = {
  machineId: string; machineKind: string; countingMode: string; pulsesPerPiece: number; runningPowerWatts?: number | null; idlePowerWatts?: number | null;
  staleAfterSeconds?: number | null; stopAlertAfterSeconds?: number | null; longIdleAlertSeconds?: number | null; maxTemperatureC?: number | null;
  maxVibrationMmS?: number | null; counterSilenceAlertSeconds?: number | null; flapTransitionsPer5Min?: number | null; alertsEnabled: boolean; isDefault: boolean;
  updatedAt?: string | null; updatedByEmail?: string | null; version: number;
};

export type MachineContext = {
  assignedWorker?: MonRef | null; assignedSince?: string | null; verifiedWorker?: MonRef | null; verifiedCamera?: string | null; verifiedTrack?: string | null;
  verifiedAt?: string | null; note: string;
};

export type MachineDetail = {
  machine: Machine; profile: MachineProfile; today: MachineMetrics; recentTransitions: StateTransition[]; recentTelemetry: Telemetry[]; recentCounts: CounterEvent[];
  alerts: MonitoringAlert[]; context?: MachineContext | null; canConfigure: boolean; canCorrect: boolean; canSeeProduction: boolean;
};

export type IotDevice = {
  deviceId: string; code: string; name: string; category: string; factoryId: string; factoryCode: string; isSimulated: boolean; configured: boolean;
  deviceType: string; deviceTypeLabel: string; protocol: string; connectionVia: string; gateway?: MonRef | null; mqttClientId?: string | null;
  telemetryIntervalSeconds?: number | null; settings: Record<string, string>; machine?: MonRef | null; workstation?: MonRef | null; line?: MonLine | null;
  status: string; health: string; healthLabel: string; lastHeartbeatAt?: string | null; lastSeenAt?: string | null; firmwareVersion?: string | null;
  registeredAt: string; hasKey: boolean; keyPrefix?: string | null; keyIssuedAt?: string | null; topics: string[]; messagesLastHour: number; version: number;
};

export type DeviceKeyIssued = { deviceId: string; deviceCode: string; key: string; keyPrefix: string; issuedAt: string; note: string };

export type SimulatedMessage = { deviceCode: string; machineCode?: string | null; channel: string; topic: string; payload: string; outcome: string; code?: string | null; error?: string | null };
export type SimulationResult = { scenario: string; machines: number; messages: SimulatedMessage[]; notes: string[] };
export type SimulationRun = {
  id: string; factoryId: string; target: string; profile: string; status: string; startedAt: string; endsAt: string; endedAt?: string | null; intervalSeconds: number;
  machines: number; messagesSent: number; messagesRejected: number; lastError?: string | null; startedByEmail: string;
};

export const MACHINE_STATES: MachineState[] = ["RUNNING", "IDLE", "STOPPED", "ERROR", "MAINTENANCE", "OFFLINE", "UNKNOWN"];

export const STATE_LABEL: Record<string, string> = {
  RUNNING: "Running", STOPPED: "Stopped", IDLE: "Idle", MAINTENANCE: "Maintenance", ERROR: "Error", OFFLINE: "Offline", UNKNOWN: "Unknown"
};

export const STATE_TONE: Record<string, BadgeTone> = {
  RUNNING: "success", IDLE: "info", STOPPED: "warning", ERROR: "danger", MAINTENANCE: "accent", OFFLINE: "neutral", UNKNOWN: "neutral"
};

/** Bar colours for state durations (CSS variables with fallbacks). */
export const STATE_COLOR: Record<string, string> = {
  RUNNING: "var(--ok, #16a34a)", IDLE: "var(--info, #0284c7)", STOPPED: "var(--risk-medium, #d97706)", ERROR: "var(--risk-high, #dc2626)",
  MAINTENANCE: "var(--accent, #7c3aed)", OFFLINE: "#94a3b8", UNKNOWN: "#cbd5e1"
};

export const CONDITION_LABEL: Record<string, string> = {
  UNEXPECTED_STOP: "Unexpected stoppage", LONG_IDLE: "Long idle", ABNORMAL_TEMPERATURE: "Abnormal temperature", HIGH_VIBRATION: "High vibration",
  COUNTER_DISCONNECTED: "Counter disconnected", UNUSUAL_TRANSITION: "Unusual transitions"
};

export const DEVICE_TYPES = [
  { id: "SEWING_MACHINE_SENSOR", label: "Sewing machine sensor", category: "MachineSensor" },
  { id: "RUN_STOP_SENSOR", label: "Machine running / stopped sensor", category: "MachineSensor" },
  { id: "POWER_SENSOR", label: "Machine power sensor", category: "MachineSensor" },
  { id: "CUTTING_MACHINE_SENSOR", label: "Cutting machine sensor", category: "MachineSensor" },
  { id: "PRESSING_MACHINE_SENSOR", label: "Ironing / pressing machine sensor", category: "MachineSensor" },
  { id: "PIECE_COUNTER", label: "Production piece counter", category: "ProductionCounter" },
  { id: "RFID_READER", label: "RFID reader", category: "RfidReader" },
  { id: "ESP32", label: "ESP32 device", category: "Esp32Gateway" },
  { id: "INDUSTRIAL_GATEWAY", label: "Industrial gateway", category: "IndustrialGateway" },
  { id: "MQTT_DEVICE", label: "MQTT-connected device", category: "Other" },
  { id: "GENERIC_SENSOR", label: "Generic IoT sensor", category: "Other" }
] as const;

export const SIM_SCENARIOS = [
  { id: "RUNNING", label: "Machine running" },
  { id: "STOPPED", label: "Machine stopped" },
  { id: "IDLE", label: "Machine idle" },
  { id: "ERROR", label: "Machine error (thread break)" },
  { id: "OFFLINE", label: "Devices go offline" },
  { id: "ONLINE", label: "Devices come back online" },
  { id: "HEARTBEAT", label: "Heartbeat" },
  { id: "PIECES", label: "Production pieces", needs: "count" },
  { id: "COUNTER_RESET", label: "Counter reset" },
  { id: "TEMPERATURE", label: "Temperature reading", needs: "value" },
  { id: "VIBRATION", label: "Vibration reading", needs: "value" },
  { id: "DUPLICATE", label: "Duplicate message (same id twice)" },
  { id: "DELAYED", label: "Delayed messages", needs: "delay" },
  { id: "INVALID", label: "Invalid payload" }
] as const;

export const MACHINE_DISCLAIMER =
  "Machine data only. A running machine doesn't prove anyone is operating it, and a production count is machine output, not an individual's output. " +
  "Machine running time is never worker working time; attendance, predicted activity and verified workstation identity are separate records.";

export function stateLabel(s: string | null | undefined): string {
  return s ? STATE_LABEL[s] ?? s : "—";
}

/** "1 h 05 min" from seconds; "—" for nothing. */
export function hours(seconds: number | null | undefined): string {
  if (!seconds || seconds < 1) return "—";
  const m = Math.round(seconds / 60);
  if (m < 60) return `${m} min`;
  return `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, "0")} min`;
}

export function num(v: number | null | undefined, digits = 1, unit = ""): string {
  return v === null || v === undefined ? "—" : `${v.toFixed(digits)}${unit}`;
}
