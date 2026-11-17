using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Planning;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Planning;
using BunonBrain.Application.Production;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static BunonBrain.Infrastructure.Planning.PlanningScope;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>
/// Production exceptions: rule-based detections (output behind the published target, delivery at
/// risk, line unavailable, capacity shortage, missing counter data, excess allocation, unplanned
/// interruption), each with its evidence, reason and what to look into. Detection can be run on
/// demand or by the scheduled check; an exception stays open (one per detection key) while the
/// condition lasts and is closed automatically when it no longer holds. People acknowledge and
/// resolve exceptions with a note. Nothing here names a person as a cause.
/// </summary>
public sealed class ExceptionService : IProductionExceptionService
{
    private readonly BunonBrainDbContext _db;
    private readonly ProductionOptions _options;

    public ExceptionService(BunonBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    // ── Reads ───────────────────────────────────────────────────────────────

    public async Task<AccessResult<PagedResult<ExceptionDto>>> ListAsync(AccessContext caller, ExceptionQuery q, CancellationToken ct)
    {
        if (q.Page < 1 || q.PageSize is < 1 or > 200) return AccessResult<PagedResult<ExceptionDto>>.Invalid("paging_invalid", "page must be 1 or more and pageSize from 1 to 200.");
        if (q.FactoryId is { } f && !caller.CanAccessFactory(f)) return AccessResult<PagedResult<ExceptionDto>>.Forbidden("factory_out_of_scope", "You can only see your own factories.");
        string? status = null, type = null, severity = null;
        if (q.Status is not null && (status = ExceptionStatuses.Parse(q.Status)) is null) return AccessResult<PagedResult<ExceptionDto>>.Invalid("status_invalid", $"status must be one of: {string.Join(", ", ExceptionStatuses.All)}.");
        if (q.Type is not null && (type = ExceptionTypes.Parse(q.Type)) is null) return AccessResult<PagedResult<ExceptionDto>>.Invalid("type_invalid", $"type must be one of: {string.Join(", ", ExceptionTypes.All)}.");
        if (q.Severity is not null && (severity = Severities.Parse(q.Severity)) is null) return AccessResult<PagedResult<ExceptionDto>>.Invalid("severity_invalid", $"severity must be one of: {string.Join(", ", Severities.All)}.");
        var query = Exceptions(_db, caller);
        if (q.FactoryId is { } fid) query = query.Where(e => e.FactoryId == fid);
        if (status is not null) query = query.Where(e => e.Status == status);
        if (type is not null) query = query.Where(e => e.Type == type);
        if (severity is not null) query = query.Where(e => e.Severity == severity);
        if (Clean(q.LineId) is { } line) query = query.Where(e => e.LineId == line);
        if (q.OrderId is { } oid) query = query.Where(e => e.OrderId == oid);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(e => e.Status == "RESOLVED" ? 1 : 0).ThenByDescending(e => e.Severity == "HIGH" ? 3 : e.Severity == "MEDIUM" ? 2 : 1).ThenByDescending(e => e.LastDetectedAt)
            .Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        return AccessResult<PagedResult<ExceptionDto>>.Ok(new PagedResult<ExceptionDto>(await DtosAsync(_db, rows, ct), q.Page, q.PageSize, total));
    }

    public async Task<AccessResult<ExceptionDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var e = await Exceptions(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? AccessResult<ExceptionDto>.NotFound("exception_not_found", "Exception not found in your scope.") : AccessResult<ExceptionDto>.Ok((await DtosAsync(_db, new[] { e }, ct))[0]);
    }

    /// <summary>Open and acknowledged exceptions for the dashboard (most severe first).</summary>
    internal static async Task<IReadOnlyList<ExceptionDto>> QueryAsync(BunonBrainDbContext db, AccessContext caller, Guid factoryId, string source, IReadOnlyCollection<string> lineIds, CancellationToken ct)
    {
        var rows = await Exceptions(db, caller).Where(e => e.FactoryId == factoryId && e.Status != ExceptionStatuses.Resolved && e.SourceType == source
                                                          && (e.LineId == null || lineIds.Contains(e.LineId)))
            .OrderByDescending(e => e.Severity == "HIGH" ? 3 : e.Severity == "MEDIUM" ? 2 : 1).ThenByDescending(e => e.LastDetectedAt).Take(50).ToListAsync(ct);
        return await DtosAsync(db, rows, ct);
    }

    // ── Acknowledge / resolve ───────────────────────────────────────────────

    public async Task<AccessResult<ExceptionDto>> AcknowledgeAsync(AccessContext caller, Guid id, ExceptionNote body, string? traceId, CancellationToken ct)
    {
        var e = await Exceptions(_db, caller, tracking: true).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return AccessResult<ExceptionDto>.NotFound("exception_not_found", "Exception not found in your scope.");
        if (body.ExpectedVersion is null) return AccessResult<ExceptionDto>.Invalid("version_required", "expectedVersion is required.");
        if (e.Status != ExceptionStatuses.Open) return AccessResult<ExceptionDto>.Conflict("exception_not_open", "Only an open exception can be acknowledged.");
        if (body.ExpectedVersion != e.Version) return AccessResult<ExceptionDto>.Conflict("version_conflict", "The exception changed since you loaded it. Reload and try again.");
        e.Status = ExceptionStatuses.Acknowledged; e.AcknowledgedAt = DateTimeOffset.UtcNow; e.AcknowledgedByEmail = caller.Email;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.exception_acknowledged", "production_exception", e.Id.ToString(), ExceptionStatuses.Open, e.Status, $"{e.Title} {Clean(body.Note)}", traceId, e.FactoryId));
        return await SaveAsync(e, ct);
    }

    public async Task<AccessResult<ExceptionDto>> ResolveAsync(AccessContext caller, Guid id, ExceptionNote body, string? traceId, CancellationToken ct)
    {
        var e = await Exceptions(_db, caller, tracking: true).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return AccessResult<ExceptionDto>.NotFound("exception_not_found", "Exception not found in your scope.");
        if (body.ExpectedVersion is null) return AccessResult<ExceptionDto>.Invalid("version_required", "expectedVersion is required.");
        if (Clean(body.Note) is null) return AccessResult<ExceptionDto>.Invalid("note_required", "Say what was found or done to resolve it.");
        if (body.Note is { Length: > 500 }) return AccessResult<ExceptionDto>.Invalid("note_invalid", "The note is limited to 500 characters.");
        if (e.Status == ExceptionStatuses.Resolved) return AccessResult<ExceptionDto>.Conflict("exception_resolved", "This exception is already resolved.");
        if (body.ExpectedVersion != e.Version) return AccessResult<ExceptionDto>.Conflict("version_conflict", "The exception changed since you loaded it. Reload and try again.");
        var before = e.Status;
        e.Status = ExceptionStatuses.Resolved; e.ResolvedAt = DateTimeOffset.UtcNow; e.ResolvedByEmail = caller.Email; e.ResolutionNote = Clean(body.Note); e.AutoResolved = false;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.exception_resolved", "production_exception", e.Id.ToString(), before, e.Status, $"{e.Title}: {e.ResolutionNote}", traceId, e.FactoryId));
        return await SaveAsync(e, ct);
    }

    private async Task<AccessResult<ExceptionDto>> SaveAsync(ProductionException e, CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return AccessResult<ExceptionDto>.Conflict("version_conflict", "The exception changed since you loaded it. Reload and try again."); }
        return AccessResult<ExceptionDto>.Ok((await DtosAsync(_db, new[] { e }, ct))[0]);
    }

    // ── Detection ───────────────────────────────────────────────────────────

    public async Task<AccessResult<ExceptionEvaluationDto>> EvaluateAsync(AccessContext caller, ExceptionEvaluateRequest body, string? traceId, CancellationToken ct)
    {
        var fr = await PlanningSetupService.ResolveFactoryAsync(_db, caller, body.FactoryId, ct);
        if (fr.Outcome != AccessOutcome.Ok) return new AccessResult<ExceptionEvaluationDto>(fr.Outcome, null, fr.Code, fr.Message);
        if (caller.Scope < RoleScope.Factory) return AccessResult<ExceptionEvaluationDto>.Forbidden("evaluate_scope", "The exception check runs for the whole factory; it is run by a factory manager or on schedule.");
        var source = IotSourceTypes.Parse(body.Source ?? IotSourceTypes.Real);
        if (source is null) return AccessResult<ExceptionEvaluationDto>.Invalid("source_invalid", "source must be REAL or SIMULATED.");
        var r = await EvaluateFactoryAsync(fr.Value!.Id, source, DateTimeOffset.UtcNow, ct);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.exceptions_evaluated", "production_exception", fr.Value.Id.ToString(), null, null,
            $"{source}: {r.Raised} raised, {r.Refreshed} refreshed, {r.AutoResolved} closed", traceId, fr.Value.Id));
        await _db.SaveChangesAsync(ct);
        return AccessResult<ExceptionEvaluationDto>.Ok(r);
    }

    /// <summary>Runs every rule for one factory and brings the stored exceptions in line with what is detected now.</summary>
    public async Task<ExceptionEvaluationDto> EvaluateFactoryAsync(Guid factoryId, string source, DateTimeOffset now, CancellationToken ct)
    {
        var notes = new List<string>();
        var clock = await FactoryClock.LoadAsync(_db, factoryId, ct);
        var today = clock.Today(now);
        var detections = new List<DetectedException>();
        var checkedCount = 0;

        var plans = await _db.DailyProductionPlans.AsNoTracking().Include(p => p.Items)
            .Where(p => p.FactoryId == factoryId && p.Status == PlanStatuses.Published && (p.PlanDate == today || p.PlanDate == today.AddDays(-1))).ToListAsync(ct);
        var items = plans.SelectMany(p => p.Items).ToList();
        var calc = new PlanProgressCalculator(_db);
        var evals = await calc.EvaluateAsync(factoryId, items, clock, source, now, ct);
        var orderIds = evals.Select(e => e.Item.OrderId).Distinct().ToList();
        var orders = await _db.ProductionOrders.AsNoTracking().Where(o => o.FactoryId == factoryId && (o.Status == ProductionOrderStatus.Planned || o.Status == ProductionOrderStatus.InProgress || orderIds.Contains(o.Id))).ToDictionaryAsync(o => o.Id, ct);
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factoryId).ToDictionaryAsync(l => l.Id, ct);
        var shiftNames = clock.Shifts.ToDictionary(s => s.Id, s => s.Name);

        // Items: output behind target, missing counter data.
        foreach (var e in evals)
        {
            if (e.Schedule is null) continue;
            // A finished shift is judged once, soon after it ends; older ones are history.
            if (e.Schedule.IsOver(now) && now - e.Schedule.End > TimeSpan.FromHours(24)) continue;
            checkedCount++;
            var o = orders.GetValueOrDefault(e.Item.OrderId);
            var f = new ItemFacts(e.Item.Id, e.Item.OrderId, o?.OrderNumber ?? "?", e.Item.LineId, lines.GetValueOrDefault(e.Item.LineId)?.Name ?? e.Item.LineId, e.Item.ShiftId,
                shiftNames.GetValueOrDefault(e.Item.ShiftId, "?"), e.Item.PlanDate, e.Progress, e.LineHasOutputPoint, e.Reading?.Events ?? 0, e.Reading?.LastEventAt,
                o is not null && OrderLifecycle.IsLive(o.Status));
            Add(detections, ExceptionRules.OutputBehind(f), source);
            Add(detections, ExceptionRules.MissingCounter(f), source);
        }

        // Orders: delivery at risk, excess allocation.
        var live = orders.Values.Where(o => OrderLifecycle.IsLive(o.Status)).ToList();
        var outputs = await OrderOutputs.ForOrdersAsync(_db, factoryId, live.Select(o => o.Id).ToList(), clock, source, now, ct);
        var allocations = await _db.ProductionAllocations.AsNoTracking().Where(a => a.FactoryId == factoryId && a.Status != AllocationStatuses.Cancelled).ToListAsync(ct);
        foreach (var o in live)
        {
            checkedCount++;
            var output = outputs.GetValueOrDefault(o.Id);
            var fc = PlanningMonitorService.ForecastOf(o, output, clock, today);
            Add(detections, ExceptionRules.DeliveryAtRisk(new OrderFacts(o.Id, o.OrderNumber, o.Buyer, o.DueDate, o.Quantity, output?.Produced ?? 0, output?.Basis ?? ActualBases.None, fc, today)), source);
            var mine = allocations.Where(a => a.OrderId == o.Id).ToList();
            var total = mine.Sum(a => a.Quantity);
            if (total > o.Quantity)
            {
                var worst = mine.OrderByDescending(a => a.OverAllocated).ThenByDescending(a => a.CreatedAt).First();
                Add(detections, ExceptionRules.ExcessAllocation(new AllocationFacts(worst.Id, o.Id, o.OrderNumber, worst.LineId, lines.GetValueOrDefault(worst.LineId)?.Name ?? worst.LineId, o.Quantity, total,
                    mine.Any(a => a.OverAllocated))), source);
            }
        }

        // Lines with work today: availability and interruptions from the machine states.
        var workLines = evals.Where(e => e.Item.PlanDate == today).Select(e => e.Item.LineId).Distinct().ToList();
        if (workLines.Count > 0)
        {
            var machines = await (from m in _db.Machines.AsNoTracking()
                                  join w in _db.Workstations.AsNoTracking() on m.WorkstationId equals w.Id
                                  join s in _db.MachineStates.AsNoTracking() on m.Id equals s.MachineId
                                  where m.IsActive && workLines.Contains(w.LineId)
                                  select new { w.LineId, m.Code, s.State, s.Since, s.InMaintenance }).ToListAsync(ct);
            foreach (var lineId in workLines)
            {
                checkedCount++;
                var line = lines.GetValueOrDefault(lineId);
                if (line is null) continue;
                var ms = machines.Where(m => m.LineId == lineId).ToList();
                var unavailable = ms.Where(m => m.InMaintenance || m.State is MachineStates.Error or MachineStates.Maintenance or MachineStates.Offline).ToList();
                Add(detections, ExceptionRules.LineUnavailable(new LineFacts(lineId, line.Name, line.IsActive, ms.Count, unavailable.Count, unavailable.Select(m => m.Code).OrderBy(c => c).ToList(), true), today), source);

                // Stopped during a scheduled, running shift (outside breaks): minutes are productive minutes since the machine stopped.
                var running = evals.Where(e => e.Item.LineId == lineId && e.Item.PlanDate == today && e.Schedule is { } sc && sc.InProgress(now)).ToList();
                foreach (var r in running)
                {
                    var sc = r.Schedule!;
                    var stopped = ms.Where(m => !m.InMaintenance && m.State is MachineStates.Stopped or MachineStates.Error)
                        .Select(m =>
                        {
                            var from = m.Since > sc.Start ? m.Since : sc.Start;
                            var minutes = (int)Math.Floor(sc.ProductiveMinutesUntil(now) - sc.ProductiveMinutesUntil(from));
                            return (m.Code, m.State, Minutes: Math.Max(0, minutes));
                        }).Where(x => x.Minutes > 0).ToList();
                    Add(detections, ExceptionRules.Interruption(new StoppageFacts(lineId, line.Name, r.Item.ShiftId, shiftNames.GetValueOrDefault(r.Item.ShiftId), today, stopped, (int)PlanningSetupService.InterruptionMinutes)), source);
                }
            }
        }

        // Capacity: open allocations of live orders starting within two weeks.
        var capacity = new CapacityService(_db);
        foreach (var a in allocations.Where(a => (a.Status == AllocationStatuses.Planned || a.Status == AllocationStatuses.Active) && a.PlannedEnd >= today && a.PlannedStart <= today.AddDays(14)))
        {
            var o = orders.GetValueOrDefault(a.OrderId);
            if (o is null || !OrderLifecycle.IsLive(o.Status)) continue;
            checkedCount++;
            var est = await capacity.ForAllocationInternalAsync(a, clock, ct);
            Add(detections, ExceptionRules.CapacityShortage(o.Id, o.OrderNumber, a.Id, a.LineId, est.LineName, est.Result), source);
        }

        return await PersistAsync(factoryId, source, now, detections, checkedCount, notes, ct);
    }

    private static void Add(List<DetectedException> list, DetectedException? d, string source)
    {
        if (d is not null) list.Add(d);
    }

    private async Task<ExceptionEvaluationDto> PersistAsync(Guid factoryId, string source, DateTimeOffset now, IReadOnlyList<DetectedException> detections, int checkedCount, List<string> notes, CancellationToken ct)
    {
        int raised = 0, refreshed = 0, closed = 0;
        var open = await _db.ProductionExceptions.Where(e => e.FactoryId == factoryId && e.SourceType == source && e.Status != ExceptionStatuses.Resolved).ToListAsync(ct);
        var seen = new HashSet<string>();
        foreach (var d in detections)
        {
            if (!seen.Add(d.DedupeKey)) continue;
            var data = JsonSerializer.Serialize(d.Data, Json);
            var existing = open.FirstOrDefault(e => e.DedupeKey == d.DedupeKey);
            if (existing is not null)
            {
                existing.Severity = d.Severity; existing.Reason = d.Reason; existing.Title = d.Title; existing.SupportingData = data; existing.SuggestedInvestigation = d.SuggestedInvestigation;
                existing.LastDetectedAt = now; existing.Occurrences++;
                refreshed++;
            }
            else
            {
                _db.ProductionExceptions.Add(new ProductionException
                {
                    FactoryId = factoryId, Type = d.Type, Severity = d.Severity, Status = ExceptionStatuses.Open, DedupeKey = d.DedupeKey, OrderId = d.OrderId, AllocationId = d.AllocationId,
                    PlanItemId = d.PlanItemId, LineId = d.LineId, ShiftId = d.ShiftId, ProductionDate = d.Date, Title = d.Title, Reason = d.Reason, SupportingData = data,
                    SuggestedInvestigation = d.SuggestedInvestigation, SourceType = source, DetectedAt = now, LastDetectedAt = now,
                });
                raised++;
            }
        }
        // What is no longer detected closes itself, with the system as the closer.
        foreach (var e in open.Where(e => !seen.Contains(e.DedupeKey)))
        {
            e.Status = ExceptionStatuses.Resolved; e.ResolvedAt = now; e.ResolvedByEmail = "system"; e.AutoResolved = true; e.ResolutionNote = "The condition is no longer detected.";
            closed++;
        }
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A parallel check created the same exception; the next run reconciles.
            notes.Add("A parallel check saved some of the same exceptions; run the check again to reconcile.");
            foreach (var entry in _db.ChangeTracker.Entries<ProductionException>().Where(x => x.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
        }
        return new ExceptionEvaluationDto(factoryId, source, checkedCount, raised, refreshed, closed, notes, now);
    }

    // ── Mapping ─────────────────────────────────────────────────────────────

    internal static async Task<IReadOnlyList<ExceptionDto>> DtosAsync(BunonBrainDbContext db, IReadOnlyList<ProductionException> rows, CancellationToken ct)
    {
        var orderIds = rows.Where(r => r.OrderId != null).Select(r => r.OrderId!.Value).Distinct().ToList();
        var lineIds = rows.Where(r => r.LineId != null).Select(r => r.LineId!).Distinct().ToList();
        var orders = await db.ProductionOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);
        var lines = await db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return rows.Select(e =>
        {
            IReadOnlyList<Fact> data;
            try { data = JsonSerializer.Deserialize<List<Fact>>(e.SupportingData, Json) ?? new List<Fact>(); } catch (JsonException) { data = new List<Fact>(); }
            return new ExceptionDto(e.Id, e.FactoryId, e.Type, ExceptionTypes.Label(e.Type), e.Severity, e.Status, e.OrderId, e.OrderId is { } o ? orders.GetValueOrDefault(o) : null, e.AllocationId,
                e.PlanItemId, e.LineId, e.LineId is { } l ? lines.GetValueOrDefault(l) : null, e.ShiftId, e.ProductionDate is { } d ? Date(d) : null, e.Title, e.Reason, data,
                e.SuggestedInvestigation, e.SourceType, e.DetectedAt, e.LastDetectedAt, e.Occurrences, e.AcknowledgedAt, e.AcknowledgedByEmail, e.ResolvedAt, e.ResolvedByEmail,
                e.ResolutionNote, e.AutoResolved, e.Version);
        }).ToList();
    }
}
