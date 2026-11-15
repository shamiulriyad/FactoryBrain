using System.Text;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Iot;
using BunonBrain.Infrastructure.Analytics;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Analytics calculations without a database: periods and time-zone
/// boundaries, KPIs with missing denominators, machine uptime / downtime
/// (missing data is never downtime), attendance aggregation, overtime, the
/// insight rules (evidence, reliability, no blame), AI confidence bands, the
/// metric catalog and the CSV / PDF report writers.
/// </summary>
public sealed class AnalyticsUnitTests
{
    private static readonly TimeZoneInfo Dhaka = TimeZoneInfo.CreateCustomTimeZone("Asia/Dhaka-test", TimeSpan.FromHours(6), "Dhaka", "Dhaka");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 6, 0, 0, TimeSpan.Zero); // 12:00 in Dhaka

    // ─── Periods ───────────────────────────────────────────────────────────

    [Fact]
    public void Days_are_factory_local_and_never_reach_into_the_future()
    {
        var today = AnalyticsPeriod.Resolve(null, null, Dhaka, Now);
        Assert.Equal((new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15)), (today.From, today.To));
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.Zero), today.FromUtc);   // local midnight = 18:00 UTC the day before
        Assert.Equal(Now, today.ToUtc);                                                          // the running day ends now
        var past = AnalyticsPeriod.Resolve(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2), Dhaka, Now);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero), past.ToUtc);      // end of 2 Nov local
        Assert.Equal(2, past.Days);
        var future = AnalyticsPeriod.Resolve(null, new DateOnly(2026, 12, 31), Dhaka, Now);
        Assert.Equal(new DateOnly(2026, 11, 15), future.To);
        var long_ = AnalyticsPeriod.Resolve(new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 15), Dhaka, Now);
        Assert.True(long_.Truncated);
        Assert.Equal(AnalyticsPeriod.MaxDays, long_.Days);
        var swapped = AnalyticsPeriod.Resolve(new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 10), Dhaka, Now);
        Assert.Equal(swapped.From, swapped.To);
    }

    // ─── KPIs ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_ratio_without_a_denominator_is_unavailable_not_zero()
    {
        var none = Kpi.Ratio("attendance.rate", 0, 0);
        Assert.Null(none.Value);
        Assert.Equal(Availability.NoData, none.Availability);
        var some = Kpi.Ratio("attendance.rate", 45, 50);
        Assert.Equal((90.0, 45.0, 50.0, Availability.Ok), (some.Value!.Value, some.Numerator!.Value, some.Denominator!.Value, some.Availability));
        Assert.Equal(MetricKinds.Derived, some.Kind);
        Assert.Equal(Availability.NotPermitted, Kpi.Hidden("production.pieces", "production.view").Availability);
        Assert.Throws<KeyNotFoundException>(() => Kpi.Count("no.such.metric", 1));
    }

    [Fact]
    public void Every_metric_is_fully_defined()
    {
        Assert.NotEmpty(MetricCatalog.All);
        Assert.Equal(MetricCatalog.All.Count, MetricCatalog.All.Select(m => m.Id).Distinct().Count());
        foreach (var m in MetricCatalog.All)
        {
            Assert.Contains(m.Kind, new[] { MetricKinds.Measured, MetricKinds.Derived, MetricKinds.AiEstimate });
            Assert.False(string.IsNullOrWhiteSpace(m.Formula), m.Id);
            Assert.False(string.IsNullOrWhiteSpace(m.Window), m.Id);
            Assert.False(string.IsNullOrWhiteSpace(m.MissingData), m.Id);
            Assert.NotEmpty(m.Sources);
        }
        // Everything from the camera AI is labelled as an estimate (except identity counts, derived from verified associations).
        Assert.All(MetricCatalog.All.Where(m => m.Id.StartsWith("ai.") && !m.Id.StartsWith("ai.verified")), m => Assert.Equal(MetricKinds.AiEstimate, m.Kind));
        Assert.Contains("never downtime", MetricCatalog.Get("machine.uptime").Excludes);
        Assert.Contains("never count as attendance", MetricCatalog.Get("attendance.present").Excludes);
    }

    // ─── Machines ──────────────────────────────────────────────────────────

    [Fact]
    public void Uptime_uses_confirmed_state_time_and_missing_data_is_a_gap_not_downtime()
    {
        var a = new MachineAvailability(RunningSeconds: 6 * 3600, IdleSeconds: 3600, StoppedSeconds: 3600, ErrorSeconds: 0, MaintenanceSeconds: 3600,
            OfflineSeconds: 2 * 3600, UnknownSeconds: 0);
        Assert.Equal(8 * 3600, a.KnownSeconds);
        Assert.Equal(75.0, a.UptimePct);                 // 6 / 8
        Assert.Equal(12.5, a.DowntimePct);               // stopped only, not offline
        Assert.Equal(2 * 3600, a.GapSeconds);
        Assert.Equal(80.0, a.CoveragePct);               // 8 known / (11 − 1 maintenance)
        var onlyOffline = new MachineAvailability(0, 0, 0, 0, 0, 3600, 3600);
        Assert.Null(onlyOffline.UptimePct);
        Assert.Null(onlyOffline.DowntimePct);
        Assert.Equal(0.0, onlyOffline.CoveragePct);

        // From transitions: an offline period inside running time.
        var t0 = Now.AddHours(-4);
        var totals = IotMetrics.Durations(MachineStates.Running, new[] { (t0.AddHours(1), MachineStates.Offline), (t0.AddHours(2), MachineStates.Running) }, t0, Now);
        var m = MachineAvailability.From(totals);
        Assert.Equal((3 * 3600.0, 3600.0, 0.0), (m.RunningSeconds, m.OfflineSeconds, m.DowntimeSeconds));
        Assert.Equal(100.0, m.UptimePct);
        Assert.Equal(MachineAvailability.Sum(new[] { a, onlyOffline }).OfflineSeconds, 3 * 3600);
    }

    // ─── Attendance ────────────────────────────────────────────────────────

    private static readonly DateOnly D = new(2026, 11, 15);
    private static readonly Guid Shift = Guid.NewGuid();
    private static ScheduledDay Sched(Guid w, int startHourLocal = 8, int grace = 10) =>
        new(w, D, Shift, new DateTimeOffset(D.ToDateTime(new TimeOnly(startHourLocal, 0)), TimeSpan.FromHours(6)).ToUniversalTime(), grace);
    private static SessionFact Session(Guid w, bool checkedIn = true, bool late = false, int minutes = 240, int breaks = 30) =>
        new(w, D, checkedIn, late, false, minutes, breaks, 0, 0, false);

    [Fact]
    public void Attendance_counts_each_worker_once_and_keeps_unscheduled_check_ins_out_of_the_rate()
    {
        var (a, b, c, d, e) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var schedule = new[] { Sched(a), Sched(b), Sched(c), Sched(d, startHourLocal: 20) };            // d's shift hasn't started at 12:00
        var sessions = new[]
        {
            Session(a), Session(a, minutes: 60),          // two sessions, one worker
            Session(b, late: true),
            Session(e),                                   // checked in without a schedule
        };
        var day = AttendanceAggregation.Daily(new[] { D }, schedule, sessions, Now).Single();
        Assert.Equal(4, day.Scheduled);
        Assert.Equal(3, day.ScheduledStarted);
        Assert.Equal(2, day.ScheduledPresent);
        Assert.Equal(3, day.Present);                      // a, b, e
        Assert.Equal(1, day.Unscheduled);
        Assert.Equal(1, day.Absent);                       // c (d's shift hasn't started)
        Assert.Equal(1, day.Late);
        Assert.Equal(66.7, day.RatePct);                   // 2 of 3 started
        Assert.Equal(240 + 60 + 240 + 240, day.AttendanceMinutes);

        var empty = AttendanceAggregation.Daily(new[] { D }, Array.Empty<ScheduledDay>(), Array.Empty<SessionFact>(), Now).Single();
        Assert.Null(empty.RatePct);
        Assert.Equal(0, empty.Absent);
    }

    [Fact]
    public void Schedules_follow_shift_working_days_and_assignment_dates()
    {
        var w = Guid.NewGuid();
        var assignments = new[] { new ShiftAssignmentFact(w, Shift, new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 20), new TimeOnly(8, 0), 10, 0b0111111) };
        var dates = Enumerable.Range(0, 10).Select(i => new DateOnly(2026, 11, 12).AddDays(i));
        var s = AttendanceAggregation.Schedule(dates, assignments, Dhaka);
        // 14 Nov (Sat, bit 6 off) excluded; 15–20 Nov (Sun–Fri) scheduled; nothing outside the assignment.
        Assert.Equal(new[] { 15, 16, 17, 18, 19, 20 }, s.Select(x => x.Date.Day));
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 2, 0, 0, TimeSpan.Zero), s[0].ShiftStartUtc);
    }

    [Fact]
    public void Overtime_compares_actual_with_approved_and_ignores_unapproved_requests()
    {
        var w = Guid.NewGuid();
        var rows = new[]
        {
            new OvertimeFact(w, "l1", D, "Completed", 120, 120, 150),
            new OvertimeFact(w, "l1", D, "Approved", 60, 60, null),
            new OvertimeFact(Guid.NewGuid(), "l1", D, "PendingApproval", 90, null, null),
            new OvertimeFact(Guid.NewGuid(), "l1", D, "Rejected", 90, null, 30),
        };
        var o = OvertimeSummary.Of(rows);
        Assert.Equal((4, 2, 1, 1), (o.Requests, o.Approved, o.Pending, o.Rejected));
        Assert.Equal((180, 150, 1), (o.ApprovedMinutes, o.ActualMinutes, o.WorkersWithOvertime));
        Assert.Equal(83.3, o.ActualVsApprovedPct);
        Assert.Null(OvertimeSummary.Of(Array.Empty<OvertimeFact>()).ActualVsApprovedPct);
    }

    // ─── Insights ──────────────────────────────────────────────────────────

    private static InsightFacts Facts(LineFacts[]? lines = null, MachineFacts[]? machines = null, AlertFacts[]? alerts = null, CoverageFacts? coverage = null) =>
        new(Guid.NewGuid(), "F1", AnalyticsPeriod.Resolve(D, D, Dhaka, Now), "REAL", lines ?? Array.Empty<LineFacts>(), machines ?? Array.Empty<MachineFacts>(),
            alerts ?? Array.Empty<AlertFacts>(), coverage, Array.Empty<string>());

    private static LineFacts Line(double planned = 0, double actual = 0, int withCounter = 4, int machines = 4, int scheduled = 0, int present = 0, int plannedOps = 0,
        int presentToday = 0, int empty = 0, int stations = 0, int attendanceMin = 0, int otActual = 0, int otApproved = 0) =>
        new("l1", "Line 1", planned, actual, withCounter, machines, scheduled, present, plannedOps, presentToday, empty, stations, attendanceMin, otActual, otApproved);

    [Fact]
    public void A_line_below_its_plan_is_reported_with_evidence_reliability_and_no_blame()
    {
        var i = InsightEngine.Evaluate(Facts(new[] { Line(planned: 1000, actual: 500) }), Now).Single();
        Assert.Equal(InsightTypes.LineBelowTarget, i.Type);
        Assert.Equal(InsightSeverities.High, i.Severity);           // 50 % < 60 %
        Assert.Equal(Reliability.High, i.Reliability);
        Assert.Contains(i.Evidence, e => e.MetricId == "production.planned");
        Assert.Contains("production_plans", i.DataSources);
        Assert.Contains("not evidence that the line's workers underperformed", i.Caveat);
        Assert.DoesNotContain("lazy", (i.Recommendation + i.Title).ToLowerInvariant());
        // Without counters the same finding is low-reliability; at 90 % or without a plan there is none.
        Assert.Equal(Reliability.Low, InsightEngine.Evaluate(Facts(new[] { Line(planned: 1000, actual: 500, withCounter: 0) }), Now).Single().Reliability);
        Assert.Empty(InsightEngine.Evaluate(Facts(new[] { Line(planned: 1000, actual: 900) }), Now));
        Assert.Empty(InsightEngine.Evaluate(Facts(new[] { Line(planned: 0, actual: 0) }), Now));
    }

    [Fact]
    public void Machine_rules_ignore_offline_time_and_need_enough_confirmed_data()
    {
        MachineFacts M(MachineAvailability t, int errors = 0, bool counter = false, long pieces = 0) => new(Guid.NewGuid(), "M-1", "l1", "Line 1", t, errors, counter, pieces);
        var down = new MachineAvailability(3600, 0, 3 * 3600, 0, 0, 0, 0);
        Assert.Contains(InsightEngine.Evaluate(Facts(machines: new[] { M(down) }), Now), x => x.Type == InsightTypes.HighMachineDowntime);
        // Lots of offline time is a coverage issue, not downtime.
        var offline = new MachineAvailability(3600, 0, 0, 0, 0, 20 * 3600, 0);
        Assert.DoesNotContain(InsightEngine.Evaluate(Facts(machines: new[] { M(offline) }), Now), x => x.Type == InsightTypes.HighMachineDowntime);
        // Too little confirmed data.
        Assert.Empty(InsightEngine.Evaluate(Facts(machines: new[] { M(new MachineAvailability(600, 0, 1200, 0, 0, 0, 0)) }), Now));
        Assert.Contains(InsightEngine.Evaluate(Facts(machines: new[] { M(new MachineAvailability(8 * 3600, 0, 0, 0, 0, 0, 0), errors: 4) }), Now),
            x => x.Type == InsightTypes.RepeatedMachineErrors);
        Assert.Contains(InsightEngine.Evaluate(Facts(machines: new[] { M(new MachineAvailability(2 * 3600, 0, 0, 0, 0, 0, 0), counter: true) }), Now),
            x => x.Type == InsightTypes.CounterInactivity);
        Assert.Empty(InsightEngine.Evaluate(Facts(machines: new[] { M(new MachineAvailability(2 * 3600, 0, 0, 0, 0, 0, 0), counter: true, pieces: 40) }), Now));
    }

    [Fact]
    public void Workforce_alert_and_coverage_rules()
    {
        var shortage = InsightEngine.Evaluate(Facts(new[] { Line(scheduled: 20, present: 12) }), Now);
        Assert.Contains(shortage, x => x.Type == InsightTypes.AttendanceShortage && x.Caveat.Contains("Leave"));
        Assert.Empty(InsightEngine.Evaluate(Facts(new[] { Line(scheduled: 3, present: 0) }), Now));           // too few to judge
        Assert.Contains(InsightEngine.Evaluate(Facts(new[] { Line(plannedOps: 30, presentToday: 20) }), Now), x => x.Type == InsightTypes.AttendanceShortage);
        Assert.Contains(InsightEngine.Evaluate(Facts(new[] { Line(empty: 3, stations: 30) }), Now), x => x.Type == InsightTypes.AssignmentGap);
        Assert.Contains(InsightEngine.Evaluate(Facts(new[] { Line(attendanceMin: 1000, otActual: 300, otApproved: 300) }), Now), x => x.Type == InsightTypes.HighOvertime);
        Assert.Contains(InsightEngine.Evaluate(Facts(new[] { Line(attendanceMin: 100_000, otActual: 200, otApproved: 100) }), Now), x => x.Type == InsightTypes.HighOvertime);
        Assert.Contains(InsightEngine.Evaluate(Facts(alerts: new[] { new AlertFacts("MACHINE_ERROR", "Machine M-1 reported an error", null, 1, 7) }), Now),
            x => x.Type == InsightTypes.RepeatedAlerts);
        var cov = InsightEngine.Evaluate(Facts(coverage: new CoverageFacts(10, 5, 4, 2, 40, 3)), Now).Single();
        Assert.Equal(InsightTypes.LowDataCoverage, cov.Type);
        Assert.Equal(3, cov.Evidence.Count);
        Assert.Empty(InsightEngine.Evaluate(Facts(coverage: new CoverageFacts(10, 10, 4, 4, 95, 3)), Now));
        // Every insight says where its data came from and what to look into.
        foreach (var x in shortage) { Assert.NotEmpty(x.DataSources); Assert.False(string.IsNullOrWhiteSpace(x.Recommendation)); }
    }

    [Fact]
    public void Confidence_bands_cover_zero_to_one()
    {
        Assert.Equal("< 0.5", ConfidenceBands.Of(0));
        Assert.Equal("0.5 – 0.7", ConfidenceBands.Of(0.5));
        Assert.Equal("0.7 – 0.9", ConfidenceBands.Of(0.89));
        Assert.Equal("≥ 0.9", ConfidenceBands.Of(1.0));
    }

    // ─── Reports ───────────────────────────────────────────────────────────

    private static ReportDocument Doc(string source = "REAL") => new(ReportTypes.Production, "Production report", Now, "manager@example.test", "F1 · Factory one",
        D.AddDays(-6), D, "Asia/Dhaka", source, new[] { new ReportFilter("Line", "Line 1") },
        new[]
        {
            new ReportSection("Production", new[] { Kpi.Count("production.pieces", 1200), Kpi.Ratio("production.achievement", 0, 0) }, new[]
            {
                new ReportTable("By line", new[] { "Name", "Pieces" }, new[] { (IReadOnlyList<object?>)new object?[] { "=HYPERLINK(\"x\")", 1200L }, new object?[] { "Line \"2\", east", -5 } }),
            }),
        },
        new[] { "Only 3 of 4 machines report counts." }, new[] { MetricCatalog.Get("production.pieces") });

    [Fact]
    public void Csv_reports_carry_scope_filters_warnings_and_definitions_and_are_safe_for_spreadsheets()
    {
        var csv = ReportCsv.Write(Doc());
        Assert.Contains("Production report", csv);
        Assert.Contains("F1 · Factory one", csv);
        Assert.Contains("2026-11-09 to 2026-11-15 (Asia/Dhaka)", csv);
        Assert.Contains("Filter: Line,Line 1", csv);
        Assert.Contains("Warning,Only 3 of 4 machines report counts.", csv);
        Assert.Contains("Metric definitions", csv);
        Assert.Contains("REAL data", csv);
        Assert.Contains("'=HYPERLINK", csv);                // formula injection guard
        Assert.Contains("\"Line \"\"2\"\", east\",-5", csv); // quoting; numbers stay numbers
        Assert.Contains("NO_DATA", csv);                     // unavailable, not 0
        Assert.Contains("SIMULATED data", ReportCsv.Write(Doc("SIMULATED")));
        Assert.EndsWith("-SIMULATED.csv", ReportCsv.FileName(Doc("SIMULATED"), "csv"));
    }

    [Fact]
    public void Pdf_reports_are_valid_pdfs_and_replace_characters_the_standard_fonts_cannot_show()
    {
        var bytes = PdfReportWriter.Write(Doc("SIMULATED"));
        Assert.True(bytes.Length > 1000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("a - b -> c >= 2 x 3", PdfReportWriter.Clean("a — b → c ≥ 2 × 3"));
        Assert.Equal("?????", PdfReportWriter.Clean("বাংলা"));
        // A long report spans pages without failing.
        var rows = Enumerable.Range(0, 400).Select(i => (IReadOnlyList<object?>)new object?[] { $"Machine {i}", i * 1.5, "a fairly long note that wraps over the column width" }).ToList();
        var big = Doc() with { Sections = new[] { new ReportSection("Big", Array.Empty<Kpi>(), new[] { new ReportTable("Rows", new[] { "Name", "Hours", "Note" }, rows) }) } };
        Assert.Equal("%PDF", Encoding.ASCII.GetString(PdfReportWriter.Write(big), 0, 4));
    }

    [Fact]
    public void Report_types_are_known_and_operations_reports_have_fixed_lengths()
    {
        Assert.Equal(9, ReportTypes.All.Count);
        Assert.Equal(ReportTypes.WeeklyOperations, ReportTypes.Parse("Weekly-Operations"));
        Assert.Null(ReportTypes.Parse("salary"));
        Assert.Equal((1, 7, 30), (ReportTypes.FixedDays(ReportTypes.DailyOperations)!.Value, ReportTypes.FixedDays(ReportTypes.WeeklyOperations)!.Value,
            ReportTypes.FixedDays(ReportTypes.MonthlyOperations)!.Value));
        Assert.Null(ReportTypes.FixedDays(ReportTypes.Production));
    }
}
