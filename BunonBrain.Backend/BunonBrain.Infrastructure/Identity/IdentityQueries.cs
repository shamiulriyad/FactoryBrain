using System.Text.Json;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Identity;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Identity;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Identity;

/// <summary>Scope rules and DTO mapping shared by the identity services.</summary>
public static class IdentityQueries
{
    /// <summary>Identity data is people data: it needs <c>monitoring.view</c>; supervisors see their lines only.</summary>
    public static bool CanView(AccessContext caller) => caller.Has(Permissions.MonitoringView);

    public static IQueryable<WorkerTrackAssociation> InScope(BunonBrainDbContext db, AccessContext caller)
    {
        var q = db.WorkerTrackAssociations.AsNoTracking();
        if (!CanView(caller)) return q.Where(_ => false);
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

    public static bool CanSee(AccessContext caller, Guid factoryId, string? lineId) => CanView(caller) && caller.Scope switch
    {
        RoleScope.Platform => true,
        RoleScope.Factory => caller.CanAccessFactory(factoryId),
        RoleScope.Line => caller.CanAccessFactory(factoryId) && lineId is not null && caller.LineIds.Contains(lineId),
        _ => false,
    };

    /// <summary>VERIFIED, not ended, not expired, and its workstation assignment (if any) still running.</summary>
    public static IQueryable<WorkerTrackAssociation> CurrentlyVerified(BunonBrainDbContext db, IQueryable<WorkerTrackAssociation> q, DateTimeOffset now) =>
        q.Where(a => a.Status == AssociationStatuses.Verified && a.EndAt == null && a.ExpiresAt > now
                     && (a.AssignmentId == null || db.Assignments.Any(s => s.Id == a.AssignmentId && (s.EndAt == null || s.EndAt > now))));

    public static string Code(long number) => $"IA-{number:D6}";

    public static List<Guid> Guids(string json)
    {
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static List<string> Strings(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    internal sealed class Names
    {
        public Dictionary<Guid, string> Factories = new();
        public Dictionary<Guid, (string Code, string Name)> Cameras = new();
        public Dictionary<Guid, (string Code, string Name)> Workers = new();
        public Dictionary<Guid, string> Workstations = new();
        public Dictionary<Guid, (string Name, string Type)> Zones = new();
        public Dictionary<string, string> Lines = new();
        public HashSet<Guid> EndedAssignments = new();
    }

    internal static async Task<Names> NamesAsync(BunonBrainDbContext db, IReadOnlyCollection<WorkerTrackAssociation> rows, IEnumerable<Guid>? extraWorkers,
        DateTimeOffset now, CancellationToken ct)
    {
        var n = new Names();
        var fac = rows.Select(r => r.FactoryId).Distinct().ToList();
        n.Factories = await db.Factories.AsNoTracking().Where(f => fac.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.Code, ct);
        var cams = rows.Select(r => r.CameraId).Distinct().ToList();
        n.Cameras = (await db.Cameras.AsNoTracking().Where(c => cams.Contains(c.Id)).Select(c => new { c.Id, c.Code, c.Name }).ToListAsync(ct))
            .ToDictionary(c => c.Id, c => (c.Code, c.Name));
        var wk = rows.Where(r => r.WorkerId != null).Select(r => r.WorkerId!.Value).Concat(extraWorkers ?? Array.Empty<Guid>()).Distinct().ToList();
        n.Workers = (await db.Workers.AsNoTracking().Where(w => wk.Contains(w.Id)).Select(w => new { w.Id, w.WorkerCode, w.FullName }).ToListAsync(ct))
            .ToDictionary(w => w.Id, w => (w.WorkerCode, w.FullName));
        var ws = rows.Where(r => r.WorkstationId != null).Select(r => r.WorkstationId!.Value).Distinct().ToList();
        n.Workstations = await db.Workstations.AsNoTracking().Where(w => ws.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var zones = rows.Where(r => r.ZoneId != null).Select(r => r.ZoneId!.Value).Distinct().ToList();
        n.Zones = (await db.CameraZones.AsNoTracking().Where(z => zones.Contains(z.Id)).Select(z => new { z.Id, z.Name, z.ZoneType }).ToListAsync(ct))
            .ToDictionary(z => z.Id, z => (z.Name, z.ZoneType));
        var lines = rows.Where(r => r.LineId != null).Select(r => r.LineId!).Distinct().ToList();
        n.Lines = await db.ProductionLines.AsNoTracking().Where(l => lines.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var asg = rows.Where(r => r.AssignmentId != null).Select(r => r.AssignmentId!.Value).Distinct().ToList();
        n.EndedAssignments = (await db.Assignments.AsNoTracking().Where(a => asg.Contains(a.Id) && a.EndAt != null && a.EndAt <= now)
            .Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        return n;
    }

    internal static bool IsCurrent(WorkerTrackAssociation a, Names n, DateTimeOffset now) =>
        a.Status == AssociationStatuses.Verified && a.EndAt is null && a.ExpiresAt > now && (a.AssignmentId is null || !n.EndedAssignments.Contains(a.AssignmentId.Value));

    internal static AssociationDto ToDto(WorkerTrackAssociation a, Names n, DateTimeOffset now)
    {
        var current = IsCurrent(a, n, now);
        MonRef? Worker(Guid? id) => id is { } w && n.Workers.TryGetValue(w, out var x) ? new MonRef(w, x.Code, x.Name) : null;
        var cam = n.Cameras.TryGetValue(a.CameraId, out var c) ? new MonRef(a.CameraId, c.Code, c.Name) : new MonRef(a.CameraId, "?", null);
        return new AssociationDto(a.Id, Code(a.Number), a.FactoryId, n.Factories.GetValueOrDefault(a.FactoryId, "?"), cam, a.TrackId, a.Status, current,
            current ? Worker(a.WorkerId) : null,
            a.WorkstationId is { } ws ? new MonRef(ws, n.Workstations.GetValueOrDefault(ws, "?"), null) : null,
            a.ZoneId is { } z && n.Zones.TryGetValue(z, out var zi) ? new MonZone(z, zi.Name, zi.Type) : null,
            a.LineId is null ? null : new MonLine(a.LineId, n.Lines.GetValueOrDefault(a.LineId, a.LineId)),
            a.VerificationMethod, a.ReasonCode, AssociationReasons.Describe(a.ReasonCode), a.Summary, a.IsSimulated,
            AssociationStatuses.NeedsReview.Contains(a.Status) && a.EndAt is null, Guids(a.CandidateWorkers).Count,
            a.StartAt, a.EndAt, a.ExpiresAt, a.UpdatedAt, a.Version);
    }

    internal static async Task<IReadOnlyList<AssociationDto>> DtosAsync(BunonBrainDbContext db, IReadOnlyList<WorkerTrackAssociation> rows, DateTimeOffset now,
        CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<AssociationDto>();
        var n = await NamesAsync(db, rows, null, now, ct);
        return rows.Select(r => ToDto(r, n, now)).ToList();
    }

    internal static async Task<IReadOnlyList<RfidReadDto>> ReadDtosAsync(BunonBrainDbContext db, IReadOnlyList<RfidReadEvent> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<RfidReadDto>();
        var devices = rows.Select(r => r.DeviceId).Distinct().ToList();
        var codes = await db.Devices.AsNoTracking().Where(d => devices.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Code, ct);
        var cards = rows.Where(r => r.CardId != null).Select(r => r.CardId!.Value).Distinct().ToList();
        var labels = await db.WorkerCards.AsNoTracking().Where(c => cards.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Label, ct);
        var wk = rows.Where(r => r.WorkerId != null).Select(r => r.WorkerId!.Value).Distinct().ToList();
        var workers = (await db.Workers.AsNoTracking().Where(w => wk.Contains(w.Id)).Select(w => new { w.Id, w.WorkerCode, w.FullName }).ToListAsync(ct))
            .ToDictionary(w => w.Id);
        return rows.Select(r => new RfidReadDto(r.Id, r.FactoryId, r.DeviceId, codes.GetValueOrDefault(r.DeviceId, "?"), CardIdentifiers.Masked(r.CardSuffix),
            r.CardId is { } cid ? labels.GetValueOrDefault(cid) : null,
            r.WorkerId is { } w && workers.TryGetValue(w, out var x) ? new MonRef(w, x.WorkerCode, x.FullName) : null,
            r.Source, r.IsSimulated, r.OccurredAt, r.ReceivedAt, r.Outcome, r.OutcomeCode, r.Evaluation, r.EvaluationCode,
            r.EvaluationCode is null ? null : AssociationReasons.Describe(r.EvaluationCode), r.EvaluatedAt)).ToList();
    }
}
