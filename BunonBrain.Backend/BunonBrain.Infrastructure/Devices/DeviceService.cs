using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Devices;
using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Devices;

/// <summary>Device queries shared by the device and camera services.</summary>
public static class DeviceQueries
{
    /// <summary>
    /// Devices whose health (see <see cref="DeviceRules.Health"/>) is
    /// <paramref name="health"/> at <paramref name="now"/>. Uses the stored
    /// offline deadline, so it is a plain comparison in SQL.
    /// </summary>
    public static IQueryable<Device> WithHealth(IQueryable<Device> devices, string health, DateTimeOffset now) => health switch
    {
        HealthStatuses.Disabled => devices.Where(d => d.Status == DeviceStatuses.Disabled),
        HealthStatuses.Maintenance => devices.Where(d => d.Status == DeviceStatuses.Maintenance),
        HealthStatuses.Online => devices.Where(d => d.Status == DeviceStatuses.Active && d.ConnectionStatus == ConnectionStatuses.Online
                                                    && d.OfflineDeadline != null && d.OfflineDeadline >= now),
        HealthStatuses.Offline => devices.Where(d => d.Status == DeviceStatuses.Active && (d.ConnectionStatus == ConnectionStatuses.Offline
                                                     || (d.ConnectionStatus == ConnectionStatuses.Online && (d.OfflineDeadline == null || d.OfflineDeadline < now)))),
        HealthStatuses.Unknown => devices.Where(d => d.Status == DeviceStatuses.Active && d.ConnectionStatus == ConnectionStatuses.Unknown),
        _ => devices.Where(_ => false),
    };

    /// <summary>Devices the caller may see. Supervisors: devices placed on their lines.</summary>
    public static IQueryable<Device> InScope(BunonBrainDbContext db, AccessContext caller)
    {
        var all = db.Devices.AsNoTracking();
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => all,
            RoleScope.Factory => all.Where(d => factories.Contains(d.FactoryId)),
            RoleScope.Line => all.Where(d => factories.Contains(d.FactoryId) && d.LineId != null && lines.Contains(d.LineId)),
            _ => all.Where(_ => false),
        };
    }

    public static bool CanConfigure(AccessContext caller, Guid factoryId) =>
        caller.Scope >= RoleScope.Factory && caller.CanAccessFactory(factoryId);

    public static DateTimeOffset? Deadline(DateTimeOffset? lastSeen, int offlineAfterSeconds) =>
        lastSeen?.AddSeconds(offlineAfterSeconds);

    public static async Task<IReadOnlyList<DeviceDto>> ToDtosAsync(BunonBrainDbContext db, IReadOnlyList<Device> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<DeviceDto>();
        var factoryIds = rows.Select(r => r.FactoryId).Distinct().ToList();
        var lineIds = rows.Where(r => r.LineId != null).Select(r => r.LineId!).Distinct().ToList();
        var wsIds = rows.Where(r => r.WorkstationId != null).Select(r => r.WorkstationId!.Value).Distinct().ToList();
        var machineIds = rows.Where(r => r.MachineId != null).Select(r => r.MachineId!.Value).Distinct().ToList();
        var cameraIds = rows.Where(r => r.CameraId != null).Select(r => r.CameraId!.Value).Distinct().ToList();
        var readerIds = rows.Where(r => r.AttendanceReaderId != null).Select(r => r.AttendanceReaderId!.Value).Distinct().ToList();

        var factories = await db.Factories.AsNoTracking().Where(f => factoryIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Code, ct);
        var lines = await db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new WorkerLineRef(l.Id, l.Name), ct);
        var ws = await db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => new DeviceLinkRef(w.Id, w.Code, w.Name), ct);
        var machines = await db.Machines.AsNoTracking().Where(m => machineIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, m => new DeviceLinkRef(m.Id, m.Code, m.MachineType), ct);
        var cameras = await db.Cameras.AsNoTracking().Where(c => cameraIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => new DeviceLinkRef(c.Id, c.Code, c.Name), ct);
        var readers = await db.AttendanceReaders.AsNoTracking().Where(r => readerIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => new DeviceLinkRef(r.Id, r.Code, r.Name), ct);
        var now = DateTimeOffset.UtcNow;

        return rows.Select(d =>
        {
            var h = DeviceRules.Health(d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds, now);
            return new DeviceDto(
                d.Id, d.Code, d.Name, d.Category, d.IsSimulated, d.IsSimulated ? "Simulated" : "Real", d.Status, d.StatusReason,
                h.Connection, h.Health, h.Label, h.PhysicallyConnected, h.Stale, d.FactoryId, factories.GetValueOrDefault(d.FactoryId, "?"),
                d.LineId is { } l ? lines.GetValueOrDefault(l) ?? new WorkerLineRef(l, l) : null,
                d.WorkstationId is { } w ? ws.GetValueOrDefault(w) : null,
                d.MachineId is { } m ? machines.GetValueOrDefault(m) : null,
                d.CameraId is { } c ? cameras.GetValueOrDefault(c) : null,
                d.AttendanceReaderId is { } r ? readers.GetValueOrDefault(r) : null,
                d.Manufacturer, d.Model, d.SerialNumber, d.FirmwareVersion, DeviceRules.ReadMetadata(d.Metadata), d.InstalledOn,
                d.HeartbeatIntervalSeconds, d.OfflineAfterSeconds, d.LastSeenAt, d.LastHeartbeatAt, d.LastConnectedAt, d.LastDisconnectedAt,
                d.ConnectionChangedAt, d.LastErrorAt, d.LastErrorCode, d.LastErrorMessage, d.CreatedAt, d.UpdatedAt);
        }).ToList();
    }

    public static DeviceEventDto EventDto(DeviceEvent e, string deviceCode) => new(
        e.Id, e.DeviceId, deviceCode, e.EventType, e.Severity, e.Source, e.IsSimulated, e.OccurredAt, e.ReceivedAt,
        e.Code, e.Message, e.FromState, e.ToState, e.RecordedByEmail);
}

/// <summary>
/// Device registration, configuration and health views (see
/// <see cref="IDeviceService"/>). Placement links are checked against the
/// database: line, workstation, machine, camera and attendance reader must
/// belong to the device's factory and agree with each other.
/// </summary>
public sealed class DeviceService : IDeviceService
{
    public const int MaxPageSize = 200;
    public const int DefaultHeartbeatSeconds = 60;
    public const int DefaultOfflineAfterSeconds = 180;
    private readonly BunonBrainDbContext _db;

    private readonly IMonitoringSystemHooks _monitoring;

    public DeviceService(BunonBrainDbContext db, IMonitoringSystemHooks monitoring) { _db = db; _monitoring = monitoring; }

    private static AccessResult<T> NotFound<T>() => AccessResult<T>.NotFound("device_not_found", "Device not found.");

    private static AccessResult<T> NoConfigure<T>() =>
        AccessResult<T>.Forbidden("factory_scope_required", "Only factory-level administrators configure devices.");

    // ─── Queries ───────────────────────────────────────────────────────────

    public async Task<PagedResult<DeviceDto>> ListAsync(AccessContext caller, DeviceListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var now = DateTimeOffset.UtcNow;
        var devices = DeviceQueries.InScope(_db, caller);
        if (q.FactoryId is { } f) devices = devices.Where(d => d.FactoryId == f);
        if (DeviceCategories.Parse(q.Category) is { } cat) devices = devices.Where(d => d.Category == cat);
        if (DeviceStatuses.Parse(q.Status) is { } st) devices = devices.Where(d => d.Status == st);
        if (HealthStatuses.Parse(q.Health) is { } h) devices = DeviceQueries.WithHealth(devices, h, now);
        if (q.Simulated is { } sim) devices = devices.Where(d => d.IsSimulated == sim);
        if (!string.IsNullOrWhiteSpace(q.LineId)) devices = devices.Where(d => d.LineId == q.LineId.Trim());
        if (q.CameraId is { } cam) devices = devices.Where(d => d.CameraId == cam);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            devices = devices.Where(d => EF.Functions.ILike(d.Code, pattern, "\\") || EF.Functions.ILike(d.Name, pattern, "\\")
                                         || (d.SerialNumber != null && EF.Functions.ILike(d.SerialNumber, pattern, "\\")));
        }
        var total = await devices.CountAsync(ct);
        var desc = q.Sort?.StartsWith('-') == true;
        devices = q.Sort?.TrimStart('-').ToLowerInvariant() switch
        {
            "name" => desc ? devices.OrderByDescending(d => d.Name) : devices.OrderBy(d => d.Name),
            "category" => desc ? devices.OrderByDescending(d => d.Category).ThenBy(d => d.Code) : devices.OrderBy(d => d.Category).ThenBy(d => d.Code),
            "lastseen" => desc ? devices.OrderByDescending(d => d.LastSeenAt) : devices.OrderBy(d => d.LastSeenAt),
            "health" => desc ? devices.OrderByDescending(d => d.Status).ThenByDescending(d => d.ConnectionStatus).ThenBy(d => d.Code)
                             : devices.OrderBy(d => d.Status).ThenBy(d => d.ConnectionStatus).ThenBy(d => d.Code),
            _ => desc ? devices.OrderByDescending(d => d.Code) : devices.OrderBy(d => d.Code),
        };
        var rows = await devices.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<DeviceDto>(await DeviceQueries.ToDtosAsync(_db, rows, ct), page, size, total);
    }

    public async Task<AccessResult<DeviceDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var d = await DeviceQueries.InScope(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        return d is null ? NotFound<DeviceDto>() : AccessResult<DeviceDto>.Ok((await DeviceQueries.ToDtosAsync(_db, new[] { d }, ct))[0]);
    }

    public async Task<AccessResult<PagedResult<DeviceHeartbeatDto>>> HeartbeatsAsync(AccessContext caller, Guid id, int page, int pageSize, CancellationToken ct)
    {
        if (!await DeviceQueries.InScope(_db, caller).AnyAsync(d => d.Id == id, ct)) return NotFound<PagedResult<DeviceHeartbeatDto>>();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var q = _db.DeviceHeartbeats.AsNoTracking().Where(h => h.DeviceId == id);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(h => h.OccurredAt).ThenByDescending(h => h.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return AccessResult<PagedResult<DeviceHeartbeatDto>>.Ok(new PagedResult<DeviceHeartbeatDto>(rows.Select(h => new DeviceHeartbeatDto(
            h.Id, h.DeviceId, h.OccurredAt, h.ReceivedAt, h.Source, h.IsSimulated, h.MessageId, h.FirmwareVersion,
            DeviceRules.ReadMetrics(h.Metrics), h.RecordedByEmail)).ToList(), page, pageSize, total));
    }

    public async Task<PagedResult<DeviceEventDto>> EventsAsync(AccessContext caller, DeviceEventQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MaxPageSize);
        var devices = DeviceQueries.InScope(_db, caller);
        if (q.FactoryId is { } f) devices = devices.Where(d => d.FactoryId == f);
        if (q.DeviceId is { } id) devices = devices.Where(d => d.Id == id);
        var events = from e in _db.DeviceEvents.AsNoTracking()
                     join d in devices on e.DeviceId equals d.Id
                     select new { e, d.Code };
        if (DeviceEventTypes.Parse(q.EventType) is { } type) events = events.Where(x => x.e.EventType == type);
        if (DeviceMessageSources.Parse(q.Source) is { } source) events = events.Where(x => x.e.Source == source);
        if (q.Simulated is { } sim) events = events.Where(x => x.e.IsSimulated == sim);
        if (q.From is { } from) events = events.Where(x => x.e.OccurredAt >= from);
        if (q.To is { } to) events = events.Where(x => x.e.OccurredAt <= to);
        var total = await events.CountAsync(ct);
        var rows = await events.OrderByDescending(x => x.e.OccurredAt).ThenByDescending(x => x.e.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<DeviceEventDto>(rows.Select(x => DeviceQueries.EventDto(x.e, x.Code)).ToList(), page, size, total);
    }

    public async Task<DeviceHealthOverviewDto> HealthAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var devices = DeviceQueries.InScope(_db, caller);
        if (factoryId is { } f) devices = devices.Where(d => d.FactoryId == f);
        var rows = await devices.OrderBy(d => d.Code).Take(5000).ToListAsync(ct);
        var health = rows.Select(d => (Device: d, H: DeviceRules.Health(d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds, now))).ToList();
        var byHealth = HealthStatuses.All.ToDictionary(h => h, h => health.Count(x => x.H.Health == h));
        var byCategory = DeviceCategories.All.Select(c =>
        {
            var inCat = health.Where(x => x.Device.Category == c).ToList();
            return new CategoryHealthDto(c, inCat.Count, inCat.Count(x => x.H.Health == HealthStatuses.Online), inCat.Count(x => x.H.Health == HealthStatuses.Offline),
                inCat.Count(x => x.H.Health == HealthStatuses.Unknown), inCat.Count(x => x.H.Health == HealthStatuses.Maintenance),
                inCat.Count(x => x.H.Health == HealthStatuses.Disabled));
        }).Where(c => c.Total > 0).ToList();
        var since = now.AddHours(-24);
        var ids = rows.Select(d => d.Id).ToList();
        var errors = await _db.DeviceEvents.AsNoTracking().CountAsync(e => ids.Contains(e.DeviceId) && e.EventType == DeviceEventTypes.Error && e.OccurredAt >= since, ct);
        var attention = health
            .Where(x => x.H.Health == HealthStatuses.Offline || x.H.Stale || (x.Device.LastErrorAt is { } at && at >= since && x.Device.Status != DeviceStatuses.Disabled))
            .OrderByDescending(x => x.H.Stale).ThenBy(x => x.Device.LastSeenAt).Take(20).Select(x => x.Device).ToList();
        var codes = rows.ToDictionary(d => d.Id, d => d.Code);
        var recent = await _db.DeviceEvents.AsNoTracking().Where(e => ids.Contains(e.DeviceId)).OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id)
            .Take(15).ToListAsync(ct);
        return new DeviceHealthOverviewDto(now, rows.Count, byHealth, byCategory,
            rows.Count(d => !d.IsSimulated), rows.Count(d => d.IsSimulated),
            health.Count(x => x.H.PhysicallyConnected),
            health.Count(x => x.Device.IsSimulated && x.H.Health == HealthStatuses.Online),
            health.Count(x => x.H.Stale), errors,
            await DeviceQueries.ToDtosAsync(_db, attention, ct),
            recent.Select(e => DeviceQueries.EventDto(e, codes.GetValueOrDefault(e.DeviceId, "?"))).ToList());
    }

    // ─── Commands ──────────────────────────────────────────────────────────

    public async Task<AccessResult<DeviceDto>> CreateAsync(AccessContext caller, CreateDeviceRequest r, string? traceId, CancellationToken ct)
    {
        if (!DeviceQueries.CanConfigure(caller, r.FactoryId))
            return AccessResult<DeviceDto>.Forbidden("factory_out_of_scope", "You can only register devices in your own factory.");
        if (!await _db.Factories.AnyAsync(f => f.Id == r.FactoryId && f.IsActive, ct))
            return AccessResult<DeviceDto>.Invalid("factory_not_found", "Factory not found or deactivated.");
        var category = DeviceCategories.Parse(r.Category)!;
        var code = DeviceRules.NormaliseCode(r.Code);
        if (await _db.Devices.AnyAsync(d => d.FactoryId == r.FactoryId && d.Code == code, ct))
            return AccessResult<DeviceDto>.Conflict("device_code_taken", $"This factory already has a device {code}.");
        var links = await CheckLinksAsync(r.FactoryId, category, r.IsSimulated, r.LineId, r.WorkstationId, r.MachineId, r.CameraId, r.AttendanceReaderId, null, ct);
        if (links.Error is { } error) return error.As<DeviceDto>();
        if (!DeviceRules.TryMetadata(r.Metadata, out var metadata, out var metaError))
            return AccessResult<DeviceDto>.Invalid("metadata_invalid", metaError!);
        var interval = r.HeartbeatIntervalSeconds ?? DefaultHeartbeatSeconds;
        var offline = r.OfflineAfterSeconds ?? Math.Max(DefaultOfflineAfterSeconds, interval * 3);
        if (offline <= interval)
            return AccessResult<DeviceDto>.Invalid("offline_window_too_short", "\"Offline after\" must be longer than the heartbeat interval.");

        var now = DateTimeOffset.UtcNow;
        var device = new Device
        {
            FactoryId = r.FactoryId, Code = code, Name = r.Name.Trim(), Category = category, IsSimulated = r.IsSimulated,
            LineId = links.LineId, WorkstationId = links.WorkstationId, MachineId = r.MachineId, CameraId = r.CameraId, AttendanceReaderId = r.AttendanceReaderId,
            Manufacturer = Clean(r.Manufacturer), Model = Clean(r.Model), SerialNumber = Clean(r.SerialNumber), FirmwareVersion = Clean(r.FirmwareVersion),
            Metadata = metadata, InstalledOn = r.InstalledOn, HeartbeatIntervalSeconds = interval, OfflineAfterSeconds = offline,
            CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email, CreatedAt = now, UpdatedAt = now,
        };
        _db.Devices.Add(device);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "device.created", AccessAudit.EntityDevice, device.Id.ToString(), null, device.Status,
            $"device {code} ({category}, {(r.IsSimulated ? "simulated" : "real")}) registered", traceId, device.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<DeviceDto>.Conflict("device_code_taken", "That device ID, camera or reader is already registered to another device.");
        }
        _db.ChangeTracker.Clear();
        return AccessResult<DeviceDto>.Created((await DeviceQueries.ToDtosAsync(_db, new[] { device }, ct))[0]);
    }

    public async Task<AccessResult<DeviceDto>> UpdateAsync(AccessContext caller, Guid id, UpdateDeviceRequest r, string? traceId, CancellationToken ct)
    {
        var current = await DeviceQueries.InScope(_db, caller).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (current is null) return NotFound<DeviceDto>();
        if (!DeviceQueries.CanConfigure(caller, current.FactoryId)) return NoConfigure<DeviceDto>();
        var category = DeviceCategories.Parse(r.Category)!;
        var links = await CheckLinksAsync(current.FactoryId, category, current.IsSimulated, r.LineId, r.WorkstationId, r.MachineId, r.CameraId, r.AttendanceReaderId, id, ct);
        if (links.Error is { } error) return error.As<DeviceDto>();
        if (!DeviceRules.TryMetadata(r.Metadata, out var metadata, out var metaError))
            return AccessResult<DeviceDto>.Invalid("metadata_invalid", metaError!);

        var d = await _db.Devices.FirstAsync(x => x.Id == id, ct);
        var changes = new List<string>();
        if (d.Name != r.Name.Trim()) changes.Add("name");
        if (d.Category != category) changes.Add($"category {d.Category} → {category}");
        if (d.LineId != links.LineId || d.WorkstationId != links.WorkstationId || d.MachineId != r.MachineId || d.CameraId != r.CameraId
            || d.AttendanceReaderId != r.AttendanceReaderId) changes.Add("placement / links");
        if (d.Manufacturer != Clean(r.Manufacturer) || d.Model != Clean(r.Model) || d.SerialNumber != Clean(r.SerialNumber)
            || d.FirmwareVersion != Clean(r.FirmwareVersion) || d.InstalledOn != r.InstalledOn) changes.Add("details");
        if (d.Metadata != metadata) changes.Add("metadata");
        if (d.HeartbeatIntervalSeconds != r.HeartbeatIntervalSeconds || d.OfflineAfterSeconds != r.OfflineAfterSeconds)
            changes.Add($"heartbeat {r.HeartbeatIntervalSeconds}s / offline after {r.OfflineAfterSeconds}s");
        if (changes.Count > 0)
        {
            d.Name = r.Name.Trim();
            d.Category = category;
            d.LineId = links.LineId;
            d.WorkstationId = links.WorkstationId;
            d.MachineId = r.MachineId;
            d.CameraId = r.CameraId;
            d.AttendanceReaderId = r.AttendanceReaderId;
            d.Manufacturer = Clean(r.Manufacturer);
            d.Model = Clean(r.Model);
            d.SerialNumber = Clean(r.SerialNumber);
            d.FirmwareVersion = Clean(r.FirmwareVersion);
            d.InstalledOn = r.InstalledOn;
            d.Metadata = metadata;
            d.HeartbeatIntervalSeconds = r.HeartbeatIntervalSeconds;
            d.OfflineAfterSeconds = r.OfflineAfterSeconds;
            d.OfflineDeadline = DeviceQueries.Deadline(d.LastSeenAt, d.OfflineAfterSeconds);
            d.UpdatedAt = DateTimeOffset.UtcNow;
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "device.updated", AccessAudit.EntityDevice, d.Id.ToString(), null, null,
                $"device {d.Code}: {string.Join("; ", changes)}", traceId, d.FactoryId));
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                _db.ChangeTracker.Clear();
                return AccessResult<DeviceDto>.Conflict("device_link_taken", "That camera or reader is already linked to another device.");
            }
        }
        _db.ChangeTracker.Clear();
        return await GetAsync(caller, id, ct);
    }

    public async Task<AccessResult<DeviceDto>> SetStatusAsync(AccessContext caller, Guid id, SetDeviceStatusRequest r, string? traceId, CancellationToken ct)
    {
        var current = await DeviceQueries.InScope(_db, caller).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (current is null) return NotFound<DeviceDto>();
        if (!DeviceQueries.CanConfigure(caller, current.FactoryId)) return NoConfigure<DeviceDto>();
        var status = DeviceStatuses.Parse(r.Status)!;
        if (status == current.Status) return await GetAsync(caller, id, ct);
        var d = await _db.Devices.FirstAsync(x => x.Id == id, ct);
        var now = DateTimeOffset.UtcNow;
        var reason = Clean(r.Reason);
        var statusEvent = new DeviceEvent
        {
            DeviceId = d.Id, FactoryId = d.FactoryId, EventType = DeviceEventTypes.StatusChanged,
            Severity = status == DeviceStatuses.Active ? DeviceSeverities.Info : DeviceSeverities.Warning, Source = DeviceMessageSources.Manual,
            IsSimulated = d.IsSimulated, OccurredAt = now, ReceivedAt = now, FromState = d.Status, ToState = status, Message = reason,
            RecordedByUserId = caller.UserId, RecordedByEmail = caller.Email,
        };
        _db.DeviceEvents.Add(statusEvent);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "device.status_changed", AccessAudit.EntityDevice, d.Id.ToString(), d.Status, status,
            $"device {d.Code}: {d.Status} → {status}" + (reason is null ? "" : $" ({reason})"), traceId, d.FactoryId));
        d.Status = status;
        d.StatusReason = reason;
        d.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        await _monitoring.DeviceEventsAsync(new[] { statusEvent.Id }, caller, ct);
        return await GetAsync(caller, id, ct);
    }

    public async Task<AccessResult<bool>> DeleteAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct)
    {
        var current = await DeviceQueries.InScope(_db, caller).FirstOrDefaultAsync(d => d.Id == id, ct);
        if (current is null) return NotFound<bool>();
        if (!DeviceQueries.CanConfigure(caller, current.FactoryId)) return NoConfigure<bool>();
        if (await _db.DeviceHeartbeats.AnyAsync(h => h.DeviceId == id, ct) || await _db.DeviceEvents.AnyAsync(e => e.DeviceId == id, ct))
            return AccessResult<bool>.Conflict("device_has_history",
                "This device has heartbeats or events, which are kept as history. Disable the device instead.");
        var d = await _db.Devices.FirstAsync(x => x.Id == id, ct);
        _db.Devices.Remove(d);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "device.deleted", AccessAudit.EntityDevice, d.Id.ToString(), d.Status, "deleted",
            $"device {d.Code} ({d.Name}) deleted: it never sent a message", traceId, d.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23503" })
        {
            _db.ChangeTracker.Clear();
            return AccessResult<bool>.Conflict("device_has_history", "This device just received a message. Disable it instead.");
        }
        return AccessResult<bool>.Ok(true);
    }

    // ─── Relationship checks ───────────────────────────────────────────────

    private sealed record Links(string? LineId, Guid? WorkstationId, AccessResult<object>? Error)
    {
        public static Links Fail(string code, string message) => new(null, null, AccessResult<object>.Invalid(code, message));
        public static Links Taken(string code, string message) => new(null, null, AccessResult<object>.Conflict(code, message));
    }

    /// <summary>
    /// Placement inside the factory, consistent with itself: a workstation on
    /// the given line, a machine standing at the given workstation. A machine
    /// or workstation fills in the line (and workstation) when they are not given.
    /// </summary>
    private async Task<Links> CheckLinksAsync(Guid factoryId, string category, bool simulated, string? lineId, Guid? workstationId, Guid? machineId,
        Guid? cameraId, Guid? readerId, Guid? self, CancellationToken ct)
    {
        var line = Clean(lineId);
        var ws = workstationId;
        if (machineId is { } mId)
        {
            var m = await _db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mId, ct);
            if (m is null || m.FactoryId != factoryId) return Links.Fail("machine_not_in_factory", "That machine doesn't belong to this factory.");
            if (ws is not null && m.WorkstationId is { } at && at != ws)
                return Links.Fail("machine_not_at_workstation", $"Machine {m.Code} stands at another workstation.");
            ws ??= m.WorkstationId;
        }
        if (ws is { } wId)
        {
            var w = await _db.Workstations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == wId, ct);
            if (w is null || w.FactoryId != factoryId) return Links.Fail("workstation_not_in_factory", "That workstation doesn't belong to this factory.");
            if (line is not null && line != w.LineId) return Links.Fail("workstation_not_on_line", $"Workstation {w.Code} is not on line {line}.");
            line = w.LineId;
        }
        if (line is not null)
        {
            var l = await _db.ProductionLines.AsNoTracking().Where(x => x.Id == line).Select(x => new { x.FactoryId }).FirstOrDefaultAsync(ct);
            if (l is null || l.FactoryId != factoryId) return Links.Fail("line_not_in_factory", "That production line doesn't belong to this factory.");
        }
        if (cameraId is { } cId)
        {
            if (category != DeviceCategories.Camera) return Links.Fail("camera_link_requires_camera_category", "Only a device of category Camera can report a camera's health.");
            var cam = await _db.Cameras.AsNoTracking().Where(x => x.Id == cId).Select(x => new { x.FactoryId }).FirstOrDefaultAsync(ct);
            if (cam is null || cam.FactoryId != factoryId) return Links.Fail("camera_not_in_factory", "That camera doesn't belong to this factory.");
            if (await _db.Devices.AnyAsync(d => d.CameraId == cId && d.Id != self, ct))
                return Links.Taken("camera_has_device", "That camera already has a health device.");
        }
        if (readerId is { } rId)
        {
            if (category != DeviceCategories.RfidReader) return Links.Fail("reader_link_requires_rfid_category", "Only an RFID reader device can be linked to an attendance reader.");
            var reader = await _db.AttendanceReaders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rId, ct);
            if (reader is null || reader.FactoryId != factoryId) return Links.Fail("reader_not_in_factory", "That attendance reader doesn't belong to this factory.");
            if (reader.IsSimulated != simulated)
                return Links.Fail("reader_mode_mismatch", reader.IsSimulated
                    ? "That attendance reader is simulated; link it to a simulated device."
                    : "That attendance reader is a real reader; link it to a real device.");
            if (await _db.Devices.AnyAsync(d => d.AttendanceReaderId == rId && d.Id != self, ct))
                return Links.Taken("reader_has_device", "That attendance reader is already linked to another device.");
        }
        return new Links(line, ws, null);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
