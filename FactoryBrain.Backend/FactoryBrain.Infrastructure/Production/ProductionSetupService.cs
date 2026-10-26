using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Production;
using FactoryBrain.Application.Production;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static FactoryBrain.Infrastructure.Production.ProductionSupport;

namespace FactoryBrain.Infrastructure.Production;

/// <summary>
/// Plant configuration: lines, the shift calendar and styles with their
/// operation bulletins (the factory-approved SMVs every efficiency and
/// target figure uses). Writes are Admin-only, audited and use the row
/// version for optimistic concurrency. Nothing is ever seeded.
/// </summary>
public sealed class ProductionSetupService : IProductionSetupService
{
    private readonly FactoryBrainDbContext _db;
    private readonly ProductionOptions _options;

    public ProductionSetupService(FactoryBrainDbContext db, ProductionOptions options) { _db = db; _options = options; }

    public static readonly IReadOnlyDictionary<string, string> Definitions = new Dictionary<string, string>
    {
        ["units"] = "Quantities are garments (pieces); time is minutes; rates are pieces per hour; SMV is standard minutes per piece. Percentages are rounded half away from zero to 1 decimal.",
        ["timeZone"] = "Shifts, plan dates, due dates and production dates are plant-local in the configured time zone; instants are UTC. A shift belongs to the date it starts.",
        ["outputVariance"] = "Actual − planned good output for the same line, order and time window, pieces.",
        ["achievementPct"] = "Actual ÷ planned output × 100, only when planned > 0. This is not efficiency.",
        ["lineEfficiencyPct"] = "Earned standard minutes (good sewing output × style SMV) ÷ available minutes (operators present × interval minutes, excluding planned breaks) × 100. Downtime stays in the available minutes; records without operators or SMV are excluded and counted.",
        ["efficiencyExcludingDowntimePct"] = "As line efficiency, with operators × recorded downtime minutes removed from the available minutes.",
        ["plannedOutput"] = "Target rate (operators × 60 × target efficiency ÷ SMV) during the plan's scheduled minutes, capped at the planned quantity. Without an SMV the planned quantity is spread evenly over the scheduled minutes.",
        ["requiredRate"] = "Remaining quantity ÷ remaining scheduled line-hours before the due time.",
        ["wip"] = "Good pieces out of one stage − (good + rejected) out of the next, as of now; buffers are never summed. Negative balances are reported as inconsistent.",
        ["capacityUtilizationPct"] = "Scheduled plan line-minutes ÷ calendar shift line-minutes of the active lines in the window × 100 (line loading).",
        ["bottleneck"] = "The time-studied operation with the lowest capacity (3600 × operators ÷ cycle seconds), with queue counts and line output as supporting evidence; without time studies, the busiest station at standard minutes (labelled as such).",
        ["lineBalance"] = "Station load = Σ SMV ÷ stations sharing each operation; balance efficiency = Σ SMV ÷ (stations × largest load) × 100; smoothness index = √Σ(largest load − load)².",
        ["delayRisk"] = "Rule-based level from the observed rate and the remaining scheduled time; not a probability.",
    };

    // ── Config ──────────────────────────────────────────────────────────────

    public async Task<ProductionConfigDto> ConfigAsync(CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var bySource = await _db.ProductionOutputEvents.AsNoTracking().Where(e => e.RecordedAt >= since)
            .GroupBy(e => e.Source).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int Count(ProductionDataSource s) => bySource.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        var integration = Count(ProductionDataSource.Integration);
        var offset = _options.TimeZone.GetUtcOffset(DateTime.UtcNow);
        return new ProductionConfigDto(
            _options.TimeZoneId,
            $"UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset.Duration():hh\\:mm}",
            _options.RequireSeparateApprover, _options.MaxBackfillDays, _options.MaxIntervalHours, _options.RateLookbackDays,
            _options.RiskSlackPct, _options.ObservationMaxAgeDays, _options.ConfigurationError,
            await _db.ProductionLines.CountAsync(l => l.IsActive, ct),
            await _db.ProductionShifts.CountAsync(s => s.IsActive, ct),
            await _db.ProductionStyles.CountAsync(s => s.IsActive, ct),
            await _db.ProductionOrders.CountAsync(o => o.Status == ProductionOrderStatus.Open, ct),
            new[]
            {
                new DataSourceStatus("manual", "available", "Operators record output per line, order, stage and interval on the Production page.", Count(ProductionDataSource.Manual)),
                new DataSourceStatus("import", "available", "CSV files are parsed in the browser and posted to POST /api/production/output/import; each row's sourceRef deduplicates re-imports.", Count(ProductionDataSource.Import)),
                new DataSourceStatus("integration", integration > 0 ? "receiving" : "not_connected",
                    integration > 0
                        ? "Records with source=integration arrived through POST /api/production/output in the last 24 hours."
                        : "No ERP, MES or machine-data connector is configured. A future connector can post to POST /api/production/output with source=integration and its own sourceRef.",
                    integration),
            },
            Definitions);
    }

    // ── Lines ───────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProductionLineDto>> ListLinesAsync(bool includeInactive, CancellationToken ct) =>
        (await _db.ProductionLines.AsNoTracking().Where(l => includeInactive || l.IsActive).OrderBy(l => l.Id).ToListAsync(ct))
        .Select(ToDto).ToList();

    public async Task<ProductionResult<ProductionLineDto>> CreateLineAsync(ProductionLineWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = Errors();
        var id = Clean(body.Id)?.ToLowerInvariant();
        if (!ProductionWire.IsSlug(id, 32)) errors.Add("id", "Use 1–32 lowercase letters, digits, hyphens or underscores (e.g. line-3).");
        ValidateLine(errors, body.Name, body.Workstations, required: true);
        if (errors.Count > 0) return ProductionResult<ProductionLineDto>.Invalid(errors);
        if (await _db.ProductionLines.AnyAsync(l => l.Id == id, ct))
            return ProductionResult<ProductionLineDto>.Conflict("duplicate_line", $"A line with id '{id}' already exists.");

        var now = DateTime.UtcNow;
        var line = new ProductionLine { Id = id!, Name = body.Name!.Trim(), Workstations = body.Workstations!.Value, IsActive = body.IsActive ?? true, CreatedAt = now, UpdatedAt = now };
        _db.ProductionLines.Add(line);
        _db.AuditLog.Add(Audit(actor, traceId, "production_line.created", "production_line", line.Id, null, "active",
            $"{line.Name} · {line.Workstations} workstations"));
        return await SaveAsync(() => ProductionResult<ProductionLineDto>.Created(ToDto(line)), "duplicate_line", "A line with this id or name already exists.", ct);
    }

    public async Task<ProductionResult<ProductionLineDto>> UpdateLineAsync(string id, ProductionLineWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var line = await _db.ProductionLines.FirstOrDefaultAsync(l => l.Id == id, ct);
        if (line is null) return ProductionResult<ProductionLineDto>.NotFound("line_not_found");
        var errors = Errors();
        if (body.ExpectedVersion is null) errors.Add("expectedVersion", "expectedVersion is required.");
        ValidateLine(errors, body.Name, body.Workstations, required: false);
        if (errors.Count > 0) return ProductionResult<ProductionLineDto>.Invalid(errors);
        if (body.ExpectedVersion != line.Version) return VersionConflict<ProductionLineDto>("line");
        if (body.IsActive == false && line.IsActive
            && await _db.ProductionPlans.AnyAsync(p => p.LineId == id && (p.Status == ProductionPlanStatus.Active || p.Status == ProductionPlanStatus.PendingApproval), ct))
            return ProductionResult<ProductionLineDto>.Conflict("line_in_use", "The line has active or pending plans. Complete or cancel them before deactivating it.");

        var before = $"{line.Name} · {line.Workstations} workstations · {(line.IsActive ? "active" : "inactive")}";
        if (body.Name is not null) line.Name = body.Name.Trim();
        if (body.Workstations is { } w) line.Workstations = w;
        if (body.IsActive is { } a) line.IsActive = a;
        line.UpdatedAt = DateTime.UtcNow;
        var after = $"{line.Name} · {line.Workstations} workstations · {(line.IsActive ? "active" : "inactive")}";
        _db.AuditLog.Add(Audit(actor, traceId, "production_line.updated", "production_line", line.Id, before, after, null));
        return await SaveAsync(() => ProductionResult<ProductionLineDto>.Ok(ToDto(line)), "duplicate_line", "Another line already has this name.", ct);
    }

    private static void ValidateLine(Dictionary<string, string[]> errors, string? name, int? workstations, bool required)
    {
        if (required || name is not null)
            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80) errors.Add("name", "Name is required (at most 80 characters).");
        if (required || workstations is not null)
            if (workstations is not (>= 1 and <= 300)) errors.Add("workstations", "Workstations must be from 1 to 300.");
    }

    // ── Shifts ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ShiftDto>> ListShiftsAsync(bool includeInactive, CancellationToken ct) =>
        (await _db.ProductionShifts.AsNoTracking().Where(s => includeInactive || s.IsActive).ToListAsync(ct))
        .OrderBy(s => s.StartLocal).Select(ShiftToDto).ToList();

    public async Task<ProductionResult<ShiftDto>> CreateShiftAsync(ShiftWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = Errors();
        var code = Clean(body.Code)?.ToLowerInvariant();
        if (!ProductionWire.IsSlug(code, 16)) errors.Add("code", "Use 1–16 lowercase letters, digits, hyphens or underscores (e.g. day).");
        var shift = new ProductionShift { Code = code ?? "" };
        ApplyShift(errors, shift, body, creating: true);
        if (errors.Count > 0) return ProductionResult<ShiftDto>.Invalid(errors);
        if (await _db.ProductionShifts.AnyAsync(s => s.Code == code, ct))
            return ProductionResult<ShiftDto>.Conflict("duplicate_shift", $"A shift with code '{code}' already exists.");
        if (shift.IsActive && await OverlapError(shift, ct) is { } overlap) return ProductionResult<ShiftDto>.Invalid("start", overlap);

        shift.CreatedAt = shift.UpdatedAt = DateTime.UtcNow;
        _db.ProductionShifts.Add(shift);
        _db.AuditLog.Add(Audit(actor, traceId, "production_shift.created", "production_shift", shift.Code, null, Describe(shift), null));
        return await SaveAsync(() => ProductionResult<ShiftDto>.Created(ShiftToDto(shift)), "duplicate_shift", "A shift with this code or name already exists.", ct);
    }

    public async Task<ProductionResult<ShiftDto>> UpdateShiftAsync(string code, ShiftWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var shift = await _db.ProductionShifts.FirstOrDefaultAsync(s => s.Code == code, ct);
        if (shift is null) return ProductionResult<ShiftDto>.NotFound("shift_not_found");
        var errors = Errors();
        if (body.ExpectedVersion is null) errors.Add("expectedVersion", "expectedVersion is required.");
        var before = Describe(shift);
        ApplyShift(errors, shift, body, creating: false);
        if (errors.Count > 0) return ProductionResult<ShiftDto>.Invalid(errors);
        if (body.ExpectedVersion != shift.Version) return VersionConflict<ShiftDto>("shift");
        if (shift.IsActive && await OverlapError(shift, ct) is { } overlap) return ProductionResult<ShiftDto>.Invalid("start", overlap);

        shift.UpdatedAt = DateTime.UtcNow;
        _db.AuditLog.Add(Audit(actor, traceId, "production_shift.updated", "production_shift", shift.Code, before, Describe(shift),
            "Plans and summaries are evaluated against the current calendar."));
        return await SaveAsync(() => ProductionResult<ShiftDto>.Ok(ShiftToDto(shift)), "duplicate_shift", "Another shift already has this name.", ct);
    }

    private static void ApplyShift(Dictionary<string, string[]> errors, ProductionShift s, ShiftWrite b, bool creating)
    {
        if (creating || b.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(b.Name) || b.Name.Trim().Length > 60) errors.Add("name", "Name is required (at most 60 characters).");
            else s.Name = b.Name.Trim();
        }
        if (creating || b.Start is not null)
        {
            if (ProductionWire.TryParseTime(b.Start, out var t)) s.StartLocal = t; else errors.Add("start", "Start must be a local time HH:mm.");
        }
        if (creating || b.End is not null)
        {
            if (ProductionWire.TryParseTime(b.End, out var t)) s.EndLocal = t; else errors.Add("end", "End must be a local time HH:mm.");
        }
        if (creating || b.BreakStart is not null || b.BreakMinutes is not null)
        {
            if (string.IsNullOrWhiteSpace(b.BreakStart)) s.BreakStartLocal = null;
            else if (ProductionWire.TryParseTime(b.BreakStart, out var t)) s.BreakStartLocal = t;
            else errors.Add("breakStart", "Break start must be a local time HH:mm.");
            s.BreakMinutes = b.BreakMinutes ?? 0;
            if (s.BreakMinutes is < 0 or > 240) errors.Add("breakMinutes", "Break minutes must be from 0 to 240.");
            if (s.BreakMinutes > 0 && s.BreakStartLocal is null) errors.Add("breakStart", "Give the break start when the shift has a break.");
        }
        if (creating || b.Weekdays is not null)
        {
            if (ProductionWire.WeekdayMask(b.Weekdays) is { } mask) s.Weekdays = mask;
            else errors.Add("weekdays", $"Give at least one working day from: {string.Join(", ", ProductionWire.DayNames)}.");
        }
        if (b.IsActive is { } active) s.IsActive = active;
        if (errors.ContainsKey("start") || errors.ContainsKey("end") || errors.ContainsKey("breakStart")) return;

        if (s.StartLocal == s.EndLocal) { errors.Add("end", "End must differ from start."); return; }
        var spec = Spec(s);
        var working = spec.LengthMinutes - s.BreakMinutes;
        if (working <= 0) errors.Add("breakMinutes", "The break is as long as the shift.");
        if (spec.LengthMinutes > 16 * 60) errors.Add("end", "A shift can be at most 16 hours long.");
        if (s.BreakStartLocal is not null && s.BreakMinutes > 0)
        {
            var cal = new ProductionCalendar(TimeZoneInfo.Utc, new[] { spec });
            var day = new DateOnly(2026, 1, 5);
            var (start, end) = cal.Occurrence(spec, day);
            var brk = cal.BreakOf(spec, day)!.Value;
            if (brk.Start < start || brk.End > end) errors.Add("breakStart", "The break must lie inside the shift.");
        }
    }

    private async Task<string?> OverlapError(ProductionShift shift, CancellationToken ct)
    {
        var others = await _db.ProductionShifts.AsNoTracking().Where(s => s.IsActive && s.Code != shift.Code).ToListAsync(ct);
        var overlaps = ProductionCalendar.FindOverlaps(others.Select(Spec).Append(Spec(shift)).ToList())
            .Where(p => p.A == shift.Code || p.B == shift.Code).ToList();
        return overlaps.Count == 0 ? null
            : $"The shift overlaps {string.Join(", ", overlaps.Select(p => p.A == shift.Code ? p.B : p.A))}: every instant must belong to at most one shift.";
    }

    private static string Describe(ProductionShift s) =>
        $"{s.Name} {ProductionWire.Time(s.StartLocal)}–{ProductionWire.Time(s.EndLocal)}" +
        (s.BreakMinutes > 0 ? $" · break {ProductionWire.Time(s.BreakStartLocal!.Value)} {s.BreakMinutes} min" : "") +
        $" · {string.Join(",", ProductionWire.Weekdays(s.Weekdays))} · {(s.IsActive ? "active" : "inactive")}";

    private static ShiftDto ShiftToDto(ProductionShift s)
    {
        var spec = Spec(s);
        return new ShiftDto(s.Code, s.Name, ProductionWire.Time(s.StartLocal), ProductionWire.Time(s.EndLocal),
            s.BreakStartLocal is { } b ? ProductionWire.Time(b) : null, s.BreakMinutes, ProductionWire.Weekdays(s.Weekdays),
            spec.Overnight, spec.LengthMinutes - s.BreakMinutes, s.IsActive, s.Version);
    }

    // ── Styles ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<StyleDto>> ListStylesAsync(bool includeInactive, CancellationToken ct) =>
        (await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).Where(s => includeInactive || s.IsActive)
            .OrderBy(s => s.Code).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<StyleDto?> GetStyleAsync(Guid id, CancellationToken ct)
    {
        var s = await _db.ProductionStyles.AsNoTracking().Include(x => x.Operations).FirstOrDefaultAsync(x => x.Id == id, ct);
        return s is null ? null : ToDto(s);
    }

    public async Task<ProductionResult<StyleDto>> CreateStyleAsync(StyleWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var errors = Errors();
        var style = new ProductionStyle();
        ApplyStyleFields(errors, style, body, creating: true);
        var ops = ValidateOperations(errors, body.Operations ?? Array.Empty<StyleOperationWrite>(), existing: null);
        if (errors.Count > 0) return ProductionResult<StyleDto>.Invalid(errors);
        if (await _db.ProductionStyles.AnyAsync(s => s.Code == style.Code, ct))
            return ProductionResult<StyleDto>.Conflict("duplicate_style", $"A style with code '{style.Code}' already exists.");

        style.CreatedAt = style.UpdatedAt = DateTime.UtcNow;
        foreach (var o in ops) { o.StyleId = style.Id; style.Operations.Add(o); }
        _db.ProductionStyles.Add(style);
        _db.AuditLog.Add(Audit(actor, traceId, "production_style.created", "production_style", style.Id.ToString(), null, DescribeStyle(style), null));
        return await SaveAsync(() => ProductionResult<StyleDto>.Created(ToDto(style)), "duplicate_style", "A style with this code already exists.", ct);
    }

    public async Task<ProductionResult<StyleDto>> UpdateStyleAsync(Guid id, StyleWrite body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var style = await _db.ProductionStyles.Include(s => s.Operations).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (style is null) return ProductionResult<StyleDto>.NotFound("style_not_found");
        var errors = Errors();
        if (body.ExpectedVersion is null) errors.Add("expectedVersion", "expectedVersion is required.");
        var before = DescribeStyle(style);
        ApplyStyleFields(errors, style, body, creating: false);
        List<StyleOperation>? ops = null;
        if (body.Operations is not null) ops = ValidateOperations(errors, body.Operations, style.Operations);
        if (errors.Count > 0) return ProductionResult<StyleDto>.Invalid(errors);
        if (body.ExpectedVersion != style.Version) return VersionConflict<StyleDto>("style");

        if (ops is not null)
        {
            var keep = ops.Select(o => o.Id).ToHashSet();
            var removed = style.Operations.Where(o => !keep.Contains(o.Id)).ToList();
            if (removed.Count > 0)
            {
                var removedIds = removed.Select(o => o.Id).ToList();
                var observed = await _db.OperationObservations.AnyAsync(o => removedIds.Contains(o.OperationId), ct);
                var inLayout = await _db.LineLayoutStations.AnyAsync(s => s.OperationIds.Any(x => removedIds.Contains(x)), ct);
                if (observed || inLayout)
                    return ProductionResult<StyleDto>.Invalid("operations",
                        $"Operations {string.Join(", ", removed.Select(o => o.Sequence))} have time studies or are on a recorded layout, so they can't be removed. Keep them (you can still change their SMV).");
                _db.StyleOperations.RemoveRange(removed);
            }
            foreach (var o in ops)
            {
                var existing = style.Operations.FirstOrDefault(e => e.Id == o.Id);
                if (existing is null) { o.StyleId = style.Id; _db.StyleOperations.Add(o); }
                else { existing.Sequence = o.Sequence; existing.Name = o.Name; existing.Smv = o.Smv; existing.MachineType = o.MachineType; existing.Predecessors = o.Predecessors; }
            }
        }
        style.UpdatedAt = DateTime.UtcNow;
        _db.AuditLog.Add(Audit(actor, traceId, "production_style.updated", "production_style", style.Id.ToString(), before, DescribeStyle(style, ops),
            ops is null ? null : "Operation bulletin replaced; line-balance proposals computed from the old bulletin can no longer be applied."));

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                if (ops is not null) await ParkRenumberedOperationsAsync(style, ops, ct);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch (DbUpdateConcurrencyException) { await tx.RollbackAsync(ct); return VersionConflict<StyleDto>("style"); }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                await tx.RollbackAsync(ct);
                return ProductionResult<StyleDto>.Conflict("duplicate_style", "Another style already has this code.");
            }
            var fresh = await _db.ProductionStyles.AsNoTracking().Include(s => s.Operations).FirstAsync(s => s.Id == id, ct);
            return ProductionResult<StyleDto>.Ok(ToDto(fresh));
        });
    }

    /// <summary>
    /// Sequence numbers may be swapped or shifted between kept operations. Each
    /// operation whose number changes is first moved to a free number (one no
    /// old or new operation uses), so the unique (style, sequence) index never
    /// sees a transient duplicate when EF then writes the final numbers.
    /// </summary>
    private async Task ParkRenumberedOperationsAsync(ProductionStyle style, IReadOnlyList<StyleOperation> ops, CancellationToken ct)
    {
        var used = style.Operations.Select(o => _db.Entry(o).Property(x => x.Sequence).OriginalValue).Concat(ops.Select(o => o.Sequence)).ToHashSet();
        var free = Enumerable.Range(1, 9999).Reverse().Where(n => !used.Contains(n)).GetEnumerator();
        foreach (var op in style.Operations)
        {
            var entry = _db.Entry(op);
            if (entry.State != EntityState.Modified) continue;
            var original = entry.Property(x => x.Sequence).OriginalValue;
            if (original == op.Sequence) continue;
            free.MoveNext();
            var parked = free.Current;
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE production_style_operations SET \"Sequence\" = {parked} WHERE \"Id\" = {op.Id}", ct);
            entry.Property(x => x.Sequence).OriginalValue = parked;
        }
    }

    private static void ApplyStyleFields(Dictionary<string, string[]> errors, ProductionStyle s, StyleWrite b, bool creating)
    {
        if (creating || b.Code is not null)
        {
            var code = Clean(b.Code);
            if (code is null || code.Length > 40) errors.Add("code", "Style code is required (at most 40 characters).");
            else s.Code = code;
        }
        if (creating || b.Name is not null)
        {
            var name = Clean(b.Name);
            if (name is null || name.Length > 120) errors.Add("name", "Name is required (at most 120 characters).");
            else s.Name = name;
        }
        if (b.ProductType is not null) s.ProductType = Truncate(Clean(b.ProductType), 64);
        if (b.ClearApprovedSmv == true) s.ApprovedSmv = null;
        else if (b.ApprovedSmv is { } smv)
        {
            if (smv is > 0 and <= 600) s.ApprovedSmv = Math.Round(smv, 4);
            else errors.Add("approvedSmv", "Approved SMV must be more than 0 and at most 600 minutes.");
        }
        if (b.IsActive is { } a) s.IsActive = a;
    }

    private static List<StyleOperation> ValidateOperations(Dictionary<string, string[]> errors, IReadOnlyList<StyleOperationWrite> input, IReadOnlyList<StyleOperation>? existing)
    {
        var ops = new List<StyleOperation>();
        if (input.Count > LineBalancer.MaxOperations) { errors.Add("operations", $"At most {LineBalancer.MaxOperations} operations."); return ops; }
        foreach (var (o, i) in input.Select((o, i) => (o, i)))
        {
            var where = $"Operation {i + 1}";
            if (o.Sequence is < 1 or > 9999) errors.Add("operations", $"{where}: sequence must be from 1 to 9999.");
            var name = Clean(o.Name);
            if (name is null || name.Length > 120) errors.Add("operations", $"{where}: name is required (at most 120 characters).");
            if (!(o.Smv > 0 && o.Smv <= 120)) errors.Add("operations", $"{where}: SMV must be more than 0 and at most 120 minutes.");
            if (o.MachineType is { Length: > 32 }) errors.Add("operations", $"{where}: machine type is at most 32 characters.");
            if (o.Id is { } id && existing is not null && existing.All(e => e.Id != id))
                errors.Add("operations", $"{where}: operation {id} does not belong to this style.");
            ops.Add(new StyleOperation
            {
                Id = o.Id ?? Guid.NewGuid(),
                Sequence = o.Sequence,
                Name = name ?? "",
                Smv = Math.Round(o.Smv, 4),
                MachineType = Clean(o.MachineType),
                Predecessors = (o.Predecessors ?? Array.Empty<int>()).Distinct().OrderBy(x => x).ToArray(),
            });
        }
        if (errors.ContainsKey("operations")) return ops;
        foreach (var p in LineBalancer.Validate(ops.Select(ToBalance).ToList()).Where(p => !p.StartsWith("The style has no operation", StringComparison.Ordinal)))
            errors.Add("operations", p);
        return ops;
    }

    private static string DescribeStyle(ProductionStyle s, IReadOnlyList<StyleOperation>? ops = null)
    {
        var list = ops ?? s.Operations;
        return $"{s.Code} · {s.Name} · approved SMV {(s.ApprovedSmv is { } a ? a.ToString("0.###") : "none")} · {list.Count} operations, Σ SMV {list.Sum(o => o.Smv):0.###} · {(s.IsActive ? "active" : "inactive")}";
    }

    // ── Shared ──────────────────────────────────────────────────────────────

    private static ProductionResult<T> VersionConflict<T>(string what) =>
        ProductionResult<T>.Conflict("version_conflict", $"The {what} changed since you loaded it. Reload and try again.");

    private async Task<ProductionResult<T>> SaveAsync<T>(Func<ProductionResult<T>> ok, string duplicateCode, string duplicateMessage, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
            return ok();
        }
        catch (DbUpdateConcurrencyException) { return VersionConflict<T>("record"); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return ProductionResult<T>.Conflict(duplicateCode, duplicateMessage); }
    }
}
