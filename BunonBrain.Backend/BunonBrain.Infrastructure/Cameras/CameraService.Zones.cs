using BunonBrain.Application.Access;
using BunonBrain.Application.Cameras;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Cameras;
using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Cameras;

// Zones: normalised polygons over the camera's reference frame. A zone that
// names a workstation or line must point inside the camera's coverage (the
// camera has to be able to see it). Every create / change writes a revision
// row (append-only), with the frame the polygon was drawn on.

public sealed partial class CameraService
{
    public async Task<AccessResult<IReadOnlyList<CameraZoneDto>>> ZonesAsync(AccessContext caller, Guid cameraId, bool includeInactive, CancellationToken ct)
    {
        if (await FindAsync(caller, cameraId, ct) is null) return NotFound<IReadOnlyList<CameraZoneDto>>();
        var zones = _db.CameraZones.AsNoTracking().Where(z => z.CameraId == cameraId);
        if (!includeInactive) zones = zones.Where(z => z.IsActive);
        var rows = await zones.OrderByDescending(z => z.IsActive).ThenBy(z => z.Name).ToListAsync(ct);
        return AccessResult<IReadOnlyList<CameraZoneDto>>.Ok(await ZoneDtosAsync(rows, ct));
    }

    public async Task<AccessResult<CameraZoneDto>> CreateZoneAsync(AccessContext caller, Guid cameraId, CreateCameraZoneRequest r, string? traceId, CancellationToken ct)
    {
        var camera = await FindAsync(caller, cameraId, ct);
        if (camera is null) return NotFound<CameraZoneDto>();
        if (!CanConfigure(caller, camera.FactoryId)) return NoConfigure<CameraZoneDto>();
        if (camera.ReferenceImageKey is null)
            return AccessResult<CameraZoneDto>.Invalid("reference_frame_required",
                "Add a reference frame first (upload an image or grab a frame from the recorded video): zones are drawn over it.");
        var check = await CheckZoneAsync(camera, r.Name, r.ZoneType, r.Points, r.WorkstationId, r.LineId, active: true, ignoreZoneId: null, ct);
        if (check.Error is { } error) return error.As<CameraZoneDto>();

        if (await _db.CameraZones.CountAsync(z => z.CameraId == cameraId && z.IsActive, ct) >= CameraRules.MaxZonesPerCamera)
            return AccessResult<CameraZoneDto>.Conflict("too_many_zones", $"A camera can have at most {CameraRules.MaxZonesPerCamera} active zones.");

        var now = DateTimeOffset.UtcNow;
        var zone = new CameraZone
        {
            CameraId = cameraId, FactoryId = camera.FactoryId, Name = r.Name.Trim(), ZoneType = check.Type!, WorkstationId = check.WorkstationId,
            LineId = check.LineId, Polygon = ZoneGeometry.Serialize(check.Points!), Color = r.Color?.ToUpperInvariant(), Notes = Clean(r.Notes),
            IsActive = true, Version = 1, CreatedAt = now, CreatedByEmail = caller.Email, UpdatedAt = now, UpdatedByEmail = caller.Email,
        };
        _db.CameraZones.Add(zone);
        _db.CameraZoneRevisions.Add(Revision(zone, "Created", camera.ReferenceImageKey, caller, now));
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "camera_zone.created", AccessAudit.EntityCameraZone, zone.Id.ToString(), null, "active",
            $"camera {camera.Code}: zone '{zone.Name}' ({zone.ZoneType}, {check.Points!.Count} points) created", traceId, camera.FactoryId));
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return AccessResult<CameraZoneDto>.Created((await ZoneDtosAsync(new[] { zone }, ct))[0]);
    }

    public async Task<AccessResult<CameraZoneDto>> UpdateZoneAsync(AccessContext caller, Guid zoneId, UpdateCameraZoneRequest r, string? traceId, CancellationToken ct)
    {
        var existing = await _db.CameraZones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == zoneId, ct);
        var camera = existing is null ? null : await FindAsync(caller, existing.CameraId, ct);
        if (existing is null || camera is null) return AccessResult<CameraZoneDto>.NotFound("zone_not_found", "Zone not found.");
        if (!CanConfigure(caller, camera.FactoryId)) return NoConfigure<CameraZoneDto>();
        // A zone being deactivated isn't held to the coverage rules (its workstation may have been removed already).
        var check = await CheckZoneAsync(camera, r.Name, r.ZoneType, r.Points, r.WorkstationId, r.LineId, active: r.IsActive, ignoreZoneId: zoneId, ct);
        if (check.Error is { } error) return error.As<CameraZoneDto>();
        if (r.IsActive && !existing.IsActive && camera.ReferenceImageKey is null)
            return AccessResult<CameraZoneDto>.Invalid("reference_frame_required", "Add a reference frame before reactivating zones.");

        var strategy = _db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await CameraLocks.ZoneAsync(_db, zoneId, ct);
            var zone = await _db.CameraZones.FirstAsync(z => z.Id == zoneId, ct);
            if (r.ExpectedVersion is { } expected && expected != zone.Version)
            {
                await tx.RollbackAsync(ct);
                return AccessResult<CameraZoneDto>.Conflict("zone_version_conflict",
                    $"Someone else changed this zone (now version {zone.Version}). Reload it and make your change again.");
            }
            if (r.IsActive && !zone.IsActive
                && await _db.CameraZones.CountAsync(z => z.CameraId == zone.CameraId && z.IsActive, ct) >= CameraRules.MaxZonesPerCamera)
            {
                await tx.RollbackAsync(ct);
                return AccessResult<CameraZoneDto>.Conflict("too_many_zones", $"A camera can have at most {CameraRules.MaxZonesPerCamera} active zones.");
            }
            var polygon = ZoneGeometry.Serialize(check.Points!);
            var color = r.Color?.ToUpperInvariant();
            var changed = zone.Name != r.Name.Trim() || zone.ZoneType != check.Type || zone.WorkstationId != check.WorkstationId || zone.LineId != check.LineId
                          || !SamePolygon(zone.Polygon, check.Points!) || zone.Color != color || zone.Notes != Clean(r.Notes) || zone.IsActive != r.IsActive;
            if (changed)
            {
                var action = zone.IsActive == r.IsActive ? "Updated" : r.IsActive ? "Reactivated" : "Deactivated";
                var now = DateTimeOffset.UtcNow;
                zone.Name = r.Name.Trim();
                zone.ZoneType = check.Type!;
                zone.WorkstationId = check.WorkstationId;
                zone.LineId = check.LineId;
                zone.Polygon = polygon;
                zone.Color = color;
                zone.Notes = Clean(r.Notes);
                zone.IsActive = r.IsActive;
                zone.Version++;
                zone.UpdatedAt = now;
                zone.UpdatedByEmail = caller.Email;
                _db.CameraZoneRevisions.Add(Revision(zone, action, camera.ReferenceImageKey, caller, now));
                _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "camera_zone." + action.ToLowerInvariant(), AccessAudit.EntityCameraZone, zone.Id.ToString(),
                    $"v{zone.Version - 1}", $"v{zone.Version}", $"camera {camera.Code}: zone '{zone.Name}' {action.ToLowerInvariant()}", traceId, camera.FactoryId));
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return AccessResult<CameraZoneDto>.Ok(default!);
        });
        _db.ChangeTracker.Clear();
        if (outcome.Outcome != AccessOutcome.Ok) return outcome;
        var row = await _db.CameraZones.AsNoTracking().FirstAsync(z => z.Id == zoneId, ct);
        return AccessResult<CameraZoneDto>.Ok((await ZoneDtosAsync(new[] { row }, ct))[0]);
    }

    public async Task<AccessResult<IReadOnlyList<CameraZoneRevisionDto>>> ZoneHistoryAsync(AccessContext caller, Guid zoneId, CancellationToken ct)
    {
        var zone = await _db.CameraZones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == zoneId, ct);
        var camera = zone is null ? null : await FindAsync(caller, zone.CameraId, ct);
        if (zone is null || camera is null) return AccessResult<IReadOnlyList<CameraZoneRevisionDto>>.NotFound("zone_not_found", "Zone not found.");
        var rows = await _db.CameraZoneRevisions.AsNoTracking().Where(v => v.ZoneId == zoneId).OrderByDescending(v => v.Version).ToListAsync(ct);
        return AccessResult<IReadOnlyList<CameraZoneRevisionDto>>.Ok(rows.Select(v => new CameraZoneRevisionDto(
            v.Id, v.ZoneId, v.Version, v.Action, v.Name, v.ZoneType, v.WorkstationId, v.LineId, ZoneGeometry.Deserialize(v.Polygon), v.IsActive,
            v.ReferenceImageKey is not null && v.ReferenceImageKey == camera.ReferenceImageKey, v.ChangedAt, v.ChangedByEmail)).ToList());
    }

    // ─── Checks ────────────────────────────────────────────────────────────

    private sealed record ZoneCheck(string? Type, IReadOnlyList<ZonePoint>? Points, Guid? WorkstationId, string? LineId, AccessResult<object>? Error)
    {
        public static ZoneCheck Fail(string code, string message) => new(null, null, null, null, AccessResult<object>.Invalid(code, message));
    }

    private async Task<ZoneCheck> CheckZoneAsync(Camera camera, string name, string zoneType, IReadOnlyList<ZonePoint>? points, Guid? workstationId, string? lineId,
        bool active, Guid? ignoreZoneId, CancellationToken ct)
    {
        var type = ZoneTypes.Parse(zoneType);
        if (type is null) return ZoneCheck.Fail("zone_type_invalid", $"Zone type: one of {string.Join(", ", ZoneTypes.All)}.");
        var clean = ZoneGeometry.Normalise(points, out var geoError, out var geoMessage);
        if (clean is null) return ZoneCheck.Fail(geoError!, geoMessage!);

        var line = Clean(lineId);
        if (workstationId is { } wsId)
        {
            var ws = await _db.Workstations.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wsId, ct);
            if (ws is null || ws.FactoryId != camera.FactoryId)
                return ZoneCheck.Fail("workstation_not_in_factory", "That workstation doesn't belong to this camera's factory.");
            if (line is not null && line != ws.LineId)
                return ZoneCheck.Fail("workstation_not_on_line", $"Workstation {ws.Code} is not on line {line}.");
            line = ws.LineId;
            if (active)
            {
                if (!ws.IsActive) return ZoneCheck.Fail("workstation_inactive", $"Workstation {ws.Code} is deactivated.");
                var covered = camera.LineId == ws.LineId || await _db.CameraCoverage.AnyAsync(v => v.CameraId == camera.Id && v.RemovedAt == null
                    && (v.WorkstationId == ws.Id || (v.Kind == CoverageKinds.Line && v.LineId == ws.LineId)), ct);
                if (!covered)
                    return ZoneCheck.Fail("workstation_not_covered", $"Add {ws.Code} (or its line) to the camera's coverage before drawing a zone for it.");
            }
        }
        else if (type == ZoneTypes.Workstation)
            return ZoneCheck.Fail("workstation_required", "A workstation zone needs the workstation it marks.");

        if (workstationId is null && line is not null)
        {
            var l = await _db.ProductionLines.AsNoTracking().Where(x => x.Id == line).Select(x => new { x.FactoryId }).FirstOrDefaultAsync(ct);
            if (l is null || l.FactoryId != camera.FactoryId)
                return ZoneCheck.Fail("line_not_in_factory", "That production line doesn't belong to this camera's factory.");
            if (active && camera.LineId != line && !await _db.CameraCoverage.AnyAsync(v => v.CameraId == camera.Id && v.RemovedAt == null && v.LineId == line, ct))
                return ZoneCheck.Fail("line_not_covered", $"Add line {line} to the camera's coverage before drawing a zone for it.");
        }
        else if (workstationId is null && type == ZoneTypes.ProductionLine)
            return ZoneCheck.Fail("line_required", "A production-line zone needs the line it marks.");

        if (active)
        {
            var trimmed = name.Trim();
            var taken = await _db.CameraZones.AnyAsync(z => z.CameraId == camera.Id && z.IsActive && z.Id != ignoreZoneId
                && z.Name.ToLower() == trimmed.ToLower(), ct);
            if (taken) return new ZoneCheck(null, null, null, null, AccessResult<object>.Conflict("zone_name_taken", $"This camera already has an active zone '{trimmed}'."));
        }
        return new ZoneCheck(type, clean, workstationId, line, null);
    }

    private static bool SamePolygon(string stored, IReadOnlyList<ZonePoint> points) => ZoneGeometry.Deserialize(stored).SequenceEqual(points);

    private static CameraZoneRevision Revision(CameraZone z, string action, string? frameKey, AccessContext caller, DateTimeOffset at) => new()
    {
        ZoneId = z.Id, CameraId = z.CameraId, FactoryId = z.FactoryId, Version = z.Version, Action = action, Name = z.Name, ZoneType = z.ZoneType,
        WorkstationId = z.WorkstationId, LineId = z.LineId, Polygon = z.Polygon, IsActive = z.IsActive, ReferenceImageKey = frameKey,
        ChangedAt = at, ChangedByUserId = caller.UserId, ChangedByEmail = caller.Email,
    };

    private async Task<IReadOnlyList<CameraZoneDto>> ZoneDtosAsync(IReadOnlyList<CameraZone> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<CameraZoneDto>();
        var wsIds = rows.Where(z => z.WorkstationId != null).Select(z => z.WorkstationId!.Value).Distinct().ToList();
        var workstations = wsIds.Count == 0 ? new Dictionary<Guid, CameraWorkstationRef>()
            : await _db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id))
                .ToDictionaryAsync(w => w.Id, w => new CameraWorkstationRef(w.Id, w.Code, w.LineId, w.IsActive), ct);
        var lineIds = rows.Where(z => z.LineId != null).Select(z => z.LineId!).Distinct().ToList();
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new WorkerLineRef(l.Id, l.Name), ct);
        return rows.Select(z =>
        {
            var pts = ZoneGeometry.Deserialize(z.Polygon);
            return new CameraZoneDto(z.Id, z.CameraId, z.Name, z.ZoneType,
                z.WorkstationId is { } w ? workstations.GetValueOrDefault(w) : null,
                z.LineId is { } l ? lines.GetValueOrDefault(l) ?? new WorkerLineRef(l, l) : null,
                pts, Math.Round(Math.Abs(ZoneGeometry.SignedArea(pts)), 6), z.Color, z.Notes, z.IsActive, z.Version,
                z.CreatedAt, z.CreatedByEmail, z.UpdatedAt, z.UpdatedByEmail);
        }).ToList();
    }
}
