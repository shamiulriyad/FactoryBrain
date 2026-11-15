using BunonBrain.Application.Identity;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Identity;

/// <summary>
/// Gathers, from the database, the facts the rule engine judges a read by,
/// as they were at the time of the read: the reader mapping in force, the
/// worker's assignment, other workers assigned to / read at the same
/// location, the camera zones of the workstation and the tracks observed in
/// them (from the computer-vision service's monitoring events), and the
/// worker's currently valid associations. It decides nothing.
/// </summary>
public sealed class IdentityEvidenceEvaluator
{
    private readonly BunonBrainDbContext _db;
    public IdentityEvidenceEvaluator(BunonBrainDbContext db) { _db = db; }

    /// <summary>The workstation a mapping places readers at (null: the mapping is broader than one workstation).</summary>
    public async Task<(MappingFact? Fact, List<CameraZone> Zones)> MappingAsync(Guid? mappingId, CancellationToken ct)
    {
        if (mappingId is null) return (null, new());
        var m = await _db.RfidReaderMappings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mappingId, ct);
        if (m is null) return (null, new());
        Guid? ws = null;
        var zones = new List<CameraZone>();
        if (m.Precision == MappingPrecisions.Workstation && m.WorkstationId is { } w)
        {
            ws = w;
            zones = await _db.CameraZones.AsNoTracking().Where(z => z.WorkstationId == w && z.IsActive && z.FactoryId == m.FactoryId).ToListAsync(ct);
        }
        else if (m.Precision == MappingPrecisions.Zone && m.CameraZoneId is { } zid)
        {
            var z = await _db.CameraZones.AsNoTracking().FirstOrDefaultAsync(x => x.Id == zid && x.IsActive, ct);
            if (z is not null) { zones.Add(z); ws = z.WorkstationId; }
        }
        var activeCameras = await _db.Cameras.AsNoTracking().Where(c => c.Status == "Active").Select(c => c.Id).ToListAsync(ct);
        zones = zones.Where(z => activeCameras.Contains(z.CameraId)).ToList();
        var code = ws is { } id ? await _db.Workstations.AsNoTracking().Where(x => x.Id == id).Select(x => x.Code).FirstOrDefaultAsync(ct) : null;
        return (new MappingFact(m.Id, m.Precision, ws, code, m.CameraZoneId), zones);
    }

    public async Task<ReadFacts?> FactsAsync(RfidReadEvent read, IdentityOptions o, DateTimeOffset now, CancellationToken ct)
    {
        if (read.WorkerId is not { } worker) return null;
        var at = read.OccurredAt;
        var (mapping, zones) = await MappingAsync(read.MappingId, ct);
        var ws = mapping?.WorkstationId;

        // The worker's assignment at the time of the read (the one at this workstation if there is one).
        var assignments = await _db.Assignments.AsNoTracking()
            .Where(a => a.WorkerId == worker && a.StartAt <= at && (a.EndAt == null || a.EndAt > at)).ToListAsync(ct);
        var asg = assignments.FirstOrDefault(a => a.WorkstationId == ws) ?? assignments.OrderByDescending(a => a.IsPrimary).FirstOrDefault();
        AssignmentFact? assignment = null;
        if (asg is not null)
        {
            var code = await _db.Workstations.AsNoTracking().Where(x => x.Id == asg.WorkstationId).Select(x => x.Code).FirstOrDefaultAsync(ct) ?? "?";
            assignment = new AssignmentFact(asg.Id, asg.WorkstationId, code);
        }

        var otherAssigned = 0;
        var otherReads = new List<OtherReadFact>();
        if (ws is { } w)
        {
            otherAssigned = await _db.Assignments.AsNoTracking()
                .Where(a => a.WorkstationId == w && a.WorkerId != worker && a.StartAt <= at && (a.EndAt == null || a.EndAt > at))
                .Select(a => a.WorkerId).Distinct().CountAsync(ct);
            // Reads at the same location: any mapping (then or now) of this workstation or of one of its zones.
            var zoneIds = await _db.CameraZones.AsNoTracking().Where(z => z.WorkstationId == w).Select(z => z.Id).ToListAsync(ct);
            var mappingIds = await _db.RfidReaderMappings.AsNoTracking()
                .Where(m => m.WorkstationId == w || (m.CameraZoneId != null && zoneIds.Contains(m.CameraZoneId.Value))).Select(m => m.Id).ToListAsync(ct);
            var lo = at - o.Before;
            var hi = at + o.After;
            otherReads = await _db.RfidReadEvents.AsNoTracking()
                .Where(r => r.FactoryId == read.FactoryId && r.Id != read.Id && r.Outcome == RfidReadOutcomes.Accepted && r.WorkerId != null
                            && r.WorkerId != worker && r.IsSimulated == read.IsSimulated && r.MappingId != null && mappingIds.Contains(r.MappingId.Value)
                            && r.OccurredAt >= lo && r.OccurredAt <= hi)
                .Select(r => new OtherReadFact(r.Id, r.WorkerId!.Value, r.OccurredAt)).ToListAsync(ct);
        }

        var zoneFacts = new List<CameraZoneFact>();
        foreach (var z in zones)
            zoneFacts.Add(await ZoneFactAsync(z, at, o, now, ct));

        var workerVerified = await IdentityQueries.CurrentlyVerified(_db, _db.WorkerTrackAssociations.AsNoTracking(), now)
            .Where(a => a.WorkerId == worker)
            .Select(a => new WorkerVerifiedFact(a.Id, a.CameraId, a.TrackId, a.WorkstationId)).ToListAsync(ct);
        return new ReadFacts(read.Id, worker, read.IsSimulated, at, mapping, assignment, otherAssigned, otherReads, zoneFacts, workerVerified);
    }

    /// <summary>
    /// The tracks in a zone around a moment, from the monitoring events the
    /// computer-vision service (or the monitoring simulator) sent: each
    /// track's presence runs from ZONE_ENTERED to its next ZONE_EXITED or
    /// PERSON_LOST (or is still open).
    /// </summary>
    private async Task<CameraZoneFact> ZoneFactAsync(CameraZone z, DateTimeOffset at, IdentityOptions o, DateTimeOffset now, CancellationToken ct)
    {
        var from = at - o.Validity;
        var to = at + o.After;
        var cam = await _db.Cameras.AsNoTracking().Where(c => c.Id == z.CameraId).Select(c => new { c.Code, c.LineId }).FirstAsync(ct);
        var zoneEvents = await _db.MonitoringEvents.AsNoTracking()
            .Where(e => e.CameraId == z.CameraId && e.ZoneId == z.Id && e.TrackId != null && e.OccurredAt >= from && e.OccurredAt <= to
                        && (e.EventType == MonitoringEventTypes.ZoneEntered || e.EventType == MonitoringEventTypes.ZoneExited))
            .Select(e => new { e.Id, e.EventType, e.TrackId, e.OccurredAt, e.SourceType }).ToListAsync(ct);
        var tracks = zoneEvents.Select(e => e.TrackId!).Distinct().ToList();
        var lost = await _db.MonitoringEvents.AsNoTracking()
            .Where(e => e.CameraId == z.CameraId && e.TrackId != null && tracks.Contains(e.TrackId) && e.EventType == MonitoringEventTypes.PersonLost
                        && e.OccurredAt >= from)
            .Select(e => new { e.Id, e.EventType, e.TrackId, e.OccurredAt, e.SourceType }).ToListAsync(ct);
        var verified = await IdentityQueries.CurrentlyVerified(_db, _db.WorkerTrackAssociations.AsNoTracking(), now)
            .Where(a => a.CameraId == z.CameraId && tracks.Contains(a.TrackId)).Select(a => new { a.TrackId, a.WorkerId, a.Id }).ToListAsync(ct);

        var facts = new List<TrackFact>();
        foreach (var g in zoneEvents.Concat(lost).GroupBy(e => e.TrackId!))
        {
            DateTimeOffset? enteredAt = null;
            string? enteredRef = null;
            var simulated = false;
            (DateTimeOffset In, DateTimeOffset? Out, string Ref, bool Sim)? best = null;
            foreach (var e in g.OrderBy(e => e.OccurredAt).ThenBy(e => e.EventType == MonitoringEventTypes.ZoneEntered ? 0 : 1))
            {
                if (e.EventType == MonitoringEventTypes.ZoneEntered)
                {
                    if (enteredAt is null) { enteredAt = e.OccurredAt; enteredRef = e.Id.ToString(); simulated = e.SourceType == MonitoringSourceTypes.Simulated; }
                }
                else if (enteredAt is not null)
                {
                    best = Pick(best, (enteredAt.Value, e.OccurredAt, enteredRef!, simulated), at);
                    enteredAt = null;
                }
            }
            if (enteredAt is not null) best = Pick(best, (enteredAt.Value, null, enteredRef!, simulated), at);
            if (best is { } b)
            {
                var v = verified.FirstOrDefault(x => x.TrackId == g.Key);
                facts.Add(new TrackFact(g.Key, b.Sim, b.In, b.Out, b.Ref, v?.WorkerId, v?.Id));
            }
        }
        return new CameraZoneFact(z.CameraId, cam.Code, z.Id, z.Name, z.LineId ?? cam.LineId, facts);
    }

    /// <summary>Of a track's presence intervals, the one closest to the read.</summary>
    private static (DateTimeOffset In, DateTimeOffset? Out, string Ref, bool Sim) Pick(
        (DateTimeOffset In, DateTimeOffset? Out, string Ref, bool Sim)? current, (DateTimeOffset In, DateTimeOffset? Out, string Ref, bool Sim) next, DateTimeOffset at)
    {
        static double Distance((DateTimeOffset In, DateTimeOffset? Out, string Ref, bool Sim) x, DateTimeOffset at) =>
            at < x.In ? (x.In - at).TotalSeconds : x.Out is { } o && at > o ? (at - o).TotalSeconds : 0;
        return current is null || Distance(next, at) < Distance(current.Value, at) ? next : current.Value;
    }
}
