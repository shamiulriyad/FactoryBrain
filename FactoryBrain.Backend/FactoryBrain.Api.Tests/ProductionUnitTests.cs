using FactoryBrain.Application.Production;
using FactoryBrain.Domain.Entities;

namespace FactoryBrain.Api.Tests;

/// <summary>Shared fixtures for the production calculation tests. Every number is hand-computed in the test that uses it.</summary>
internal static class Prod
{
    /// <summary>A fixed UTC+06:00 zone (no DST), like Asia/Dhaka.</summary>
    public static readonly TimeZoneInfo Plus6 = TimeZoneInfo.CreateCustomTimeZone("Test/Plus6", TimeSpan.FromHours(6), "UTC+6 test", "UTC+6 test");

    /// <summary>UTC+1 with DST (+2) from the last Sunday of March 02:00 to the last Sunday of October 03:00.</summary>
    public static readonly TimeZoneInfo Dst = TimeZoneInfo.CreateCustomTimeZone("Test/Dst", TimeSpan.FromHours(1), "DST test", "STD", "DST", new[]
    {
        TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2000, 1, 1), new DateTime(2099, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
    });

    public const int AllDays = 0b1111111;
    /// <summary>Saturday–Thursday (Friday off).</summary>
    public const int SatToThu = 0b1011111;

    public static ShiftSpec Day => new("day", new TimeOnly(8, 0), new TimeOnly(17, 0), new TimeOnly(13, 0), 60, SatToThu);
    public static ShiftSpec Night => new("night", new TimeOnly(22, 0), new TimeOnly(6, 0), new TimeOnly(2, 0), 30, AllDays);

    public static DateTime Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    public static BalanceOperation Op(int seq, double smv, string? machine = null, params int[] preds) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{seq:D12}"), seq, $"Op {seq}", smv, machine, preds);
}

public sealed class ProductionMetricsTests
{
    [Fact]
    public void Variance_is_actual_minus_planned() => Assert.Equal(-15, ProductionMetrics.Variance(85, 100));

    [Theory]
    [InlineData(85, 100, 85.0)]
    [InlineData(1, 3, 33.3)]
    [InlineData(2, 3, 66.7)]
    [InlineData(1, 8, 12.5)]     // 12.5 exactly: no rounding
    [InlineData(3, 16, 18.8)]    // 18.75 rounds half away from zero
    public void Achievement_is_actual_over_planned_rounded_to_one_decimal(int actual, int planned, double expected) =>
        Assert.Equal(expected, ProductionMetrics.AchievementPct(actual, planned));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Achievement_is_not_calculable_without_a_positive_plan(int planned) =>
        Assert.Null(ProductionMetrics.AchievementPct(10, planned));

    [Fact]
    public void Efficiency_needs_available_minutes()
    {
        Assert.Equal(83.3, ProductionMetrics.EfficiencyPct(1000, 1200));
        Assert.Null(ProductionMetrics.EfficiencyPct(1000, 0));
    }

    [Fact]
    public void Required_rate_handles_done_and_no_time_left()
    {
        Assert.Equal(30.0, ProductionMetrics.RequiredRatePerHour(600, 1200));
        Assert.Equal(0.0, ProductionMetrics.RequiredRatePerHour(0, 0));
        Assert.Null(ProductionMetrics.RequiredRatePerHour(10, 0));
    }

    [Fact]
    public void Hourly_target_is_operators_times_sixty_times_efficiency_over_smv()
    {
        // 30 operators × 60 min × 0.60 ÷ 10 min/piece = 108 pieces/hour.
        Assert.Equal(108.0, ProductionMetrics.HourlyTarget(30, 60, 10)!.Value, 6);
        Assert.Null(ProductionMetrics.HourlyTarget(30, 60, null));
        Assert.Null(ProductionMetrics.HourlyTarget(30, 60, 0));
    }

    [Fact]
    public void Smv_prefers_the_approved_value_then_the_bulletin()
    {
        Assert.Equal((12.5, SmvBasis.Approved), StyleSmv.Resolve(12.5, new[] { 1.0, 2.0 }));
        Assert.Equal((3.0, SmvBasis.OperationBulletin), StyleSmv.Resolve(null, new[] { 1.0, 2.0 }));
        Assert.Equal(((double?)null, SmvBasis.Missing), StyleSmv.Resolve(null, Array.Empty<double>()));
        Assert.Equal(((double?)null, SmvBasis.Missing), StyleSmv.Resolve(null, new[] { 1.0, 0.0 }));
    }
}

public sealed class EfficiencyCalculatorTests
{
    private static double NoBreaks(DateTime a, DateTime b) => 0;

    [Fact]
    public void Earned_over_available_minutes()
    {
        // 100 pcs × 10 SMV = 1000 earned; 20 operators × 60 min = 1200 available → 83.3 %.
        var r = EfficiencyCalculator.Calculate(new[]
        {
            new EfficiencyInput(100, 10, 20, Prod.Utc(2026, 10, 26, 2), Prod.Utc(2026, 10, 26, 3), null),
        }, NoBreaks);
        Assert.Equal((1000.0, 1200.0, 83.3), (r.EarnedMinutes, r.AvailableMinutes, r.EfficiencyPct));
    }

    [Fact]
    public void Planned_breaks_are_not_available_time()
    {
        var cal = new ProductionCalendar(Prod.Plus6, new[] { Prod.Day });
        // 12:00–14:00 local (06:00–08:00 UTC) includes the 13:00–14:00 break: 60 min × 20 operators available.
        var r = EfficiencyCalculator.Calculate(new[]
        {
            new EfficiencyInput(100, 10, 20, Prod.Utc(2026, 10, 26, 6), Prod.Utc(2026, 10, 26, 8), null),
        }, cal.BreakMinutesWithin);
        Assert.Equal(1200.0, r.AvailableMinutes);
        Assert.Equal(83.3, r.EfficiencyPct);
    }

    [Fact]
    public void Records_without_operators_or_smv_are_excluded_and_counted()
    {
        var t0 = Prod.Utc(2026, 10, 26, 2);
        var r = EfficiencyCalculator.Calculate(new[]
        {
            new EfficiencyInput(100, 10, 20, t0, t0.AddHours(1), null),
            new EfficiencyInput(500, 10, null, t0.AddHours(1), t0.AddHours(2), null),
            new EfficiencyInput(500, null, 20, t0.AddHours(2), t0.AddHours(3), null),
        }, NoBreaks);
        Assert.Equal((1, 1, 1), (r.RecordsUsed, r.RecordsMissingOperators, r.RecordsMissingSmv));
        Assert.Equal(83.3, r.EfficiencyPct);
    }

    [Fact]
    public void Downtime_stays_in_line_efficiency_and_is_removed_in_the_second_figure()
    {
        // 15 min downtime × 20 operators = 300 operator-minutes: 1000 ÷ 1200 = 83.3 %, 1000 ÷ 900 = 111.1 %.
        var t0 = Prod.Utc(2026, 10, 26, 2);
        var r = EfficiencyCalculator.Calculate(new[] { new EfficiencyInput(100, 10, 20, t0, t0.AddHours(1), 15) }, NoBreaks);
        Assert.Equal((83.3, 111.1, 300.0), (r.EfficiencyPct, r.EfficiencyExcludingDowntimePct, r.DowntimeOperatorMinutes));
    }

    [Fact]
    public void No_usable_records_means_no_efficiency_not_zero()
    {
        var r = EfficiencyCalculator.Calculate(Array.Empty<EfficiencyInput>(), NoBreaks);
        Assert.Null(r.EfficiencyPct);
    }
}

public sealed class WipCalculatorTests
{
    [Fact]
    public void Each_buffer_is_upstream_good_minus_downstream_good_and_rejects()
    {
        var wip = WipCalculator.Calculate(new[]
        {
            new StageCount(ProductionStage.SewingInput, 500, 0, 0, 5),
            new StageCount(ProductionStage.SewingOutput, 300, 10, 25, 6),
        });
        var sewing = wip.Single(b => b.Key == "in_sewing");
        // 500 loaded − (300 good + 10 rejected) = 190; the 25 in rework are still in the line.
        Assert.Equal((190, "ok"), (sewing.Pieces, sewing.State));
        // Nothing recorded at finishing yet: all 300 sewn pieces wait for it. Cutting isn't tracked: no buffer.
        Assert.Equal((300, "missing_to"), (wip.Single(b => b.Key == "awaiting_finishing").Pieces, wip.Single(b => b.Key == "awaiting_finishing").State));
        Assert.Equal(2, wip.Count);
    }

    [Fact]
    public void Missing_upstream_counts_and_negative_balances_are_not_hidden()
    {
        var wip = WipCalculator.Calculate(new[]
        {
            new StageCount(ProductionStage.SewingInput, 100, 0, 0, 1),
            new StageCount(ProductionStage.SewingOutput, 150, 0, 0, 2),
            new StageCount(ProductionStage.Finishing, 80, 0, 0, 1),
        });
        Assert.Equal(("inconsistent", (int?)null), (wip.Single(b => b.Key == "in_sewing").State, wip.Single(b => b.Key == "in_sewing").Pieces));
        Assert.Equal(70, wip.Single(b => b.Key == "awaiting_finishing").Pieces);
        Assert.Equal("missing_to", wip.Single(b => b.Key == "awaiting_packing").State);
        Assert.DoesNotContain(wip, b => b.Key == "cut_not_loaded");
    }
}

public sealed class ProductionCalendarTests
{
    [Fact]
    public void Day_shift_windows_exclude_the_break_and_add_overtime_after_the_shift()
    {
        var cal = new ProductionCalendar(Prod.Plus6, new[] { Prod.Day });
        var monday = new DateOnly(2026, 10, 26);
        var w = cal.WindowsFor(monday, overtimeMinutes: 120);
        Assert.Equal(3, w.Count);
        Assert.Equal((Prod.Utc(2026, 10, 26, 2), Prod.Utc(2026, 10, 26, 7)), (w[0].StartUtc, w[0].EndUtc));   // 08:00–13:00 local
        Assert.Equal((Prod.Utc(2026, 10, 26, 8), Prod.Utc(2026, 10, 26, 11)), (w[1].StartUtc, w[1].EndUtc));  // 14:00–17:00 local
        Assert.Equal((Prod.Utc(2026, 10, 26, 11), Prod.Utc(2026, 10, 26, 13), (string?)null), (w[2].StartUtc, w[2].EndUtc, w[2].ShiftCode));
        Assert.Equal(480 + 120, w.Sum(x => x.Minutes));
        Assert.Empty(cal.WindowsFor(new DateOnly(2026, 10, 30)));   // Friday is not a working day
    }

    [Fact]
    public void Overnight_shift_belongs_to_the_date_it_started()
    {
        var cal = new ProductionCalendar(Prod.Plus6, new[] { Prod.Day, Prod.Night });
        // 01:00 local on the 27th = 19:00 UTC on the 26th: the night shift of the 26th.
        Assert.Equal((new DateOnly(2026, 10, 26), "night"), cal.Classify(Prod.Utc(2026, 10, 26, 19)));
        // 18:00 local: between shifts.
        Assert.Equal((new DateOnly(2026, 10, 26), (string?)null), cal.Classify(Prod.Utc(2026, 10, 26, 12)));
        var night = cal.WindowsFor(new DateOnly(2026, 10, 26), new[] { "night" });
        // 22:00–02:00 and 02:30–06:00 local: 240 + 210 minutes, the break after midnight excluded.
        Assert.Equal(new[] { 240.0, 210.0 }, night.Select(x => x.Minutes));
        Assert.All(night, x => Assert.Equal(new DateOnly(2026, 10, 26), x.ProductionDate));
        Assert.Equal(30, cal.BreakMinutesWithin(Prod.Utc(2026, 10, 26, 19), Prod.Utc(2026, 10, 26, 21)));
    }

    [Fact]
    public void Dst_gap_and_overlap_resolve_deterministically()
    {
        var cal = new ProductionCalendar(Prod.Dst, Array.Empty<ShiftSpec>());
        // 02:30 doesn't exist on 2026-03-29: it moves to 03:00 local (UTC+2) = 01:00 UTC.
        Assert.Equal(Prod.Utc(2026, 3, 29, 1), cal.ToUtc(new DateOnly(2026, 3, 29), new TimeOnly(2, 30)));
        // 02:30 happens twice on 2026-10-25: the first occurrence (UTC+2) = 00:30 UTC.
        Assert.Equal(Prod.Utc(2026, 10, 25, 0, 30), cal.ToUtc(new DateOnly(2026, 10, 25), new TimeOnly(2, 30)));
    }

    [Fact]
    public void A_shift_across_the_spring_change_has_one_hour_less_real_time()
    {
        var shift = new ShiftSpec("early", new TimeOnly(0, 0), new TimeOnly(8, 0), null, 0, Prod.AllDays);
        var cal = new ProductionCalendar(Prod.Dst, new[] { shift });
        Assert.Equal(7 * 60, cal.WindowsFor(new DateOnly(2026, 3, 29)).Sum(w => w.Minutes));
        Assert.Equal(8 * 60, cal.WindowsFor(new DateOnly(2026, 3, 30)).Sum(w => w.Minutes));
        Assert.Equal(9 * 60, cal.WindowsFor(new DateOnly(2026, 10, 25)).Sum(w => w.Minutes));
    }

    [Fact]
    public void Overlapping_shifts_are_detected()
    {
        var evening = new ShiftSpec("evening", new TimeOnly(16, 0), new TimeOnly(23, 0), null, 0, Prod.AllDays);
        Assert.Empty(ProductionCalendar.FindOverlaps(new[] { Prod.Day, Prod.Night }));
        Assert.Equal(new[] { ("day", "evening"), ("evening", "night") }, ProductionCalendar.FindOverlaps(new[] { Prod.Day, Prod.Night, evening }));
    }

    [Fact]
    public void Weekday_names_round_trip()
    {
        Assert.Equal(Prod.SatToThu, ProductionWire.WeekdayMask(new[] { "sat", "sun", "mon", "tue", "wed", "thu" }));
        Assert.Null(ProductionWire.WeekdayMask(new[] { "funday" }));
        Assert.Null(ProductionWire.WeekdayMask(Array.Empty<string>()));
        Assert.Equal(new[] { "sun", "mon", "tue", "wed", "thu", "sat" }, ProductionWire.Weekdays(Prod.SatToThu));
    }
}

public sealed class PlanScheduleTests
{
    private static readonly IReadOnlyList<WorkWindow> TwoHours = new[]
    {
        new WorkWindow(Prod.Utc(2026, 10, 26, 2), Prod.Utc(2026, 10, 26, 3), new DateOnly(2026, 10, 26), "day"),
        new WorkWindow(Prod.Utc(2026, 10, 26, 4), Prod.Utc(2026, 10, 26, 5), new DateOnly(2026, 10, 26), null),
    };

    [Fact]
    public void Planned_output_accrues_at_the_target_rate_only_in_scheduled_minutes_and_stops_at_the_quantity()
    {
        var s = new PlanSchedule(TwoHours, 100, hourlyTarget: 60);
        Assert.Equal(PlannedOutputMethod.TargetRate, s.Method);
        Assert.Equal(120, s.Capacity, 6);
        Assert.Equal(30, s.PlannedBetween(Prod.Utc(2026, 10, 26, 2), Prod.Utc(2026, 10, 26, 2, 30)), 6);
        Assert.Equal(60, s.CumulativeAt(Prod.Utc(2026, 10, 26, 3, 59)), 6);   // the gap adds nothing
        Assert.Equal(100, s.CumulativeAt(Prod.Utc(2026, 10, 26, 6)), 6);     // capped at the planned quantity
        Assert.Equal(40, s.PlannedBetween(Prod.Utc(2026, 10, 26, 0), Prod.Utc(2026, 10, 27, 0), w => w.Overtime), 6);
    }

    [Fact]
    public void Without_smv_the_quantity_is_spread_evenly()
    {
        var s = new PlanSchedule(TwoHours, 100, hourlyTarget: null);
        Assert.Equal(PlannedOutputMethod.EvenSpread, s.Method);
        Assert.Equal(50, s.PlannedBetween(Prod.Utc(2026, 10, 26, 2), Prod.Utc(2026, 10, 26, 3)), 6);
    }
}

public sealed class OutputRulesTests
{
    private static readonly DateTime Now = Prod.Utc(2026, 10, 26, 12);
    private static readonly ShiftSpec Evening = new("eve", new TimeOnly(17, 0), new TimeOnly(21, 0), null, 0, Prod.AllDays);
    private static readonly ProductionCalendar Cal = new(Prod.Plus6, new[] { Prod.Day, Evening });

    private static (CheckedOutput? Result, Dictionary<string, string[]> Errors) Check(DateTime start, DateTime end, int qty = 50, double? downtime = null)
    {
        var errors = new Dictionary<string, string[]>();
        var r = OutputRules.Check(new OutputValues(start, end, qty, 0, 0, 20, downtime, null, null), Now, ProductionOptions.Default, Cal, errors);
        return (r, errors);
    }

    [Fact]
    public void A_valid_interval_is_classified_by_its_start()
    {
        var (r, errors) = Check(Prod.Utc(2026, 10, 26, 9), Prod.Utc(2026, 10, 26, 10));
        Assert.Empty(errors);
        Assert.Equal((new DateOnly(2026, 10, 26), "day"), (r!.ProductionDate, r.ShiftCode));
    }

    [Fact]
    public void Future_too_long_and_too_old_intervals_are_rejected()
    {
        Assert.Contains("intervalEnd", Check(Now.AddMinutes(-50), Now.AddMinutes(10)).Errors.Keys);
        Assert.Contains("intervalEnd", Check(Now.AddHours(-13), Now).Errors.Keys);
        Assert.Contains("intervalStart", Check(Now.AddDays(-40), Now.AddDays(-40).AddHours(1)).Errors.Keys);
        Assert.Contains("intervalEnd", Check(Now, Now.AddMinutes(-1)).Errors.Keys);
    }

    [Fact]
    public void An_interval_across_a_shift_change_must_be_split()
    {
        // 16:30–17:30 local runs from the day shift into the evening shift.
        var (r, errors) = Check(Prod.Utc(2026, 10, 26, 10, 30), Prod.Utc(2026, 10, 26, 11, 30));
        Assert.Null(r);
        Assert.Contains("spans shifts day and eve", errors["intervalEnd"][0]);
    }

    [Fact]
    public void Downtime_cannot_exceed_the_interval_and_quantities_cannot_be_negative()
    {
        Assert.Contains("downtimeMinutes", Check(Prod.Utc(2026, 10, 26, 9), Prod.Utc(2026, 10, 26, 10), downtime: 61).Errors.Keys);
        Assert.Contains("quantity", Check(Prod.Utc(2026, 10, 26, 9), Prod.Utc(2026, 10, 26, 10), qty: -1).Errors.Keys);
    }

    [Fact]
    public void Work_outside_every_shift_has_no_shift_code()
    {
        // 21:00–22:00 local is after the evening shift.
        var (r, errors) = Check(Prod.Utc(2026, 10, 25, 15), Prod.Utc(2026, 10, 25, 16));
        Assert.Empty(errors);
        Assert.Null(r!.ShiftCode);
        Assert.Equal(new DateOnly(2026, 10, 25), r.ProductionDate);
    }
}

public sealed class LineBalancerTests
{
    [Fact]
    public void A_three_operation_chain_on_three_stations_is_perfectly_balanced()
    {
        var ops = new[] { Prod.Op(1, 1.0), Prod.Op(2, 1.0, null, 1), Prod.Op(3, 1.0, null, 2) };
        var r = LineBalancer.Propose(ops, 3, null);
        Assert.True(r.Feasible);
        Assert.Equal((3, 1.0, 100.0, 0.0, 60.0), (r.Proposed!.Stations, r.Proposed.CycleTimeMinutes, r.Proposed.BalanceEfficiencyPct, r.Proposed.SmoothnessIndex, r.Proposed.CapacityPerHour));
        Assert.Equal(new[] { 1, 2, 3 }, r.ProposedStations.Select(s => ops.Single(o => o.Id == s.OperationIds.Single()).Sequence));
    }

    [Fact]
    public void Two_stations_report_the_best_found_and_the_lower_bound_without_claiming_optimality()
    {
        var ops = new[] { Prod.Op(1, 1.0), Prod.Op(2, 1.0, null, 1), Prod.Op(3, 1.0, null, 2) };
        var r = LineBalancer.Propose(ops, 2, null);
        Assert.True(r.Feasible);
        // 3 min of work on 2 stations: lower bound 1.5 min; whole operations give 2.0 min (75 % balance efficiency).
        Assert.Equal((2, 2.0, 75.0), (r.Proposed!.Stations, r.Proposed.CycleTimeMinutes, r.Proposed.BalanceEfficiencyPct));
        Assert.Equal(1.5, r.LowerBoundCycleTimeMinutes);
        Assert.Contains(r.Assumptions, a => a.Contains("may not be optimal"));
        Assert.Contains("Not proven optimal", r.Method);
    }

    [Fact]
    public void A_long_operation_is_split_over_parallel_stations()
    {
        var r = LineBalancer.Propose(new[] { Prod.Op(1, 2.5) }, 3, null);
        Assert.True(r.Feasible);
        Assert.Equal(3, r.Proposed!.Stations);
        Assert.Equal(0.833, r.Proposed.CycleTimeMinutes);
        Assert.Equal(72.0, r.Proposed.CapacityPerHour);
    }

    [Fact]
    public void Precedence_and_machine_types_are_respected_and_the_result_is_deterministic()
    {
        var ops = new[]
        {
            Prod.Op(1, 0.5, "SNLS"), Prod.Op(2, 0.7, "SNLS", 1), Prod.Op(3, 0.4, "OL", 1),
            Prod.Op(4, 0.6, "SNLS", 2, 3), Prod.Op(5, 0.3, null, 4),
        };
        var a = LineBalancer.Propose(ops, 4, null);
        var b = LineBalancer.Propose(ops, 4, null);
        Assert.True(a.Feasible);
        Assert.Equal(a.ProposedStations.Select(s => string.Join(",", s.OperationIds)), b.ProposedStations.Select(s => string.Join(",", s.OperationIds)));
        Assert.True(a.Proposed!.Stations <= 4);
        Assert.True(a.Proposed.CycleTimeMinutes >= a.LowerBoundCycleTimeMinutes);
        Assert.Empty(LineBalancer.CheckLayout(ops, a.ProposedStations));
        Assert.Equal(2.5, a.Proposed.TotalSmv);
    }

    [Fact]
    public void Missing_or_invalid_inputs_are_explained_instead_of_balanced()
    {
        var none = LineBalancer.Propose(Array.Empty<BalanceOperation>(), 5, null);
        Assert.Equal(("missing_inputs", false), (none.Status, none.Feasible));
        Assert.Contains("no operation bulletin", none.Problems[0]);

        var cyclic = LineBalancer.Propose(new[] { Prod.Op(1, 1, null, 2), Prod.Op(2, 1, null, 1) }, 2, null);
        Assert.Equal("invalid_inputs", cyclic.Status);
        Assert.Contains(cyclic.Problems, p => p.Contains("cycle"));

        var unknown = LineBalancer.Propose(new[] { Prod.Op(1, 1, null, 9) }, 2, null);
        Assert.Contains(unknown.Problems, p => p.Contains("unknown operation 9"));

        var noSmv = LineBalancer.Propose(new[] { Prod.Op(1, 0) }, 2, null);
        Assert.Contains(noSmv.Problems, p => p.Contains("no positive SMV"));
    }

    [Fact]
    public void Too_few_workstations_for_the_machine_types_is_infeasible()
    {
        var r = LineBalancer.Propose(new[] { Prod.Op(1, 0.5, "SNLS"), Prod.Op(2, 0.5, "OL", 1) }, 1, null);
        Assert.Equal(("infeasible", false), (r.Status, r.Feasible));
        Assert.Contains("2 machine types", r.Problems[0]);
        Assert.Empty(r.ProposedStations);
    }

    [Fact]
    public void An_unreachable_target_is_reported_with_the_stations_it_would_need()
    {
        var ops = new[] { Prod.Op(1, 1.0), Prod.Op(2, 1.0, null, 1), Prod.Op(3, 1.0, null, 2) };
        var r = LineBalancer.Propose(ops, 3, targetOutputPerHour: 80);
        Assert.True(r.Feasible);
        Assert.Equal((false, 0.75, 6), (r.TargetMet, r.TargetCycleTimeMinutes, r.StationsNeededForTarget));
        Assert.Contains(r.Problems, p => p.Contains("about 6 workstations"));
    }

    [Fact]
    public void Workload_metrics_of_a_recorded_layout()
    {
        var ops = new[] { Prod.Op(1, 1.0), Prod.Op(2, 1.0, null, 1), Prod.Op(3, 1.0, null, 2) };
        var m = LineBalancer.Measure(ops, new[] { new StationAssignment(1, new[] { ops[0].Id }), new StationAssignment(2, new[] { ops[1].Id, ops[2].Id }) });
        // Loads 1 and 2: cycle 2, 3 ÷ (2 × 2) = 75 %, √((2−1)² + 0²) = 1, 60 ÷ 2 = 30 pcs/h.
        Assert.Equal((2.0, 75.0, 25.0, 1.0, 30.0, 2), (m.CycleTimeMinutes, m.BalanceEfficiencyPct, m.BalanceDelayPct, m.SmoothnessIndex, m.CapacityPerHour, m.BottleneckStationNo));

        var shared = LineBalancer.Measure(ops.Take(1).ToList(), new[] { new StationAssignment(1, new[] { ops[0].Id }), new StationAssignment(2, new[] { ops[0].Id }) });
        Assert.All(shared.StationLoads, s => Assert.Equal(0.5, s.LoadMinutes));
    }

    [Fact]
    public void Layout_constraint_problems_are_listed()
    {
        var ops = new[] { Prod.Op(1, 1.0, "SNLS"), Prod.Op(2, 1.0, "OL", 1), Prod.Op(3, 1.0, null, 2) };
        var issues = LineBalancer.CheckLayout(ops, new[]
        {
            new StationAssignment(1, new[] { ops[0].Id, ops[1].Id }),   // mixes SNLS and OL
            new StationAssignment(0, new[] { ops[0].Id }),
        });
        Assert.Contains(issues, i => i.Code == "mixed_machine_types");
        Assert.Contains(issues, i => i.Code == "operation_unassigned" && i.Message.Contains("Operation 3"));

        var order = LineBalancer.CheckLayout(ops, new[]
        {
            new StationAssignment(1, new[] { ops[2].Id }),
            new StationAssignment(2, new[] { ops[0].Id }),
            new StationAssignment(3, new[] { ops[1].Id }),
        });
        Assert.Contains(order, i => i.Code == "precedence_violation" && i.Message.Contains("Operation 3"));
    }
}

public sealed class BottleneckAnalyzerTests
{
    private static readonly BalanceOperation[] Ops = { Prod.Op(1, 0.5), Prod.Op(2, 1.0, null, 1), Prod.Op(3, 0.4, null, 2) };
    private static readonly DateTime At = Prod.Utc(2026, 10, 25, 4);

    [Fact]
    public void The_constraint_is_the_lowest_measured_capacity_and_queues_corroborate_it()
    {
        var r = BottleneckAnalyzer.Analyze(Ops, new[]
        {
            new CycleObservation(Ops[0].Id, 30, 1, 5, At, 10),
            new CycleObservation(Ops[1].Id, 66, 1, 40, At, 10),
        }, null, observedLineRatePerHour: 52);
        Assert.Equal("observed", r.Status);
        Assert.Equal((2, 54.5), (r.Constraint!.Sequence, r.ConstraintCapacityPerHour));
        Assert.Contains(r.Evidence, e => e.Contains("supports it being the constraint"));
        Assert.Contains(r.Evidence, e => e.Contains("consistent with it limiting the line"));
        Assert.Contains(r.Limitations, l => l.Contains("1 of 3 operations have no recent time study"));
        // 1.0 SMV × 60 ÷ 66 s = 90.9 % of standard.
        Assert.Equal(90.9, r.Operations.Single(o => o.Sequence == 2).PerformancePct);
    }

    [Fact]
    public void Contradicting_queue_evidence_is_reported_as_a_limitation()
    {
        var r = BottleneckAnalyzer.Analyze(Ops, new[]
        {
            new CycleObservation(Ops[0].Id, 30, 1, 60, At, 10),
            new CycleObservation(Ops[1].Id, 66, 1, 3, At, 10),
        }, null, null);
        Assert.Equal(2, r.Constraint!.Sequence);
        Assert.Contains(r.Limitations, l => l.Contains("not the measured constraint"));
    }

    [Fact]
    public void Without_time_studies_the_recorded_layout_gives_a_labelled_standard_estimate()
    {
        var layout = new[] { new StationAssignment(1, new[] { Ops[0].Id }), new StationAssignment(2, new[] { Ops[1].Id, Ops[2].Id }) };
        var r = BottleneckAnalyzer.Analyze(Ops, Array.Empty<CycleObservation>(), layout, null);
        Assert.Equal(("standard_only", 2, 42.9), (r.Status, r.ConstraintStationNo, r.ConstraintCapacityPerHour));
        Assert.Contains(r.Limitations, l => l.Contains("not on measured cycle times"));
    }

    [Fact]
    public void Without_time_studies_or_layout_no_operation_is_named()
    {
        var r = BottleneckAnalyzer.Analyze(Ops, Array.Empty<CycleObservation>(), null, null);
        Assert.Equal("insufficient_data", r.Status);
        Assert.Null(r.Constraint);   // not "the largest SMV"
        Assert.Equal(2, r.MissingInputs.Count);
    }
}

public sealed class DelayRiskEstimatorTests
{
    // Calendar: one shift 08:00–16:00 UTC every day. Now: Monday 12:00. Due: end of Wednesday.
    private static readonly DateTime Now = Prod.Utc(2026, 10, 26, 12);
    private static readonly DateTime Due = Prod.Utc(2026, 10, 29, 0);
    private static readonly ProductionCalendar Cal = new(TimeZoneInfo.Utc,
        new[] { new ShiftSpec("day", new TimeOnly(8, 0), new TimeOnly(16, 0), null, 0, Prod.AllDays) });
    private static readonly IReadOnlyList<WorkWindow> Plan = Cal.Windows(new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 30));

    // 280 + 320 + 120 pieces over 8 + 8 + 4 scheduled hours = 36 pieces/hour; daily 35, 40, 30.
    private static readonly DailyRateSample[] Recent =
    {
        new(new DateOnly(2026, 10, 24), 280, 480, 8),
        new(new DateOnly(2026, 10, 25), 320, 480, 8),
        new(new DateOnly(2026, 10, 26), 120, 240, 4),
    };

    private static DelayRiskResult Estimate(int produced, IReadOnlyList<WorkWindow>? windows = null, DailyRateSample[]? recent = null) =>
        DelayRiskEstimator.Estimate(new DelayRiskInput(1000, produced, Due, Now, windows ?? Plan, 40, recent ?? Recent, 3, 10));

    [Fact]
    public void Low_risk_when_the_observed_rate_finishes_well_before_the_due_time()
    {
        var r = Estimate(400);
        // 600 remaining over 20 scheduled hours = 30/h required; 36/h observed.
        Assert.Equal(("low", 600, 1200.0, 30.0, 36.0), (r.Level, r.RemainingQuantity, r.RemainingScheduledMinutes, r.RequiredRatePerLineHour, r.ObservedRatePerLineHour));
        // 600 ÷ 36 = 16 h 40 min of scheduled time: 4 h today, 8 h tomorrow, 4 h 40 min on Wednesday.
        Assert.Equal(Prod.Utc(2026, 10, 28, 12, 40), r.ProjectedCompletionUtc);
        Assert.Equal(Prod.Utc(2026, 10, 28, 11), r.ProjectedCompletionEarliestUtc);   // at 40/h
        Assert.Equal(Prod.Utc(2026, 10, 28, 16), r.ProjectedCompletionLatestUtc);     // at 30/h
        Assert.Equal((30.0, 40.0), (r.ObservedRateLow, r.ObservedRateHigh));
    }

    [Fact]
    public void Medium_risk_when_the_required_rate_is_within_the_slack()
    {
        // 680 ÷ 20 h = 34/h ≥ 36 × 0.9 = 32.4.
        Assert.Equal("medium", Estimate(320).Level);
    }

    [Fact]
    public void High_risk_with_shortfall_when_the_projection_misses_the_due_time()
    {
        var r = Estimate(200);
        Assert.Equal("high", r.Level);
        Assert.True(r.ProjectedCompletionUtc > Due);
        Assert.Equal(80, r.ProjectedShortfall);   // 800 − 36 × 20
    }

    [Fact]
    public void Insufficient_data_without_a_plan_or_without_output()
    {
        var noPlan = Estimate(400, windows: Array.Empty<WorkWindow>());
        Assert.Equal("insufficient_data", noPlan.Level);
        Assert.Contains(noPlan.MissingInputs, m => m.Contains("active production plan"));

        var noOutput = Estimate(0, recent: new[] { new DailyRateSample(new DateOnly(2026, 10, 26), 0, 240, 0) });
        Assert.Equal("insufficient_data", noOutput.Level);
        Assert.Null(noOutput.ObservedRatePerLineHour);
        Assert.Null(noOutput.ProjectedCompletionUtc);
    }

    [Fact]
    public void A_finished_order_is_complete()
    {
        Assert.Equal(("complete", 0), (Estimate(1000).Level, Estimate(1200).RemainingQuantity));
    }

    [Fact]
    public void Parallel_lines_add_their_scheduled_time()
    {
        var day = new DateOnly(2026, 10, 26);
        var two = new[]
        {
            new WorkWindow(Prod.Utc(2026, 10, 26, 8), Prod.Utc(2026, 10, 26, 16), day, "day"),
            new WorkWindow(Prod.Utc(2026, 10, 26, 8), Prod.Utc(2026, 10, 26, 16), day, "day"),
        };
        // 480 pieces at 60 per line-hour = 8 line-hours = 4 h on two lines.
        Assert.Equal(Prod.Utc(2026, 10, 26, 12), DelayRiskEstimator.Project(two, Prod.Utc(2026, 10, 26, 8), 480, 60));
        Assert.Null(DelayRiskEstimator.Project(two, Prod.Utc(2026, 10, 26, 8), 2000, 60));
    }
}

public sealed class ProductionRecommenderTests
{
    private static readonly DelayRiskResult High = new("high", 800, 1200, 40, 36, 30, 40, 40, Prod.Utc(2026, 10, 29, 10), null, null, null, 80, 20,
        new[] { "observed" }, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    [Fact]
    public void Shortfall_recommendations_show_their_arithmetic_and_caveats()
    {
        var recs = ProductionRecommender.For(High, new RecommendationContext(1, 60, 50, 30,
            new[] { ("line-1", "operation 2 (Collar)", 30.0, "observed") }, 45, new[] { "needle break" }));
        // 800 ÷ 36 = 22.2 line-hours needed, 20 scheduled → about 2.3 extra.
        Assert.Contains("2.3 extra line-hours", recs.Single(r => r.Code == "overtime").Action);
        // 40 × 50 ÷ (60 × 0.6) = 55.6 → 56 operators.
        Assert.Contains("56 operators", recs.Single(r => r.Code == "add_operators").Action);
        Assert.Contains("operation 2 (Collar)", recs.Single(r => r.Code == "relieve_constraint").Action);
        Assert.Contains("roughly 27 pieces", recs.Single(r => r.Code == "reduce_downtime").Reason);
        var escalate = recs.Single(r => r.Code == "escalate");
        Assert.Contains("does not show the cause", escalate.Caveat);
        Assert.All(recs, r => Assert.False(string.IsNullOrWhiteSpace(r.Caveat)));
    }

    [Fact]
    public void Insufficient_data_asks_for_data_instead_of_suggesting_interventions()
    {
        var r = High with { Level = "insufficient_data", ObservedRatePerLineHour = null, MissingInputs = new[] { "Recorded sewing output." } };
        var recs = ProductionRecommender.For(r, new RecommendationContext(1, null, null, null, Array.Empty<(string, string, double, string)>(), 0, Array.Empty<string>()));
        Assert.Equal("record_data", Assert.Single(recs).Code);
    }
}

public sealed class ProductionOptionsTests
{
    [Fact]
    public void Defaults_are_documented_values()
    {
        var o = ProductionOptions.FromEnvironment(_ => null);
        Assert.Equal(("UTC", true, 31, 12, 3, 10.0, 14), (o.TimeZoneId, o.RequireSeparateApprover, o.MaxBackfillDays, o.MaxIntervalHours, o.RateLookbackDays, o.RiskSlackPct, o.ObservationMaxAgeDays));
        Assert.Null(o.ConfigurationError);
    }

    [Fact]
    public void Invalid_values_fall_back_to_defaults_and_say_so()
    {
        var env = new Dictionary<string, string>
        {
            ["PRODUCTION_TIME_ZONE"] = "Mars/Olympus", ["PRODUCTION_MAX_BACKFILL_DAYS"] = "0", ["PRODUCTION_REQUIRE_SEPARATE_APPROVER"] = "maybe",
        };
        var o = ProductionOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(("UTC", 31, true), (o.TimeZoneId, o.MaxBackfillDays, o.RequireSeparateApprover));
        Assert.Contains("PRODUCTION_TIME_ZONE", o.ConfigurationError);
        Assert.Contains("PRODUCTION_MAX_BACKFILL_DAYS", o.ConfigurationError);
        Assert.Contains("PRODUCTION_REQUIRE_SEPARATE_APPROVER", o.ConfigurationError);
    }

    [Fact]
    public void Plant_time_zones_resolve_by_iana_or_windows_id()
    {
        Assert.NotNull(ProductionOptions.ResolveTimeZone("Asia/Dhaka") ?? ProductionOptions.ResolveTimeZone("Bangladesh Standard Time"));
        Assert.Same(TimeZoneInfo.Utc, ProductionOptions.ResolveTimeZone("utc"));
    }
}
