using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Planning;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Planning;
using BunonBrain.Application.Production;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static BunonBrain.Infrastructure.Planning.PlanningScope;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>
/// Capacity estimates from the line's real configuration: the factory's shifts,
/// the style's SMV and machine requirements, the line's efficiency and operator
/// assumptions, the workers assigned to its workstations, the state of its machines
/// and the other allocations on it. Missing inputs make the estimate incomplete
/// (and say what to configure); nothing is invented.
/// </summary>
public sealed class CapacityService : ICapacityService
{
    public const int MaxDays = 93;
    public const string Disclaimer = "An estimate from the configured inputs, not measured output and not a promise. Check the assumptions before relying on it.";

    private readonly BunonBrainDbContext _db;

    public CapacityService(BunonBrainDbContext db) { _db = db; }

    public async Task<AccessResult<CapacityEstimateDto>> EstimateAsync(AccessContext caller, string lineId, Guid styleId, DateOnly from, DateOnly to, int quantity, Guid? excludeAllocationId,
        CancellationToken ct)
    {
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line?.FactoryId is not { } fid || !caller.CanAccessLine(line.Id, fid)) return AccessResult<CapacityEstimateDto>.NotFound("line_not_found", "Line not found in your scope.");
        if (to < from) return AccessResult<CapacityEstimateDto>.Invalid("dates_invalid", "The end date can't be before the start date.");
        if ((to.DayNumber - from.DayNumber) + 1 > MaxDays) return AccessResult<CapacityEstimateDto>.Invalid("range_too_long", $"At most {MaxDays} days.");
        if (quantity < 0 || quantity > 10_000_000) return AccessResult<CapacityEstimateDto>.Invalid("quantity_invalid", "Quantity must be from 0 to 10,000,000.");
        var style = await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).FirstOrDefaultAsync(s => s.Id == styleId, ct);
        if (style is null) return AccessResult<CapacityEstimateDto>.NotFound("style_not_found", "Style not found.");
        var clock = await FactoryClock.LoadAsync(_db, fid, ct);
        return AccessResult<CapacityEstimateDto>.Ok(await BuildAsync(line, style, from, to, quantity, excludeAllocationId, clock, $"{style.Code} on {line.Name}", ct));
    }

    public async Task<AccessResult<CapacityEstimateDto>> ForAllocationAsync(AccessContext caller, Guid allocationId, CancellationToken ct)
    {
        var a = await Allocations(_db, caller).FirstOrDefaultAsync(x => x.Id == allocationId, ct);
        if (a is null) return AccessResult<CapacityEstimateDto>.NotFound("allocation_not_found", "Allocation not found in your scope.");
        var order = await _db.ProductionOrders.AsNoTracking().FirstAsync(o => o.Id == a.OrderId, ct);
        var style = await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).FirstAsync(s => s.Id == order.StyleId, ct);
        var line = await _db.ProductionLines.AsNoTracking().FirstAsync(l => l.Id == a.LineId, ct);
        var clock = await FactoryClock.LoadAsync(_db, a.FactoryId, ct);
        return AccessResult<CapacityEstimateDto>.Ok(await BuildAsync(line, style, a.PlannedStart, a.PlannedEnd, a.Quantity, a.Id, clock, $"{order.OrderNumber} on {line.Name}", ct));
    }

    /// <summary>The estimate for an allocation without a caller (the scheduled exception check).</summary>
    internal async Task<CapacityEstimateDto> ForAllocationInternalAsync(ProductionAllocation a, FactoryClock clock, CancellationToken ct)
    {
        var order = await _db.ProductionOrders.AsNoTracking().FirstAsync(o => o.Id == a.OrderId, ct);
        var style = await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).FirstAsync(s => s.Id == order.StyleId, ct);
        var line = await _db.ProductionLines.AsNoTracking().FirstAsync(l => l.Id == a.LineId, ct);
        return await BuildAsync(line, style, a.PlannedStart, a.PlannedEnd, a.Quantity, a.Id, clock, $"{order.OrderNumber} on {line.Name}", ct);
    }

    public async Task<AccessResult<CapacityOverviewDto>> OverviewAsync(AccessContext caller, Guid factoryId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (!caller.CanAccessFactory(factoryId)) return AccessResult<CapacityOverviewDto>.Forbidden("factory_out_of_scope", "You can only see your own factories.");
        if (to < from) return AccessResult<CapacityOverviewDto>.Invalid("dates_invalid", "The end date can't be before the start date.");
        if ((to.DayNumber - from.DayNumber) + 1 > MaxDays) return AccessResult<CapacityOverviewDto>.Invalid("range_too_long", $"At most {MaxDays} days.");
        var clock = await FactoryClock.LoadAsync(_db, factoryId, ct);
        var allocs = await Allocations(_db, caller).Where(a => a.FactoryId == factoryId && (a.Status == AllocationStatuses.Planned || a.Status == AllocationStatuses.Active)
                                                              && a.PlannedStart <= to && a.PlannedEnd >= from).ToListAsync(ct);
        var linesQ = _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factoryId);
        if (caller.IsLineScoped) { var ls = caller.LineIds.ToList(); linesQ = linesQ.Where(l => ls.Contains(l.Id)); }
        var lines = await linesQ.OrderBy(l => l.Name).ToListAsync(ct);
        var orderIds = allocs.Select(a => a.OrderId).Distinct().ToList();
        var orders = await _db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var styleIds = orders.Values.Select(o => o.StyleId).Distinct().ToList();
        var styles = await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).Where(s => styleIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);

        var rows = new List<LineCapacityRow>();
        var warnings = new List<string>();
        foreach (var line in lines)
        {
            var mine = allocs.Where(a => a.LineId == line.Id).OrderBy(a => a.PlannedStart).ToList();
            if (mine.Count == 0) continue;
            var estimates = new List<CapacityEstimateDto>();
            var missing = new List<CapacityMissing>();
            double? demand = 0;
            double? capacityMinutes = null;
            foreach (var a in mine)
            {
                var order = orders[a.OrderId];
                var style = styles[order.StyleId];
                var s = a.PlannedStart < from ? from : a.PlannedStart;
                var e = a.PlannedEnd > to ? to : a.PlannedEnd;
                var est = await BuildAsync(line, style, s, e, a.Quantity, a.Id, clock, $"{order.OrderNumber} on {line.Name}", ct);
                estimates.Add(est);
                foreach (var m in est.Result.Missing) if (!missing.Any(x => x.Code == m.Code)) missing.Add(m);
                var (smv, _) = StyleSmv.Resolve(style.ApprovedSmv, style.Operations.Select(o => o.Smv));
                var share = ShareInRange(a, from, to, clock);
                if (smv is null) demand = null; else if (demand is not null) demand += a.Quantity * share * smv.Value;
            }
            // Line-wide: standard minutes demanded by every allocation against the standard minutes the line can deliver.
            var settings = await _db.LineCapacitySettings.AsNoTracking().FirstOrDefaultAsync(x => x.LineId == line.Id, ct);
            int? ops = settings?.PlannedOperators;
            if (ops is null) { var assigned = await AssignedOperatorsAsync(line.Id, ct); if (assigned > 0) ops = assigned; }
            var minutes = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => clock.ProductiveMinutes(from.AddDays(i))).Sum();
            if (settings?.EfficiencyPct is > 0 && ops is > 0 && minutes > 0) capacityMinutes = Math.Round(minutes * ops.Value * settings.EfficiencyPct.Value / 100.0, 1);
            var util = demand is { } dm && capacityMinutes is > 0 ? Math.Round(dm / capacityMinutes.Value * 100.0, 1) : (double?)null;
            var complete = util is not null && missing.Count == 0;
            var notes = new List<string>();
            if (mine.Count > 1) notes.Add($"{mine.Count} allocations share this line; utilisation adds their standard minutes (quantity × SMV) against the line's standard minutes.");
            if (!line.IsActive) notes.Add("The line is deactivated.");
            rows.Add(new LineCapacityRow(line.Id, line.Name, line.IsActive, mine.Count, Math.Round(minutes / 60.0, 1), demand is { } d2 ? Math.Round(d2, 1) : null, capacityMinutes, util,
                complete ? CapacityEstimator.Complete : CapacityEstimator.Incomplete, missing, estimates, notes));
        }
        if (rows.Count == 0) warnings.Add("No planned or active allocation falls in this period for the lines you can see.");
        if (rows.Any(r => r.Status == CapacityEstimator.Incomplete)) warnings.Add("Some lines have incomplete estimates: configure what they list (SMV, efficiency, operators) before trusting any number.");
        return AccessResult<CapacityOverviewDto>.Ok(new CapacityOverviewDto(factoryId, Date(from), Date(to), rows, warnings, Disclaimer));
    }

    // ── Estimate ────────────────────────────────────────────────────────────

    private async Task<CapacityEstimateDto> BuildAsync(ProductionLine line, ProductionStyle style, DateOnly from, DateOnly to, int quantity, Guid? exclude, FactoryClock clock, string label,
        CancellationToken ct)
    {
        var fid = line.FactoryId!.Value;
        var ops = style.Operations.OrderBy(o => o.Sequence).ToList();
        var (smv, basis) = StyleSmv.Resolve(style.ApprovedSmv, ops.Select(o => o.Smv));
        var settings = await _db.LineCapacitySettings.AsNoTracking().FirstOrDefaultAsync(s => s.LineId == line.Id, ct);

        int? operators = settings?.PlannedOperators;
        string? opSource = operators is null ? null : "Line capacity settings (planned operators)";
        if (operators is null)
        {
            var assigned = await AssignedOperatorsAsync(line.Id, ct);
            if (assigned > 0) { operators = assigned; opSource = $"Workers currently assigned to the line's workstations ({assigned})"; }
        }

        var days = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => from.AddDays(i)).Select(d => new CapacityDay(d, clock.ProductiveMinutes(d))).ToList();

        // Other commitments on the same line in the same dates.
        var others = await _db.ProductionAllocations.AsNoTracking()
            .Where(a => a.LineId == line.Id && (a.Status == AllocationStatuses.Planned || a.Status == AllocationStatuses.Active) && a.PlannedStart <= to && a.PlannedEnd >= from
                        && (exclude == null || a.Id != exclude))
            .ToListAsync(ct);
        var orderNumbers = await _db.ProductionOrders.AsNoTracking().Where(o => others.Select(a => a.OrderId).Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);
        var commitments = others.Select(a => CapacityEstimator.Commit(a.Id, a.OrderId, orderNumbers.GetValueOrDefault(a.OrderId, "?"), a.Quantity, a.PlannedStart, a.PlannedEnd, a.Status,
            from, to, clock.IsWorkingDay)).Where(c => c.OverlapDays > 0).ToList();

        // Machines: what the style needs against what the line has working.
        var required = ops.Select(o => o.MachineType).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var machines = new List<MachineTypeCheck>();
        if (required.Count > 0)
        {
            var lineMachines = await (from m in _db.Machines.AsNoTracking()
                                      join w in _db.Workstations.AsNoTracking() on m.WorkstationId equals w.Id
                                      where w.LineId == line.Id && m.IsActive
                                      select new { m.Id, m.MachineType }).ToListAsync(ct);
            var ids = lineMachines.Select(m => m.Id).ToList();
            var faulty = await _db.MachineStates.AsNoTracking().Where(s => ids.Contains(s.MachineId) && (s.State == MachineStates.Error || s.InMaintenance || s.State == MachineStates.Maintenance))
                .Select(s => s.MachineId).ToListAsync(ct);
            var list = lineMachines.Select(m => ((string?)m.MachineType, faulty.Contains(m.Id))).ToList();
            machines = required.Select(r => CapacityEstimator.CheckMachineType(r, list)).ToList();
        }

        var input = new CapacityInput(smv, smv is null ? null : ProductionWire.SmvBasisName(basis), settings?.EfficiencyPct, operators, opSource, line.Workstations, days, quantity, commitments, machines);
        var result = CapacityEstimator.Estimate(input);
        var shifts = clock.Shifts.Where(s => days.Any(d => s.WorksOn(d.Date.DayOfWeek))).Select(s => s.Name).ToList();
        var assumptions = result.Assumptions.Append(new CapacityAssumption("Shifts assumed worked", shifts.Count == 0 ? "none scheduled" : string.Join(", ", shifts),
            "Every active shift of the factory is assumed to run on this line; narrow it through the daily plan's shift assignments")).ToList();
        result = result with { Assumptions = assumptions };
        return new CapacityEstimateDto(label, line.Id, line.Name, style.Code, Date(from), Date(to), result, DateTimeOffset.UtcNow, true, Disclaimer);
    }

    private async Task<int> AssignedOperatorsAsync(string lineId, CancellationToken ct) =>
        await _db.Assignments.AsNoTracking().Where(a => a.LineId == lineId && a.EndAt == null && a.Status == "Active").Select(a => a.WorkerId).Distinct().CountAsync(ct);

    /// <summary>The share of an allocation's working days that fall inside a range.</summary>
    private static double ShareInRange(ProductionAllocation a, DateOnly from, DateOnly to, FactoryClock clock)
    {
        int total = 0, inside = 0;
        for (var d = a.PlannedStart; d <= a.PlannedEnd; d = d.AddDays(1))
        {
            if (!clock.IsWorkingDay(d)) continue;
            total++;
            if (d >= from && d <= to) inside++;
        }
        return total == 0 ? 0 : inside / (double)total;
    }
}
