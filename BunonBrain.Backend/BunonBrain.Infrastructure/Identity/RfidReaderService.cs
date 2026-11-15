using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Identity;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Identity;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Devices;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Identity;

/// <summary>
/// RFID readers (devices of category RfidReader, reused from the device
/// registry) and their location mappings. A mapping says where a reader is
/// and how precisely: WORKSTATION (one workstation), ZONE (one camera zone,
/// usually of one workstation), LINE, FLOOR or AREA. Only WORKSTATION, or a
/// ZONE of a workstation, can place a worker at a workstation. A new mapping
/// closes the current one, so history shows where a reader was for every read.
/// </summary>
public sealed class RfidReaderService : IRfidReaderService
{
    private readonly BunonBrainDbContext _db;
    public RfidReaderService(BunonBrainDbContext db) { _db = db; }

    public async Task<IReadOnlyList<RfidReaderDto>> ReadersAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        if (!caller.HasAny(Permissions.DevicesView, Permissions.MonitoringView)) return Array.Empty<RfidReaderDto>();
        var q = DeviceQueries.InScope(_db, caller).Where(d => d.Category == DeviceCategories.RfidReader);
        if (factoryId is { } f) q = q.Where(d => d.FactoryId == f);
        var devices = await q.OrderBy(d => d.Code).Take(500).ToListAsync(ct);
        return await DtosAsync(caller, devices, ct);
    }

    private async Task<IReadOnlyList<RfidReaderDto>> DtosAsync(AccessContext caller, List<Device> devices, CancellationToken ct)
    {
        if (devices.Count == 0) return Array.Empty<RfidReaderDto>();
        var now = DateTimeOffset.UtcNow;
        var ids = devices.Select(d => d.Id).ToList();
        var mappings = await _db.RfidReaderMappings.AsNoTracking().Where(m => ids.Contains(m.DeviceId) && m.EffectiveTo == null).ToListAsync(ct);
        var mappingDtos = await MappingDtosAsync(mappings, ct);
        var factories = await _db.Factories.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Code, ct);
        var hourAgo = now.AddHours(-1);
        var reads = await _db.RfidReadEvents.AsNoTracking().Where(r => ids.Contains(r.DeviceId) && r.OccurredAt >= now.AddDays(-7))
            .GroupBy(r => r.DeviceId).Select(g => new { g.Key, Hour = g.Count(r => r.OccurredAt >= hourAgo), Last = g.Max(r => r.OccurredAt) })
            .ToDictionaryAsync(x => x.Key, ct);
        // The camera zones of each mapped workstation (what the engine will look at).
        var wsIds = mappings.Where(m => m.WorkstationId != null).Select(m => m.WorkstationId!.Value).ToList();
        var zoneIds = mappings.Where(m => m.CameraZoneId != null).Select(m => m.CameraZoneId!.Value).ToList();
        var zones = await _db.CameraZones.AsNoTracking().Where(z => z.IsActive && ((z.WorkstationId != null && wsIds.Contains(z.WorkstationId.Value)) || zoneIds.Contains(z.Id)))
            .ToListAsync(ct);
        var camIds = zones.Select(z => z.CameraId).Distinct().ToList();
        var cams = await _db.Cameras.AsNoTracking().Where(c => camIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        return devices.Select(d =>
        {
            var m = mappings.FirstOrDefault(x => x.DeviceId == d.Id);
            var mz = m is null ? new List<CameraZone>()
                : m.Precision == MappingPrecisions.Zone ? zones.Where(z => z.Id == m.CameraZoneId).ToList()
                : m.Precision == MappingPrecisions.Workstation ? zones.Where(z => z.WorkstationId == m.WorkstationId).ToList() : new List<CameraZone>();
            reads.TryGetValue(d.Id, out var r);
            var health = DeviceRules.Health(d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds, now).Health;
            return new RfidReaderDto(d.Id, d.Code, d.Name, d.FactoryId, factories.GetValueOrDefault(d.FactoryId, "?"), d.IsSimulated, d.Status, health,
                d.LastSeenAt, m is null ? null : mappingDtos[m.Id], mz.Select(z => new MonZone(z.Id, z.Name, z.ZoneType)).ToList(),
                mz.Select(z => cams.GetValueOrDefault(z.CameraId, "?")).Distinct().OrderBy(c => c).ToList(), r?.Hour ?? 0, r?.Last,
                caller.Has(Permissions.DevicesManage) && DeviceQueries.CanConfigure(caller, d.FactoryId));
        }).ToList();
    }

    private async Task<Dictionary<Guid, ReaderMappingDto>> MappingDtosAsync(List<RfidReaderMapping> rows, CancellationToken ct)
    {
        var deps = rows.Where(r => r.DepartmentId != null).Select(r => r.DepartmentId!.Value).Distinct().ToList();
        var floors = rows.Where(r => r.FloorId != null).Select(r => r.FloorId!.Value).Distinct().ToList();
        var lines = rows.Where(r => r.LineId != null).Select(r => r.LineId!).Distinct().ToList();
        var ws = rows.Where(r => r.WorkstationId != null).Select(r => r.WorkstationId!.Value).Distinct().ToList();
        var zs = rows.Where(r => r.CameraZoneId != null).Select(r => r.CameraZoneId!.Value).Distinct().ToList();
        var depN = await _db.Departments.AsNoTracking().Where(d => deps.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var floorN = await _db.ProductionFloors.AsNoTracking().Where(f => floors.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var lineN = await _db.ProductionLines.AsNoTracking().Where(l => lines.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var wsN = await _db.Workstations.AsNoTracking().Where(w => ws.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var zones = await _db.CameraZones.AsNoTracking().Where(z => zs.Contains(z.Id)).Select(z => new { z.Id, z.Name, z.CameraId }).ToListAsync(ct);
        var zcams = zones.Select(z => z.CameraId).Distinct().ToList();
        var camN = await _db.Cameras.AsNoTracking().Where(c => zcams.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        return rows.ToDictionary(r => r.Id, r =>
        {
            var z = zones.FirstOrDefault(x => x.Id == r.CameraZoneId);
            return new ReaderMappingDto(r.Id, r.DeviceId, r.Precision, MappingPrecisions.WorkstationLevel.Contains(r.Precision), r.DepartmentId,
                r.DepartmentId is { } d ? depN.GetValueOrDefault(d) : null, r.FloorId, r.FloorId is { } f ? floorN.GetValueOrDefault(f) : null,
                r.LineId is null ? null : new MonLine(r.LineId, lineN.GetValueOrDefault(r.LineId, r.LineId)),
                r.WorkstationId is { } w ? new MonRef(w, wsN.GetValueOrDefault(w, "?"), null) : null, r.CameraZoneId, z?.Name,
                z is null ? null : camN.GetValueOrDefault(z.CameraId), r.Location, r.EffectiveFrom, r.EffectiveTo, r.CreatedByEmail);
        });
    }

    public async Task<AccessResult<RfidReaderDto>> SetMappingAsync(AccessContext caller, Guid deviceId, ReaderMappingRequest req, string? traceId, CancellationToken ct)
    {
        var d = await DeviceQueries.InScope(_db, caller).FirstOrDefaultAsync(x => x.Id == deviceId && x.Category == DeviceCategories.RfidReader, ct);
        if (d is null) return AccessResult<RfidReaderDto>.NotFound("reader_not_found", "RFID reader not found.");
        if (!DeviceQueries.CanConfigure(caller, d.FactoryId))
            return AccessResult<RfidReaderDto>.Forbidden("factory_scope_required", "Only factory-level device administrators map readers.");
        var precision = MappingPrecisions.Parse(req.Precision);
        if (precision is null) return AccessResult<RfidReaderDto>.Invalid("precision_invalid", $"Precision: one of {string.Join(", ", MappingPrecisions.All)}.");
        var location = string.IsNullOrWhiteSpace(req.Location) ? null : req.Location.Trim();
        if (location is { Length: > 200 }) return AccessResult<RfidReaderDto>.Invalid("location_too_long", "Location is at most 200 characters.");

        var m = new RfidReaderMapping
        {
            FactoryId = d.FactoryId, DeviceId = d.Id, Precision = precision, Location = location, CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email,
        };
        switch (precision)
        {
            case MappingPrecisions.Workstation:
                var ws = req.WorkstationId is { } wid ? await _db.Workstations.AsNoTracking().FirstOrDefaultAsync(w => w.Id == wid && w.FactoryId == d.FactoryId && w.IsActive, ct) : null;
                if (ws is null) return AccessResult<RfidReaderDto>.Invalid("workstation_required", "A WORKSTATION mapping needs an active workstation of the reader's factory.");
                (m.WorkstationId, m.LineId, m.FloorId, m.DepartmentId) = (ws.Id, ws.LineId, ws.FloorId, ws.DepartmentId);
                break;
            case MappingPrecisions.Zone:
                var zone = req.CameraZoneId is { } zid ? await _db.CameraZones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == zid && z.FactoryId == d.FactoryId && z.IsActive, ct) : null;
                if (zone is null) return AccessResult<RfidReaderDto>.Invalid("zone_required", "A ZONE mapping needs an active camera zone of the reader's factory.");
                m.CameraZoneId = zone.Id;
                m.WorkstationId = zone.WorkstationId;
                m.LineId = zone.LineId;
                if (zone.WorkstationId is { } zws && await _db.Workstations.AsNoTracking().FirstOrDefaultAsync(w => w.Id == zws, ct) is { } zw)
                    (m.LineId, m.FloorId, m.DepartmentId) = (zw.LineId, zw.FloorId, zw.DepartmentId);
                break;
            case MappingPrecisions.Line:
                var lineId = req.LineId?.Trim();
                if (string.IsNullOrEmpty(lineId) || !await _db.ProductionLines.AnyAsync(l => l.Id == lineId && l.FactoryId == d.FactoryId, ct))
                    return AccessResult<RfidReaderDto>.Invalid("line_required", "A LINE mapping needs a production line of the reader's factory.");
                m.LineId = lineId;
                break;
            case MappingPrecisions.Floor:
                if (req.FloorId is not { } fid || !await _db.ProductionFloors.AnyAsync(f => f.Id == fid && f.FactoryId == d.FactoryId, ct))
                    return AccessResult<RfidReaderDto>.Invalid("floor_required", "A FLOOR mapping needs a production floor of the reader's factory.");
                m.FloorId = fid;
                break;
            default:
                if (location is null && req.DepartmentId is null)
                    return AccessResult<RfidReaderDto>.Invalid("location_required", "An AREA mapping needs a location description or a department.");
                if (req.DepartmentId is { } dep && !await _db.Departments.AnyAsync(x => x.Id == dep && x.FactoryId == d.FactoryId, ct))
                    return AccessResult<RfidReaderDto>.Invalid("department_not_in_factory", "The department is not in the reader's factory.");
                m.DepartmentId = req.DepartmentId;
                break;
        }

        var now = DateTimeOffset.UtcNow;
        m.EffectiveFrom = now;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await IdentityAssociationService.LockFactoryAsync(_db, d.FactoryId, ct);
            var current = await _db.RfidReaderMappings.Where(x => x.DeviceId == d.Id && x.EffectiveTo == null).ToListAsync(ct);
            foreach (var c in current) { c.EffectiveTo = now; c.EndedByEmail = caller.Email; }
            _db.RfidReaderMappings.Add(m);
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "rfid_reader.mapped", AccessAudit.EntityReaderMapping, m.Id.ToString(),
                current.FirstOrDefault()?.Precision, precision,
                $"Reader {d.Code} mapped at {precision} precision" + (location is null ? "" : $" ({location})"), traceId, d.FactoryId));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        _db.ChangeTracker.Clear();
        var dev = await _db.Devices.AsNoTracking().FirstAsync(x => x.Id == d.Id, ct);
        return AccessResult<RfidReaderDto>.Ok((await DtosAsync(caller, new List<Device> { dev }, ct))[0]);
    }

    public async Task<AccessResult<IReadOnlyList<ReaderMappingDto>>> MappingHistoryAsync(AccessContext caller, Guid deviceId, CancellationToken ct)
    {
        if (!caller.HasAny(Permissions.DevicesView, Permissions.MonitoringView) ||
            !await DeviceQueries.InScope(_db, caller).AnyAsync(x => x.Id == deviceId && x.Category == DeviceCategories.RfidReader, ct))
            return AccessResult<IReadOnlyList<ReaderMappingDto>>.NotFound("reader_not_found", "RFID reader not found.");
        var rows = await _db.RfidReaderMappings.AsNoTracking().Where(m => m.DeviceId == deviceId).OrderByDescending(m => m.EffectiveFrom).ToListAsync(ct);
        var dtos = await MappingDtosAsync(rows, ct);
        return AccessResult<IReadOnlyList<ReaderMappingDto>>.Ok(rows.Select(r => dtos[r.Id]).ToList());
    }
}
