using System.Text.Json;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Dtos.Approvals;
using FactoryBrain.Application.Dtos.Production;
using FactoryBrain.Application.Production;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FactoryBrain.Infrastructure.Production;

/// <summary>Helpers shared by the production services: audit rows, line locks, the approval-queue bridge.</summary>
internal static class ProductionSupport
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// An audit row. States (<c>draft</c>, <c>active</c>…) go in the short
    /// state columns; descriptive before / after values (a whole record's
    /// fields) are written into the reason so nothing is cut off.
    /// </summary>
    public static AuditLogEntry Audit(AuditActor actor, string? traceId, string action, string entityType, string entityId,
        string? from, string? to, string? reason)
    {
        const int maxState = 32;
        var details = new List<string>();
        if (from is { Length: > maxState }) details.Add($"before: {from}");
        if (to is { Length: > maxState }) details.Add($"after: {to}");
        if (!string.IsNullOrWhiteSpace(reason)) details.Add(reason.Trim());
        return new AuditLogEntry
        {
            OccurredAt = DateTime.UtcNow,
            ActorUserId = actor.UserId,
            ActorEmail = actor.Email,
            ActorRole = actor.Role,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            FromState = from is { Length: > maxState } ? null : from,
            ToState = to is { Length: > maxState } ? null : to,
            Reason = details.Count == 0 ? null : Truncate(string.Join(" | ", details), 1000),
            TraceId = traceId,
        };
    }

    /// <summary>
    /// Serialises writes that touch one line (output intervals, plan
    /// activation) for the rest of the transaction, so two concurrent
    /// requests can't both pass an overlap check.
    /// </summary>
    public static Task LockLineAsync(FactoryBrainDbContext db, string lineId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"production_line:" + lineId}, 0))", ct);

    public static bool IsUniqueViolation(DbUpdateException ex, string? constraintContains = null) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && (constraintContains is null || (pg.ConstraintName?.Contains(constraintContains, StringComparison.Ordinal) ?? false));

    public static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    public static string NewReference(string prefix, Guid id, DateTime now) => $"{prefix}-{now:yyyyMMdd}-{id.ToString("N")[..8].ToUpperInvariant()}";

    public static Dictionary<string, string[]> Errors() => new();

    public static void Add(this Dictionary<string, string[]> errors, string key, string message) =>
        errors[key] = errors.TryGetValue(key, out var e) ? e.Append(message).ToArray() : new[] { message };

    // ── Approval queue bridge ───────────────────────────────────────────────

    /// <summary>
    /// Queue a request in the existing approval workflow (an insight plus a
    /// pending <see cref="ApprovalRequest"/>), in the caller's transaction.
    /// The production service never decides it: a reviewer does, on the
    /// approvals page, and the plan or proposal only changes when an
    /// authorised user then activates / applies it.
    /// </summary>
    public static string QueueApproval(FactoryBrainDbContext db, string kind, string title, string finding, string action,
        RiskTier risk, string evidenceDomain, string entityId, AuditActor actor, DateTime now)
    {
        var stamp = Guid.NewGuid().ToString("N");
        var insight = new Insight
        {
            Id = $"ins-{kind}-{stamp}",
            AgentId = "production-planning",
            AgentLabel = "Production planning",
            Title = Truncate(title, 300)!,
            TitleBn = Truncate(title, 300)!,
            Finding = Truncate(finding, 2000)!,
            FindingBn = Truncate(finding, 2000)!,
            Stage = Stage.PendingApproval,
            Recommendation = new InsightRecommendation
            {
                Title = Truncate(title, 300)!,
                TitleBn = Truncate(title, 300)!,
                Action = Truncate(action, 2000)!,
                ActionBn = Truncate(action, 2000)!,
                RiskTier = risk,
                TargetStage = Stage.PendingApproval,
            },
            Evidence = new List<EvidenceRow> { new() { Domain = evidenceDomain, Count = 1, PreviewIds = new List<string> { entityId } } },
            // Not a model score: the request is a person's submission.
            Confidence = 1.0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var approval = new ApprovalRequest
        {
            Id = $"apr-{kind}-{stamp}",
            InsightId = insight.Id,
            Title = insight.Title,
            TitleBn = insight.TitleBn,
            Reason = $"Submitted by {actor.Email}",
            RiskTier = risk,
            Stage = Stage.PendingApproval,
            CreatedAt = now,
        };
        db.Insights.Add(insight);
        db.Approvals.Add(approval);
        return approval.Id;
    }

    public static ApprovalRefDto? Ref(ApprovalRequest? a) => a is null ? null
        : new ApprovalRefDto(a.Id, StageNames.ToWire(a.Stage), StageNames.ToWire(a.RiskTier), a.CreatedAt, a.DecidedAt, a.DecidedByEmail, a.DecisionReason);

    public static async Task<Dictionary<string, ApprovalRequest>> ApprovalsAsync(FactoryBrainDbContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        var list = ids.Where(i => i is not null).Select(i => i!).Distinct().ToList();
        if (list.Count == 0) return new();
        return await db.Approvals.AsNoTracking().Where(a => list.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
    }

    /// <summary>
    /// Why an approved request still may not take effect: the configured
    /// separation of duties (the approver must not be the submitter).
    /// </summary>
    public static string? SeparationProblem(ProductionOptions options, ApprovalRequest approval, Guid? submitterId, string? submitterEmail)
    {
        if (!options.RequireSeparateApprover) return null;
        var same = (approval.DecidedByUserId is { } d && submitterId is { } s && d == s)
                   || (approval.DecidedByEmail is not null && submitterEmail is not null
                       && string.Equals(approval.DecidedByEmail, submitterEmail, StringComparison.OrdinalIgnoreCase));
        return same
            ? "The approval was decided by the same person who submitted the request. Another reviewer must approve it (PRODUCTION_REQUIRE_SEPARATE_APPROVER)."
            : null;
    }

    // ── Shared mappings ─────────────────────────────────────────────────────

    public static ShiftSpec Spec(ProductionShift s) => new(s.Code, s.StartLocal, s.EndLocal, s.BreakStartLocal, s.BreakMinutes, s.Weekdays);

    public static ProductionLineDto ToDto(ProductionLine l) => new(l.Id, l.Name, l.Workstations, l.IsActive, l.Version, l.UpdatedAt);

    public static StyleDto ToDto(ProductionStyle s)
    {
        var ops = s.Operations.OrderBy(o => o.Sequence).ToList();
        var (smv, basis) = StyleSmv.Resolve(s.ApprovedSmv, ops.Select(o => o.Smv));
        return new StyleDto(s.Id, s.Code, s.Name, s.ProductType, s.ApprovedSmv, smv, ProductionWire.SmvBasisName(basis),
            Math.Round(ops.Sum(o => o.Smv), 4), s.IsActive, s.Version,
            ops.Select(o => new StyleOperationDto(o.Id, o.Sequence, o.Name, o.Smv, o.MachineType, o.Predecessors)).ToList());
    }

    public static BalanceOperation ToBalance(StyleOperation o) => new(o.Id, o.Sequence, o.Name, o.Smv, o.MachineType, o.Predecessors);

    public static WipBufferDto ToDto(WipBuffer w) =>
        new(w.Key, w.Label, ProductionWire.Stage(w.From), ProductionWire.Stage(w.To), w.Pieces, w.State);

    public static PlanValues Values(ProductionPlan p) => new(p.LineId, ProductionWire.Date(p.StartDate), ProductionWire.Date(p.EndDate),
        p.PlannedQuantity, p.Operators, p.TargetEfficiencyPct, p.ShiftCodes.ToList(), p.OvertimeMinutesPerDay, p.Notes);
}

/// <summary>
/// Loads the plant calendar, style SMVs and plan schedules for the read
/// side. Everything is computed from the persisted rows at query time, so
/// late or corrected output changes every summary that covers it.
/// </summary>
internal sealed class ProductionReadModel
{
    private readonly FactoryBrainDbContext _db;
    private readonly ProductionOptions _options;
    private ProductionCalendar? _calendar;

    public ProductionReadModel(FactoryBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    public ProductionOptions Options => _options;

    public async Task<ProductionCalendar> CalendarAsync(CancellationToken ct)
    {
        if (_calendar is not null) return _calendar;
        var shifts = await _db.ProductionShifts.AsNoTracking().Where(s => s.IsActive).ToListAsync(ct);
        return _calendar = new ProductionCalendar(_options.TimeZone, shifts.Select(ProductionSupport.Spec));
    }

    /// <summary>SMV per style id (approved, else bulletin total, else missing).</summary>
    public async Task<Dictionary<Guid, (double? Smv, SmvBasis Basis)>> StyleSmvAsync(IEnumerable<Guid> styleIds, CancellationToken ct)
    {
        var ids = styleIds.Distinct().ToList();
        var styles = await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).Where(s => ids.Contains(s.Id)).ToListAsync(ct);
        return styles.ToDictionary(s => s.Id, s => StyleSmv.Resolve(s.ApprovedSmv, s.Operations.Select(o => o.Smv)));
    }

    public static PlanSchedule Schedule(ProductionPlan p, ProductionCalendar cal, double? smv) =>
        new(cal.Windows(p.StartDate, p.EndDate, p.ShiftCodes, p.OvertimeMinutesPerDay), p.PlannedQuantity,
            ProductionMetrics.HourlyTarget(p.Operators, p.TargetEfficiencyPct, smv));

    /// <summary>End of the due date in plant-local time, as UTC.</summary>
    public DateTime DueUtc(ProductionCalendar cal, DateOnly due) => cal.ToUtc(due.AddDays(1), TimeOnly.MinValue);

    /// <summary>
    /// Delay risk for one order from its persisted output, its active plans
    /// and the calendar (see <see cref="DelayRiskEstimator"/>), with
    /// recommendations.
    /// </summary>
    public async Task<RiskAssessmentDto> RiskAsync(ProductionOrder order, DateTime now, CancellationToken ct, bool withRecommendations = true)
    {
        var cal = await CalendarAsync(ct);
        var smv = (await StyleSmvAsync(new[] { order.StyleId }, ct)).GetValueOrDefault(order.StyleId);
        // Active plans give the remaining schedule; completed ones still count as scheduled time in the observed window.
        var worked = await _db.ProductionPlans.AsNoTracking()
            .Where(p => p.OrderId == order.Id && (p.Status == ProductionPlanStatus.Active || p.Status == ProductionPlanStatus.Completed)).ToListAsync(ct);
        var plans = worked.Where(p => p.Status == ProductionPlanStatus.Active).ToList();
        var schedules = plans.Select(p => (Plan: p, Schedule: ProductionReadModel.Schedule(p, cal, smv.Smv))).ToList();
        var windows = schedules.SelectMany(s => s.Schedule.Windows).ToList();
        var pastWindows = worked.SelectMany(p => ScheduledUntilClosed(p, cal)).ToList();

        var produced = await _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => e.OrderId == order.Id && !e.IsSuperseded && e.Stage == ProductionStage.SewingOutput)
            .SumAsync(e => (int?)e.Quantity, ct) ?? 0;

        // Observed rate: output and scheduled plan time on each of the last N production dates up to now.
        var today = cal.LocalDate(now);
        var fromDate = today.AddDays(-(_options.RateLookbackDays - 1));
        var lookbackStart = cal.ToUtc(fromDate.AddDays(-1), TimeOnly.MinValue);
        var recentEvents = await _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => e.OrderId == order.Id && !e.IsSuperseded && e.Stage == ProductionStage.SewingOutput
                        && e.ProductionDate >= fromDate && e.ProductionDate <= today)
            .Select(e => new { e.ProductionDate, e.Quantity, e.LineId, e.DowntimeMinutes, e.DowntimeReason, e.OperatorsPresent, e.IntervalStartUtc, e.IntervalEndUtc })
            .ToListAsync(ct);
        var samples = new List<DailyRateSample>();
        for (var d = fromDate; d <= today; d = d.AddDays(1))
        {
            var dayWindows = pastWindows.Where(w => w.ProductionDate == d).ToList();
            var minutes = ProductionCalendar.MinutesWithin(dayWindows, lookbackStart, now);
            var ev = recentEvents.Where(e => e.ProductionDate == d).ToList();
            samples.Add(new DailyRateSample(d, ev.Sum(e => e.Quantity), minutes, ev.Count));
        }

        double? plannedRate = schedules.Count == 0 || schedules.Any(s => s.Schedule.Method != PlannedOutputMethod.TargetRate) ? null
            : ProductionMetrics.Round1(schedules.Sum(s => s.Schedule.RatePerMinute * 60));
        var due = DueUtc(cal, order.DueDate);
        var risk = DelayRiskEstimator.Estimate(new DelayRiskInput(order.Quantity, produced, due, now, windows, plannedRate,
            samples, _options.RateLookbackDays, _options.RiskSlackPct));

        var lineIds = plans.Select(p => p.LineId).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        IReadOnlyList<Recommendation> recs = Array.Empty<Recommendation>();
        if (withRecommendations) recs = await RecommendAsync(order, risk, smv.Smv, plans, lineIds, recentEvents.Select(e =>
            (e.Quantity, e.OperatorsPresent, e.IntervalStartUtc, e.IntervalEndUtc, e.DowntimeMinutes, e.DowntimeReason)).ToList(), now, ct);

        return new RiskAssessmentDto(order.Id, order.OrderNumber, now, due, lineIds, risk.Level, risk.RemainingQuantity,
            risk.RemainingScheduledMinutes, risk.RequiredRatePerLineHour, risk.ObservedRatePerLineHour, risk.ObservedRateLow, risk.ObservedRateHigh,
            risk.PlannedRatePerLineHour, risk.ProjectedCompletionUtc, risk.ProjectedCompletionEarliestUtc, risk.ProjectedCompletionLatestUtc,
            risk.ScheduleEndUtc, risk.ProjectedShortfall, risk.ObservedLineHours,
            $"Observed-rate baseline over the last {_options.RateLookbackDays} production days; level thresholds: projection after due = high, required rate within {_options.RiskSlackPct:0.#} % of observed = medium.",
            risk.Observations, risk.Assumptions, risk.Limitations, risk.MissingInputs, recs);
    }

    /// <summary>Recommendations for a risk result, with efficiency, constraint and downtime context.</summary>
    private async Task<IReadOnlyList<Recommendation>> RecommendAsync(ProductionOrder order, DelayRiskResult risk, double? smv,
        IReadOnlyList<ProductionPlan> plans, IReadOnlyList<string> lineIds,
        IReadOnlyList<(int Quantity, int? Operators, DateTime Start, DateTime End, double? Downtime, string? Reason)> recent,
        DateTime now, CancellationToken ct)
    {
        var cal = await CalendarAsync(ct);
        var eff = EfficiencyCalculator.Calculate(
            recent.Select(e => new EfficiencyInput(e.Quantity, smv, e.Operators, e.Start, e.End, e.Downtime)), cal.BreakMinutesWithin);
        var constraints = new List<(string, string, double, string)>();
        foreach (var lineId in lineIds)
        {
            var b = await BottleneckAsync(lineId, order.StyleId, now, ct);
            if (b.Status != "insufficient_data" && b.ConstraintCapacityPerHour is { } cap && b.Constraint is { } c)
                constraints.Add((lineId, $"operation {c.Sequence} ({c.Name})", cap, b.Status));
        }
        var reasons = recent.Where(e => e.Reason != null).Select(e => e.Reason!).Distinct().Take(5).ToList();
        return ProductionRecommender.For(risk, new RecommendationContext(lineIds.Count, eff.EfficiencyPct, smv,
            plans.Count == 0 ? null : (int)Math.Round(plans.Average(p => p.Operators)), constraints,
            recent.Sum(e => e.Downtime ?? 0), reasons));
    }

    /// <summary>A plan's scheduled windows; a completed plan's stop when it was closed.</summary>
    private static IEnumerable<WorkWindow> ScheduledUntilClosed(ProductionPlan p, ProductionCalendar cal)
    {
        foreach (var w in cal.Windows(p.StartDate, p.EndDate, p.ShiftCodes, p.OvertimeMinutesPerDay))
        {
            if (p.Status == ProductionPlanStatus.Active || p.ClosedAt is not { } closed) { yield return w; continue; }
            if (w.StartUtc >= closed) yield break;
            yield return w.EndUtc <= closed ? w : w with { EndUtc = closed };
        }
    }

    /// <summary>Bottleneck analysis for one line and style from recent time studies and the current layout.</summary>
    public async Task<BottleneckResult> BottleneckAsync(string lineId, Guid styleId, DateTime now, CancellationToken ct)
    {
        var ops = await _db.StyleOperations.AsNoTracking().Where(o => o.StyleId == styleId).OrderBy(o => o.Sequence).ToListAsync(ct);
        var cutoff = now.AddDays(-_options.ObservationMaxAgeDays);
        var all = await _db.OperationObservations.AsNoTracking().Where(o => o.LineId == lineId && o.StyleId == styleId).ToListAsync(ct);
        var latestPerOp = all.GroupBy(o => o.OperationId).Select(g => g.OrderByDescending(o => o.ObservedAtUtc).First()).ToList();
        var fresh = latestPerOp.Where(o => o.ObservedAtUtc >= cutoff).ToList();
        var stale = latestPerOp.Count - fresh.Count;
        var layout = await _db.LineLayouts.AsNoTracking().Include(l => l.Stations)
            .FirstOrDefaultAsync(l => l.LineId == lineId && l.StyleId == styleId && l.IsCurrent, ct);

        // Recorded line output per hour for this style over the observation window (sewing output with operators).
        var orderIds = await _db.ProductionOrders.AsNoTracking().Where(o => o.StyleId == styleId).Select(o => o.Id).ToListAsync(ct);
        var recent = await _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => e.LineId == lineId && !e.IsSuperseded && e.Stage == ProductionStage.SewingOutput && orderIds.Contains(e.OrderId)
                        && e.IntervalStartUtc >= cutoff)
            .Select(e => new { e.Quantity, e.IntervalStartUtc, e.IntervalEndUtc }).ToListAsync(ct);
        double? lineRate = null;
        if (recent.Count > 0)
        {
            var cal = await CalendarAsync(ct);
            var minutes = recent.Sum(e => (e.IntervalEndUtc - e.IntervalStartUtc).TotalMinutes - cal.BreakMinutesWithin(e.IntervalStartUtc, e.IntervalEndUtc));
            if (minutes > 0) lineRate = ProductionMetrics.Round1(recent.Sum(e => e.Quantity) / (minutes / 60));
        }

        return BottleneckAnalyzer.Analyze(
            ops.Select(ProductionSupport.ToBalance).ToList(),
            fresh.Select(o => new CycleObservation(o.OperationId, o.CycleSeconds, o.OperatorsAssigned, o.QueueBefore, o.ObservedAtUtc, o.SampleCount)).ToList(),
            layout?.Stations.Select(s => new StationAssignment(s.StationNo, s.OperationIds)).ToList(),
            lineRate, stale);
    }
}
