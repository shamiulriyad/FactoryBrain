using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Planning;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Planning;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using static BunonBrain.Infrastructure.Planning.PlanningScope;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>A line's floor and department, as its workstations place it.</summary>
internal sealed record LinePlacement(Guid? FloorId, string? FloorName, Guid? DepartmentId, string? DepartmentName);

/// <summary>
/// Planning context (the factory's shifts, lines, floors and departments, and what the
/// caller may do) and the two pieces of configuration planning needs and must not guess:
/// which counters measure finished garments, and each line's capacity assumptions.
/// </summary>
public sealed class PlanningSetupService : IPlanningSetupService
{
    public const string OutputPointLimitMessage = "A line can have at most this many output counters.";
    public const int MaxOutputPointsPerLine = 5;
    public const double InterruptionMinutes = 10;

    private readonly BunonBrainDbContext _db;

    public PlanningSetupService(BunonBrainDbContext db) { _db = db; }

    // ── Context ─────────────────────────────────────────────────────────────

    public async Task<AccessResult<IReadOnlyList<FactoryRefDto>>> FactoriesAsync(AccessContext caller, CancellationToken ct)
    {
        var q = _db.Factories.AsNoTracking().Where(f => f.IsActive);
        if (!caller.IsPlatform) { var ids = caller.FactoryIds.ToList(); q = q.Where(f => ids.Contains(f.Id)); }
        var rows = await q.OrderBy(f => f.Code).Select(f => new FactoryRefDto(f.Id, f.Code, f.Name)).ToListAsync(ct);
        return AccessResult<IReadOnlyList<FactoryRefDto>>.Ok(rows);
    }

    internal static async Task<AccessResult<Factory>> ResolveFactoryAsync(BunonBrainDbContext db, AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        if (factoryId is { } id)
        {
            if (!caller.CanAccessFactory(id)) return AccessResult<Factory>.Forbidden("factory_out_of_scope", "You can only use your own factories.");
            var f = await db.Factories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return f is null ? AccessResult<Factory>.NotFound("factory_not_found", "Factory not found.") : AccessResult<Factory>.Ok(f);
        }
        var q = db.Factories.AsNoTracking().Where(f => f.IsActive);
        if (!caller.IsPlatform) { var ids = caller.FactoryIds.ToList(); q = q.Where(f => ids.Contains(f.Id)); }
        var list = await q.OrderBy(f => f.Code).Take(2).ToListAsync(ct);
        if (list.Count == 1) return AccessResult<Factory>.Ok(list[0]);
        return list.Count == 0
            ? AccessResult<Factory>.Forbidden("no_factory", "Your account has no factory assigned.")
            : AccessResult<Factory>.Invalid("factory_required", "Choose a factory (factoryId).");
    }

    internal static async Task<IReadOnlyDictionary<string, LinePlacement>> PlacementsAsync(BunonBrainDbContext db, IReadOnlyCollection<string> lineIds, CancellationToken ct)
    {
        if (lineIds.Count == 0) return new Dictionary<string, LinePlacement>();
        var rows = await db.Workstations.AsNoTracking().Where(w => lineIds.Contains(w.LineId) && w.IsActive)
            .GroupBy(w => new { w.LineId, w.FloorId, w.DepartmentId }).Select(g => new { g.Key.LineId, g.Key.FloorId, g.Key.DepartmentId, N = g.Count() }).ToListAsync(ct);
        var floorIds = rows.Where(r => r.FloorId != null).Select(r => r.FloorId!.Value).Distinct().ToList();
        var deptIds = rows.Select(r => r.DepartmentId).Distinct().ToList();
        var floors = await db.ProductionFloors.AsNoTracking().Where(f => floorIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var depts = await db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return rows.GroupBy(r => r.LineId).ToDictionary(g => g.Key, g =>
        {
            var floor = g.Where(x => x.FloorId != null).OrderByDescending(x => x.N).FirstOrDefault();
            var dept = g.OrderByDescending(x => x.N).First();
            return new LinePlacement(floor?.FloorId, floor?.FloorId is { } fid ? floors.GetValueOrDefault(fid) : null, dept.DepartmentId, depts.GetValueOrDefault(dept.DepartmentId));
        });
    }

    public async Task<AccessResult<PlanningContextDto>> ContextAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        var fr = await ResolveFactoryAsync(_db, caller, factoryId, ct);
        if (fr.Outcome != AccessOutcome.Ok) return new AccessResult<PlanningContextDto>(fr.Outcome, null, fr.Code, fr.Message);
        var factory = fr.Value!;
        var clock = await FactoryClock.LoadAsync(_db, factory.Id, ct);
        var linesQ = _db.ProductionLines.AsNoTracking().Where(l => l.FactoryId == factory.Id);
        if (caller.IsLineScoped) { var ls = caller.LineIds.ToList(); linesQ = linesQ.Where(l => ls.Contains(l.Id)); }
        var lines = await linesQ.OrderBy(l => l.Name).ToListAsync(ct);
        var ids = lines.Select(l => l.Id).ToList();
        var placements = await PlacementsAsync(_db, ids, ct);
        var settings = await _db.LineCapacitySettings.AsNoTracking().Where(s => ids.Contains(s.LineId)).ToDictionaryAsync(s => s.LineId, ct);
        var points = await _db.LineOutputPoints.AsNoTracking().Where(p => p.IsActive && ids.Contains(p.LineId)).GroupBy(p => p.LineId).Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var floors = await _db.ProductionFloors.AsNoTracking().Where(f => f.FactoryId == factory.Id && f.IsActive).OrderBy(f => f.Code).Select(f => new PlanningFloorDto(f.Id, f.Code, f.Name)).ToListAsync(ct);
        var depts = await _db.Departments.AsNoTracking().Where(d => d.FactoryId == factory.Id && d.IsActive).OrderBy(d => d.Code).Select(d => new PlanningDepartmentDto(d.Id, d.Code, d.Name)).ToListAsync(ct);

        var lineDtos = lines.Select(l =>
        {
            var p = placements.GetValueOrDefault(l.Id);
            var s = settings.GetValueOrDefault(l.Id);
            return new PlanningLineDto(l.Id, l.Name, l.IsActive, l.Workstations, p?.FloorId, p?.FloorName, p?.DepartmentId, p?.DepartmentName, s?.EfficiencyPct, s?.PlannedOperators,
                points.GetValueOrDefault(l.Id), caller.CanAccessLine(l.Id, l.FactoryId));
        }).ToList();
        var shifts = clock.Shifts.Select(s => new PlanningShiftDto(s.Id, s.Code, s.Name, s.Start.ToString("HH:mm"), s.End.ToString("HH:mm"), s.IsOvernight, s.ProductiveMinutes,
            AttendanceRules.DayNames(s.WorkingDays))).ToList();

        var caps = new PlanningCapabilitiesDto(
            caller.Has(Permissions.PlanningOrdersManage) || caller.Has(Permissions.ProductionManage),
            caller.Has(Permissions.PlanningStylesManage) || caller.Has(Permissions.ProductionSetup),
            caller.Has(Permissions.PlanningAllocationsManage), caller.Has(Permissions.PlanningTargetsManage), caller.Has(Permissions.PlanningPlansManage),
            caller.Has(Permissions.PlanningPlansApprove), caller.Has(Permissions.PlanningPlansPublish), caller.Has(Permissions.PlanningExceptionsReview),
            caller.Has(Permissions.PlanningSetup), caller.Has(Permissions.PlanningReportsExport), PlanningScope.SeesDrafts(caller), caller.IsLineScoped);
        return AccessResult<PlanningContextDto>.Ok(new PlanningContextDto(factory.Id, factory.Code, factory.Name, clock.ZoneId, Date(clock.Today(DateTimeOffset.UtcNow)), shifts, lineDtos,
            floors, depts, caps, new PlanningLimitsDto(MaxOutputPointsPerLine, PlanMath.DefaultTolerance * 100, (int)InterruptionMinutes, true)));
    }

    // ── Capacity settings ───────────────────────────────────────────────────

    public async Task<AccessResult<CapacitySettingsDto>> GetCapacitySettingsAsync(AccessContext caller, string lineId, CancellationToken ct)
    {
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line?.FactoryId is not { } fid || !caller.CanAccessLine(line.Id, fid)) return AccessResult<CapacitySettingsDto>.NotFound("line_not_found", "Line not found in your scope.");
        var s = await _db.LineCapacitySettings.AsNoTracking().FirstOrDefaultAsync(x => x.LineId == lineId, ct);
        return AccessResult<CapacitySettingsDto>.Ok(Map(lineId, fid, s));
    }

    public async Task<AccessResult<CapacitySettingsDto>> SaveCapacitySettingsAsync(AccessContext caller, string lineId, CapacitySettingsWrite body, string? traceId, CancellationToken ct)
    {
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line?.FactoryId is not { } fid || !caller.CanAccessFactory(fid)) return AccessResult<CapacitySettingsDto>.NotFound("line_not_found", "Line not found in your scope.");
        if (caller.Scope < RoleScope.Factory) return AccessResult<CapacitySettingsDto>.Forbidden("setup_scope", "Capacity settings are changed at factory level.");
        if (body.EfficiencyPct is { } e && !(e > 0 && e <= 150)) return AccessResult<CapacitySettingsDto>.Invalid("efficiency_invalid", "Efficiency must be above 0 and at most 150 %.");
        if (body.PlannedOperators is { } o && o is < 1 or > 500) return AccessResult<CapacitySettingsDto>.Invalid("operators_invalid", "Planned operators must be from 1 to 500.");
        if (body.Notes is { Length: > 1000 }) return AccessResult<CapacitySettingsDto>.Invalid("notes_invalid", "Notes are limited to 1000 characters.");

        var s = await _db.LineCapacitySettings.FirstOrDefaultAsync(x => x.LineId == lineId, ct);
        var before = s is null ? null : $"efficiency {s.EfficiencyPct}%, operators {s.PlannedOperators}";
        if (s is null)
        {
            s = new LineCapacitySetting { LineId = lineId, FactoryId = fid, UpdatedByEmail = caller.Email };
            _db.LineCapacitySettings.Add(s);
        }
        else if (body.ExpectedVersion is null || body.ExpectedVersion != s.Version)
            return AccessResult<CapacitySettingsDto>.Conflict("version_conflict", "The settings changed since you loaded them. Reload and try again.");
        if (body.ClearEfficiency == true) s.EfficiencyPct = null; else if (body.EfficiencyPct is { } eff) s.EfficiencyPct = Math.Round(eff, 1);
        if (body.ClearOperators == true) s.PlannedOperators = null; else if (body.PlannedOperators is { } ops) s.PlannedOperators = ops;
        if (body.Notes is not null) s.Notes = Clean(body.Notes);
        s.UpdatedAt = DateTimeOffset.UtcNow;
        s.UpdatedByEmail = caller.Email;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.capacity_settings_saved", "line_capacity_setting", lineId, before, $"efficiency {s.EfficiencyPct}%, operators {s.PlannedOperators}",
            $"Capacity assumptions of line {line.Name}", traceId, fid));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return AccessResult<CapacitySettingsDto>.Conflict("version_conflict", "The settings changed since you loaded them. Reload and try again."); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return AccessResult<CapacitySettingsDto>.Conflict("version_conflict", "The settings were just created by someone else. Reload and try again."); }
        var fresh = await _db.LineCapacitySettings.AsNoTracking().FirstAsync(x => x.LineId == lineId, ct);
        return AccessResult<CapacitySettingsDto>.Ok(Map(lineId, fid, fresh));
    }

    private static CapacitySettingsDto Map(string lineId, Guid factoryId, LineCapacitySetting? s) =>
        new(lineId, factoryId, s?.EfficiencyPct, s?.PlannedOperators, s?.Notes, s?.UpdatedAt, s?.UpdatedByEmail, s?.Version ?? 0);

    // ── Output points ───────────────────────────────────────────────────────

    public async Task<AccessResult<IReadOnlyList<OutputPointDto>>> OutputPointsAsync(AccessContext caller, Guid? factoryId, string? lineId, CancellationToken ct)
    {
        var fr = await ResolveFactoryAsync(_db, caller, factoryId, ct);
        if (fr.Outcome != AccessOutcome.Ok) return new AccessResult<IReadOnlyList<OutputPointDto>>(fr.Outcome, null, fr.Code, fr.Message);
        var q = _db.LineOutputPoints.AsNoTracking().Where(p => p.FactoryId == fr.Value!.Id && p.IsActive);
        if (Clean(lineId) is { } l) q = q.Where(p => p.LineId == l);
        if (caller.IsLineScoped) { var ls = caller.LineIds.ToList(); q = q.Where(p => ls.Contains(p.LineId)); }
        var points = await q.OrderBy(p => p.LineId).ThenBy(p => p.CreatedAt).ToListAsync(ct);
        return AccessResult<IReadOnlyList<OutputPointDto>>.Ok(await DtosAsync(points, ct));
    }

    public async Task<AccessResult<OutputPointDto>> AddOutputPointAsync(AccessContext caller, OutputPointWrite body, string? traceId, CancellationToken ct)
    {
        if (Clean(body.LineId) is not { } lineId) return AccessResult<OutputPointDto>.Invalid("line_required", "lineId is required.");
        if (body.DeviceId is not { } deviceId) return AccessResult<OutputPointDto>.Invalid("device_required", "deviceId is required.");
        if (body.Note is { Length: > 300 }) return AccessResult<OutputPointDto>.Invalid("note_invalid", "The note is limited to 300 characters.");
        var line = await _db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId, ct);
        if (line?.FactoryId is not { } fid || !caller.CanAccessFactory(fid)) return AccessResult<OutputPointDto>.NotFound("line_not_found", "Line not found in your scope.");
        if (caller.Scope < RoleScope.Factory) return AccessResult<OutputPointDto>.Forbidden("setup_scope", "Output counters are configured at factory level.");
        var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId && d.FactoryId == fid, ct);
        if (device is null) return AccessResult<OutputPointDto>.NotFound("device_not_found", "The device doesn't exist in this line's factory.");
        if (device.Category != DeviceCategories.ProductionCounter)
            return AccessResult<OutputPointDto>.Invalid("device_not_counter", "Only a production counter device can measure a line's output.");
        if (device.Status == "Disabled") return AccessResult<OutputPointDto>.Invalid("device_disabled", "The device is disabled.");
        if (device.MachineId is { } mid)
        {
            var profile = await _db.MachineMonitoringProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.MachineId == mid, ct);
            if (profile?.CountingMode == CountingModes.CycleNotPiece)
                return AccessResult<OutputPointDto>.Invalid("counter_counts_cycles", "This counter's machine counts cycles, not garments, so it can't measure finished output.");
        }
        if (await _db.LineOutputPoints.AnyAsync(p => p.LineId == lineId && p.DeviceId == deviceId && p.IsActive, ct))
            return AccessResult<OutputPointDto>.Conflict("output_point_exists", "This counter already measures the line's output.");
        if (await _db.LineOutputPoints.CountAsync(p => p.LineId == lineId && p.IsActive, ct) >= MaxOutputPointsPerLine)
            return AccessResult<OutputPointDto>.Conflict("output_point_limit", $"A line can have at most {MaxOutputPointsPerLine} output counters.");

        var point = new LineOutputPoint { FactoryId = fid, LineId = lineId, DeviceId = deviceId, Note = Clean(body.Note), CreatedByEmail = caller.Email };
        _db.LineOutputPoints.Add(point);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.output_point_added", "line_output_point", point.Id.ToString(), null, "active",
            $"Counter {device.Code} now measures finished output of line {line.Name}", traceId, fid));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return AccessResult<OutputPointDto>.Conflict("output_point_exists", "This counter already measures the line's output."); }
        return AccessResult<OutputPointDto>.Created((await DtosAsync(new[] { point }, ct))[0]);
    }

    public async Task<AccessResult<OutputPointDto>> RemoveOutputPointAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct)
    {
        var point = await _db.LineOutputPoints.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (point is null || !caller.CanAccessFactory(point.FactoryId)) return AccessResult<OutputPointDto>.NotFound("output_point_not_found", "Output counter not found in your scope.");
        if (caller.Scope < RoleScope.Factory) return AccessResult<OutputPointDto>.Forbidden("setup_scope", "Output counters are configured at factory level.");
        if (!point.IsActive) return AccessResult<OutputPointDto>.Conflict("output_point_inactive", "This counter was already removed.");
        point.IsActive = false;
        point.DeactivatedAt = DateTimeOffset.UtcNow;
        point.DeactivatedByEmail = caller.Email;
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "planning.output_point_removed", "line_output_point", point.Id.ToString(), "active", "removed",
            $"Counter no longer measures finished output of line {point.LineId}", traceId, point.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<OutputPointDto>.Ok((await DtosAsync(new[] { point }, ct))[0]);
    }

    private async Task<IReadOnlyList<OutputPointDto>> DtosAsync(IReadOnlyList<LineOutputPoint> points, CancellationToken ct)
    {
        var deviceIds = points.Select(p => p.DeviceId).Distinct().ToList();
        var lineIds = points.Select(p => p.LineId).Distinct().ToList();
        var devices = await _db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var machineIds = devices.Values.Where(d => d.MachineId != null).Select(d => d.MachineId!.Value).ToList();
        var modes = await _db.MachineMonitoringProfiles.AsNoTracking().Where(p => machineIds.Contains(p.MachineId)).ToDictionaryAsync(p => p.MachineId, p => p.CountingMode, ct);
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return points.Select(p =>
        {
            var d = devices.GetValueOrDefault(p.DeviceId);
            return new OutputPointDto(p.Id, p.FactoryId, p.LineId, lines.GetValueOrDefault(p.LineId, p.LineId), p.DeviceId, d?.Code ?? "?", d?.Name ?? "?", d?.IsSimulated ?? false,
                d?.ConnectionStatus ?? "Unknown", d?.MachineId is { } m ? modes.GetValueOrDefault(m) : null, p.Note, p.IsActive, p.CreatedAt, p.CreatedByEmail);
        }).ToList();
    }
}
