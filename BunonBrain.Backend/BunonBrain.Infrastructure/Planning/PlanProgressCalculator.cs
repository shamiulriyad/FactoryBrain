using BunonBrain.Application.Dtos.Planning;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Planning;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>One plan item with what is known about its output.</summary>
internal sealed record ItemEvaluation(
    DailyPlanItem Item, ShiftSchedule? Schedule, ItemProgress Progress, PlanItemActualDto Dto, CounterReading? Reading, bool LineHasOutputPoint, string Note);

/// <summary>
/// Evaluates published plan items against actual production. Rules, in order,
/// for each item (line, date, shift):
/// <list type="number">
///   <item>The line has a designated output counter: the actual is that counter's
///   pieces (plus audited corrections) in the item's shift — verified. A counter that was alive
///   (heartbeats) but counted nothing is zero; a counter that sent nothing at all is "no data".</item>
///   <item>No output counter: output recorded by people for the order on the line, date and shift — labelled RECORDED, not verified.</item>
///   <item>Neither: no evidence — the actual is empty, never zero.</item>
/// </list>
/// REAL and SIMULATED never mix; simulated runs ignore manual records.
/// </summary>
internal sealed class PlanProgressCalculator
{
    private readonly BunonBrainDbContext _db;
    private readonly PlanningActuals _actuals;

    public PlanProgressCalculator(BunonBrainDbContext db) { _db = db; _actuals = new PlanningActuals(db); }

    public PlanningActuals Actuals => _actuals;

    public async Task<IReadOnlyList<ItemEvaluation>> EvaluateAsync(Guid factoryId, IReadOnlyList<DailyPlanItem> items, FactoryClock clock, string source, DateTimeOffset now, CancellationToken ct)
    {
        var result = new List<ItemEvaluation>();
        if (items.Count == 0) return result;
        var today = clock.Today(now);
        var evaluable = items.Where(i => i.PlanDate <= today).ToList();
        IReadOnlyDictionary<string, IReadOnlyList<PlanningActuals.Point>> points = new Dictionary<string, IReadOnlyList<PlanningActuals.Point>>();
        IReadOnlyDictionary<(string Line, DateOnly Date, Guid? Shift), CounterReading> readings = new Dictionary<(string, DateOnly, Guid?), CounterReading>();
        IReadOnlyList<RecordedOutput> recorded = Array.Empty<RecordedOutput>();
        if (evaluable.Count > 0)
        {
            var from = evaluable.Min(i => i.PlanDate);
            var to = evaluable.Max(i => i.PlanDate);
            points = await _actuals.PointsAsync(factoryId, source, ct);
            readings = await _actuals.ReadAsync(factoryId, from, to, source, points, clock, now, evaluable.Select(i => i.LineId).Distinct().ToList(), ct);
            if (source == IotSourceTypes.Real)
                recorded = await _actuals.RecordedAsync(evaluable.Select(i => i.OrderId).Distinct().ToList(), from, to, evaluable.Select(i => i.LineId).Distinct().ToList(), factoryId, ct);
        }

        foreach (var item in items)
        {
            var schedule = clock.Schedule(item.ShiftId, item.PlanDate);
            if (schedule is null)
            {
                var dto = new PlanItemActualDto(null, null, null, null, null, ItemStates.NoData, ActualBases.None, false, null, source, "The shift is no longer active, so its schedule is unknown.");
                result.Add(new ItemEvaluation(item, null, new ItemProgress(item.TargetQuantity, null, null, null, null, null, ItemStates.NoData, ActualBases.None, 0, 0), dto, null, false, dto.Note));
                continue;
            }
            if (item.PlanDate > today || !schedule.HasStarted(now))
            {
                var p0 = PlanMath.Progress(item.TargetQuantity, null, ActualBases.None, schedule, now);
                var dto0 = Dto(p0, source, item.PlanDate > today ? "Not started: the date is in the future." : "Not started: the shift hasn't begun.");
                result.Add(new ItemEvaluation(item, schedule, p0, dto0, null, points.ContainsKey(item.LineId), dto0.Note));
                continue;
            }

            var hasPoint = points.ContainsKey(item.LineId);
            int? actual = null;
            string basis = ActualBases.None;
            string note;
            CounterReading? reading = readings.TryGetValue((item.LineId, item.PlanDate, item.ShiftId), out var r) ? r : null;
            if (hasPoint)
            {
                basis = ActualBases.Counter;
                if (reading is { HasEvidence: true } rr) { actual = rr.Total; note = $"Verified: {rr.Pieces} counted pieces{(rr.CorrectionPieces != 0 ? $" and {rr.CorrectionPieces:+#;-#} from audited corrections" : "")} at the line's output counter."; }
                else if (_actuals.WasAlive(item.LineId, schedule.Start, schedule.End < now ? schedule.End : now)) { actual = 0; note = "The output counter is reporting but has counted no pieces in this shift."; }
                else { basis = ActualBases.None; note = "No data from the line's output counter in this shift: missing data, not zero production."; }
            }
            else
            {
                var rec = recorded.Where(x => x.OrderId == item.OrderId && x.LineId == item.LineId && x.Date == item.PlanDate && x.ShiftId == item.ShiftId).ToList();
                if (rec.Count > 0) { actual = rec.Sum(x => x.Good); basis = ActualBases.Recorded; note = "Recorded by people in the production output records; not verified by a counter."; }
                else note = source == IotSourceTypes.Simulated
                    ? "The line has no simulated output counter."
                    : "The line has no output counter configured and no recorded output for this shift.";
            }
            var progress = PlanMath.Progress(item.TargetQuantity, actual, basis, schedule, now);
            result.Add(new ItemEvaluation(item, schedule, progress, Dto(progress, source, note), reading, hasPoint, note));
        }
        return result;
    }

    private static PlanItemActualDto Dto(ItemProgress p, string source, string note) =>
        new(p.Actual, p.Remaining, p.AchievementPct, p.ExpectedByNow, p.VarianceToExpected, p.State, p.Basis, p.Basis == ActualBases.Counter,
            p.Actual is { } a ? PlanMath.HourlyRate(a, p.ProductiveMinutesElapsed) : null, source, note);
}
