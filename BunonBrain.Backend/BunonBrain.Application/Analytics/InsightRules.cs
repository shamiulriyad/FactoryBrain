namespace BunonBrain.Application.Analytics;

// Rule-based operational insights. Each rule looks at aggregated facts and
// says what was observed, from which data, how reliable the data is, and
// what to look into. Rules never claim a root cause: a line below target is
// not evidence that its workers underperform; a machine with downtime may be
// waiting for material. Thresholds are constants documented in ANALYTICS.md.

public static class InsightTypes
{
    public const string LineBelowTarget = "LINE_BELOW_TARGET";
    public const string HighMachineDowntime = "HIGH_MACHINE_DOWNTIME";
    public const string RepeatedMachineErrors = "REPEATED_MACHINE_ERRORS";
    public const string CounterInactivity = "COUNTER_INACTIVITY";
    public const string AttendanceShortage = "ATTENDANCE_SHORTAGE";
    public const string AssignmentGap = "ASSIGNMENT_GAP";
    public const string HighOvertime = "HIGH_OVERTIME";
    public const string LowDataCoverage = "LOW_DATA_COVERAGE";
    public const string RepeatedAlerts = "REPEATED_ALERTS";
    public static readonly IReadOnlyList<string> All = new[]
    {
        LineBelowTarget, HighMachineDowntime, RepeatedMachineErrors, CounterInactivity, AttendanceShortage, AssignmentGap, HighOvertime, LowDataCoverage, RepeatedAlerts,
    };
}

public static class InsightSeverities
{
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
}

/// <summary>How far the evidence can be trusted.</summary>
public static class Reliability
{
    public const string High = "HIGH";
    public const string Medium = "MEDIUM";
    public const string Low = "LOW";
}

public static class InsightThresholds
{
    /// <summary>Line output below this share of its plan.</summary>
    public const double BelowTargetPct = 85;
    public const double FarBelowTargetPct = 60;
    /// <summary>Planned output must be at least this to judge a line.</summary>
    public const double MinPlannedPieces = 20;
    /// <summary>Confirmed downtime above this share of confirmed-state time…</summary>
    public const double HighDowntimePct = 25;
    /// <summary>…over at least this much confirmed-state time.</summary>
    public const double MinKnownHours = 1;
    public const int RepeatedErrors = 3;
    /// <summary>Running this long without a single counted piece (machines with a counter).</summary>
    public const double CounterSilentRunningHours = 1;
    /// <summary>Scheduled workers who checked in below this share…</summary>
    public const double ShortageAttendancePct = 85;
    /// <summary>…with at least this many scheduled worker-days.</summary>
    public const int MinScheduled = 5;
    /// <summary>Present workers below planned operators by this share.</summary>
    public const double StaffingShortagePct = 90;
    public const int MinEmptyWorkstations = 1;
    /// <summary>Actual overtime above this share of attendance minutes, or above approved.</summary>
    public const double HighOvertimeSharePct = 15;
    public const double OvertimeOverApprovedPct = 110;
    /// <summary>Machine data coverage below this.</summary>
    public const double LowCoveragePct = 60;
    public const double LowDeviceOnlinePct = 70;
    public const int RepeatedAlertOccurrences = 5;
}

public sealed record LineFacts(string LineId, string LineName, double Planned, double Actual, int MachinesWithCounter, int Machines,
    int ScheduledStarted, int ScheduledPresent, int PlannedOperators, int PresentWorkers, int EmptyWorkstations, int ActiveWorkstations,
    int AttendanceMinutes, int ActualOvertimeMinutes, int ApprovedOvertimeMinutes);

public sealed record MachineFacts(Guid MachineId, string Code, string? LineId, string? LineName, MachineAvailability Time, int Errors, bool HasCounter,
    long Pieces);

public sealed record AlertFacts(string AlertType, string Title, string? Subject, int Alerts, int Occurrences);

public sealed record CoverageFacts(int Devices, int DevicesOnline, int ActiveCameras, int ReportingCameras, double MachineCoveragePct, int MonitoredMachines);

public sealed record InsightFacts(Guid FactoryId, string FactoryCode, AnalyticsPeriod Period, string Source, IReadOnlyList<LineFacts> Lines,
    IReadOnlyList<MachineFacts> Machines, IReadOnlyList<AlertFacts> Alerts, CoverageFacts? Coverage, IReadOnlyList<string> SectionsAvailable);

public sealed record InsightEvidence(string Label, string Value, string? MetricId = null);

public sealed record OperationalInsight(
    string Id,
    string Type,
    string Title,
    string Severity,
    string Reliability,
    string ReliabilityReason,
    Guid FactoryId,
    string FactoryCode,
    string? LineId,
    string? LineName,
    Guid? MachineId,
    string? MachineCode,
    DateTimeOffset DetectedAt,
    IReadOnlyList<InsightEvidence> Evidence,
    IReadOnlyList<string> DataSources,
    string Recommendation,
    string Caveat,
    string Source);

public static class InsightEngine
{
    private static string F(double v, int digits = 0) => v.ToString(digits == 0 ? "0" : "0." + new string('0', digits), System.Globalization.CultureInfo.InvariantCulture);

    public static IReadOnlyList<OperationalInsight> Evaluate(InsightFacts f, DateTimeOffset now)
    {
        var list = new List<OperationalInsight>();
        var period = $"{f.Period.From:yyyy-MM-dd} – {f.Period.To:yyyy-MM-dd}";
        OperationalInsight New(string type, string title, string severity, string reliability, string why, string? lineId, string? lineName, Guid? machineId,
            string? machineCode, IReadOnlyList<InsightEvidence> evidence, IReadOnlyList<string> sources, string recommendation, string caveat) =>
            new($"{type}:{lineId ?? machineId?.ToString() ?? "factory"}:{f.Period.From:yyyyMMdd}-{f.Period.To:yyyyMMdd}", type, title, severity, reliability, why,
                f.FactoryId, f.FactoryCode, lineId, lineName, machineId, machineCode, now, evidence, sources, recommendation, caveat, f.Source);

        foreach (var l in f.Lines)
        {
            // Output below an existing plan.
            if (l.Planned >= InsightThresholds.MinPlannedPieces)
            {
                var pct = l.Actual / l.Planned * 100;
                if (pct < InsightThresholds.BelowTargetPct)
                {
                    var counted = l.Machines == 0 ? 0 : (double)l.MachinesWithCounter / l.Machines;
                    var (rel, why) = l.MachinesWithCounter == 0
                        ? (Reliability.Low, "No machine on this line has a production counter: counted output may be incomplete.")
                        : counted < 0.8
                            ? (Reliability.Medium, $"Only {l.MachinesWithCounter} of {l.Machines} machines on the line report counts.")
                            : (Reliability.High, "Counts come from counters on most machines of the line.");
                    list.Add(New(InsightTypes.LineBelowTarget, $"{l.LineName}: output {F(pct)} % of plan", pct < InsightThresholds.FarBelowTargetPct ? InsightSeverities.High : InsightSeverities.Medium,
                        rel, why, l.LineId, l.LineName, null, null,
                        new[]
                        {
                            new InsightEvidence("Counted output", $"{F(l.Actual)} pieces", "production.pieces"),
                            new InsightEvidence("Planned output", $"{F(l.Planned)} pieces", "production.planned"),
                            new InsightEvidence("Period", period),
                        },
                        new[] { "production_counter_events", "production_corrections", "production_plans" },
                        "Check the line's machine downtime, material supply, style changeover and counter placement for this period before drawing conclusions.",
                        "Lower output alone doesn't show why: it is not evidence that the line's workers underperformed."));
                }
            }
            // Attendance below schedule.
            if (l.ScheduledStarted >= InsightThresholds.MinScheduled)
            {
                var pct = (double)l.ScheduledPresent / l.ScheduledStarted * 100;
                if (pct < InsightThresholds.ShortageAttendancePct)
                    list.Add(New(InsightTypes.AttendanceShortage, $"{l.LineName}: {F(pct)} % of scheduled workers checked in", pct < 70 ? InsightSeverities.High : InsightSeverities.Medium,
                        Reliability.High, "From attendance punches and shift assignments.", l.LineId, l.LineName, null, null,
                        new[]
                        {
                            new InsightEvidence("Scheduled (shift started)", $"{l.ScheduledStarted} worker-days", "workers.scheduled"),
                            new InsightEvidence("Checked in", $"{l.ScheduledPresent} worker-days", "attendance.present"),
                            new InsightEvidence("Period", period),
                        },
                        new[] { "attendance_sessions", "worker_shift_assignments", "shifts" },
                        "Check approved leave and missing punches (attendance review) before planning cover.",
                        "Leave not yet approved still counts as absence; approved leave and holidays are excluded."));
            }
            // Fewer present than planned operators.
            if (l.PlannedOperators > 0 && l.PresentWorkers < l.PlannedOperators * InsightThresholds.StaffingShortagePct / 100)
                list.Add(New(InsightTypes.AttendanceShortage, $"{l.LineName}: {l.PresentWorkers} present for {l.PlannedOperators} planned operators", InsightSeverities.Medium,
                    Reliability.Medium, "Planned operators come from the active production plan; present workers from today's punches on the line.", l.LineId, l.LineName, null, null,
                    new[]
                    {
                        new InsightEvidence("Planned operators", $"{l.PlannedOperators}", "staffing.required"),
                        new InsightEvidence("Present on the line", $"{l.PresentWorkers}", "attendance.present"),
                    },
                    new[] { "production_plans", "attendance_sessions" },
                    "Check whether operators were moved from another line or are on approved leave.",
                    "A worker's line comes from their attendance session; transfers during the day may not be reflected."));
            // Workstations without anyone assigned.
            if (l.EmptyWorkstations >= InsightThresholds.MinEmptyWorkstations && l.ActiveWorkstations > 0)
                list.Add(New(InsightTypes.AssignmentGap, $"{l.LineName}: {l.EmptyWorkstations} of {l.ActiveWorkstations} workstations unassigned", InsightSeverities.Low,
                    Reliability.High, "From the workstation assignment register.", l.LineId, l.LineName, null, null,
                    new[] { new InsightEvidence("Workstations without an assignment", $"{l.EmptyWorkstations} / {l.ActiveWorkstations}", "assignment.empty_workstations") },
                    new[] { "workstations", "workstation_assignments" },
                    "Check whether these workstations are in use; assign workers or deactivate unused workstations.",
                    "An assignment is a schedule, not proof of presence."));
            // Overtime.
            var share = l.AttendanceMinutes > 0 ? (double)l.ActualOvertimeMinutes / l.AttendanceMinutes * 100 : 0;
            var overApproved = l.ApprovedOvertimeMinutes > 0 && (double)l.ActualOvertimeMinutes / l.ApprovedOvertimeMinutes * 100 > InsightThresholds.OvertimeOverApprovedPct;
            if ((l.AttendanceMinutes > 0 && share > InsightThresholds.HighOvertimeSharePct) || overApproved)
                list.Add(New(InsightTypes.HighOvertime, $"{l.LineName}: high overtime", InsightSeverities.Medium, Reliability.High,
                    "From overtime requests and attendance punches.", l.LineId, l.LineName, null, null,
                    new[]
                    {
                        new InsightEvidence("Actual overtime", $"{F(l.ActualOvertimeMinutes / 60.0, 1)} h", "overtime.actual"),
                        new InsightEvidence("Approved overtime", $"{F(l.ApprovedOvertimeMinutes / 60.0, 1)} h", "overtime.approved"),
                        new InsightEvidence("Share of attendance time", $"{F(share, 1)} %", "attendance.minutes"),
                    },
                    new[] { "overtime_requests", "attendance_sessions" },
                    "Review the line's plan and staffing against the hours worked; check overtime limits in the attendance policy.",
                    "Overtime limits here are warnings for review, not legal advice."));
        }

        foreach (var m in f.Machines)
        {
            var known = m.Time.KnownSeconds / 3600.0;
            if (known >= InsightThresholds.MinKnownHours && m.Time.DowntimePct is { } dp && dp > InsightThresholds.HighDowntimePct)
                list.Add(New(InsightTypes.HighMachineDowntime, $"{m.Code}: {F(dp)} % confirmed downtime", dp > 50 ? InsightSeverities.High : InsightSeverities.Medium,
                    m.Time.CoveragePct is >= 80 ? Reliability.High : Reliability.Medium,
                    $"Sensor-confirmed state for {F(m.Time.CoveragePct ?? 0)} % of the period; offline / unknown time is not counted as downtime.",
                    m.LineId, m.LineName, m.MachineId, m.Code,
                    new[]
                    {
                        new InsightEvidence("Stopped + error", $"{F(m.Time.DowntimeSeconds / 3600.0, 1)} h", "machine.downtime"),
                        new InsightEvidence("Confirmed-state time", $"{F(known, 1)} h", "machine.known_time"),
                        new InsightEvidence("Errors", $"{m.Errors}", "machine.errors"),
                    },
                    new[] { "machine_state_transitions" },
                    "Look at the machine's state history and alerts; check maintenance records and material or style changeovers.",
                    "A stopped machine may be waiting for material or an operator break; it says nothing about who worked."));
            if (m.Errors >= InsightThresholds.RepeatedErrors)
                list.Add(New(InsightTypes.RepeatedMachineErrors, $"{m.Code}: {m.Errors} errors", m.Errors >= 2 * InsightThresholds.RepeatedErrors ? InsightSeverities.High : InsightSeverities.Medium,
                    Reliability.High, "Errors reported by the machine sensor.", m.LineId, m.LineName, m.MachineId, m.Code,
                    new[] { new InsightEvidence("Transitions into ERROR", $"{m.Errors}", "machine.errors"), new InsightEvidence("Period", period) },
                    new[] { "machine_state_transitions", "machine_telemetry" },
                    "Check the error codes in the machine's telemetry and schedule maintenance.", "Repeated sensor errors can also come from a faulty sensor."));
            if (m.HasCounter && m.Pieces == 0 && m.Time.RunningSeconds / 3600.0 >= InsightThresholds.CounterSilentRunningHours)
                list.Add(New(InsightTypes.CounterInactivity, $"{m.Code}: running {F(m.Time.RunningSeconds / 3600.0, 1)} h without counted pieces", InsightSeverities.Medium,
                    Reliability.Medium, "Running time from the machine sensor; no counter event added pieces.", m.LineId, m.LineName, m.MachineId, m.Code,
                    new[]
                    {
                        new InsightEvidence("Running", $"{F(m.Time.RunningSeconds / 3600.0, 1)} h", "machine.running"),
                        new InsightEvidence("Counted pieces", "0", "production.pieces"),
                    },
                    new[] { "machine_state_transitions", "production_counter_events" },
                    "Check the counter's connection and placement, and its counting rule (cycles vs pieces).",
                    "The machine may have run without producing (setup, testing, idling at power)."));
        }

        foreach (var a in f.Alerts.Where(a => a.Occurrences >= InsightThresholds.RepeatedAlertOccurrences))
            list.Add(New(InsightTypes.RepeatedAlerts, $"{a.Title}: {a.Occurrences} occurrences", a.Occurrences >= 3 * InsightThresholds.RepeatedAlertOccurrences ? InsightSeverities.High : InsightSeverities.Medium,
                Reliability.High, "From the monitoring alert history.", null, null, null, a.Subject,
                new[] { new InsightEvidence("Alerts", $"{a.Alerts}"), new InsightEvidence("Occurrences", $"{a.Occurrences}", "alerts.open"), new InsightEvidence("Type", a.AlertType) },
                new[] { "monitoring_alerts", "monitoring_alert_actions" },
                "Find out why the condition keeps coming back rather than resolving each occurrence.", "Repeated alerts can also mean an alert threshold needs tuning."));

        if (f.Coverage is { } c)
        {
            var devicesPct = c.Devices > 0 ? (double)c.DevicesOnline / c.Devices * 100 : 100;
            var evidence = new List<InsightEvidence>();
            if (c.Devices > 0 && devicesPct < InsightThresholds.LowDeviceOnlinePct) evidence.Add(new("Devices online", $"{c.DevicesOnline} / {c.Devices}", "devices.online"));
            if (c.MonitoredMachines > 0 && c.MachineCoveragePct < InsightThresholds.LowCoveragePct) evidence.Add(new("Machine data coverage", $"{F(c.MachineCoveragePct)} %", "machine.data_gap"));
            if (c.ActiveCameras > 0 && c.ReportingCameras < c.ActiveCameras) evidence.Add(new("Cameras reporting", $"{c.ReportingCameras} / {c.ActiveCameras}", "cameras.coverage"));
            if (evidence.Count > 0)
                list.Add(New(InsightTypes.LowDataCoverage, "Low sensor or camera data coverage", evidence.Count > 1 ? InsightSeverities.Medium : InsightSeverities.Low,
                    Reliability.High, "From device health and the period's machine and camera data.", null, null, null, null, evidence,
                    new[] { "devices", "machine_state_transitions", "monitoring_events", "cameras" },
                    "Check the devices and cameras without data before relying on this period's machine or camera figures.",
                    "Other insights for this period are less reliable where coverage is low."));
        }

        return list.OrderByDescending(i => i.Severity switch { InsightSeverities.High => 3, InsightSeverities.Medium => 2, _ => 1 }).ThenBy(i => i.Type).ToList();
    }
}
