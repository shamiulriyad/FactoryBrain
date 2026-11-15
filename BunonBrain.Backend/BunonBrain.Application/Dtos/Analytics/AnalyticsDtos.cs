using BunonBrain.Application.Analytics;
using BunonBrain.Application.Dtos.Monitoring;

namespace BunonBrain.Application.Dtos.Analytics;

/// <summary>
/// The filters shared by every analytics page and report. Dates are
/// factory-local days. <c>Source</c>: REAL (default) or SIMULATED — never both.
/// <c>EventSource</c>: DEVICE, MANUAL or SIMULATOR (how records arrived), where a
/// page's data records it. <c>Family</c> (AI activity): generic or garment.
/// </summary>
public sealed record AnalyticsFilter(
    Guid? FactoryId = null,
    Guid? DepartmentId = null,
    Guid? FloorId = null,
    string? LineId = null,
    Guid? WorkstationId = null,
    Guid? MachineId = null,
    Guid? ShiftId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Source = null,
    string? EventSource = null,
    string? Family = null);

/// <summary>What a response covers: scope, period, data source, filters, and which sections the caller may see.</summary>
public sealed record AnalyticsScopeDto(
    Guid? FactoryId,
    string Scope,
    DateOnly From,
    DateOnly To,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string TimeZone,
    bool Truncated,
    string Source,
    string? EventSource,
    IReadOnlyList<ReportFilter> Filters,
    IReadOnlyList<string> Sections,
    DateTimeOffset GeneratedAt);

public sealed record KpiGroup(string Id, string Title, IReadOnlyList<Kpi> Kpis, string? Link, string? Note);

/// <summary>When a data source last delivered data, and how much, in the scope and period.</summary>
public sealed record DataSourceStatus(string Source, string Label, long Records, DateTimeOffset? LastRecordAt, string Availability, string? Note);

public sealed record FactoryOverviewDto(AnalyticsScopeDto Scope, IReadOnlyList<KpiGroup> Groups, IReadOnlyList<DataSourceStatus> Coverage, IReadOnlyList<string> Warnings);

// ─── Production ────────────────────────────────────────────────────────────

public sealed record AnalyticsGroup(string Key, string Label, long Pieces, double? SharePct, double? PiecesPerHour, double? Planned = null, double? AchievementPct = null);

public sealed record TrendPoint(string Key, string Label, DateTimeOffset Start, double Value, double? Planned = null);

public sealed record PlanVsActual(string LineId, string LineName, double Planned, long Actual, double? AchievementPct, int Plans, string Note);

public sealed record CounterQuality(int CountEvents, int Resets, int Excluded, int SuspectJumps, long CorrectionPieces, int Corrections, string DuplicateHandling);

public sealed record ProductionAnalyticsDto(
    AnalyticsScopeDto Scope,
    IReadOnlyList<Kpi> Kpis,
    IReadOnlyList<AnalyticsGroup> ByFactory,
    IReadOnlyList<AnalyticsGroup> ByDepartment,
    IReadOnlyList<AnalyticsGroup> ByFloor,
    IReadOnlyList<AnalyticsGroup> ByLine,
    IReadOnlyList<AnalyticsGroup> ByWorkstation,
    IReadOnlyList<AnalyticsGroup> ByMachine,
    IReadOnlyList<AnalyticsGroup> ByShift,
    IReadOnlyList<TrendPoint> Hourly,
    IReadOnlyList<TrendPoint> Daily,
    IReadOnlyList<PlanVsActual> PlanVsActual,
    CounterQuality Quality,
    IReadOnlyList<string> Warnings);

// ─── Machines ──────────────────────────────────────────────────────────────

public sealed record MachinePerformanceRow(
    Guid MachineId, string Code, MonLine? Line, MonRef? Workstation, double RunningHours, double IdleHours, double StoppedHours, double ErrorHours,
    double MaintenanceHours, double OfflineHours, double UnknownHours, double? UptimePct, double? DowntimePct, double? CoveragePct, int Errors, long Pieces,
    bool Monitored);

public sealed record LineAvailabilityRow(string LineId, string LineName, int Machines, int Monitored, double RunningHours, double DowntimeHours, double GapHours,
    double? UptimePct, double? DowntimePct, double? CoveragePct, int Errors);

public sealed record MachineDayPoint(DateOnly Date, double RunningHours, double DowntimeHours, double IdleHours, double GapHours, double? UptimePct, int Errors);

public sealed record MachineAnalyticsDto(
    AnalyticsScopeDto Scope,
    IReadOnlyList<Kpi> Kpis,
    IReadOnlyList<MachinePerformanceRow> Machines,
    IReadOnlyList<LineAvailabilityRow> Lines,
    IReadOnlyList<MachineDayPoint> Daily,
    IReadOnlyList<string> Warnings);

// ─── Workforce ─────────────────────────────────────────────────────────────

public sealed record AttendanceDayRow(DateOnly Date, int Scheduled, int ScheduledStarted, int ScheduledPresent, int Present, int Absent, int Late,
    int EarlyDeparture, double? RatePct, int BreakMinutes, int OverBreakMinutes, int NeedsReview);

public sealed record WorkforceGroupRow(string Key, string Label, int Workers, int ScheduledStarted, int ScheduledPresent, int Present, double? RatePct,
    int Late, int EarlyDeparture, int Assigned, double? AssignmentCoveragePct, int PlannedOperators, int PresentToday, string? StaffingNote);

public sealed record OvertimeDayRow(DateOnly Date, int ApprovedMinutes, int ActualMinutes, int Requests);

/// <summary>Counts that look alike but are different things, kept side by side and never added together.</summary>
public sealed record WorkforceConcept(string Id, string Label, long Value, string Kind, string Note);

public sealed record WorkforceAnalyticsDto(
    AnalyticsScopeDto Scope,
    IReadOnlyList<Kpi> Kpis,
    IReadOnlyList<AttendanceDayRow> Daily,
    IReadOnlyList<WorkforceGroupRow> ByShift,
    IReadOnlyList<WorkforceGroupRow> ByDepartment,
    IReadOnlyList<WorkforceGroupRow> ByLine,
    OvertimeSummary Overtime,
    IReadOnlyList<OvertimeDayRow> OvertimeDaily,
    IReadOnlyList<WorkforceConcept> Concepts,
    IReadOnlyList<string> Warnings);

// ─── AI activity ───────────────────────────────────────────────────────────

public sealed record ActivityShare(string Activity, long Predictions, double? SharePct);

public sealed record ActivityDayRow(DateOnly Date, long Predictions, long Uncertain, IReadOnlyList<ActivityShare> Activities);

public sealed record ActivityLineRow(string LineId, string LineName, long Predictions, long Uncertain, IReadOnlyList<ActivityShare> Activities);

public sealed record ConfidenceBand(string Band, long Predictions, double? SharePct);

public sealed record CameraCoverageRow(Guid CameraId, string Code, long HoursWithData, long Tracks, long Predictions, double ObservationHours, DateTimeOffset? LastEventAt);

public sealed record IdentityCoverage(long TracksObserved, long VerifiedTracks, long UnverifiedTracks, long AmbiguousOrReview, long VerifiedWorkers, string Note);

public sealed record ActivityInsightsDto(
    AnalyticsScopeDto Scope,
    string Family,
    IReadOnlyList<Kpi> Kpis,
    IReadOnlyList<ActivityShare> Distribution,
    IReadOnlyList<ActivityDayRow> Daily,
    IReadOnlyList<ActivityLineRow> ByLine,
    IReadOnlyList<ConfidenceBand> Confidence,
    IReadOnlyList<CameraCoverageRow> Cameras,
    IdentityCoverage Identity,
    string Disclaimer,
    IReadOnlyList<string> Warnings);

// ─── Insights and reports ──────────────────────────────────────────────────

public sealed record InsightRuleStatus(string Type, bool Evaluated, string? Reason);

public sealed record OperationalInsightsDto(AnalyticsScopeDto Scope, IReadOnlyList<OperationalInsight> Insights, IReadOnlyList<InsightRuleStatus> Rules,
    IReadOnlyList<string> Warnings, string Note);

public sealed record ReportTypeDto(string Type, string Title, bool Available, string? Reason);

public sealed record ReportFile(string FileName, string ContentType, byte[] Content);
