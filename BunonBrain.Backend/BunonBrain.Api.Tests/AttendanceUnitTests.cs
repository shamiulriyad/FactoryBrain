using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Attendance;
using BunonBrain.Application.Validation;
using P = BunonBrain.Application.Access.Permissions;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>The attendance calculation engine and rules, without a database.</summary>
public sealed class AttendanceUnitTests
{
    private static readonly TimeZoneInfo Dhaka = AttendanceRules.ResolveTimeZone("Asia/Dhaka")!;
    private static readonly DateOnly Day = new(2026, 11, 10); // a Tuesday

    private static DateTimeOffset At(DateOnly d, int h, int m = 0) => new(d.ToDateTime(new TimeOnly(h, m)), TimeSpan.FromHours(6));

    private static ShiftSpec Shift(string start, string end, int lateGrace = 10, int earlyGrace = 5, params BreakSpec[] breaks) =>
        new(Guid.NewGuid(), TimeOnly.Parse(start), TimeOnly.Parse(end), lateGrace, earlyGrace, 0b1111111, breaks);

    // ─── Shifts and time zones ─────────────────────────────────────────────

    [Fact]
    public void Dhaka_is_utc_plus_six_and_local_times_become_utc_instants()
    {
        var start = AttendanceCalculator.ToUtc(Day, new TimeOnly(8, 0), Dhaka);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 2, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(TimeSpan.Zero, start.Offset);
        // 20:30 UTC on the 9th is already the 10th in Dhaka.
        Assert.Equal(Day, AttendanceCalculator.LocalDate(new DateTimeOffset(2026, 11, 9, 20, 30, 0, TimeSpan.Zero), Dhaka));
        Assert.Equal(Day.AddDays(-1), AttendanceCalculator.LocalDate(new DateTimeOffset(2026, 11, 9, 17, 59, 0, TimeSpan.Zero), Dhaka));
    }

    [Theory]
    [InlineData("08:00", "17:00", false, 540)]
    [InlineData("17:00", "01:00", true, 480)]
    [InlineData("22:00", "06:00", true, 480)]
    [InlineData("00:00", "08:00", false, 480)]
    public void Overnight_shifts_end_the_next_day(string start, string end, bool overnight, int minutes)
    {
        var s = Shift(start, end);
        Assert.Equal(overnight, s.IsOvernight);
        Assert.Equal(minutes, s.ScheduledMinutes);
        var occ = AttendanceCalculator.Occurrence(Day, s, Dhaka);
        Assert.Equal(minutes, (int)(occ.End - occ.Start).TotalMinutes);
        Assert.Equal(overnight ? Day.AddDays(1) : Day, AttendanceCalculator.LocalDate(occ.End, Dhaka));
    }

    [Fact]
    public void A_punch_belongs_to_the_shift_occurrence_around_it_including_overnight_and_early_arrival()
    {
        var night = Shift("22:00", "06:00");
        ShiftSpec? On(DateOnly _) => night;
        // 21:30 (early) and 23:00 on the 10th, and 05:00 on the 11th all belong to the night shift that started on the 10th.
        foreach (var t in new[] { At(Day, 21, 30), At(Day, 23), At(Day.AddDays(1), 5) })
            Assert.Equal(Day, AttendanceCalculator.ResolveOccurrence(t, Dhaka, 180, On)!.WorkDate);
        // 06:10 on the 11th is after that shift and long before the next one.
        Assert.Null(AttendanceCalculator.ResolveOccurrence(At(Day.AddDays(1), 10), Dhaka, 180, On));

        // A shift that doesn't work on that weekday is never matched.
        var weekdays = new ShiftSpec(Guid.NewGuid(), new TimeOnly(8, 0), new TimeOnly(17, 0), 10, 5, AttendanceRules.MaskFromNames(new[] { "Mon" })!.Value, Array.Empty<BreakSpec>());
        Assert.Null(AttendanceCalculator.ResolveOccurrence(At(Day, 9), Dhaka, 180, _ => weekdays)); // Tuesday
        Assert.NotNull(AttendanceCalculator.ResolveOccurrence(At(Day.AddDays(-1), 9), Dhaka, 180, _ => weekdays)); // Monday
    }

    // ─── Session figures ───────────────────────────────────────────────────

    [Fact]
    public void A_day_shift_with_lateness_a_long_lunch_and_time_beyond_the_shift()
    {
        var s = Shift("08:00", "17:00", 10, 5, new BreakSpec(new TimeOnly(13, 0), 60, false), new BreakSpec(null, 15, true));
        var occ = AttendanceCalculator.Occurrence(Day, s, Dhaka);
        var breaks = new[] { new TakenBreak(At(Day, 13), At(Day, 14, 15), 60) }; // 75 minutes of lunch
        var f = AttendanceCalculator.Calculate(At(Day, 8, 25), At(Day, 18, 30), s, occ, breaks, Array.Empty<OvertimeWindow>());

        Assert.Equal(605, f.AttendanceMinutes);         // 08:25 → 18:30
        Assert.Equal(540, f.ScheduledMinutes);          // 08:00 → 17:00
        Assert.Equal(60, f.ScheduledBreakMinutes);      // only the unpaid lunch
        Assert.Equal(480, f.NetScheduledMinutes);
        Assert.Equal(75, f.BreakMinutes);
        Assert.Equal(15, f.OverBreakMinutes);
        Assert.Equal(530, f.NetWorkingMinutes);         // breaks never count as work
        Assert.Equal(25, f.LateMinutes);                // beyond the 10-minute grace: the whole delay counts
        Assert.Equal(0, f.EarlyDepartureMinutes);
        Assert.Equal(90, f.BeyondShiftMinutes);         // 17:00 → 18:30, not overtime by itself
        Assert.Equal(0, f.ApprovedOvertimeMinutes);
    }

    [Fact]
    public void Grace_periods_and_early_departure()
    {
        var s = Shift("08:00", "17:00", lateGrace: 10, earlyGrace: 5);
        var occ = AttendanceCalculator.Occurrence(Day, s, Dhaka);
        var onTime = AttendanceCalculator.Calculate(At(Day, 8, 10), At(Day, 16, 56), s, occ, Array.Empty<TakenBreak>(), Array.Empty<OvertimeWindow>());
        Assert.Equal((0, 0, false, false), (onTime.LateMinutes, onTime.EarlyDepartureMinutes, onTime.IsLate, onTime.IsEarlyDeparture));
        var early = AttendanceCalculator.Calculate(At(Day, 8), At(Day, 15, 30), s, occ, Array.Empty<TakenBreak>(), Array.Empty<OvertimeWindow>());
        Assert.Equal(90, early.EarlyDepartureMinutes);
        Assert.True(early.IsEarlyDeparture);
    }

    [Fact]
    public void An_overnight_session_is_computed_on_utc_instants()
    {
        var s = Shift("22:00", "06:00", 10, 5, new BreakSpec(new TimeOnly(2, 0), 30, false));
        var occ = AttendanceCalculator.Occurrence(Day, s, Dhaka);
        var f = AttendanceCalculator.Calculate(At(Day, 22, 15), At(Day.AddDays(1), 6, 40), s, occ,
            new[] { new TakenBreak(At(Day.AddDays(1), 2), At(Day.AddDays(1), 2, 30), 30) }, Array.Empty<OvertimeWindow>());
        Assert.Equal(505, f.AttendanceMinutes);   // 22:15 → 06:40 next day
        Assert.Equal(480, f.ScheduledMinutes);
        Assert.Equal(15, f.LateMinutes);
        Assert.Equal(40, f.BeyondShiftMinutes);
        Assert.Equal(475, f.NetWorkingMinutes);
    }

    [Fact]
    public void Missing_punches_and_breaks_outside_the_session_do_not_produce_time()
    {
        var s = Shift("08:00", "17:00");
        var occ = AttendanceCalculator.Occurrence(Day, s, Dhaka);
        var noOut = AttendanceCalculator.Calculate(At(Day, 8), null, s, occ, new[] { new TakenBreak(At(Day, 12), null, 30) }, Array.Empty<OvertimeWindow>());
        Assert.Equal((0, 0, 0), (noOut.AttendanceMinutes, noOut.NetWorkingMinutes, noOut.BreakMinutes));
        var noIn = AttendanceCalculator.Calculate(null, At(Day, 17), s, occ, Array.Empty<TakenBreak>(), Array.Empty<OvertimeWindow>());
        Assert.Equal((0, 0), (noIn.AttendanceMinutes, noIn.LateMinutes));
        // A break recorded partly before the check-in only counts inside the session.
        var clipped = AttendanceCalculator.Calculate(At(Day, 9), At(Day, 10), s, occ, new[] { new TakenBreak(At(Day, 8, 30), At(Day, 9, 15), 15) }, Array.Empty<OvertimeWindow>());
        Assert.Equal(15, clipped.BreakMinutes);
        // Without a shift there is nothing scheduled, late or beyond.
        var unscheduled = AttendanceCalculator.Calculate(At(Day, 9), At(Day, 12), null, null, Array.Empty<TakenBreak>(), Array.Empty<OvertimeWindow>());
        Assert.Equal((180, 0, 0, 0), (unscheduled.AttendanceMinutes, unscheduled.ScheduledMinutes, unscheduled.LateMinutes, unscheduled.BeyondShiftMinutes));
    }

    // ─── Overtime ──────────────────────────────────────────────────────────

    [Fact]
    public void Actual_overtime_is_attendance_inside_the_approved_window_minus_breaks()
    {
        var (ws, we) = (At(Day, 17), At(Day, 19));
        Assert.Equal(90, AttendanceCalculator.ActualOvertimeMinutes(At(Day, 8), At(Day, 18, 30), ws, we, Array.Empty<TakenBreak>()));
        Assert.Equal(120, AttendanceCalculator.ActualOvertimeMinutes(At(Day, 8), At(Day, 21), ws, we, Array.Empty<TakenBreak>()));  // capped at the window
        Assert.Equal(100, AttendanceCalculator.ActualOvertimeMinutes(At(Day, 8), At(Day, 21), ws, we, new[] { new TakenBreak(At(Day, 18), At(Day, 18, 20), 15) }));
        Assert.Equal(0, AttendanceCalculator.ActualOvertimeMinutes(At(Day, 8), At(Day, 16), ws, we, Array.Empty<TakenBreak>()));
        Assert.Equal(0, AttendanceCalculator.ActualOvertimeMinutes(At(Day, 8), null, ws, we, Array.Empty<TakenBreak>()));
    }

    [Theory]
    [InlineData(120, 100, 15, 30, 90)]   // min(approved, actual), rounded down to 15
    [InlineData(60, 200, 15, 30, 60)]    // never more than approved
    [InlineData(120, 25, 15, 30, 0)]     // below the minimum
    [InlineData(90, 89, 1, 0, 89)]
    public void Payable_suggestion_never_exceeds_approved_or_actual(int approved, int actual, int rounding, int minimum, int expected) =>
        Assert.Equal(expected, AttendanceCalculator.PayableSuggestion(approved, actual, rounding, minimum));

    // ─── Rules and validation ──────────────────────────────────────────────

    [Fact]
    public void Working_days_round_trip_between_names_and_mask()
    {
        var mask = AttendanceRules.MaskFromNames(new[] { "sat", "Sunday", "MON", "Tue", "wed", "thu" })!.Value;
        Assert.Equal(new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Sat" }, AttendanceRules.DayNames(mask));
        Assert.False(AttendanceRules.WorksOn(mask, DayOfWeek.Friday));
        Assert.Null(AttendanceRules.MaskFromNames(new[] { "Funday" }));
        Assert.Null(AttendanceRules.MaskFromNames(Array.Empty<string>()));
    }

    [Fact]
    public void Shift_and_request_validation()
    {
        var v = new CreateShiftRequestValidator();
        CreateShiftRequest Ok() => new(Guid.NewGuid(), "NIGHT", "Night", "22:00", "06:00", new[] { "Sat", "Sun", "Mon", "Tue", "Wed", "Thu" },
            Breaks: new[] { new ShiftBreakDto(null, "Prayer", "Fajr", "05:00", 15, true) });
        Assert.True(v.Validate(Ok()).IsValid);
        Assert.False(v.Validate(Ok() with { StartLocal = "25:00" }).IsValid);
        Assert.False(v.Validate(Ok() with { EndLocal = "22:00" }).IsValid);
        Assert.False(v.Validate(Ok() with { WorkingDays = Array.Empty<string>() }).IsValid);
        Assert.False(v.Validate(Ok() with { Code = "bad code" }).IsValid);
        Assert.False(v.Validate(Ok() with { Breaks = new[] { new ShiftBreakDto(null, "Nap", "x", null, 20, false) } }).IsValid);
        Assert.False(new CreateOvertimeRequestValidator().Validate(new CreateOvertimeRequest(Guid.NewGuid(), At(Day, 17), At(Day, 16), "x")).IsValid);
        Assert.False(new CreateOvertimeRequestValidator().Validate(new CreateOvertimeRequest(Guid.NewGuid(), At(Day, 17), At(Day.AddDays(1), 17), "x")).IsValid);
        Assert.False(new CreateCorrectionRequestValidator().Validate(new CreateCorrectionRequest(null, null, "x")).IsValid);
        Assert.False(new CreateCorrectionRequestValidator().Validate(new CreateCorrectionRequest(At(Day, 9), At(Day, 8), "x")).IsValid);
        Assert.False(new UpdatePolicyRequestValidator().Validate(new UpdatePolicyRequest("Mars/Olympus", 120, 240, 16, 7, 180, 15, 30, 120, 720)).IsValid);
        Assert.True(new UpdatePolicyRequestValidator().Validate(new UpdatePolicyRequest("Asia/Dhaka", 120, 240, 16, 7, 180, 15, 30, 120, 720)).IsValid);
    }

    private static IReadOnlyList<string> PermsOf(string role) => AccessCatalog.Role(role)!.Permissions;

    [Fact]
    public void Attendance_permissions_follow_the_roles()
    {
        // HR administers everything attendance.
        foreach (var p in new[] { P.AttendanceView, P.AttendanceRecord, P.AttendanceManage, P.AttendanceApprove, P.ShiftsManage, P.ShiftsAssign, P.OvertimeView, P.OvertimeManage, P.OvertimeApprove })
            Assert.Contains(p, PermsOf(R.HrOfficer));
        // Manager: visibility and approvals, no configuration.
        foreach (var p in new[] { P.AttendanceView, P.AttendanceApprove, P.OvertimeView, P.OvertimeApprove }) Assert.Contains(p, PermsOf(R.FactoryManager));
        foreach (var p in new[] { P.AttendanceManage, P.ShiftsManage }) Assert.DoesNotContain(p, PermsOf(R.FactoryManager));
        // Supervisor: their lines' attendance, punches, shift assignment and overtime.
        foreach (var p in new[] { P.AttendanceView, P.AttendanceRecord, P.ShiftsAssign, P.OvertimeManage, P.OvertimeApprove }) Assert.Contains(p, PermsOf(R.ProductionSupervisor));
        foreach (var p in new[] { P.AttendanceManage, P.AttendanceApprove, P.ShiftsManage }) Assert.DoesNotContain(p, PermsOf(R.ProductionSupervisor));
        // Monitoring operator: read-only context.
        Assert.Contains(P.AttendanceView, PermsOf(R.MonitoringOperator));
        foreach (var p in new[] { P.AttendanceRecord, P.AttendanceManage, P.OvertimeManage, P.ShiftsAssign }) Assert.DoesNotContain(p, PermsOf(R.MonitoringOperator));
        // IT: nothing attendance; Worker: own records only.
        Assert.Empty(PermsOf(R.DeviceAdmin).Where(p => p.StartsWith("attendance.") || p.StartsWith("overtime.") || p.StartsWith("shifts.")));
        Assert.Equal(new[] { P.NotificationsView, P.SelfView }.OrderBy(x => x), PermsOf(R.Worker).OrderBy(x => x));
        foreach (var p in new[] { P.AttendanceView, P.AttendanceRecord, P.AttendanceManage, P.AttendanceApprove, P.ShiftsManage, P.ShiftsAssign, P.OvertimeView, P.OvertimeManage, P.OvertimeApprove })
            Assert.False(AccessCatalog.Permission(p)!.Reserved, p);
    }

    [Theory]
    [InlineData("Asia/Dhaka", 6.0)]
    [InlineData(" asia/dhaka ", 6.0)]
    [InlineData("UTC", 0.0)]
    [InlineData("Etc/UTC", 0.0)]
    [InlineData("Asia/Kolkata", 5.5)]
    public void Time_zones_resolve_to_the_right_offset(string id, double hours)
    {
        var tz = AttendanceRules.ResolveTimeZone(id);
        Assert.NotNull(tz);
        Assert.Equal(TimeSpan.FromHours(hours), tz!.GetUtcOffset(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(TimeSpan.FromHours(hours), tz.GetUtcOffset(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Mars/Olympus")]
    public void Unknown_time_zones_do_not_resolve(string id) => Assert.Null(AttendanceRules.ResolveTimeZone(id));

    [Fact]
    public void Just_after_midnight_in_Dhaka_is_already_the_next_work_day()
    {
        // 18:59 UTC on 13 Nov is 00:59 on 14 Nov in Dhaka.
        var at = new DateTimeOffset(2026, 11, 13, 18, 59, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 11, 14), AttendanceCalculator.LocalDate(at, AttendanceRules.ResolveTimeZone(AttendanceRules.DefaultTimeZone)!));
    }
}
