using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Cameras;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Cameras;
using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Devices;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Settings;
using BunonBrain.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Cameras;

/// <summary>
/// Cameras, coverage, sources, reference frames and zones (see
/// <see cref="ICameraService"/>). Every relationship is checked against the
/// database: the floor, department, line and workstations must belong to
/// the camera's factory, and a covered workstation must be on the camera's
/// floor. Every change is audited in the same transaction.
/// </summary>
public sealed partial class CameraService : ICameraService
{
    public const int MaxPageSize = 200;
    public const string NotConnectedMessage =
        "Computer-vision processing is not part of this phase. No detection, tracking or recognition runs on this camera, and no AI results exist for it.";

    private readonly BunonBrainDbContext _db;
    private readonly ICameraFileStore _files;
    private readonly AiSecretProtector _secrets;
    private readonly ILogger<CameraService> _log;

    public CameraService(BunonBrainDbContext db, ICameraFileStore files, AiSecretProtector secrets, ILogger<CameraService> log)
    {
        _db = db; _files = files; _secrets = secrets; _log = log;
    }

    // ─── Reach ─────────────────────────────────────────────────────────────

    /// <summary>Cameras the caller may see. Supervisors: cameras whose main line or active coverage is one of their lines.</summary>
    internal static IQueryable<Camera> InScope(BunonBrainDbContext db, AccessContext caller)
    {
        var all = db.Cameras.AsNoTracking();
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => all,
            RoleScope.Factory => all.Where(c => factories.Contains(c.FactoryId)),
            RoleScope.Line => all.Where(c => factories.Contains(c.FactoryId)
                && ((c.LineId != null && lines.Contains(c.LineId))
                    || db.CameraCoverage.Any(v => v.CameraId == c.Id && v.RemovedAt == null && lines.Contains(v.LineId)))),
            _ => all.Where(_ => false),
        };
    }

    private static bool CanConfigure(AccessContext caller, Guid factoryId) =>
        caller.Scope >= RoleScope.Factory && caller.CanAccessFactory(factoryId);

    private static AccessResult<T> NotFound<T>() => AccessResult<T>.NotFound("camera_not_found", "Camera not found.");

    private static AccessResult<T> NoConfigure<T>() =>
        AccessResult<T>.Forbidden("factory_scope_required", "Only factory-level administrators configure cameras.");

    private async Task<Camera?> FindAsync(AccessContext caller, Guid id, CancellationToken ct) =>
        await InScope(_db, caller).FirstOrDefaultAsync(c => c.Id == id, ct);

    // ─── Queries ───────────────────────────────────────────────────────────

    public async Task<PagedResult<CameraDto>> ListAsync(AccessContext caller, CameraListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var cams = Filter(InScope(_db, caller), q);
        var total = await cams.CountAsync(ct);
        var desc = q.Sort?.StartsWith('-') == true;
        var key = q.Sort?.TrimStart('-').ToLowerInvariant();
        cams = key switch
        {
            "name" => desc ? cams.OrderByDescending(c => c.Name) : cams.OrderBy(c => c.Name),
            "status" => desc ? cams.OrderByDescending(c => c.Status).ThenBy(c => c.Code) : cams.OrderBy(c => c.Status).ThenBy(c => c.Code),
            "type" => desc ? cams.OrderByDescending(c => c.CameraType).ThenBy(c => c.Code) : cams.OrderBy(c => c.CameraType).ThenBy(c => c.Code),
            "updated" => desc ? cams.OrderByDescending(c => c.UpdatedAt) : cams.OrderBy(c => c.UpdatedAt),
            _ => desc ? cams.OrderByDescending(c => c.Code) : cams.OrderBy(c => c.Code),
        };
        var rows = await cams.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<CameraDto>(await ToDtosAsync(caller, rows, false, ct), page, size, total);
    }

    private IQueryable<Camera> Filter(IQueryable<Camera> cams, CameraListQuery q)
    {
        if (q.FactoryId is { } f) cams = cams.Where(c => c.FactoryId == f);
        if (q.FloorId is { } fl) cams = cams.Where(c => c.FloorId == fl);
        if (!string.IsNullOrWhiteSpace(q.LineId))
        {
            var line = q.LineId.Trim();
            cams = cams.Where(c => c.LineId == line || _db.CameraCoverage.Any(v => v.CameraId == c.Id && v.RemovedAt == null && v.LineId == line));
        }
        if (q.WorkstationId is { } ws)
            cams = cams.Where(c => _db.CameraCoverage.Any(v => v.CameraId == c.Id && v.RemovedAt == null && v.WorkstationId == ws));
        if (CameraStatuses.Parse(q.Status) is { } status) cams = cams.Where(c => c.Status == status);
        if (CameraTypes.Parse(q.CameraType) is { } type) cams = cams.Where(c => c.CameraType == type);
        if (CameraSourceTypes.Parse(q.SourceType) is { } source)
        {
            var current = _db.CameraSources.Where(s => s.RetiredAt == null);
            cams = source == CameraSourceTypes.None
                ? cams.Where(c => !current.Any(s => s.CameraId == c.Id && s.SourceType != CameraSourceTypes.None))
                : cams.Where(c => current.Any(s => s.CameraId == c.Id && s.SourceType == source));
        }
        if (!string.IsNullOrWhiteSpace(q.Health))
        {
            if (string.Equals(q.Health.Trim(), "None", StringComparison.OrdinalIgnoreCase))
                cams = cams.Where(c => !_db.Devices.Any(d => d.CameraId == c.Id));
            else if (HealthStatuses.Parse(q.Health) is { } h)
            {
                var ids = DeviceQueries.WithHealth(_db.Devices, h, DateTimeOffset.UtcNow).Where(d => d.CameraId != null).Select(d => d.CameraId!.Value);
                cams = cams.Where(c => ids.Contains(c.Id));
            }
        }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            cams = cams.Where(c => EF.Functions.ILike(c.Code, pattern, "\\") || EF.Functions.ILike(c.Name, pattern, "\\")
                                   || (c.Location != null && EF.Functions.ILike(c.Location, pattern, "\\")));
        }
        return cams;
    }

    public async Task<AccessResult<CameraDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var c = await FindAsync(caller, id, ct);
        return c is null ? NotFound<CameraDto>() : AccessResult<CameraDto>.Ok((await ToDtosAsync(caller, new[] { c }, true, ct))[0]);
    }

    public async Task<CameraOverviewDto> OverviewAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        var cams = InScope(_db, caller);
        if (factoryId is { } f) cams = cams.Where(c => c.FactoryId == f);
        var rows = await cams.Select(c => new { c.Id, c.Status, c.CameraType, HasFrame = c.ReferenceImageKey != null }).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var sources = await _db.CameraSources.AsNoTracking().Where(s => s.RetiredAt == null && ids.Contains(s.CameraId))
            .Select(s => new { s.CameraId, s.SourceType }).ToDictionaryAsync(s => s.CameraId, s => s.SourceType, ct);
        var zones = await _db.CameraZones.AsNoTracking().Where(z => z.IsActive && ids.Contains(z.CameraId))
            .GroupBy(z => z.CameraId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var devices = await _db.Devices.AsNoTracking().Where(d => d.CameraId != null && ids.Contains(d.CameraId.Value)).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var health = devices.Select(d => DeviceRules.Health(d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds, now).Health)
            .GroupBy(h => h).ToDictionary(g => g.Key, g => g.Count());
        foreach (var h in HealthStatuses.All) health.TryAdd(h, 0);

        static Dictionary<string, int> Count(IEnumerable<string> values, IEnumerable<string> keys)
        {
            var d = keys.ToDictionary(k => k, _ => 0);
            foreach (var v in values) d[v] = d.GetValueOrDefault(v) + 1;
            return d;
        }
        var bySource = Count(rows.Select(r => sources.GetValueOrDefault(r.Id, CameraSourceTypes.None)), CameraSourceTypes.All);
        return new CameraOverviewDto(
            rows.Count,
            Count(rows.Select(r => r.Status), CameraStatuses.All),
            Count(rows.Select(r => r.CameraType), CameraTypes.All),
            bySource,
            bySource[CameraSourceTypes.None],
            rows.Count(r => !r.HasFrame),
            zones.Count,
            zones.Sum(z => z.Count),
            rows.Count - devices.Count,
            health,
            devices.Count(d => d.IsSimulated));
    }

    // ─── Commands ──────────────────────────────────────────────────────────

    public async Task<AccessResult<CameraDto>> CreateAsync(AccessContext caller, CreateCameraRequest r, string? traceId, CancellationToken ct)
    {
        if (!CanConfigure(caller, r.FactoryId))
            return AccessResult<CameraDto>.Forbidden("factory_out_of_scope", "You can only register cameras in your own factory.");
        var type = CameraTypes.Parse(r.CameraType)!;
        var lineId = Clean(r.LineId);
        if (await CheckPlacementAsync(r.FactoryId, r.FloorId, r.DepartmentId, lineId, ct) is { } placementError)
            return placementError.As<CameraDto>();
        var coverage = await ResolveCoverageAsync(r.FactoryId, r.FloorId, r.CoverageLineIds, r.WorkstationIds, ct);
        if (coverage.Error is { } coverageError) return coverageError.As<CameraDto>();

        string code;
        if (!string.IsNullOrWhiteSpace(r.Code))
        {
            code = CameraRules.NormaliseCode(r.Code);
            if (await _db.Cameras.AnyAsync(c => c.FactoryId == r.FactoryId && c.Code == code, ct))
                return AccessResult<CameraDto>.Conflict("camera_code_taken", $"This factory already has a camera {code}.");
        }
        else
        {
            code = CameraRules.NextCode(await _db.Cameras.Where(c => c.FactoryId == r.FactoryId && c.Code.StartsWith("CAM-")).Select(c => c.Code).ToListAsync(ct));
        }

        var healthMode = r.HealthDevice ?? "None";
        if (healthMode != "None" && await _db.Devices.AnyAsync(d => d.FactoryId == r.FactoryId && d.Code == code, ct))
            return AccessResult<CameraDto>.Conflict("device_code_taken",
                $"A device {code} already exists in this factory, so the camera's health device can't take that ID. Register the camera without one and link a device later.");

        var now = DateTimeOffset.UtcNow;
        var camera = new Camera
        {
            FactoryId = r.FactoryId, Code = code, Name = r.Name.Trim(), CameraType = type, FloorId = r.FloorId, DepartmentId = r.DepartmentId,
            LineId = lineId, Location = Clean(r.Location), Description = Clean(r.Description), InstalledOn = r.InstalledOn,
            Status = CameraStatuses.Active, StatusChangedAt = now, CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email,
            CreatedAt = now, UpdatedAt = now,
        };
        _db.Cameras.Add(camera);
        foreach (var item in coverage.Items!)
            _db.CameraCoverage.Add(new CameraCoverage
            {
                CameraId = camera.Id, FactoryId = camera.FactoryId, Kind = item.Kind, LineId = item.LineId, WorkstationId = item.WorkstationId,
                CreatedAt = now, CreatedByEmail = caller.Email,
            });
        if (healthMode != "None")
            _db.Devices.Add(new Device
            {
                FactoryId = camera.FactoryId, Code = code, Name = $"{camera.Name} (camera health)", Category = DeviceCategories.Camera,
                IsSimulated = healthMode == "Simulated", CameraId = camera.Id, LineId = lineId, InstalledOn = r.InstalledOn,
                CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email, CreatedAt = now, UpdatedAt = now,
            });
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "camera.created", AccessAudit.EntityCamera, camera.Id.ToString(), null, camera.Status,
            $"camera {code} ({type}) registered; {coverage.Items!.Count} coverage item(s); health device: {healthMode}", traceId, camera.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<CameraDto>.Conflict("camera_code_taken", $"The camera ID {code} was just taken. Try again.");
        }
        _db.ChangeTracker.Clear();
        return AccessResult<CameraDto>.Created((await ToDtosAsync(caller, new[] { camera }, true, ct))[0]);
    }

    public async Task<AccessResult<CameraDto>> UpdateAsync(AccessContext caller, Guid id, UpdateCameraRequest r, string? traceId, CancellationToken ct)
    {
        var current = await FindAsync(caller, id, ct);
        if (current is null) return NotFound<CameraDto>();
        if (!CanConfigure(caller, current.FactoryId)) return NoConfigure<CameraDto>();
        var lineId = Clean(r.LineId);
        if (await CheckPlacementAsync(current.FactoryId, r.FloorId, r.DepartmentId, lineId, ct,
                floorUnchanged: r.FloorId == current.FloorId, departmentUnchanged: r.DepartmentId == current.DepartmentId, lineUnchanged: lineId == current.LineId)
            is { } placementError)
            return placementError.As<CameraDto>();
        if (r.FloorId != current.FloorId)
        {
            var offFloor = await (from v in _db.CameraCoverage
                                  join w in _db.Workstations on v.WorkstationId equals w.Id
                                  where v.CameraId == id && v.RemovedAt == null && w.FloorId != null && w.FloorId != r.FloorId
                                  select w.Code).ToListAsync(ct);
            if (offFloor.Count > 0)
                return AccessResult<CameraDto>.Conflict("coverage_on_other_floor",
                    $"The camera covers {string.Join(", ", offFloor.Take(5))} on its current floor. Change its coverage before moving it to another floor.");
        }

        var cam = await _db.Cameras.FirstAsync(c => c.Id == id, ct);
        var type = CameraTypes.Parse(r.CameraType)!;
        var changes = new List<string>();
        if (cam.Name != r.Name.Trim()) changes.Add("name");
        if (cam.CameraType != type) changes.Add($"type {cam.CameraType} → {type}");
        if (cam.FloorId != r.FloorId || cam.DepartmentId != r.DepartmentId || cam.LineId != lineId) changes.Add("placement");
        if (cam.Location != Clean(r.Location) || cam.Description != Clean(r.Description)) changes.Add("location / description");
        if (cam.InstalledOn != r.InstalledOn) changes.Add("installation date");
        if (changes.Count > 0)
        {
            cam.Name = r.Name.Trim();
            cam.CameraType = type;
            cam.FloorId = r.FloorId;
            cam.DepartmentId = r.DepartmentId;
            cam.LineId = lineId;
            cam.Location = Clean(r.Location);
            cam.Description = Clean(r.Description);
            cam.InstalledOn = r.InstalledOn;
            cam.UpdatedAt = DateTimeOffset.UtcNow;
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "camera.updated", AccessAudit.EntityCamera, cam.Id.ToString(), null, null,
                $"camera {cam.Code}: {string.Join("; ", changes)}", traceId, cam.FactoryId));
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
        return await GetAsync(caller, id, ct);
    }

    public async Task<AccessResult<CameraDto>> SetStatusAsync(AccessContext caller, Guid id, SetCameraStatusRequest r, string? traceId, CancellationToken ct)
    {
        var current = await FindAsync(caller, id, ct);
        if (current is null) return NotFound<CameraDto>();
        if (!CanConfigure(caller, current.FactoryId)) return NoConfigure<CameraDto>();
        var status = CameraStatuses.Parse(r.Status)!;
        if (status == current.Status) return await GetAsync(caller, id, ct);
        var cam = await _db.Cameras.FirstAsync(c => c.Id == id, ct);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "camera.status_changed", AccessAudit.EntityCamera, cam.Id.ToString(), cam.Status, status,
            $"camera {cam.Code}: {cam.Status} → {status}" + (string.IsNullOrWhiteSpace(r.Reason) ? "" : $" ({r.Reason.Trim()})"), traceId, cam.FactoryId));
        cam.Status = status;
        cam.StatusReason = Clean(r.Reason);
        cam.StatusChangedAt = DateTimeOffset.UtcNow;
        cam.UpdatedAt = cam.StatusChangedAt;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return await GetAsync(caller, id, ct);
    }

    public async Task<AccessResult<bool>> DeleteAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct)
    {
        var current = await FindAsync(caller, id, ct);
        if (current is null) return NotFound<bool>();
        if (!CanConfigure(caller, current.FactoryId)) return NoConfigure<bool>();
        if (await DeleteBlockedAsync(id, ct) is { } reason) return AccessResult<bool>.Conflict("camera_has_history", reason);

        var keys = new List<string>();
        var strategy = _db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            keys.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await CameraLocks.CameraAsync(_db, id, ct);
            // Re-check under the lock: nothing may have gained history meanwhile.
            if (await DeleteBlockedAsync(id, ct) is { } again)
            {
                await tx.RollbackAsync(ct);
                return AccessResult<bool>.Conflict("camera_has_history", again);
            }
            var cam = await _db.Cameras.FirstAsync(c => c.Id == id, ct);
            var sources = await _db.CameraSources.Where(s => s.CameraId == id).ToListAsync(ct);
            keys.AddRange(sources.Where(s => s.VideoKey != null).Select(s => s.VideoKey!).Distinct());
            if (cam.ReferenceImageKey is { } frame) keys.Add(frame);
            _db.CameraSources.RemoveRange(sources);
            _db.CameraCoverage.RemoveRange(await _db.CameraCoverage.Where(v => v.CameraId == id).ToListAsync(ct));
            _db.Devices.RemoveRange(await _db.Devices.Where(d => d.CameraId == id).ToListAsync(ct));
            _db.Cameras.Remove(cam);
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "camera.deleted", AccessAudit.EntityCamera, cam.Id.ToString(), cam.Status, "deleted",
                $"camera {cam.Code} ({cam.Name}) deleted: it had no zones and no device history", traceId, cam.FactoryId));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AccessResult<bool>.Ok(true);
        });
        _db.ChangeTracker.Clear();
        if (outcome.Outcome == AccessOutcome.Ok)
            foreach (var key in keys) await _files.DeleteAsync(key, CancellationToken.None);
        return outcome;
    }

    /// <summary>Why the camera can't be deleted, or null when it can.</summary>
    private async Task<string?> DeleteBlockedAsync(Guid id, CancellationToken ct)
    {
        if (await _db.CameraZones.AnyAsync(z => z.CameraId == id, ct))
            return "This camera has zones (including deactivated ones), which are kept as configuration history. Deactivate the camera instead.";
        var device = await _db.Devices.AsNoTracking().Where(d => d.CameraId == id).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        if (device is { } d && (await _db.DeviceHeartbeats.AnyAsync(h => h.DeviceId == d, ct) || await _db.DeviceEvents.AnyAsync(e => e.DeviceId == d, ct)))
            return "This camera's health device has heartbeat or event history, which is kept. Deactivate the camera instead.";
        return null;
    }

    public async Task<AccessResult<CameraDto>> SetCoverageAsync(AccessContext caller, Guid id, SetCameraCoverageRequest r, string? traceId, CancellationToken ct)
    {
        var current = await FindAsync(caller, id, ct);
        if (current is null) return NotFound<CameraDto>();
        if (!CanConfigure(caller, current.FactoryId)) return NoConfigure<CameraDto>();
        var existing = await _db.CameraCoverage.AsNoTracking().Where(v => v.CameraId == id && v.RemovedAt == null).ToListAsync(ct);
        var desired = await ResolveCoverageAsync(current.FactoryId, current.FloorId, r.LineIds, r.WorkstationIds, ct,
            existing.Where(v => v.Kind == CoverageKinds.Line).Select(v => v.LineId).ToHashSet(StringComparer.Ordinal),
            existing.Where(v => v.WorkstationId != null).Select(v => v.WorkstationId!.Value).ToHashSet());
        if (desired.Error is { } error) return error.As<CameraDto>();
        var items = desired.Items!;

        // Active zones must stay inside the coverage.
        var lineSet = items.Where(i => i.Kind == CoverageKinds.Line).Select(i => i.LineId).ToHashSet(StringComparer.Ordinal);
        var wsSet = items.Where(i => i.WorkstationId != null).Select(i => i.WorkstationId!.Value).ToHashSet();
        var zones = await _db.CameraZones.AsNoTracking().Where(z => z.CameraId == id && z.IsActive).ToListAsync(ct);
        var zoneWs = zones.Where(z => z.WorkstationId != null).Select(z => z.WorkstationId!.Value).Distinct().ToList();
        var zoneWsLines = await _db.Workstations.AsNoTracking().Where(w => zoneWs.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.LineId, ct);
        var orphaned = zones.Where(z =>
            z.WorkstationId is { } w
                ? !wsSet.Contains(w) && !lineSet.Contains(zoneWsLines.GetValueOrDefault(w, "")) && current.LineId != zoneWsLines.GetValueOrDefault(w)
                : z.LineId is { } l && !lineSet.Contains(l) && current.LineId != l && !items.Any(i => i.LineId == l)).Select(z => z.Name).ToList();
        if (orphaned.Count > 0)
            return AccessResult<CameraDto>.Conflict("coverage_used_by_zones",
                $"Active zones use this coverage: {string.Join(", ", orphaned.Take(5))}. Deactivate or change those zones first.");

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await CameraLocks.CameraAsync(_db, id, ct);
            var active = await _db.CameraCoverage.Where(v => v.CameraId == id && v.RemovedAt == null).ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;
            int removed = 0, added = 0;
            foreach (var row in active)
            {
                var keep = row.WorkstationId is { } w ? wsSet.Contains(w) : lineSet.Contains(row.LineId);
                if (keep) continue;
                row.RemovedAt = now;
                row.RemovedByEmail = caller.Email;
                removed++;
            }
            foreach (var item in items)
            {
                var exists = item.WorkstationId is { } w
                    ? active.Any(a => a.RemovedAt == null && a.WorkstationId == w)
                    : active.Any(a => a.RemovedAt == null && a.Kind == CoverageKinds.Line && a.LineId == item.LineId);
                if (exists) continue;
                _db.CameraCoverage.Add(new CameraCoverage
                {
                    CameraId = id, FactoryId = current.FactoryId, Kind = item.Kind, LineId = item.LineId, WorkstationId = item.WorkstationId,
                    CreatedAt = now, CreatedByEmail = caller.Email,
                });
                added++;
            }
            if (removed + added > 0)
            {
                // Removals first, so a re-added workstation never meets its old row in the unique index.
                await _db.SaveChangesAsync(ct);
                var cam = await _db.Cameras.FirstAsync(c => c.Id == id, ct);
                cam.UpdatedAt = now;
                _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "camera.coverage_updated", AccessAudit.EntityCamera, id.ToString(), null, null,
                    $"camera {current.Code}: coverage +{added} / -{removed} (now {lineSet.Count} line(s), {wsSet.Count} workstation(s))", traceId, current.FactoryId));
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        });
        _db.ChangeTracker.Clear();
        return await GetAsync(caller, id, ct);
    }

    // ─── Relationship checks ───────────────────────────────────────────────

    /// <summary>Floor, department and line must belong to the factory, and be active unless unchanged.</summary>
    private async Task<AccessResult<object>?> CheckPlacementAsync(Guid factoryId, Guid floorId, Guid? departmentId, string? lineId, CancellationToken ct,
        bool floorUnchanged = false, bool departmentUnchanged = false, bool lineUnchanged = false)
    {
        if (!await _db.Factories.AnyAsync(f => f.Id == factoryId && f.IsActive, ct))
            return AccessResult<object>.Invalid("factory_not_found", "Factory not found or deactivated.");
        var floor = await _db.ProductionFloors.AsNoTracking().Where(f => f.Id == floorId).Select(f => new { f.FactoryId, f.IsActive }).FirstOrDefaultAsync(ct);
        if (floor is null || floor.FactoryId != factoryId)
            return AccessResult<object>.Invalid("floor_not_in_factory", "That production floor doesn't belong to this factory.");
        if (!floor.IsActive && !floorUnchanged) return AccessResult<object>.Invalid("floor_inactive", "That production floor is deactivated.");
        if (departmentId is { } d)
        {
            var dept = await _db.Departments.AsNoTracking().Where(x => x.Id == d).Select(x => new { x.FactoryId, x.IsActive }).FirstOrDefaultAsync(ct);
            if (dept is null || dept.FactoryId != factoryId)
                return AccessResult<object>.Invalid("department_not_in_factory", "That department doesn't belong to this factory.");
            if (!dept.IsActive && !departmentUnchanged) return AccessResult<object>.Invalid("department_inactive", "That department is deactivated.");
        }
        if (lineId is not null)
        {
            var line = await _db.ProductionLines.AsNoTracking().Where(l => l.Id == lineId).Select(l => new { l.FactoryId, l.IsActive }).FirstOrDefaultAsync(ct);
            if (line is null || line.FactoryId != factoryId)
                return AccessResult<object>.Invalid("line_not_in_factory", "That production line doesn't belong to this factory.");
            if (!line.IsActive && !lineUnchanged) return AccessResult<object>.Invalid("line_inactive", "That production line is deactivated.");
        }
        return null;
    }

    internal sealed record CoverageItem(string Kind, string LineId, Guid? WorkstationId);

    private sealed record CoverageResolution(IReadOnlyList<CoverageItem>? Items, AccessResult<object>? Error);

    /// <summary>
    /// Lines and workstations a camera may cover: in its factory, active
    /// (except ones already covered, which may stay after being deactivated),
    /// and for a workstation, on the camera's floor when the workstation
    /// records one.
    /// </summary>
    private async Task<CoverageResolution> ResolveCoverageAsync(Guid factoryId, Guid floorId, IReadOnlyList<string>? lineIds, IReadOnlyList<Guid>? workstationIds,
        CancellationToken ct, IReadOnlySet<string>? coveredLines = null, IReadOnlySet<Guid>? coveredWorkstations = null)
    {
        var items = new List<CoverageItem>();
        var lines = (lineIds ?? Array.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (lines.Count > 0)
        {
            var found = await _db.ProductionLines.AsNoTracking().Where(l => lines.Contains(l.Id)).Select(l => new { l.Id, l.FactoryId, l.IsActive }).ToListAsync(ct);
            foreach (var id in lines)
            {
                var line = found.FirstOrDefault(l => l.Id == id);
                if (line is null || line.FactoryId != factoryId)
                    return new(null, AccessResult<object>.Invalid("line_not_in_factory", $"Line {id} doesn't belong to this camera's factory."));
                if (!line.IsActive && coveredLines?.Contains(id) != true)
                    return new(null, AccessResult<object>.Invalid("line_inactive", $"Line {id} is deactivated."));
                items.Add(new CoverageItem(CoverageKinds.Line, id, null));
            }
        }
        var wsIds = (workstationIds ?? Array.Empty<Guid>()).Where(w => w != Guid.Empty).Distinct().ToList();
        if (wsIds.Count > 0)
        {
            var found = await _db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToListAsync(ct);
            foreach (var id in wsIds)
            {
                var ws = found.FirstOrDefault(w => w.Id == id);
                if (ws is null || ws.FactoryId != factoryId)
                    return new(null, AccessResult<object>.Invalid("workstation_not_in_factory", "A chosen workstation doesn't belong to this camera's factory."));
                if (!ws.IsActive && coveredWorkstations?.Contains(id) != true)
                    return new(null, AccessResult<object>.Invalid("workstation_inactive", $"Workstation {ws.Code} is deactivated."));
                if (ws.FloorId is { } wf && wf != floorId)
                    return new(null, AccessResult<object>.Invalid("workstation_on_other_floor",
                        $"Workstation {ws.Code} is on another production floor than the camera; a camera only covers its own floor."));
                items.Add(new CoverageItem(CoverageKinds.Workstation, ws.LineId, ws.Id));
            }
        }
        return new(items, null);
    }

    // ─── Read model ────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<CameraDto>> ToDtosAsync(AccessContext caller, IReadOnlyList<Camera> rows, bool detail, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<CameraDto>();
        var ids = rows.Select(r => r.Id).ToList();
        var factoryIds = rows.Select(r => r.FactoryId).Distinct().ToList();
        var floorIds = rows.Select(r => r.FloorId).Distinct().ToList();
        var deptIds = rows.Where(r => r.DepartmentId != null).Select(r => r.DepartmentId!.Value).Distinct().ToList();

        var factories = await _db.Factories.AsNoTracking().Where(f => factoryIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => new { f.Code, f.Name }, ct);
        var floors = await _db.ProductionFloors.AsNoTracking().Where(f => floorIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => new OrgRef(f.Id, f.Code, f.Name), ct);
        var depts = deptIds.Count == 0 ? new Dictionary<Guid, OrgRef>()
            : await _db.Departments.AsNoTracking().Where(d => deptIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => new OrgRef(d.Id, d.Code, d.Name), ct);
        var sources = await _db.CameraSources.AsNoTracking().Where(s => s.RetiredAt == null && ids.Contains(s.CameraId)).ToDictionaryAsync(s => s.CameraId, ct);
        var coverage = await _db.CameraCoverage.AsNoTracking().Where(v => v.RemovedAt == null && ids.Contains(v.CameraId)).OrderBy(v => v.CreatedAt).ToListAsync(ct);
        var wsIds = coverage.Where(v => v.WorkstationId != null).Select(v => v.WorkstationId!.Value).Distinct().ToList();
        var workstations = wsIds.Count == 0 ? new Dictionary<Guid, CameraWorkstationRef>()
            : await _db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id))
                .ToDictionaryAsync(w => w.Id, w => new CameraWorkstationRef(w.Id, w.Code, w.LineId, w.IsActive), ct);
        var lineIds = rows.Where(r => r.LineId != null).Select(r => r.LineId!).Concat(coverage.Select(v => v.LineId)).Distinct().ToList();
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new WorkerLineRef(l.Id, l.Name), ct);
        var zoneCounts = await _db.CameraZones.AsNoTracking().Where(z => z.IsActive && ids.Contains(z.CameraId))
            .GroupBy(z => z.CameraId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var devices = await _db.Devices.AsNoTracking().Where(d => d.CameraId != null && ids.Contains(d.CameraId.Value)).ToDictionaryAsync(d => d.CameraId!.Value, ct);
        var canManage = caller.Has(Permissions.CamerasManage);
        var now = DateTimeOffset.UtcNow;

        var result = new List<CameraDto>(rows.Count);
        foreach (var c in rows)
        {
            var source = sources.GetValueOrDefault(c.Id);
            var device = devices.GetValueOrDefault(c.Id);
            CameraHealthDto? health = null;
            if (device is not null)
            {
                var h = DeviceRules.Health(device.Status, device.ConnectionStatus, device.IsSimulated, device.LastSeenAt, device.OfflineAfterSeconds, now);
                health = new CameraHealthDto(device.Id, device.Code, device.IsSimulated, device.Status, h.Health, h.Connection, h.Label,
                    h.PhysicallyConnected, device.LastSeenAt, device.LastErrorAt, device.LastErrorMessage);
            }
            var zones = zoneCounts.GetValueOrDefault(c.Id);
            var lastUpdate = new[] { c.StatusChangedAt, device?.ConnectionChangedAt ?? DateTimeOffset.MinValue, device?.LastSeenAt ?? DateTimeOffset.MinValue }.Max();
            var manage = canManage && CanConfigure(caller, c.FactoryId);
            bool? deletable = null;
            string? blocked = null;
            if (detail && manage)
            {
                blocked = await DeleteBlockedAsync(c.Id, ct);
                deletable = blocked is null;
            }
            var hasSource = source is not null && source.SourceType != CameraSourceTypes.None;
            var factory = factories.GetValueOrDefault(c.FactoryId);
            result.Add(new CameraDto(
                c.Id, c.Code, c.Name, c.CameraType, c.FactoryId, factory?.Code ?? "?", factory?.Name ?? "?",
                c.DepartmentId is { } d ? depts.GetValueOrDefault(d) : null,
                floors.GetValueOrDefault(c.FloorId) ?? new OrgRef(c.FloorId, "?", "Unknown floor"),
                c.LineId is { } l ? lines.GetValueOrDefault(l) ?? new WorkerLineRef(l, l) : null,
                c.Location, c.Description, c.Status, c.StatusReason, c.StatusChangedAt, c.InstalledOn,
                source is null ? null : SourceDto(source),
                coverage.Where(v => v.CameraId == c.Id).Select(v => new CameraCoverageDto(v.Id, v.Kind,
                    lines.GetValueOrDefault(v.LineId) ?? new WorkerLineRef(v.LineId, v.LineId),
                    v.WorkstationId is { } w ? workstations.GetValueOrDefault(w) : null, v.CreatedAt, v.CreatedByEmail)).ToList(),
                zones,
                c.ReferenceImageKey is null ? null : new ReferenceFrameDto(c.ReferenceImageSource ?? ReferenceFrameSources.Upload,
                    c.ReferenceImageWidth ?? 0, c.ReferenceImageHeight ?? 0, c.ReferenceImageContentType ?? "image/png", c.ReferenceFrameSeconds,
                    c.ReferenceImageUpdatedAt ?? c.UpdatedAt),
                health,
                lastUpdate,
                new AiIntegrationDto("NotConnected", NotConnectedMessage, hasSource, c.ReferenceImageKey is not null, zones),
                new CameraCapabilities(manage, deletable, blocked),
                c.CreatedAt, c.UpdatedAt));
        }
        return result;
    }

    private static CameraSourceDto SourceDto(CameraSource s) => new(
        s.Id, s.Version, s.SourceType, s.StreamUrl, s.StreamHost, s.CredentialsCiphertext is not null, s.WebcamLabel,
        s.VideoKey is not null, s.VideoFileName, s.VideoContentType, s.VideoSizeBytes, s.Notes, "NotChecked",
        s.CreatedAt, s.CreatedByEmail, s.RetiredAt);

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>Row locks for camera writes (sources, coverage, zones, deletion), so concurrent edits queue.</summary>
internal static class CameraLocks
{
    public static Task CameraAsync(BunonBrainDbContext db, Guid cameraId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cameras WHERE \"Id\" = {cameraId} FOR UPDATE", ct);

    public static Task ZoneAsync(BunonBrainDbContext db, Guid zoneId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM camera_zones WHERE \"Id\" = {zoneId} FOR UPDATE", ct);
}
