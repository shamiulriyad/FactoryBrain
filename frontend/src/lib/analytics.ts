// Factory analytics: types mirroring /api/analytics. Every KPI carries its
// metric id (definition at /api/analytics/metrics), its kind (measured,
// derived, AI estimate) and its availability — NO_DATA and NOT_PERMITTED
// values are shown as such, never as zero.

export type Availability = "OK" | "NO_DATA" | "PARTIAL" | "NOT_PERMITTED";
export type MetricKind = "MEASURED" | "DERIVED" | "AI_ESTIMATE";

export type Kpi = {
  metricId: string; label: string; value?: number | null; unit: string; numerator?: number | null; denominator?: number | null;
  kind: MetricKind; availability: Availability; note?: string | null;
};

export type MetricDefinition = {
  id: string; name: string; kind: MetricKind; unit: string; sources: string[]; formula: string; window: string; includes: string; excludes: string;
  denominator?: string | null; missingData: string;
};

export type ReportFilter = { name: string; value: string };

export type AnalyticsScope = {
  factoryId?: string | null; scope: string; from: string; to: string; fromUtc: string; toUtc: string; timeZone: string; truncated: boolean;
  source: "REAL" | "SIMULATED"; eventSource?: string | null; filters: ReportFilter[]; sections: string[]; generatedAt: string;
};

export type KpiGroup = { id: string; title: string; kpis: Kpi[]; link?: string | null; note?: string | null };
export type DataSourceStatus = { source: string; label: string; records: number; lastRecordAt?: string | null; availability: Availability; note?: string | null };
export type FactoryOverview = { scope: AnalyticsScope; groups: KpiGroup[]; coverage: DataSourceStatus[]; warnings: string[] };

export type AnalyticsGroup = { key: string; label: string; pieces: number; sharePct?: number | null; piecesPerHour?: number | null; planned?: number | null; achievementPct?: number | null };
export type TrendPoint = { key: string; label: string; start: string; value: number; planned?: number | null };
export type PlanVsActual = { lineId: string; lineName: string; planned: number; actual: number; achievementPct?: number | null; plans: number; note: string };
export type CounterQuality = { countEvents: number; resets: number; excluded: number; suspectJumps: number; correctionPieces: number; corrections: number; duplicateHandling: string };

export type ProductionAnalytics = {
  scope: AnalyticsScope; kpis: Kpi[]; byFactory: AnalyticsGroup[]; byDepartment: AnalyticsGroup[]; byFloor: AnalyticsGroup[]; byLine: AnalyticsGroup[];
  byWorkstation: AnalyticsGroup[]; byMachine: AnalyticsGroup[]; byShift: AnalyticsGroup[]; hourly: TrendPoint[]; daily: TrendPoint[]; planVsActual: PlanVsActual[];
  quality: CounterQuality; warnings: string[];
};

type Ref = { id: string; code: string; name?: string | null };
export type MachinePerformanceRow = {
  machineId: string; code: string; line?: { id: string; name: string } | null; workstation?: Ref | null; runningHours: number; idleHours: number; stoppedHours: number;
  errorHours: number; maintenanceHours: number; offlineHours: number; unknownHours: number; uptimePct?: number | null; downtimePct?: number | null;
  coveragePct?: number | null; errors: number; pieces: number; monitored: boolean;
};
export type LineAvailabilityRow = {
  lineId: string; lineName: string; machines: number; monitored: number; runningHours: number; downtimeHours: number; gapHours: number;
  uptimePct?: number | null; downtimePct?: number | null; coveragePct?: number | null; errors: number;
};
export type MachineDayPoint = { date: string; runningHours: number; downtimeHours: number; idleHours: number; gapHours: number; uptimePct?: number | null; errors: number };
export type MachineAnalytics = { scope: AnalyticsScope; kpis: Kpi[]; machines: MachinePerformanceRow[]; lines: LineAvailabilityRow[]; daily: MachineDayPoint[]; warnings: string[] };

export type AttendanceDayRow = {
  date: string; scheduled: number; scheduledStarted: number; scheduledPresent: number; present: number; absent: number; late: number; earlyDeparture: number;
  ratePct?: number | null; breakMinutes: number; overBreakMinutes: number; needsReview: number;
};
export type WorkforceGroupRow = {
  key: string; label: string; workers: number; scheduledStarted: number; scheduledPresent: number; present: number; ratePct?: number | null; late: number;
  earlyDeparture: number; assigned: number; assignmentCoveragePct?: number | null; plannedOperators: number; presentToday: number; staffingNote?: string | null;
};
export type OvertimeSummary = {
  requests: number; approved: number; pending: number; rejected: number; requestedMinutes: number; approvedMinutes: number; actualMinutes: number;
  workersWithOvertime: number; actualVsApprovedPct?: number | null;
};
export type WorkforceConcept = { id: string; label: string; value: number; kind: MetricKind; note: string };
export type WorkforceAnalytics = {
  scope: AnalyticsScope; kpis: Kpi[]; daily: AttendanceDayRow[]; byShift: WorkforceGroupRow[]; byDepartment: WorkforceGroupRow[]; byLine: WorkforceGroupRow[];
  overtime: OvertimeSummary; overtimeDaily: { date: string; approvedMinutes: number; actualMinutes: number; requests: number }[]; concepts: WorkforceConcept[]; warnings: string[];
};

export type ActivityShare = { activity: string; predictions: number; sharePct?: number | null };
export type ActivityInsights = {
  scope: AnalyticsScope; family: string; kpis: Kpi[]; distribution: ActivityShare[];
  daily: { date: string; predictions: number; uncertain: number; activities: ActivityShare[] }[];
  byLine: { lineId: string; lineName: string; predictions: number; uncertain: number; activities: ActivityShare[] }[];
  confidence: { band: string; predictions: number; sharePct?: number | null }[];
  cameras: { cameraId: string; code: string; hoursWithData: number; tracks: number; predictions: number; observationHours: number; lastEventAt?: string | null }[];
  identity: { tracksObserved: number; verifiedTracks: number; unverifiedTracks: number; ambiguousOrReview: number; verifiedWorkers: number; note: string };
  disclaimer: string; warnings: string[];
};

export type OperationalInsight = {
  id: string; type: string; title: string; severity: "LOW" | "MEDIUM" | "HIGH"; reliability: "LOW" | "MEDIUM" | "HIGH"; reliabilityReason: string;
  factoryId: string; factoryCode: string; lineId?: string | null; lineName?: string | null; machineId?: string | null; machineCode?: string | null;
  detectedAt: string; evidence: { label: string; value: string; metricId?: string | null }[]; dataSources: string[]; recommendation: string; caveat: string; source: string;
};
export type OperationalInsights = { scope: AnalyticsScope; insights: OperationalInsight[]; rules: { type: string; evaluated: boolean; reason?: string | null }[]; warnings: string[]; note: string };

export type ReportType = { type: string; title: string; available: boolean; reason?: string | null };
export type ReportTable = { title: string; columns: string[]; rows: (string | number | boolean | null)[][]; note?: string | null };
export type ReportDocument = {
  type: string; title: string; generatedAt: string; generatedBy: string; scope: string; from: string; to: string; timeZone: string; source: string;
  filters: ReportFilter[]; sections: { title: string; kpis: Kpi[]; tables: ReportTable[]; note?: string | null }[]; warnings: string[]; definitions: MetricDefinition[];
};

export const KIND_LABEL: Record<MetricKind, string> = { MEASURED: "Measured", DERIVED: "Derived", AI_ESTIMATE: "AI estimate" };

export const INSIGHT_LABEL: Record<string, string> = {
  LINE_BELOW_TARGET: "Output below plan", HIGH_MACHINE_DOWNTIME: "High machine downtime", REPEATED_MACHINE_ERRORS: "Repeated machine errors",
  COUNTER_INACTIVITY: "Counter inactivity", ATTENDANCE_SHORTAGE: "Attendance shortage", ASSIGNMENT_GAP: "Workstation assignment gap",
  HIGH_OVERTIME: "High overtime", LOW_DATA_COVERAGE: "Low data coverage", REPEATED_ALERTS: "Repeated alerts"
};

/** "1,240" / "62.5 %" / "3.2 h" for a KPI; null when unavailable. */
export function kpiText(k: Kpi): string | null {
  if (k.availability === "NOT_PERMITTED" || k.availability === "NO_DATA" || k.value === null || k.value === undefined) return null;
  const v = k.value;
  if (k.unit === "%") return `${v.toFixed(1)} %`;
  if (k.unit === "hours") return `${v.toFixed(1)} h`;
  if (k.unit === "minutes") return v >= 120 ? `${(v / 60).toFixed(1)} h` : `${Math.round(v)} min`;
  return Number.isInteger(v) ? v.toLocaleString("en-US") : v.toFixed(1);
}
