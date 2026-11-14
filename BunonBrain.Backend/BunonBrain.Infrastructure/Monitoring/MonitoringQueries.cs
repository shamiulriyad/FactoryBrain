using System.Text.Json;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Monitoring;

/// <summary>
/// Reach and mapping shared by the monitoring services. Reach: Super Admin
/// every factory; factory roles their factories; line roles only items on
/// their lines. VISION items (people, tracks, zones, activity) also need
/// <c>monitoring.view</c>; with only <c>devices.view</c> a caller sees SYSTEM
/// items (camera, device and model status).
/// </summary>
public static class MonitoringQueries
{
    public static bool CanSeeVision(AccessContext caller) => caller.Has(Permissions.MonitoringView);

    public static IQueryable<MonitoringEvent> EventsInScope(BunonBrainDbContext db, AccessContext caller)
    {
        var q = db.MonitoringEvents.AsNoTracking();
        if (!CanSeeVision(caller)) q = q.Where(e => e.Category == MonitoringCategories.System);
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => q,
            RoleScope.Factory => q.Where(e => factories.Contains(e.FactoryId)),
            RoleScope.Line => q.Where(e => factories.Contains(e.FactoryId) && e.LineId != null && lines.Contains(e.LineId)),
            _ => q.Where(_ => false),
        };
    }

    public static IQueryable<MonitoringAlert> AlertsInScope(BunonBrainDbContext db, AccessContext caller)
    {
        var q = db.MonitoringAlerts.AsNoTracking();
        if (!CanSeeVision(caller)) q = q.Where(a => a.Category == MonitoringCategories.System);
        var factories = caller.FactoryIds.ToList();
        var lines = caller.LineIds.ToList();
        return caller.Scope switch
        {
            RoleScope.Platform => q,
            RoleScope.Factory => q.Where(a => factories.Contains(a.FactoryId)),
            RoleScope.Line => q.Where(a => factories.Contains(a.FactoryId) && a.LineId != null && lines.Contains(a.LineId)),
            _ => q.Where(_ => false),
        };
    }

    /// <summary>Whether the caller may see one item with these facts (used before pushing or acting).</summary>
    public static bool CanSee(AccessContext caller, Guid factoryId, string? lineId, string category)
    {
        if (category == MonitoringCategories.Vision && !CanSeeVision(caller)) return false;
        return caller.Scope switch
        {
            RoleScope.Platform => true,
            RoleScope.Factory => caller.FactoryIds.Contains(factoryId),
            RoleScope.Line => caller.FactoryIds.Contains(factoryId) && lineId is not null && caller.LineIds.Contains(lineId),
            _ => false,
        };
    }

    public static string AlertCode(long number) => $"ALT-{number:D6}";

    public static JsonElement Json(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    // ─── Names for many rows at once ───────────────────────────────────────

    private sealed class Names
    {
        public Dictionary<Guid, string> Factories = new();
        public Dictionary<Guid, string> Departments = new();
        public Dictionary<Guid, string> Floors = new();
        public Dictionary<string, string> Lines = new();
        public Dictionary<Guid, string> Workstations = new();
        public Dictionary<Guid, (string Code, string Name)> Cameras = new();
        public Dictionary<Guid, (string Code, string Name)> Devices = new();
        public Dictionary<Guid, (string Name, string Type)> Zones = new();
        public Dictionary<Guid, (string Code, string Name)> Workers = new();
    }

    private static async Task<Names> LoadAsync(BunonBrainDbContext db, IEnumerable<Guid> factoryIds, IEnumerable<Guid?> departmentIds,
        IEnumerable<Guid?> floorIds, IEnumerable<string?> lineIds, IEnumerable<Guid?> workstationIds, IEnumerable<Guid?> cameraIds,
        IEnumerable<Guid?> deviceIds, IEnumerable<Guid?> zoneIds, IEnumerable<Guid?> workerIds, CancellationToken ct)
    {
        static List<Guid> G(IEnumerable<Guid?> ids) => ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        var n = new Names();
        var f = factoryIds.Distinct().ToList();
        if (f.Count > 0) n.Factories = await db.Factories.AsNoTracking().Where(x => f.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var d = G(departmentIds);
        if (d.Count > 0) n.Departments = await db.Departments.AsNoTracking().Where(x => d.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var fl = G(floorIds);
        if (fl.Count > 0) n.Floors = await db.ProductionFloors.AsNoTracking().Where(x => fl.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var l = lineIds.Where(x => x is not null).Select(x => x!).Distinct().ToList();
        if (l.Count > 0) n.Lines = await db.ProductionLines.AsNoTracking().Where(x => l.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var w = G(workstationIds);
        if (w.Count > 0) n.Workstations = await db.Workstations.AsNoTracking().Where(x => w.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var c = G(cameraIds);
        if (c.Count > 0)
            n.Cameras = (await db.Cameras.AsNoTracking().Where(x => c.Contains(x.Id)).Select(x => new { x.Id, x.Code, x.Name }).ToListAsync(ct))
                .ToDictionary(x => x.Id, x => (x.Code, x.Name));
        var dv = G(deviceIds);
        if (dv.Count > 0)
            n.Devices = (await db.Devices.AsNoTracking().Where(x => dv.Contains(x.Id)).Select(x => new { x.Id, x.Code, x.Name }).ToListAsync(ct))
                .ToDictionary(x => x.Id, x => (x.Code, x.Name));
        var z = G(zoneIds);
        if (z.Count > 0)
            n.Zones = (await db.CameraZones.AsNoTracking().Where(x => z.Contains(x.Id)).Select(x => new { x.Id, x.Name, x.ZoneType }).ToListAsync(ct))
                .ToDictionary(x => x.Id, x => (x.Name, x.ZoneType));
        var wk = G(workerIds);
        if (wk.Count > 0)
            n.Workers = (await db.Workers.AsNoTracking().Where(x => wk.Contains(x.Id)).Select(x => new { x.Id, x.WorkerCode, x.FullName }).ToListAsync(ct))
                .ToDictionary(x => x.Id, x => (x.WorkerCode, x.FullName));
        return n;
    }

    private static MonLine? Line(Names n, string? id) => id is null ? null : new MonLine(id, n.Lines.GetValueOrDefault(id, id));
    private static MonRef? Ref(Dictionary<Guid, (string Code, string Name)> d, Guid? id) =>
        id is { } g && d.TryGetValue(g, out var v) ? new MonRef(g, v.Code, v.Name) : null;

    // ─── Events ────────────────────────────────────────────────────────────

    public static async Task<IReadOnlyList<MonitoringEventDto>> EventDtosAsync(BunonBrainDbContext db, IReadOnlyList<MonitoringEvent> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<MonitoringEventDto>();
        var n = await LoadAsync(db, rows.Select(r => r.FactoryId), rows.Select(r => r.DepartmentId), rows.Select(r => r.FloorId), rows.Select(r => r.LineId),
            rows.Select(r => r.WorkstationId), rows.Select(r => r.CameraId), rows.Select(r => r.DeviceId), rows.Select(r => r.ZoneId),
            rows.Select(r => r.WorkerId), ct);
        var ids = rows.Select(r => r.Id).ToList();
        var reviews = await db.MonitoringEventReviews.AsNoTracking().Where(r => ids.Contains(r.EventId))
            .GroupBy(r => r.EventId)
            .Select(g => new { g.Key, Count = g.Count(), Latest = g.OrderByDescending(x => x.ReviewedAt).ThenByDescending(x => x.Id).Select(x => x.Decision).First() })
            .ToDictionaryAsync(x => x.Key, ct);
        return rows.Select(e =>
        {
            reviews.TryGetValue(e.Id, out var rv);
            return new MonitoringEventDto(
                e.Id, e.EventType, MonitoringEventTypes.Label(e.EventType), e.Category, e.SourceType, e.SourceType == MonitoringSourceTypes.Simulated,
                e.Producer, e.Severity, e.Summary, e.FactoryId, n.Factories.GetValueOrDefault(e.FactoryId, "?"),
                e.DepartmentId is { } dId ? n.Departments.GetValueOrDefault(dId) : null,
                e.FloorId is { } fId ? n.Floors.GetValueOrDefault(fId) : null,
                Line(n, e.LineId),
                e.WorkstationId is { } wId && n.Workstations.TryGetValue(wId, out var wsCode) ? new MonRef(wId, wsCode, null) : null,
                Ref(n.Cameras, e.CameraId), Ref(n.Devices, e.DeviceId),
                e.ZoneId is { } zId && n.Zones.TryGetValue(zId, out var zone) ? new MonZone(zId, zone.Name, zone.Type) : null,
                e.TrackId,
                // Only verified identities are ever stored, so a stored worker is a verified one.
                e.WorkerId is not null && e.IdentityMethod is not null ? Ref(n.Workers, e.WorkerId) : null,
                e.IdentityMethod, e.Confidence, Json(e.Payload), e.ModelName, e.ModelVersion, e.SimulationRunId,
                e.OccurredAt, e.ReceivedAt, e.RecordedByEmail, rv?.Count ?? 0, rv?.Latest);
        }).ToList();
    }

    // ─── Alerts ────────────────────────────────────────────────────────────

    /// <summary>What the caller may do with an alert now (the endpoint still checks the permission and the service the state).</summary>
    public static AlertCan Can(AccessContext? caller, MonitoringAlert a)
    {
        if (caller is null || !AlertStatuses.IsActive(a.Status)) return new AlertCan(false, false, false, false);
        return new AlertCan(
            a.Status == AlertStatuses.Open && caller.Has(Permissions.MonitoringAlertsReview),
            a.EscalationLevel < AlertEscalationTargets.MaxLevel && caller.Has(Permissions.MonitoringAlertsEscalate),
            a.Status != AlertStatuses.Open && caller.Has(Permissions.MonitoringAlertsReview),
            caller.Has(Permissions.MonitoringAlertsDismiss));
    }

    public static async Task<IReadOnlyList<MonitoringAlertDto>> AlertDtosAsync(BunonBrainDbContext db, AccessContext? caller, IReadOnlyList<MonitoringAlert> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<MonitoringAlertDto>();
        var n = await LoadAsync(db, rows.Select(r => r.FactoryId), rows.Select(r => r.DepartmentId), rows.Select(r => r.FloorId), rows.Select(r => r.LineId),
            rows.Select(r => r.WorkstationId), rows.Select(r => r.CameraId), rows.Select(r => r.DeviceId), rows.Select(r => r.ZoneId),
            Array.Empty<Guid?>(), ct);
        return rows.Select(a => new MonitoringAlertDto(
            a.Id, AlertCode(a.Number), a.AlertType, a.Category, a.Severity, a.Title, a.Description, a.Status, a.SourceType,
            a.SourceType == MonitoringSourceTypes.Simulated, a.FactoryId, n.Factories.GetValueOrDefault(a.FactoryId, "?"),
            a.DepartmentId is { } dId ? n.Departments.GetValueOrDefault(dId) : null,
            a.FloorId is { } fId ? n.Floors.GetValueOrDefault(fId) : null,
            Line(n, a.LineId),
            a.WorkstationId is { } wId && n.Workstations.TryGetValue(wId, out var wsCode) ? new MonRef(wId, wsCode, null) : null,
            Ref(n.Cameras, a.CameraId), Ref(n.Devices, a.DeviceId),
            a.ZoneId is { } zId && n.Zones.TryGetValue(zId, out var zone) ? new MonZone(zId, zone.Name, zone.Type) : null,
            a.EventId, a.LastEventId, a.OccurrenceCount, a.FirstOccurredAt, a.LastOccurredAt, a.EscalationLevel, a.EscalatedToRole,
            a.AcknowledgedAt, a.AcknowledgedByEmail, a.EscalatedAt, a.EscalatedByEmail, a.ResolvedAt, a.ResolvedByEmail, a.ResolutionCode,
            a.ResolutionNote, a.DismissedAt, a.DismissedByEmail, a.DismissReason, Json(a.Evidence), a.CreatedAt, a.UpdatedAt, a.Version,
            Can(caller, a))).ToList();
    }

    public static AlertActionDto ActionDto(MonitoringAlertAction x) =>
        new(x.Id, x.Action, x.FromStatus, x.ToStatus, x.Note, x.EscalatedToRole, x.ResolutionCode, x.EventId, x.ActorEmail, x.ActorRole, x.At);
}
