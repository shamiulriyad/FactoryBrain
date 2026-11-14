using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Cameras;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Cameras;
using BunonBrain.Infrastructure.Devices;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Monitoring;

/// <summary>
/// The central monitoring room (see <see cref="IMonitoringRoomService"/>).
/// Camera connection comes from each camera's health device (real or
/// SIMULATED, never mixed); a camera without one is UNKNOWN. Nothing here is
/// an AI result: computer vision is not connected in this phase.
/// </summary>
public sealed class MonitoringRoomService : IMonitoringRoomService
{
    public const string AiStatus = "NOT_CONNECTED";
    public const string AiMessage =
        "Computer-vision processing is not connected in this phase. No detection, tracking or recognition runs on any camera; " +
        "vision events shown here come from the monitoring simulator (labelled SIMULATED) or from producers using the ingestion API.";

    private readonly BunonBrainDbContext _db;
    public MonitoringRoomService(BunonBrainDbContext db) { _db = db; }

    // ─── Camera connection ─────────────────────────────────────────────────

    private sealed record CameraRow(Camera Camera, CameraSource? Source, Device? Health, List<string> Workstations, List<string> CoverageLines);

    private async Task<List<CameraRow>> CameraRowsAsync(IQueryable<Camera> cams, CancellationToken ct)
    {
        var list = await cams.OrderBy(c => c.Code).Take(500).ToListAsync(ct);
        var ids = list.Select(c => c.Id).ToList();
        var sources = await _db.CameraSources.AsNoTracking().Where(s => s.RetiredAt == null && ids.Contains(s.CameraId)).ToDictionaryAsync(s => s.CameraId, ct);
        var devices = (await _db.Devices.AsNoTracking().Where(d => d.CameraId != null && ids.Contains(d.CameraId.Value)).ToListAsync(ct))
            .ToDictionary(d => d.CameraId!.Value);
        var coverage = await _db.CameraCoverage.AsNoTracking().Where(v => v.RemovedAt == null && ids.Contains(v.CameraId))
            .Select(v => new { v.CameraId, v.LineId, v.WorkstationId }).ToListAsync(ct);
        var wsIds = coverage.Where(v => v.WorkstationId != null).Select(v => v.WorkstationId!.Value).Distinct().ToList();
        var wsCodes = await _db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        return list.Select(c => new CameraRow(c, sources.GetValueOrDefault(c.Id), devices.GetValueOrDefault(c.Id),
            coverage.Where(v => v.CameraId == c.Id && v.WorkstationId != null).Select(v => wsCodes.GetValueOrDefault(v.WorkstationId!.Value, "?")).OrderBy(x => x).ToList(),
            coverage.Where(v => v.CameraId == c.Id).Select(v => v.LineId).Distinct().ToList())).ToList();
    }

    public static CameraConnectionDto Connection(Camera c, Device? health, DateTimeOffset now)
    {
        if (c.Status == CameraStatuses.Inactive) return new CameraConnectionDto("INACTIVE", "Deactivated", health?.IsSimulated ?? false, false, health?.LastSeenAt, health?.Code, null);
        if (c.Status == CameraStatuses.Maintenance) return new CameraConnectionDto("MAINTENANCE", "In maintenance", health?.IsSimulated ?? false, false, health?.LastSeenAt, health?.Code, null);
        if (health is null) return new CameraConnectionDto("UNKNOWN", "No health device: connection not monitored", false, false, null, null, null);
        var h = DeviceRules.Health(health.Status, health.ConnectionStatus, health.IsSimulated, health.LastSeenAt, health.OfflineAfterSeconds, now);
        var status = h.Health switch
        {
            HealthStatuses.Online => "ONLINE",
            HealthStatuses.Offline => "OFFLINE",
            _ => "UNKNOWN",
        };
        var label = h.Label + (health.IsSimulated && status == "ONLINE" ? " · SIMULATED health, not a real connection" : "");
        return new CameraConnectionDto(status, label, health.IsSimulated, h.PhysicallyConnected, health.LastSeenAt, health.Code,
            health.LastErrorAt is not null && health.LastErrorAt > now.AddHours(-24) ? health.LastErrorMessage : null);
    }

    public static CameraFeedDto Feed(CameraSource? s)
    {
        var type = s?.SourceType ?? CameraSourceTypes.None;
        return type switch
        {
            CameraSourceTypes.RecordedVideo => new CameraFeedDto(type, "RECORDED_VIDEO", s!.VideoKey is not null, false, null, s.VideoFileName, "N/A",
                "Recorded video (prototype source). Playing it shows the recording, not a live feed."),
            CameraSourceTypes.LocalWebcam => new CameraFeedDto(type, "LOCAL_WEBCAM", false, false, s!.WebcamLabel, null, "N/A",
                "Local webcam: a preview uses the camera of the computer you are viewing on, with your permission. It is not a factory CCTV feed."),
            CameraSourceTypes.Rtsp or CameraSourceTypes.HttpStream => new CameraFeedDto(type, "LIVE_STREAM", false, false, null, null, "NOT_CHECKED",
                "A live stream is configured, but the server does not open RTSP / HTTP streams in this phase, so no live picture is shown and the connection is not checked."),
            _ => new CameraFeedDto(CameraSourceTypes.None, "NONE", false, false, null, null, "N/A", "No video source is configured for this camera."),
        };
    }

    private async Task<Dictionary<Guid, (int Active, string? Highest)>> CameraAlertsAsync(AccessContext caller, List<Guid> ids, CancellationToken ct) =>
        (await MonitoringQueries.AlertsInScope(_db, caller).Where(a => a.CameraId != null && ids.Contains(a.CameraId.Value) && AlertStatuses.Active.Contains(a.Status))
            .Select(a => new { CameraId = a.CameraId!.Value, a.Severity }).ToListAsync(ct))
        .GroupBy(a => a.CameraId)
        .ToDictionary(g => g.Key, g => (g.Count(), (string?)g.OrderByDescending(x => AlertSeverities.Rank(x.Severity)).First().Severity));

    private async Task<Dictionary<Guid, DateTimeOffset>> CameraLastEventsAsync(AccessContext caller, List<Guid> ids, CancellationToken ct) =>
        await MonitoringQueries.EventsInScope(_db, caller).Where(e => e.CameraId != null && ids.Contains(e.CameraId.Value))
            .GroupBy(e => e.CameraId!.Value).Select(g => new { g.Key, Last = g.Max(e => e.OccurredAt) }).ToDictionaryAsync(x => x.Key, x => x.Last, ct);

    private async Task<IReadOnlyList<MonitoringCameraDto>> CameraDtosAsync(AccessContext caller, List<CameraRow> rows, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var ids = rows.Select(r => r.Camera.Id).ToList();
        var factories = await _db.Factories.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Code, ct);
        var floorIds = rows.Select(r => r.Camera.FloorId).Distinct().ToList();
        var floors = await _db.ProductionFloors.AsNoTracking().Where(f => floorIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Name, ct);
        var lineIds = rows.Where(r => r.Camera.LineId != null).Select(r => r.Camera.LineId!).Distinct().ToList();
        var lines = await _db.ProductionLines.AsNoTracking().Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var zones = await _db.CameraZones.AsNoTracking().Where(z => z.IsActive && ids.Contains(z.CameraId)).GroupBy(z => z.CameraId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var alerts = await CameraAlertsAsync(caller, ids, ct);
        var last = await CameraLastEventsAsync(caller, ids, ct);
        return rows.Select(r =>
        {
            var c = r.Camera;
            alerts.TryGetValue(c.Id, out var a);
            return new MonitoringCameraDto(c.Id, c.Code, c.Name, c.CameraType, c.Status, c.FactoryId, factories.GetValueOrDefault(c.FactoryId, "?"),
                floors.GetValueOrDefault(c.FloorId), c.LineId is null ? null : new MonLine(c.LineId, lines.GetValueOrDefault(c.LineId, c.LineId)),
                r.Workstations, Feed(r.Source), Connection(c, r.Health, now), zones.GetValueOrDefault(c.Id), a.Active, a.Highest,
                last.TryGetValue(c.Id, out var l) ? l : null, c.ReferenceImageKey is not null);
        }).ToList();
    }

    private IQueryable<Camera> FilterCameras(AccessContext caller, Guid? factoryId, Guid? floorId, Guid? departmentId, string? lineId, Guid? cameraId)
    {
        var cams = CameraService.InScope(_db, caller);
        if (factoryId is { } f) cams = cams.Where(c => c.FactoryId == f);
        if (floorId is { } fl) cams = cams.Where(c => c.FloorId == fl);
        if (departmentId is { } d) cams = cams.Where(c => c.DepartmentId == d);
        if (!string.IsNullOrWhiteSpace(lineId))
        {
            var line = lineId.Trim();
            cams = cams.Where(c => c.LineId == line || _db.CameraCoverage.Any(v => v.CameraId == c.Id && v.RemovedAt == null && v.LineId == line));
        }
        if (cameraId is { } id) cams = cams.Where(c => c.Id == id);
        return cams;
    }

    public async Task<IReadOnlyList<MonitoringCameraDto>> CamerasAsync(AccessContext caller, MonitoringCameraQuery q, CancellationToken ct)
    {
        var cams = FilterCameras(caller, q.FactoryId, q.FloorId, null, q.LineId, null);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            cams = cams.Where(c => EF.Functions.ILike(c.Code, pattern, "\\") || EF.Functions.ILike(c.Name, pattern, "\\")
                                   || (c.Location != null && EF.Functions.ILike(c.Location, pattern, "\\")));
        }
        var dtos = await CameraDtosAsync(caller, await CameraRowsAsync(cams, ct), ct);
        if (!string.IsNullOrWhiteSpace(q.Connection))
        {
            var want = q.Connection.Trim().ToUpperInvariant();
            dtos = dtos.Where(d => d.Connection.Status == want).ToList();
        }
        return dtos;
    }

    public async Task<AccessResult<MonitoringCameraDetailDto>> CameraAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var rows = await CameraRowsAsync(CameraService.InScope(_db, caller).Where(c => c.Id == id), ct);
        if (rows.Count == 0) return AccessResult<MonitoringCameraDetailDto>.NotFound("camera_not_found", "Camera not found.");
        var cam = (await CameraDtosAsync(caller, rows, ct))[0];
        var zones = await _db.CameraZones.AsNoTracking().Where(z => z.CameraId == id && z.IsActive).OrderBy(z => z.Name)
            .Select(z => new MonZone(z.Id, z.Name, z.ZoneType)).ToListAsync(ct);
        var events = await MonitoringQueries.EventsInScope(_db, caller).Where(e => e.CameraId == id)
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.ReceivedAt).Take(30).ToListAsync(ct);
        var alerts = await MonitoringQueries.AlertsInScope(_db, caller).Where(a => a.CameraId == id && AlertStatuses.Active.Contains(a.Status))
            .OrderByDescending(a => a.LastOccurredAt).Take(20).ToListAsync(ct);
        return AccessResult<MonitoringCameraDetailDto>.Ok(new MonitoringCameraDetailDto(cam, zones,
            await MonitoringQueries.EventDtosAsync(_db, events, ct), await MonitoringQueries.AlertDtosAsync(_db, caller, alerts, ct), AiStatus, AiMessage));
    }

    // ─── Overview ──────────────────────────────────────────────────────────

    public async Task<MonitoringOverviewDto> OverviewAsync(AccessContext caller, MonitoringOverviewQuery q, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var vision = MonitoringQueries.CanSeeVision(caller);

        // Cameras: the camera monitoring view needs camera access; device-only callers still see counts via health devices.
        var cameraRows = await CameraRowsAsync(FilterCameras(caller, q.FactoryId, q.FloorId, q.DepartmentId, q.LineId, q.CameraId), ct);
        var connections = cameraRows.Select(r => (Row: r, Conn: Connection(r.Camera, r.Health, now))).ToList();
        var cameraCounts = new CameraCounts(connections.Count,
            connections.Count(c => c.Conn.Status == "ONLINE"), connections.Count(c => c.Conn.Status == "OFFLINE"),
            connections.Count(c => c.Conn.Status == "UNKNOWN"), connections.Count(c => c.Conn.Status is "INACTIVE" or "MAINTENANCE"),
            connections.Count(c => c.Row.Health?.IsSimulated == true));

        // Devices.
        var devices = DeviceQueries.InScope(_db, caller);
        if (q.FactoryId is { } df) devices = devices.Where(d => d.FactoryId == df);
        if (!string.IsNullOrWhiteSpace(q.LineId)) { var line = q.LineId.Trim(); devices = devices.Where(d => d.LineId == line); }
        if (q.CameraId is { } dc) devices = devices.Where(d => d.CameraId == dc);
        var deviceRows = await devices.Select(d => new { d.Id, d.FactoryId, d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds })
            .Take(5000).ToListAsync(ct);
        var health = deviceRows.Select(d => (d.FactoryId, d.IsSimulated, H: DeviceRules.Health(d.Status, d.ConnectionStatus, d.IsSimulated, d.LastSeenAt, d.OfflineAfterSeconds, now).Health)).ToList();
        var deviceCounts = new DeviceCounts(health.Count, health.Count(h => h.H == HealthStatuses.Online), health.Count(h => h.H == HealthStatuses.Offline),
            health.Count(h => h.H == HealthStatuses.Unknown), health.Count(h => h.H == HealthStatuses.Maintenance), health.Count(h => h.H == HealthStatuses.Disabled),
            health.Count(h => h.IsSimulated), health.Count(h => !h.IsSimulated));

        // Alerts.
        var alertScope = MonitoringAlertService.Filter(MonitoringQueries.AlertsInScope(_db, caller), new MonitoringAlertQuery(
            q.FactoryId, q.DepartmentId, q.FloorId, q.LineId, q.CameraId));
        var active = alertScope.Where(a => AlertStatuses.Active.Contains(a.Status));
        var activeRows = await active.Select(a => new { a.Status, a.Severity, a.SourceType, a.LineId, a.FactoryId }).ToListAsync(ct);
        var filtered = MonitoringAlertService.Filter(alertScope, new MonitoringAlertQuery(
            Status: string.IsNullOrWhiteSpace(q.AlertStatus) ? "ACTIVE" : q.AlertStatus, From: q.From, To: q.To));
        var inFilter = await filtered.CountAsync(ct);
        var alertList = await filtered
            .OrderByDescending(a => a.Severity == AlertSeverities.Critical ? 4 : a.Severity == AlertSeverities.High ? 3 : a.Severity == AlertSeverities.Medium ? 2 : 1)
            .ThenByDescending(a => a.LastOccurredAt).Take(12).ToListAsync(ct);
        var alertCounts = new AlertCounts(activeRows.Count, activeRows.Count(a => a.Status == AlertStatuses.Open),
            activeRows.Count(a => a.Status == AlertStatuses.Escalated), activeRows.Count(a => a.Severity == AlertSeverities.Critical),
            activeRows.Count(a => a.Severity == AlertSeverities.High), activeRows.Count(a => a.SourceType == MonitoringSourceTypes.Simulated),
            activeRows.Count(a => a.SourceType == MonitoringSourceTypes.Real), inFilter);

        // Events.
        var eventScope = MonitoringQueries.EventsInScope(_db, caller);
        if (q.FactoryId is { } ef) eventScope = eventScope.Where(e => e.FactoryId == ef);
        if (q.DepartmentId is { } ed) eventScope = eventScope.Where(e => e.DepartmentId == ed);
        if (q.FloorId is { } efl) eventScope = eventScope.Where(e => e.FloorId == efl);
        if (!string.IsNullOrWhiteSpace(q.LineId)) { var line = q.LineId.Trim(); eventScope = eventScope.Where(e => e.LineId == line); }
        if (q.CameraId is { } ec) eventScope = eventScope.Where(e => e.CameraId == ec);
        if (MonitoringEventTypes.Parse(q.EventType) is { } et) eventScope = eventScope.Where(e => e.EventType == et);
        var hourAgo = now.AddHours(-1);
        var dayAgo = now.AddHours(-24);
        var lastHour = await eventScope.CountAsync(e => e.OccurredAt >= hourAgo, ct);
        var lastDay = await eventScope.CountAsync(e => e.OccurredAt >= dayAgo, ct);
        var ranged = eventScope;
        var from = q.From?.ToUniversalTime() ?? dayAgo;
        ranged = ranged.Where(e => e.OccurredAt >= from);
        if (q.To is { } to) { var u = to.ToUniversalTime(); ranged = ranged.Where(e => e.OccurredAt <= u); }
        var rangeCounts = await ranged.GroupBy(e => e.SourceType).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var recent = await ranged.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.ReceivedAt).Take(25).ToListAsync(ct);
        var eventCounts = new EventCounts(lastHour, lastDay, rangeCounts.Sum(r => r.Count),
            rangeCounts.Where(r => r.Key == MonitoringSourceTypes.Simulated).Sum(r => r.Count), rangeCounts.Where(r => r.Key == MonitoringSourceTypes.Real).Sum(r => r.Count));

        // Lines and factories.
        var lines = await LineStatusAsync(caller, q, connections.Select(c => (c.Row.Camera, c.Row.CoverageLines, c.Conn.Status)).ToList(), now, ct);
        var factories = await FactoryStatusAsync(caller, q.FactoryId, connections.Select(c => (c.Row.Camera.FactoryId, c.Conn.Status)).ToList(),
            health.Select(h => (h.FactoryId, h.H)).ToList(), activeRows.Select(a => (a.FactoryId, a.Severity)).ToList(), ct);

        return new MonitoringOverviewDto(now, vision, cameraCounts, deviceCounts, alertCounts, eventCounts,
            await MonitoringQueries.EventDtosAsync(_db, recent, ct), await MonitoringQueries.AlertDtosAsync(_db, caller, alertList, ct),
            lines, factories, AiStatus);
    }

    private async Task<IReadOnlyList<LineStatusDto>> LineStatusAsync(AccessContext caller, MonitoringOverviewQuery q,
        List<(Camera Camera, List<string> CoverageLines, string Status)> cameras, DateTimeOffset now, CancellationToken ct)
    {
        var lines = _db.ProductionLines.AsNoTracking().Where(l => l.IsActive && l.FactoryId != null);
        var factoryIds = caller.FactoryIds.ToList();
        var lineIds = caller.LineIds.ToList();
        lines = caller.Scope switch
        {
            RoleScope.Platform => lines,
            RoleScope.Factory => lines.Where(l => factoryIds.Contains(l.FactoryId!.Value)),
            RoleScope.Line => lines.Where(l => factoryIds.Contains(l.FactoryId!.Value) && lineIds.Contains(l.Id)),
            _ => lines.Where(_ => false),
        };
        if (q.FactoryId is { } f) lines = lines.Where(l => l.FactoryId == f);
        if (!string.IsNullOrWhiteSpace(q.LineId)) { var line = q.LineId.Trim(); lines = lines.Where(l => l.Id == line); }
        var rows = await lines.OrderBy(l => l.Name).Take(100).Select(l => new { l.Id, l.Name, l.FactoryId }).ToListAsync(ct);
        if (rows.Count == 0) return Array.Empty<LineStatusDto>();
        var ids = rows.Select(r => r.Id).ToList();
        var codes = await _db.Factories.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var alerts = await MonitoringQueries.AlertsInScope(_db, caller).Where(a => a.LineId != null && ids.Contains(a.LineId) && AlertStatuses.Active.Contains(a.Status))
            .Select(a => new { a.LineId, a.Severity }).ToListAsync(ct);
        var hourAgo = now.AddHours(-1);
        var events = await MonitoringQueries.EventsInScope(_db, caller).Where(e => e.LineId != null && ids.Contains(e.LineId))
            .GroupBy(e => e.LineId!).Select(g => new { g.Key, Last = g.Max(e => e.OccurredAt), Hour = g.Count(e => e.OccurredAt >= hourAgo) })
            .ToDictionaryAsync(x => x.Key, ct);
        return rows.Select(l =>
        {
            var cams = cameras.Where(c => c.Camera.LineId == l.Id || c.CoverageLines.Contains(l.Id)).ToList();
            var lineAlerts = alerts.Where(a => a.LineId == l.Id).ToList();
            var highest = lineAlerts.OrderByDescending(a => AlertSeverities.Rank(a.Severity)).FirstOrDefault()?.Severity;
            var offline = cams.Count(c => c.Status == "OFFLINE");
            var status = cams.Count == 0 ? "NO_COVERAGE"
                : AlertSeverities.Rank(highest) >= AlertSeverities.Rank(AlertSeverities.High) ? "ALERT"
                : lineAlerts.Count > 0 || offline > 0 ? "ATTENTION"
                : "NORMAL";
            events.TryGetValue(l.Id, out var ev);
            return new LineStatusDto(l.Id, l.Name, l.FactoryId, l.FactoryId is { } fid ? codes.GetValueOrDefault(fid) : null, cams.Count,
                cams.Count(c => c.Status == "ONLINE"), offline, lineAlerts.Count, highest, ev?.Last, ev?.Hour ?? 0, status);
        }).ToList();
    }

    private async Task<IReadOnlyList<FactoryStatusDto>> FactoryStatusAsync(AccessContext caller, Guid? factoryId,
        List<(Guid FactoryId, string Status)> cameras, List<(Guid FactoryId, string Health)> devices, List<(Guid FactoryId, string Severity)> alerts, CancellationToken ct)
    {
        var factories = _db.Factories.AsNoTracking().Where(f => f.IsActive);
        if (!caller.IsPlatform) { var mine = caller.FactoryIds.ToList(); factories = factories.Where(f => mine.Contains(f.Id)); }
        if (factoryId is { } id) factories = factories.Where(f => f.Id == id);
        var rows = await factories.OrderBy(f => f.Code).Take(50).Select(f => new { f.Id, f.Code, f.Name }).ToListAsync(ct);
        return rows.Select(f =>
        {
            var cams = cameras.Where(c => c.FactoryId == f.Id).ToList();
            var devs = devices.Where(d => d.FactoryId == f.Id).ToList();
            var al = alerts.Where(a => a.FactoryId == f.Id).ToList();
            var highest = al.OrderByDescending(a => AlertSeverities.Rank(a.Severity)).FirstOrDefault().Severity;
            var camsOffline = cams.Count(c => c.Status == "OFFLINE");
            var devsOffline = devs.Count(d => d.Health == HealthStatuses.Offline);
            var status = AlertSeverities.Rank(highest) >= AlertSeverities.Rank(AlertSeverities.High) ? "ALERT"
                : al.Count > 0 || camsOffline > 0 || devsOffline > 0 ? "ATTENTION" : "NORMAL";
            return new FactoryStatusDto(f.Id, f.Code, f.Name, cams.Count, cams.Count(c => c.Status == "ONLINE"), camsOffline, devs.Count, devsOffline,
                al.Count, highest, status);
        }).ToList();
    }
}
