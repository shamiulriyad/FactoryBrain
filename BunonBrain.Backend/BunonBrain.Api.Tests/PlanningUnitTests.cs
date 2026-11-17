using BunonBrain.Application.Access;
using BunonBrain.Application.Planning;
using BunonBrain.Application.Production;
using BunonBrain.Domain.Entities;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Production planning rules without a database: the order lifecycle, allocation limits, target scopes
/// and precedence, plan statuses, shift time (including overnight shifts and time-zone boundaries),
/// planned-versus-actual, delivery forecast, capacity estimation (and when it must refuse to give a
/// number), exception rules (evidence, no blame) and the permission catalog.
/// </summary>
public sealed class PlanningUnitTests
{
    private static readonly TimeZoneInfo Dhaka = TimeZoneInfo.CreateCustomTimeZone("Asia/Dhaka-test", TimeSpan.FromHours(6), "Dhaka", "Dhaka");

    // ─── Order lifecycle ───────────────────────────────────────────────────

    [Theory]
    [InlineData(ProductionOrderStatus.Draft, ProductionOrderStatus.Planned, true)]
    [InlineData(ProductionOrderStatus.Draft, ProductionOrderStatus.Cancelled, true)]
    [InlineData(ProductionOrderStatus.Draft, ProductionOrderStatus.InProgress, false)]
    [InlineData(ProductionOrderStatus.Draft, ProductionOrderStatus.Completed, false)]
    [InlineData(ProductionOrderStatus.Planned, ProductionOrderStatus.InProgress, true)]
    [InlineData(ProductionOrderStatus.Planned, ProductionOrderStatus.OnHold, true)]
    [InlineData(ProductionOrderStatus.Planned, ProductionOrderStatus.Completed, false)]
    [InlineData(ProductionOrderStatus.Planned, ProductionOrderStatus.Draft, false)]
    [InlineData(ProductionOrderStatus.InProgress, ProductionOrderStatus.Completed, true)]
    [InlineData(ProductionOrderStatus.InProgress, ProductionOrderStatus.OnHold, true)]
    [InlineData(ProductionOrderStatus.OnHold, ProductionOrderStatus.InProgress, true)]
    [InlineData(ProductionOrderStatus.OnHold, ProductionOrderStatus.Completed, false)]
    [InlineData(ProductionOrderStatus.Completed, ProductionOrderStatus.InProgress, false)]
    [InlineData(ProductionOrderStatus.Completed, ProductionOrderStatus.Cancelled, false)]
    [InlineData(ProductionOrderStatus.Cancelled, ProductionOrderStatus.Planned, false)]
    public void Order_status_changes_follow_the_lifecycle(ProductionOrderStatus from, ProductionOrderStatus to, bool allowed) =>
        Assert.Equal(allowed, OrderLifecycle.CanMove(from, to));

    [Fact]
    public void Final_orders_have_no_way_out_and_only_live_orders_can_be_scheduled()
    {
        Assert.Empty(OrderLifecycle.AllowedFrom(ProductionOrderStatus.Completed));
        Assert.Empty(OrderLifecycle.AllowedFrom(ProductionOrderStatus.Cancelled));
        Assert.True(OrderLifecycle.IsFinal(ProductionOrderStatus.Cancelled));
        Assert.True(OrderLifecycle.IsLive(ProductionOrderStatus.Planned));
        Assert.True(OrderLifecycle.IsLive(ProductionOrderStatus.InProgress));
        Assert.False(OrderLifecycle.IsLive(ProductionOrderStatus.Draft));
        Assert.False(OrderLifecycle.IsLive(ProductionOrderStatus.OnHold));
        Assert.True(OrderLifecycle.NeedsReason(ProductionOrderStatus.OnHold));
        Assert.True(OrderLifecycle.NeedsReason(ProductionOrderStatus.Cancelled));
        Assert.False(OrderLifecycle.NeedsReason(ProductionOrderStatus.Completed));
    }

    [Fact]
    public void An_order_needs_a_buyer_and_a_factory_before_it_is_planned()
    {
        Assert.Equal(new[] { "buyer", "factoryId" }, OrderLifecycle.MissingForPlanned(null, null));
        Assert.Equal(new[] { "buyer" }, OrderLifecycle.MissingForPlanned("  ", Guid.NewGuid()));
        Assert.Empty(OrderLifecycle.MissingForPlanned("H&M", Guid.NewGuid()));
    }

    [Theory]
    [InlineData("open", ProductionOrderStatus.InProgress)]
    [InlineData("closed", ProductionOrderStatus.Completed)]
    [InlineData("IN_PROGRESS", ProductionOrderStatus.InProgress)]
    [InlineData("on-hold", ProductionOrderStatus.OnHold)]
    [InlineData("Planned", ProductionOrderStatus.Planned)]
    public void The_wire_accepts_the_lifecycle_names_and_the_two_legacy_ones(string raw, ProductionOrderStatus expected)
    {
        Assert.True(ProductionWire.TryParseOrderStatus(raw, out var s));
        Assert.Equal(expected, s);
    }

    [Fact]
    public void An_unknown_order_status_is_refused() => Assert.False(ProductionWire.TryParseOrderStatus("shipped", out _));

    [Fact]
    public void Priorities_rank_from_low_to_urgent()
    {
        Assert.Equal("URGENT", OrderPriorities.Parse("urgent"));
        Assert.Null(OrderPriorities.Parse("asap"));
        Assert.True(OrderPriorities.Rank("URGENT") > OrderPriorities.Rank("HIGH"));
        Assert.True(OrderPriorities.Rank("NORMAL") > OrderPriorities.Rank("LOW"));
    }

    // ─── Allocations ───────────────────────────────────────────────────────

    [Fact]
    public void Allocations_may_not_exceed_the_order_quantity()
    {
        var ok = AllocationRules.CheckQuantity(1000, 600, 400);
        Assert.False(ok.Exceeds);
        Assert.Equal(0, ok.Remaining);
        var over = AllocationRules.CheckQuantity(1000, 600, 450);
        Assert.True(over.Exceeds);
        Assert.Equal(50, over.Excess);
        Assert.Equal(1050, over.Total);
    }

    [Fact]
    public void An_order_can_be_split_over_several_lines_up_to_its_quantity()
    {
        // Three lines in turn: each sees what the others already hold.
        var first = AllocationRules.CheckQuantity(900, 0, 300);
        var second = AllocationRules.CheckQuantity(900, 300, 300);
        var third = AllocationRules.CheckQuantity(900, 600, 300);
        Assert.All(new[] { first, second, third }, c => Assert.False(c.Exceeds));
        Assert.True(AllocationRules.CheckQuantity(900, 900, 1).Exceeds);
    }

    [Fact]
    public void Date_ranges_overlap_only_when_they_share_a_day()
    {
        var d = (int day) => new DateOnly(2026, 11, day);
        Assert.True(AllocationRules.Overlaps(d(1), d(5), d(5), d(9)));
        Assert.False(AllocationRules.Overlaps(d(1), d(4), d(5), d(9)));
        Assert.Equal(1, AllocationRules.OverlapDays(d(1), d(5), d(5), d(9)));
        Assert.Equal(0, AllocationRules.OverlapDays(d(1), d(4), d(5), d(9)));
        Assert.Equal(3, AllocationRules.OverlapDays(d(1), d(10), d(4), d(6)));
        Assert.False(AllocationRules.DatesValid(d(5), d(4)));
    }

    [Fact]
    public void Allocation_statuses_only_move_forwards()
    {
        Assert.True(AllocationStatuses.CanMove("PLANNED", "ACTIVE"));
        Assert.True(AllocationStatuses.CanMove("ACTIVE", "COMPLETED"));
        Assert.True(AllocationStatuses.CanMove("PLANNED", "CANCELLED"));
        Assert.False(AllocationStatuses.CanMove("COMPLETED", "ACTIVE"));
        Assert.False(AllocationStatuses.CanMove("CANCELLED", "PLANNED"));
        Assert.False(AllocationStatuses.Counts(AllocationStatuses.Cancelled));
        Assert.True(AllocationStatuses.Counts(AllocationStatuses.Completed));
    }

    // ─── Targets ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FACTORY", false, false, false, false, false, false, true)]
    [InlineData("FACTORY", true, false, false, false, false, false, false)]
    [InlineData("DEPARTMENT", true, false, false, false, false, false, true)]
    [InlineData("DEPARTMENT", false, false, false, false, false, false, false)]
    [InlineData("FLOOR", false, true, false, false, false, false, true)]
    [InlineData("LINE", false, false, true, false, false, false, true)]
    [InlineData("LINE", false, false, true, true, false, false, true)]
    [InlineData("LINE", false, false, false, false, false, false, false)]
    [InlineData("SHIFT", false, false, false, true, false, false, true)]
    [InlineData("SHIFT", false, false, true, true, false, false, false)]
    [InlineData("WORKSTATION", false, false, false, false, true, false, true)]
    [InlineData("MACHINE", false, false, false, false, false, true, true)]
    [InlineData("MACHINE", false, false, false, false, true, true, false)]
    public void A_target_carries_exactly_the_ids_its_level_needs(string level, bool dept, bool floor, bool line, bool shift, bool ws, bool machine, bool valid)
    {
        var scope = new TargetRules.Scope(dept ? Guid.NewGuid() : null, floor ? Guid.NewGuid() : null, line ? "line-1" : null, shift ? Guid.NewGuid() : null,
            ws ? Guid.NewGuid() : null, machine ? Guid.NewGuid() : null);
        Assert.Equal(valid, TargetRules.ValidateScope(level, scope).Count == 0);
    }

    [Fact]
    public void Only_wide_targets_wait_for_approval()
    {
        Assert.True(TargetLevels.RequiresApproval("FACTORY"));
        Assert.True(TargetLevels.RequiresApproval("DEPARTMENT"));
        Assert.True(TargetLevels.RequiresApproval("FLOOR"));
        Assert.False(TargetLevels.RequiresApproval("LINE"));
        Assert.False(TargetLevels.RequiresApproval("MACHINE"));
        Assert.True(TargetApprovals.InForce("APPROVED"));
        Assert.True(TargetApprovals.InForce("NOT_REQUIRED"));
        Assert.False(TargetApprovals.InForce("PENDING"));
        Assert.False(TargetApprovals.InForce("REJECTED"));
    }

    [Fact]
    public void Target_quantities_are_bounded_per_period()
    {
        Assert.NotNull(TargetRules.ValidateQuantity("DAILY", 0));
        Assert.Null(TargetRules.ValidateQuantity("DAILY", 600));
        Assert.NotNull(TargetRules.ValidateQuantity("HOURLY", 100_001));
        Assert.Null(TargetRules.ValidateQuantity("SHIFT", 5000));
    }

    [Fact]
    public void The_most_specific_target_wins()
    {
        var plain = new ProductionTarget { Level = "LINE" };
        var shift = new ProductionTarget { Level = "LINE", ShiftId = Guid.NewGuid() };
        var order = new ProductionTarget { Level = "LINE", OrderId = Guid.NewGuid() };
        var allocation = new ProductionTarget { Level = "LINE", OrderId = Guid.NewGuid(), AllocationId = Guid.NewGuid() };
        var ranked = new[] { plain, shift, order, allocation }.OrderByDescending(TargetRules.Specificity).ToList();
        Assert.Same(allocation, ranked[0]);
        Assert.Same(order, ranked[1]);
        Assert.Same(shift, ranked[2]);
        Assert.Same(plain, ranked[3]);
    }

    // ─── Daily plan statuses ───────────────────────────────────────────────

    [Fact]
    public void A_plan_moves_draft_review_approved_published_and_revisions_replace_it()
    {
        Assert.True(PlanStatuses.CanMove("DRAFT", "UNDER_REVIEW"));
        Assert.True(PlanStatuses.CanMove("UNDER_REVIEW", "APPROVED"));
        Assert.True(PlanStatuses.CanMove("APPROVED", "PUBLISHED"));
        Assert.True(PlanStatuses.CanMove("PUBLISHED", "REVISED"));
        Assert.True(PlanStatuses.CanMove("UNDER_REVIEW", "DRAFT"));
        Assert.False(PlanStatuses.CanMove("DRAFT", "PUBLISHED"));
        Assert.False(PlanStatuses.CanMove("DRAFT", "APPROVED"));
        Assert.False(PlanStatuses.CanMove("REVISED", "PUBLISHED"));
        Assert.False(PlanStatuses.CanMove("CANCELLED", "DRAFT"));
        Assert.False(PlanStatuses.CanMove("PUBLISHED", "DRAFT"));
    }

    [Fact]
    public void Followers_see_published_plans_never_drafts()
    {
        Assert.True(PlanStatuses.VisibleToFollowers("PUBLISHED"));
        Assert.True(PlanStatuses.VisibleToFollowers("REVISED"));
        Assert.False(PlanStatuses.VisibleToFollowers("DRAFT"));
        Assert.False(PlanStatuses.VisibleToFollowers("APPROVED"));
        Assert.True(PlanStatuses.Editable("DRAFT"));
        Assert.False(PlanStatuses.Editable("UNDER_REVIEW"));
    }

    // ─── Shift time ────────────────────────────────────────────────────────

    private static PlanShift DayShift(int workingDays = 0b0111111) =>
        new(Guid.NewGuid(), "DAY", "Day", new TimeOnly(8, 0), new TimeOnly(17, 0), workingDays, new[] { new ScheduleBreak(new TimeOnly(12, 0), 60) });

    private static PlanShift NightShift() =>
        new(Guid.NewGuid(), "NIGHT", "Night", new TimeOnly(22, 0), new TimeOnly(6, 0), 0b0111111, new[] { new ScheduleBreak(new TimeOnly(2, 0), 30) });

    [Fact]
    public void Productive_time_is_the_shift_minus_its_breaks()
    {
        Assert.Equal(540, DayShift().GrossMinutes);
        Assert.Equal(480, DayShift().ProductiveMinutes);
        Assert.Equal(480, NightShift().GrossMinutes);
        Assert.Equal(450, NightShift().ProductiveMinutes);
    }

    [Fact]
    public void A_shift_is_placed_in_utc_by_the_factory_time_zone()
    {
        var s = new ShiftSchedule(DayShift(), new DateOnly(2026, 11, 16), Dhaka); // a Monday
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero), s.Start);   // 08:00 +06:00
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 11, 0, 0, TimeSpan.Zero), s.End);
        Assert.True(s.Works);
        Assert.Equal(480, s.TotalProductiveMinutes);
    }

    [Fact]
    public void An_overnight_shift_belongs_to_the_day_it_starts_and_ends_the_next_day()
    {
        var s = new ShiftSchedule(NightShift(), new DateOnly(2026, 11, 16), Dhaka);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 16, 0, 0, TimeSpan.Zero), s.Start); // 22:00 local on the 16th
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 0, 0, 0, TimeSpan.Zero).AddDays(1), s.End); // 06:00 local on the 17th
        Assert.Equal(450, s.TotalProductiveMinutes);
        // 01:00 local on the 17th is still the 16th's night shift: 3 hours in, no break yet.
        Assert.Equal(180, s.ProductiveMinutesUntil(new DateTimeOffset(2026, 11, 16, 19, 0, 0, TimeSpan.Zero)), 3);
        // 02:30 local: 4.5 h in, of which the 02:00-02:30 break is not productive.
        Assert.Equal(240, s.ProductiveMinutesUntil(new DateTimeOffset(2026, 11, 16, 20, 30, 0, TimeSpan.Zero)), 3);
    }

    [Fact]
    public void A_shift_on_a_non_working_weekday_has_no_productive_time()
    {
        // A shift that works on Saturdays only.
        var satOnly = DayShift(1 << (int)DayOfWeek.Saturday);
        var monday = new ShiftSchedule(satOnly, new DateOnly(2026, 11, 16), Dhaka);
        Assert.False(monday.Works);
        Assert.Equal(0, monday.TotalProductiveMinutes);
        Assert.Equal(0, monday.ProductiveMinutesUntil(monday.End));
        var saturday = new ShiftSchedule(satOnly, new DateOnly(2026, 11, 14), Dhaka);
        Assert.True(saturday.Works);
    }

    [Fact]
    public void Elapsed_productive_time_skips_the_break_and_is_capped_at_the_shift()
    {
        var s = new ShiftSchedule(DayShift(), new DateOnly(2026, 11, 16), Dhaka);
        var at = (int h, int m) => new DateTimeOffset(2026, 11, 16, h, m, 0, TimeSpan.Zero);
        Assert.Equal(0, s.ProductiveMinutesUntil(at(1, 0)));               // before the shift
        Assert.Equal(240, s.ProductiveMinutesUntil(at(6, 0)), 3);          // 08:00-12:00 local
        Assert.Equal(240, s.ProductiveMinutesUntil(at(6, 30)), 3);         // inside the 12:00-13:00 break
        Assert.Equal(270, s.ProductiveMinutesUntil(at(7, 30)), 3);         // 13:30 local: 5.5 h gross minus the 1 h break... = 4.5 h productive
        Assert.Equal(480, s.ProductiveMinutesUntil(at(12, 0)), 3);         // after the shift: capped at its productive total
    }

    // ─── Planned versus actual ─────────────────────────────────────────────

    private static ShiftSchedule Morning() => new(DayShift(), new DateOnly(2026, 11, 16), Dhaka);

    private static DateTimeOffset LocalAt(int hour, int minute) => new(2026, 11, 16, hour - 6, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Before_the_shift_nothing_is_judged()
    {
        var p = PlanMath.Progress(480, null, ActualBases.None, Morning(), LocalAt(7, 0));
        Assert.Equal(ItemStates.NotStarted, p.State);
        var q = PlanMath.Progress(480, 0, ActualBases.Counter, Morning(), LocalAt(7, 0));
        Assert.Equal(ItemStates.NotStarted, q.State);
    }

    [Fact]
    public void No_evidence_is_no_data_not_zero()
    {
        var p = PlanMath.Progress(480, null, ActualBases.None, Morning(), LocalAt(11, 0));
        Assert.Equal(ItemStates.NoData, p.State);
        Assert.Null(p.Actual);
        Assert.Null(p.AchievementPct);
        Assert.Null(p.Remaining);
    }

    [Theory]
    [InlineData(180, ItemStates.OnTrack)]   // expected by 11:00 (3 productive h of 8): 180
    [InlineData(170, ItemStates.OnTrack)]   // within the 10 % tolerance
    [InlineData(100, ItemStates.Behind)]
    [InlineData(200, ItemStates.Ahead)]
    public void Mid_shift_progress_is_judged_against_the_straight_line_expectation(int actual, string state)
    {
        var p = PlanMath.Progress(480, actual, ActualBases.Counter, Morning(), LocalAt(11, 0));
        Assert.Equal(180, p.ExpectedByNow);
        Assert.Equal(state, p.State);
    }

    [Fact]
    public void A_finished_shift_is_complete_or_missed()
    {
        Assert.Equal(ItemStates.Complete, PlanMath.Progress(480, 500, ActualBases.Counter, Morning(), LocalAt(18, 0)).State);
        var missed = PlanMath.Progress(480, 400, ActualBases.Counter, Morning(), LocalAt(18, 0));
        Assert.Equal(ItemStates.Missed, missed.State);
        Assert.Equal(83.3, missed.AchievementPct);
        Assert.Equal(80, missed.Remaining);
    }

    [Fact]
    public void Achievement_and_rates_handle_a_missing_denominator()
    {
        Assert.Null(PlanMath.Pct(10, 0));
        Assert.Equal(50, PlanMath.Pct(5, 10));
        Assert.Null(PlanMath.HourlyRate(100, 0));
        Assert.Equal(60, PlanMath.HourlyRate(120, 120));
        Assert.Equal(60, PlanMath.HourlyTargetOf(480, 480));
        Assert.Null(PlanMath.HourlyTargetOf(480, 0));
    }

    // ─── Delivery forecast ─────────────────────────────────────────────────

    private static readonly Func<DateOnly, bool> SatToThu = d => d.DayOfWeek != DayOfWeek.Friday;

    [Fact]
    public void A_forecast_needs_enough_verified_days()
    {
        var f = OrderForecasts.Forecast(1000, 100, new[] { (new DateOnly(2026, 11, 15), 100) }, new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 30), SatToThu, ActualBases.Counter);
        Assert.Equal(OrderForecasts.Insufficient, f.Status);
        Assert.Null(f.ProjectedCompletion);
        Assert.Null(f.MissesDeadline);
    }

    [Fact]
    public void An_order_on_pace_is_on_track_and_one_behind_is_at_risk()
    {
        var days = new[] { (new DateOnly(2026, 11, 14), 100), (new DateOnly(2026, 11, 15), 100) };
        var onTrack = OrderForecasts.Forecast(1000, 200, days, new DateOnly(2026, 11, 15), new DateOnly(2026, 12, 31), SatToThu, ActualBases.Counter);
        Assert.Equal(OrderForecasts.OnTrack, onTrack.Status);
        Assert.Equal(8, onTrack.WorkingDaysNeeded); // 800 left at 100 a day
        Assert.False(onTrack.MissesDeadline);
        var atRisk = OrderForecasts.Forecast(1000, 200, days, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 22), SatToThu, ActualBases.Counter);
        Assert.Equal(OrderForecasts.AtRisk, atRisk.Status);
        Assert.True(atRisk.MissesDeadline);
        Assert.True(atRisk.WorkingDaysLeft < atRisk.WorkingDaysNeeded);
    }

    [Fact]
    public void Non_working_days_do_not_count_towards_the_deadline()
    {
        // From Saturday the 14th to Thursday the 19th there are 5 working days after "today" (Sat 14th): Sun..Thu; the Friday is off.
        var days = new[] { (new DateOnly(2026, 11, 12), 100), (new DateOnly(2026, 11, 13), 100) };
        var f = OrderForecasts.Forecast(700, 200, days, new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 20), SatToThu, ActualBases.Counter);
        Assert.Equal(5, f.WorkingDaysNeeded);
        Assert.Equal(5, f.WorkingDaysLeft); // Sat 14 is "today"; Sun-Thu 15-19 count; Fri 20 is off
        Assert.Equal(OrderForecasts.OnTrack, f.Status);
    }

    [Fact]
    public void A_passed_deadline_with_pieces_left_is_overdue_and_a_finished_order_is_complete()
    {
        var overdue = OrderForecasts.Forecast(1000, 400, Array.Empty<(DateOnly, int)>(), new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 15), SatToThu, ActualBases.Counter);
        Assert.Equal(OrderForecasts.Overdue, overdue.Status);
        var done = OrderForecasts.Forecast(1000, 1000, Array.Empty<(DateOnly, int)>(), new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 15), SatToThu, ActualBases.Counter);
        Assert.Equal(OrderForecasts.Complete, done.Status);
    }

    // ─── Capacity ──────────────────────────────────────────────────────────

    private static CapacityInput Input(double? smv = 10, double? eff = 60, int? ops = 20, int qty = 1000, params CapacityDay[] days) =>
        new(smv, "approved", eff, ops, "configured", 25, days.Length == 0 ? new[] { new CapacityDay(new DateOnly(2026, 11, 16), 480), new CapacityDay(new DateOnly(2026, 11, 17), 480) } : days,
            qty, Array.Empty<CapacityCommitment>(), Array.Empty<MachineTypeCheck>());

    [Fact]
    public void Capacity_is_productive_minutes_times_operators_times_efficiency_over_smv()
    {
        var r = CapacityEstimator.Estimate(Input());
        Assert.Equal(CapacityEstimator.Complete, r.Status);
        // 960 min × 20 operators × 0.60 ÷ 10 SMV = 1152 pieces
        Assert.Equal(1152.0, r.CapacityPieces);
        Assert.Equal(86.8, r.UtilizationPct);   // 1000 / 1152
        Assert.Equal(0.0, r.ShortagePieces);
        Assert.True(r.CanMeet);
        Assert.Contains(r.Assumptions, a => a.Label == "Efficiency");
    }

    [Fact]
    public void A_shortage_is_reported_when_the_plan_is_above_capacity()
    {
        var r = CapacityEstimator.Estimate(Input(qty: 1500));
        Assert.Equal(CapacityEstimator.Complete, r.Status);
        Assert.Equal(348.0, r.ShortagePieces);
        Assert.False(r.CanMeet);
        Assert.True(r.UtilizationPct > 100);
    }

    [Fact]
    public void Missing_inputs_make_the_estimate_incomplete_with_no_number()
    {
        var r = CapacityEstimator.Estimate(Input(smv: null, eff: null, ops: null));
        Assert.Equal(CapacityEstimator.Incomplete, r.Status);
        Assert.Null(r.CapacityPieces);
        Assert.Null(r.UtilizationPct);
        Assert.Null(r.ShortagePieces);
        Assert.Null(r.CanMeet);
        Assert.Equal(new[] { "smv_missing", "efficiency_missing", "operators_missing" }, r.Missing.Select(m => m.Code));
        Assert.All(r.Missing, m => Assert.False(string.IsNullOrWhiteSpace(m.HowToFix)));
    }

    [Fact]
    public void No_scheduled_time_is_incomplete_too()
    {
        var r = CapacityEstimator.Estimate(Input(days: new[] { new CapacityDay(new DateOnly(2026, 11, 20), 0) }));
        Assert.Equal(CapacityEstimator.Incomplete, r.Status);
        Assert.Contains(r.Missing, m => m.Code == "no_working_time");
    }

    [Fact]
    public void Other_allocations_on_the_line_use_up_capacity_in_proportion_to_overlap()
    {
        // 600 pieces over 6 working days overlap this 2-day range for 2 days: 200 pieces.
        var working = (DateOnly d) => d.DayOfWeek != DayOfWeek.Friday;
        var c = CapacityEstimator.Commit(Guid.NewGuid(), Guid.NewGuid(), "PO-2", 600, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 21), "ACTIVE",
            new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 17), working);
        Assert.Equal(2, c.OverlapDays);
        Assert.Equal(200.0, c.OverlappingQuantity);
        var withOther = CapacityEstimator.Estimate(Input() with { Commitments = new[] { c } });
        Assert.Equal(200.0, withOther.CommittedElsewhere);
        Assert.Equal(1200.0, withOther.PlannedQuantity + withOther.CommittedElsewhere);
        Assert.Equal(104.2, withOther.UtilizationPct);
        Assert.Single(withOther.Conflicts);
    }

    [Fact]
    public void Machine_types_are_matched_without_pretending()
    {
        var machines = new (string? Type, bool Faulty)[] { ("SNLS", false), ("SNLS", true), ("OL", true) };
        Assert.Equal("OK", CapacityEstimator.CheckMachineType("SNLS", machines).State);
        Assert.Equal("UNAVAILABLE", CapacityEstimator.CheckMachineType("OL", machines).State);
        Assert.Equal("NOT_FOUND", CapacityEstimator.CheckMachineType("Buttonhole", machines).State);
        Assert.Equal("UNVERIFIED", CapacityEstimator.CheckMachineType("SNLS", new (string?, bool)[] { (null, false) }).State);
        Assert.Equal("OK", CapacityEstimator.CheckMachineType("manual", machines).State);
    }

    [Fact]
    public void A_line_whose_only_machine_of_a_needed_type_is_down_cannot_meet_the_plan()
    {
        var r = CapacityEstimator.Estimate(Input() with { Machines = new[] { new MachineTypeCheck("OL", 0, 2, "UNAVAILABLE") } });
        Assert.Equal(CapacityEstimator.Complete, r.Status);
        Assert.False(r.CanMeet);
        Assert.Contains(r.Warnings, w => w.Contains("OL", StringComparison.Ordinal));
    }

    [Fact]
    public void More_operators_than_workstations_is_called_out()
    {
        var r = CapacityEstimator.Estimate(Input(ops: 40));
        Assert.Contains(r.Warnings, w => w.Contains("workstations", StringComparison.Ordinal));
    }

    // ─── Exceptions ────────────────────────────────────────────────────────

    private static ItemFacts Item(ItemProgress p, bool hasPoint = true, int events = 5, bool live = true) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "PO-1", "line-1", "Line 1", Guid.NewGuid(), "Day", new DateOnly(2026, 11, 16), p, hasPoint, events, null, live);

    [Fact]
    public void Output_behind_the_target_is_raised_with_its_evidence()
    {
        var p = PlanMath.Progress(480, 100, ActualBases.Counter, Morning(), LocalAt(11, 0));
        var d = ExceptionRules.OutputBehind(Item(p));
        Assert.NotNull(d);
        Assert.Equal(ExceptionTypes.OutputBehindTarget, d!.Type);
        Assert.Equal(Severities.High, d.Severity); // 100 of the 180 expected = 56 %: below 60 → HIGH
        Assert.Contains(d.Data, f => f.Label == "Actual" && f.Value == "100");
        Assert.Contains(d.Data, f => f.Label == "Target" && f.Value == "480");
        Assert.False(string.IsNullOrWhiteSpace(d.SuggestedInvestigation));
    }

    [Fact]
    public void An_exception_never_blames_a_person()
    {
        var p = PlanMath.Progress(480, 100, ActualBases.Counter, Morning(), LocalAt(11, 0));
        var all = new[]
        {
            ExceptionRules.OutputBehind(Item(p)),
            ExceptionRules.MissingCounter(Item(PlanMath.Progress(480, null, ActualBases.None, Morning(), LocalAt(11, 0)), events: 0)),
            ExceptionRules.Interruption(new StoppageFacts("line-1", "Line 1", null, "Day", new DateOnly(2026, 11, 16), new[] { ("M-1", "STOPPED", 40) }, 10)),
        };
        Assert.All(all, d => Assert.NotNull(d));
        foreach (var d in all)
        {
            var text = $"{d!.Title} {d.Reason} {d.SuggestedInvestigation}".ToLowerInvariant();
            foreach (var word in new[] { "worker", "lazy", "fault", "negligen", "blame", "slow" })
                Assert.DoesNotContain(word, text);
        }
        // Detections that point at output say outright that they are not a verdict on anyone.
        Assert.Contains("who is responsible", all[0]!.SuggestedInvestigation);
        Assert.Contains("who is responsible", all[2]!.SuggestedInvestigation);
    }

    [Fact]
    public void Output_on_track_or_unknown_raises_nothing_behind()
    {
        Assert.Null(ExceptionRules.OutputBehind(Item(PlanMath.Progress(480, 180, ActualBases.Counter, Morning(), LocalAt(11, 0)))));
        Assert.Null(ExceptionRules.OutputBehind(Item(PlanMath.Progress(480, null, ActualBases.None, Morning(), LocalAt(11, 0)))));
        // Too early in the shift to judge the pace.
        Assert.Null(ExceptionRules.OutputBehind(Item(PlanMath.Progress(480, 1, ActualBases.Counter, Morning(), LocalAt(8, 20)))));
        // The order is on hold: not the plan's concern.
        Assert.Null(ExceptionRules.OutputBehind(Item(PlanMath.Progress(480, 100, ActualBases.Counter, Morning(), LocalAt(11, 0)), live: false)));
    }

    [Fact]
    public void A_missed_shift_is_raised_once_it_has_ended()
    {
        var p = PlanMath.Progress(480, 300, ActualBases.Counter, Morning(), LocalAt(18, 0));
        var d = ExceptionRules.OutputBehind(Item(p));
        Assert.NotNull(d);
        Assert.Contains("62.5", d!.Reason);
    }

    [Fact]
    public void Missing_counter_data_is_a_data_gap_not_zero_production()
    {
        var none = PlanMath.Progress(480, null, ActualBases.None, Morning(), LocalAt(11, 0));
        var silent = ExceptionRules.MissingCounter(Item(none, hasPoint: true, events: 0));
        Assert.NotNull(silent);
        Assert.Equal(Severities.Medium, silent!.Severity);
        Assert.Contains("not zero", silent.Reason, StringComparison.OrdinalIgnoreCase);
        var unconfigured = ExceptionRules.MissingCounter(Item(none, hasPoint: false, events: 0));
        Assert.Equal(Severities.Low, unconfigured!.Severity);
        Assert.Null(ExceptionRules.MissingCounter(Item(none, hasPoint: true, events: 3))); // events exist: not a data gap
        Assert.Null(ExceptionRules.MissingCounter(Item(PlanMath.Progress(480, null, ActualBases.None, Morning(), LocalAt(8, 10)), events: 0))); // too early
    }

    [Fact]
    public void A_delivery_that_cannot_be_met_is_raised_with_the_forecast_behind_it()
    {
        var days = new[] { (new DateOnly(2026, 11, 14), 100), (new DateOnly(2026, 11, 15), 100) };
        var fc = OrderForecasts.Forecast(1000, 200, days, new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 19), SatToThu, ActualBases.Counter);
        var d = ExceptionRules.DeliveryAtRisk(new OrderFacts(Guid.NewGuid(), "PO-1", "H&M", new DateOnly(2026, 11, 19), 1000, 200, ActualBases.Counter, fc, new DateOnly(2026, 11, 15)));
        Assert.NotNull(d);
        Assert.Equal(Severities.Medium, d!.Severity);
        Assert.Contains(d.Data, f => f.Label == "Remaining" && f.Value == "800");
        var onTrack = OrderForecasts.Forecast(1000, 200, days, new DateOnly(2026, 11, 15), new DateOnly(2026, 12, 31), SatToThu, ActualBases.Counter);
        Assert.Null(ExceptionRules.DeliveryAtRisk(new OrderFacts(Guid.NewGuid(), "PO-1", null, new DateOnly(2026, 12, 31), 1000, 200, ActualBases.Counter, onTrack, new DateOnly(2026, 11, 15))));
    }

    [Fact]
    public void A_line_with_work_today_but_every_machine_down_is_unavailable()
    {
        var down = ExceptionRules.LineUnavailable(new LineFacts("line-1", "Line 1", true, 4, 4, new[] { "M-1", "M-2", "M-3", "M-4" }, true), new DateOnly(2026, 11, 16));
        Assert.NotNull(down);
        Assert.Equal(Severities.High, down!.Severity);
        Assert.Null(ExceptionRules.LineUnavailable(new LineFacts("line-1", "Line 1", true, 4, 1, new[] { "M-1" }, true), new DateOnly(2026, 11, 16)));
        Assert.Null(ExceptionRules.LineUnavailable(new LineFacts("line-1", "Line 1", true, 4, 4, new[] { "M-1" }, false), new DateOnly(2026, 11, 16)));
        Assert.NotNull(ExceptionRules.LineUnavailable(new LineFacts("line-1", "Line 1", false, 0, 0, Array.Empty<string>(), true), new DateOnly(2026, 11, 16)));
        // A line with no monitored machines can't be called unavailable on machine evidence.
        Assert.Null(ExceptionRules.LineUnavailable(new LineFacts("line-1", "Line 1", true, 0, 0, Array.Empty<string>(), true), new DateOnly(2026, 11, 16)));
    }

    [Fact]
    public void Only_stoppages_past_the_threshold_count_as_an_interruption()
    {
        var date = new DateOnly(2026, 11, 16);
        Assert.Null(ExceptionRules.Interruption(new StoppageFacts("l", "Line", null, "Day", date, new[] { ("M-1", "STOPPED", 5) }, 10)));
        var d = ExceptionRules.Interruption(new StoppageFacts("l", "Line", null, "Day", date, new[] { ("M-1", "STOPPED", 12), ("M-2", "ERROR", 40) }, 10));
        Assert.NotNull(d);
        Assert.Contains("M-2 ERROR 40 min", d!.Data.First(f => f.Label == "Machines").Value);
    }

    [Fact]
    public void Excess_allocation_is_flagged_and_softer_when_an_override_is_recorded()
    {
        Assert.Null(ExceptionRules.ExcessAllocation(new AllocationFacts(Guid.NewGuid(), Guid.NewGuid(), "PO-1", "line-1", "Line 1", 1000, 1000, false)));
        var plain = ExceptionRules.ExcessAllocation(new AllocationFacts(Guid.NewGuid(), Guid.NewGuid(), "PO-1", "line-1", "Line 1", 1000, 1100, false));
        var overridden = ExceptionRules.ExcessAllocation(new AllocationFacts(Guid.NewGuid(), Guid.NewGuid(), "PO-1", "line-1", "Line 1", 1000, 1100, true));
        Assert.Equal(Severities.Medium, plain!.Severity);
        Assert.Equal(Severities.Low, overridden!.Severity);
        Assert.Contains("100", plain.Reason);
    }

    [Fact]
    public void A_capacity_shortage_is_raised_only_from_a_complete_estimate()
    {
        var short1 = CapacityEstimator.Estimate(Input(qty: 1500));
        var d = ExceptionRules.CapacityShortage(Guid.NewGuid(), "PO-1", Guid.NewGuid(), "line-1", "Line 1", short1);
        Assert.NotNull(d);
        Assert.Contains("estimate", d!.SuggestedInvestigation, StringComparison.OrdinalIgnoreCase);
        var incomplete = CapacityEstimator.Estimate(Input(smv: null));
        Assert.Null(ExceptionRules.CapacityShortage(Guid.NewGuid(), "PO-1", Guid.NewGuid(), "line-1", "Line 1", incomplete));
        Assert.Null(ExceptionRules.CapacityShortage(Guid.NewGuid(), "PO-1", Guid.NewGuid(), "line-1", "Line 1", CapacityEstimator.Estimate(Input(qty: 500))));
    }

    [Fact]
    public void Severity_follows_the_size_of_the_shortfall()
    {
        Assert.Equal(Severities.High, PlanMath.SeverityForShortfall(40));
        Assert.Equal(Severities.Medium, PlanMath.SeverityForShortfall(70));
        Assert.Equal(Severities.Low, PlanMath.SeverityForShortfall(85));
    }

    // ─── Permissions ───────────────────────────────────────────────────────

    [Fact]
    public void Every_planning_permission_is_in_the_catalog_and_the_factory_manager_has_them_all()
    {
        var planning = AccessCatalog.AllPermissions.Where(p => p.Module == "Production planning").Select(p => p.Code).ToList();
        Assert.Equal(11, planning.Count);
        Assert.All(planning, code => Assert.StartsWith("planning.", code));
        var manager = AccessCatalog.PermissionsFor(new[] { SystemRoles.FactoryManager });
        Assert.All(planning, code => Assert.Contains(code, manager));
    }

    [Fact]
    public void Supervisors_set_targets_for_their_lines_but_do_not_allocate_approve_or_publish()
    {
        var sup = AccessCatalog.PermissionsFor(new[] { SystemRoles.ProductionSupervisor });
        Assert.Contains(Permissions.PlanningView, sup);
        Assert.Contains(Permissions.PlanningTargetsManage, sup);
        Assert.Contains(Permissions.PlanningExceptionsReview, sup);
        Assert.DoesNotContain(Permissions.PlanningAllocationsManage, sup);
        Assert.DoesNotContain(Permissions.PlanningPlansApprove, sup);
        Assert.DoesNotContain(Permissions.PlanningPlansPublish, sup);
        Assert.DoesNotContain(Permissions.PlanningOrdersManage, sup);
        Assert.Equal(RoleScope.Line, AccessCatalog.ScopeFor(new[] { SystemRoles.ProductionSupervisor }));
    }

    [Fact]
    public void Monitoring_operators_only_follow_the_plan()
    {
        var op = AccessCatalog.PermissionsFor(new[] { SystemRoles.MonitoringOperator }).Where(p => p.StartsWith("planning.", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { Permissions.PlanningView }, op);
        foreach (var role in new[] { SystemRoles.Worker, SystemRoles.HrOfficer, SystemRoles.DeviceAdmin })
            Assert.DoesNotContain(AccessCatalog.PermissionsFor(new[] { role }), p => p.StartsWith("planning.", StringComparison.Ordinal));
    }

    [Fact]
    public void Super_admin_and_factory_admin_hold_every_planning_permission()
    {
        foreach (var role in new[] { SystemRoles.SuperAdmin, SystemRoles.FactoryAdmin })
        {
            var perms = AccessCatalog.PermissionsFor(new[] { role });
            Assert.All(AccessCatalog.AllPermissions.Where(p => p.Module == "Production planning"), p => Assert.Contains(p.Code, perms));
        }
    }
}
