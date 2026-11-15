using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Identity;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Identity;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Monitoring;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BunonBrain.Infrastructure.Identity;

/// <summary>
/// The identity association engine's state keeper (see
/// <see cref="IIdentityAssociationService"/>). Evaluations of one factory are
/// serialised with a transaction-level advisory lock, and the database's
/// unique indexes (one VERIFIED association per track, one per worker and
/// camera) make two simultaneous requests unable to verify conflicting
/// identities even if the lock were bypassed: the loser re-evaluates and
/// sees the winner's association as evidence.
/// </summary>
public sealed class IdentityAssociationService : IIdentityAssociationService
{
    public const string EngineEmail = "system:identity-engine";
    public const string OverviewNote =
        "A Worker ID is shown only for a VERIFIED, currently valid association: an active RFID card read at a reader mapped to the worker's " +
        "assigned workstation, with exactly one camera track in that workstation's zone. Track ids are temporary camera numbers. " +
        "No face, appearance or biometric data is used, and nothing here is a performance measure.";

    private readonly BunonBrainDbContext _db;
    private readonly IdentityEvidenceEvaluator _evidence;
    private readonly IIdentityPublisher _publisher;
    private readonly IdentityOptions _o;
    private readonly IRfidReaderService _readers;
    private readonly ILogger<IdentityAssociationService> _log;

    public IdentityAssociationService(BunonBrainDbContext db, IdentityEvidenceEvaluator evidence, IIdentityPublisher publisher, IOptions<IdentityOptions> options,
        IRfidReaderService readers, ILogger<IdentityAssociationService> log)
    {
        _db = db; _evidence = evidence; _publisher = publisher; _o = options.Value; _readers = readers; _log = log;
    }

    internal static Task LockFactoryAsync(BunonBrainDbContext db, Guid factoryId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({"identity:" + factoryId.ToString("N")}))", ct);

    // ─── Evaluation ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AssociationDto>> EvaluateReadAsync(long readId, bool final, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var changed = await EvaluateOnceAsync(readId, final, ct);
                return await PublishAsync(changed, ct);
            }
            catch (DbUpdateException ex) when (attempt == 0 && ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg
                                               && pg.ConstraintName is WorkerTrackAssociationConfiguration.OneVerifiedPerTrack
                                                   or WorkerTrackAssociationConfiguration.OneVerifiedPerWorkerCamera)
            {
                // Another request verified this track / worker at the same moment: evaluate again with that as evidence.
                _db.ChangeTracker.Clear();
                _log.LogInformation("Identity evaluation of read {ReadId} lost a race ({Constraint}); re-evaluating.", readId, pg.ConstraintName);
            }
        }
    }

    private async Task<List<(WorkerTrackAssociation A, string Change)>> EvaluateOnceAsync(long readId, bool final, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            var factory = await _db.RfidReadEvents.AsNoTracking().Where(r => r.Id == readId).Select(r => (Guid?)r.FactoryId).FirstOrDefaultAsync(ct);
            if (factory is null) return new List<(WorkerTrackAssociation, string)>();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockFactoryAsync(_db, factory.Value, ct);
            var read = await _db.RfidReadEvents.FirstAsync(r => r.Id == readId, ct);
            var changed = new List<(WorkerTrackAssociation, string)>();
            if (read.Outcome != RfidReadOutcomes.Accepted || read.Evaluation != AssociationStatuses.Pending)
            {
                await tx.RollbackAsync(ct);
                return changed;
            }
            var now = DateTimeOffset.UtcNow;
            final = final || now > read.OccurredAt + _o.After;
            var facts = await _evidence.FactsAsync(read, _o, now, ct);
            if (facts is null)
            {
                await tx.RollbackAsync(ct);
                return changed;
            }
            var d = AssociationRuleEngine.Evaluate(facts, _o, final);

            foreach (var (id, reason) in d.Suspend.DistinctBy(s => s.AssociationId))
            {
                var a = await _db.WorkerTrackAssociations.FirstOrDefaultAsync(x => x.Id == id && x.Status == AssociationStatuses.Verified, ct);
                if (a is null) continue;
                Transition(a, AssociationStatuses.ManualReview, "SUSPENDED", reason, now, null);
                Evidence(a, new EvidenceItem(EvidenceKinds.Conflict, EvidenceEffects.Contradicts, "rfid_read", read.Id.ToString(),
                    AssociationReasons.Describe(reason), read.OccurredAt), now);
                changed.Add((a, "suspended"));
            }

            foreach (var t in d.Tracks)
            {
                if (t.RefreshAssociationId is { } refreshId)
                {
                    var a = await _db.WorkerTrackAssociations.FirstOrDefaultAsync(x => x.Id == refreshId && x.Status == AssociationStatuses.Verified, ct);
                    if (a is null) continue;
                    var expires = read.OccurredAt + _o.Validity;
                    if (expires > a.ExpiresAt) a.ExpiresAt = expires;
                    a.UpdatedAt = now;
                    a.Version++;
                    History(a, "REFRESHED", a.Status, a.Status, AssociationReasons.Describe(AssociationReasons.Refreshed) + $" Valid until {a.ExpiresAt:u}.", now, null);
                    Evidence(a, d.ReadEvidence[0], now);
                    changed.Add((a, "refreshed"));
                    continue;
                }
                if (await _db.WorkerTrackAssociations.AnyAsync(x => x.RfidReadEventId == read.Id && x.CameraId == t.CameraId && x.TrackId == t.TrackId, ct))
                    continue;
                var verified = t.Status == AssociationStatuses.Verified;
                var assoc = new WorkerTrackAssociation
                {
                    FactoryId = read.FactoryId, CameraId = t.CameraId, TrackId = t.TrackId, WorkerId = verified ? facts.WorkerId : null,
                    WorkstationId = facts.Mapping?.WorkstationId, ZoneId = t.ZoneId, LineId = t.LineId, Status = t.Status,
                    VerificationMethod = verified ? VerificationMethods.RfidWorkstation : null, ReasonCode = t.Reason,
                    Summary = Truncate(Summary(t, facts), 400), RfidReadEventId = read.Id, AssignmentId = facts.Assignment?.Id,
                    CandidateWorkers = JsonSerializer.Serialize(t.CandidateWorkers), CandidateTracks = JsonSerializer.Serialize(t.CandidateTracks),
                    IsSimulated = read.IsSimulated, StartAt = read.OccurredAt, ExpiresAt = read.OccurredAt + _o.Validity, CreatedAt = now, UpdatedAt = now,
                };
                _db.WorkerTrackAssociations.Add(assoc);
                History(assoc, "CREATED", null, t.Status, AssociationReasons.Describe(t.Reason), now, null);
                foreach (var e in t.Evidence) Evidence(assoc, e, now);
                changed.Add((assoc, "created"));
            }

            if (d.Evaluation != AssociationStatuses.Pending)
            {
                read.Evaluation = d.Evaluation;
                read.EvaluationCode = d.Reason;
                read.EvaluatedAt = now;
            }
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return changed;
        });
    }

    private static string Summary(TrackDecision t, ReadFacts f) => t.Status switch
    {
        AssociationStatuses.Verified => $"Track {t.TrackId} verified at workstation {f.Mapping?.WorkstationCode}: card read, assignment and a single track in the zone agree.",
        AssociationStatuses.Ambiguous => $"Track {t.TrackId} at {f.Mapping?.WorkstationCode}: {AssociationReasons.Describe(t.Reason)} Manual review required.",
        AssociationStatuses.ManualReview => $"Track {t.TrackId} at {f.Mapping?.WorkstationCode}: {AssociationReasons.Describe(t.Reason)}",
        _ => $"Track {t.TrackId}: {AssociationReasons.Describe(t.Reason)}",
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    internal void Transition(WorkerTrackAssociation a, string to, string action, string reasonCode, DateTimeOffset now, AccessContext? reviewer,
        string? note = null)
    {
        var from = a.Status;
        a.Status = to;
        a.ReasonCode = reasonCode;
        a.UpdatedAt = now;
        a.Version++;
        if (to is AssociationStatuses.Expired or AssociationStatuses.Revoked or AssociationStatuses.Unverified) a.EndAt ??= now;
        History(a, action, from, to, note ?? AssociationReasons.Describe(reasonCode), now, reviewer);
    }

    private void History(WorkerTrackAssociation a, string action, string? from, string to, string reason, DateTimeOffset now, AccessContext? reviewer) =>
        _db.IdentityAssociationHistory.Add(new IdentityAssociationHistory
        {
            AssociationId = a.Id, FactoryId = a.FactoryId, Action = action, FromStatus = from, ToStatus = to, WorkerId = a.WorkerId,
            Reason = Truncate(reason, 1000), IsManual = reviewer is not null, ActorUserId = reviewer?.UserId, ActorEmail = reviewer?.Email ?? EngineEmail,
            ActorRole = reviewer?.PrimaryRole, At = now,
        });

    internal void Evidence(WorkerTrackAssociation a, EvidenceItem e, DateTimeOffset now) =>
        _db.IdentityAssociationEvidence.Add(new IdentityAssociationEvidence
        {
            AssociationId = a.Id, FactoryId = a.FactoryId, Kind = e.Kind, Effect = e.Effect, RefType = e.RefType, RefId = e.RefId,
            Detail = Truncate(e.Detail, 500), ObservedAt = e.ObservedAt, RecordedAt = now,
        });

    internal async Task<IReadOnlyList<AssociationDto>> PublishAsync(List<(WorkerTrackAssociation A, string Change)> changed, CancellationToken ct)
    {
        if (changed.Count == 0) return Array.Empty<AssociationDto>();
        var now = DateTimeOffset.UtcNow;
        var dtos = await IdentityQueries.DtosAsync(_db, changed.Select(c => c.A).ToList(), now, ct);
        for (var i = 0; i < dtos.Count; i++)
            await _publisher.AssociationChangedAsync(dtos[i], changed[i].Change, ct);
        return dtos;
    }

    // ─── Keeping associations valid ───────────────────────────────────────

    public async Task<int> MaintainAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var count = 0;
        // Reads whose window has closed get their final answer.
        var due = now - _o.After;
        var pending = await _db.RfidReadEvents.AsNoTracking()
            .Where(r => r.Evaluation == AssociationStatuses.Pending && r.OccurredAt < due).OrderBy(r => r.Id).Select(r => r.Id).Take(200).ToListAsync(ct);
        foreach (var id in pending)
        {
            await EvaluateReadAsync(id, final: true, ct);
            count++;
        }
        // Verified associations that ran out, lost their track or their assignment.
        var verified = await _db.WorkerTrackAssociations.AsNoTracking()
            .Where(a => a.Status == AssociationStatuses.Verified && a.EndAt == null).Select(a => new { a.Id, a.FactoryId }).Take(1000).ToListAsync(ct);
        foreach (var f in verified.GroupBy(v => v.FactoryId))
            count += await ExpireAsync(f.Key, f.Select(x => x.Id).ToList(), now, ct);
        return count;
    }

    private async Task<int> ExpireAsync(Guid factoryId, List<Guid> ids, DateTimeOffset now, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var changed = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await LockFactoryAsync(_db, factoryId, ct);
            var rows = await _db.WorkerTrackAssociations.Where(a => ids.Contains(a.Id) && a.Status == AssociationStatuses.Verified && a.EndAt == null).ToListAsync(ct);
            var asgIds = rows.Where(r => r.AssignmentId != null).Select(r => r.AssignmentId!.Value).ToList();
            var ended = await _db.Assignments.AsNoTracking().Where(a => asgIds.Contains(a.Id) && a.EndAt != null && a.EndAt <= now)
                .ToDictionaryAsync(a => a.Id, a => a.EndAt!.Value, ct);
            var list = new List<(WorkerTrackAssociation, string)>();
            foreach (var a in rows)
            {
                var lostAt = await _db.MonitoringEvents.AsNoTracking()
                    .Where(e => e.CameraId == a.CameraId && e.TrackId == a.TrackId && e.EventType == MonitoringEventTypes.PersonLost && e.OccurredAt >= a.StartAt)
                    .OrderBy(e => e.OccurredAt).Select(e => (DateTimeOffset?)e.OccurredAt).FirstOrDefaultAsync(ct);
                string? reason = lostAt is not null ? AssociationReasons.TrackLost
                    : a.AssignmentId is { } asg && ended.ContainsKey(asg) ? AssociationReasons.AssignmentEnded
                    : a.ExpiresAt <= now ? AssociationReasons.ValidityElapsed : null;
                if (reason is null) continue;
                a.EndAt = reason switch
                {
                    AssociationReasons.TrackLost => lostAt,
                    AssociationReasons.AssignmentEnded => ended[a.AssignmentId!.Value],
                    _ => a.ExpiresAt,
                };
                Transition(a, AssociationStatuses.Expired, "EXPIRED", reason, now, null);
                list.Add((a, "expired"));
            }
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return list;
        });
        await PublishAsync(changed, ct);
        return changed.Count;
    }

    /// <summary>A track was lost: its verified association ends now (called from the track observation processor).</summary>
    internal async Task<int> TrackLostAsync(Guid factoryId, Guid cameraId, string trackId, CancellationToken ct)
    {
        var ids = await _db.WorkerTrackAssociations.AsNoTracking()
            .Where(a => a.CameraId == cameraId && a.TrackId == trackId && a.Status == AssociationStatuses.Verified && a.EndAt == null)
            .Select(a => a.Id).ToListAsync(ct);
        return ids.Count == 0 ? 0 : await ExpireAsync(factoryId, ids, DateTimeOffset.UtcNow, ct);
    }

    // ─── Queries ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<TrackIdentityDto>> TracksAsync(AccessContext caller, Guid cameraId, CancellationToken ct) =>
        (await TrackIdentitiesAsync(caller, null, null, cameraId, ct));

    private async Task<List<TrackIdentityDto>> TrackIdentitiesAsync(AccessContext caller, Guid? factoryId, string? lineId, Guid? cameraId, CancellationToken ct)
    {
        if (!IdentityQueries.CanView(caller)) return new();
        var now = DateTimeOffset.UtcNow;
        var since = now.AddMinutes(-Math.Clamp(_o.RecentTrackMinutes, 1, 120));
        var events = MonitoringQueries.EventsInScope(_db, caller)
            .Where(e => e.Category == MonitoringCategories.Vision && e.CameraId != null && e.TrackId != null && e.OccurredAt >= since
                        && (e.EventType == MonitoringEventTypes.PersonDetected || e.EventType == MonitoringEventTypes.TrackUpdated
                            || e.EventType == MonitoringEventTypes.ZoneEntered || e.EventType == MonitoringEventTypes.ZoneExited
                            || e.EventType == MonitoringEventTypes.PersonLost));
        if (factoryId is { } f) events = events.Where(e => e.FactoryId == f);
        if (!string.IsNullOrWhiteSpace(lineId)) { var l = lineId.Trim(); events = events.Where(e => e.LineId == l); }
        if (cameraId is { } c) events = events.Where(e => e.CameraId == c);
        var rows = await events.OrderBy(e => e.OccurredAt).Take(5000)
            .Select(e => new { CameraId = e.CameraId!.Value, TrackId = e.TrackId!, e.EventType, e.OccurredAt, e.ZoneId, e.SourceType }).ToListAsync(ct);
        var tracks = rows.GroupBy(r => (r.CameraId, r.TrackId))
            .Where(g => g.Last().EventType != MonitoringEventTypes.PersonLost)
            .Select(g =>
            {
                Guid? zone = null;
                foreach (var e in g)
                    if (e.EventType == MonitoringEventTypes.ZoneEntered) zone = e.ZoneId;
                    else if (e.EventType == MonitoringEventTypes.ZoneExited && e.ZoneId == zone) zone = null;
                return (g.Key.CameraId, g.Key.TrackId, Last: g.Last().OccurredAt, Zone: zone, Sim: g.Last().SourceType == MonitoringSourceTypes.Simulated);
            }).ToList();
        if (tracks.Count == 0) return new();
        var cams = tracks.Select(t => t.CameraId).Distinct().ToList();
        var trackIds = tracks.Select(t => t.TrackId).Distinct().ToList();
        var assocs = await IdentityQueries.InScope(_db, caller)
            .Where(a => cams.Contains(a.CameraId) && trackIds.Contains(a.TrackId) && a.EndAt == null).ToListAsync(ct);
        var names = await IdentityQueries.NamesAsync(_db, assocs, null, now, ct);
        var camNames = await _db.Cameras.AsNoTracking().Where(x => cams.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, ct);
        var zoneIds = tracks.Where(t => t.Zone != null).Select(t => t.Zone!.Value).Distinct().ToList();
        var zones = await _db.CameraZones.AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, z => new MonZone(z.Id, z.Name, z.ZoneType), ct);
        return tracks.Select(t =>
        {
            var mine = assocs.Where(a => a.CameraId == t.CameraId && a.TrackId == t.TrackId).ToList();
            var current = mine.FirstOrDefault(a => IdentityQueries.IsCurrent(a, names, now));
            var open = mine.Where(a => AssociationStatuses.NeedsReview.Contains(a.Status)).OrderByDescending(a => a.UpdatedAt).FirstOrDefault();
            var zone = t.Zone is { } z ? zones.GetValueOrDefault(z) : null;
            if (current is not null)
            {
                var dto = IdentityQueries.ToDto(current, names, now);
                return new TrackIdentityDto(t.CameraId, camNames.GetValueOrDefault(t.CameraId, "?"), t.TrackId, t.Last, zone, t.Sim, AssociationStatuses.Verified,
                    dto.Worker, dto.Workstation, current.Id, IdentityQueries.Code(current.Number), "Verified by RFID card, workstation assignment and camera zone.");
            }
            if (open is not null)
                return new TrackIdentityDto(t.CameraId, camNames.GetValueOrDefault(t.CameraId, "?"), t.TrackId, t.Last, zone, t.Sim, open.Status, null,
                    null, open.Id, IdentityQueries.Code(open.Number), "Manual review required.");
            return new TrackIdentityDto(t.CameraId, camNames.GetValueOrDefault(t.CameraId, "?"), t.TrackId, t.Last, zone, t.Sim, AssociationStatuses.Unverified,
                null, null, null, null, "Worker not confirmed.");
        }).OrderBy(t => t.CameraCode).ThenBy(t => t.TrackId).ToList();
    }

    public async Task<IdentityOverviewDto> OverviewAsync(AccessContext caller, Guid? factoryId, string? lineId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var tracks = await TrackIdentitiesAsync(caller, factoryId, lineId, null, ct);
        var scope = IdentityQueries.InScope(_db, caller);
        if (factoryId is { } f) scope = scope.Where(a => a.FactoryId == f);
        if (!string.IsNullOrWhiteSpace(lineId)) { var l = lineId.Trim(); scope = scope.Where(a => a.LineId == l); }
        var verifiedNow = await IdentityQueries.CurrentlyVerified(_db, scope, now).CountAsync(ct);
        var review = await scope.Where(a => AssociationStatuses.NeedsReview.Contains(a.Status) && a.EndAt == null)
            .OrderByDescending(a => a.UpdatedAt).Take(20).ToListAsync(ct);
        var reviewCount = await scope.CountAsync(a => AssociationStatuses.NeedsReview.Contains(a.Status) && a.EndAt == null, ct);
        var scopeIds = scope.Select(a => a.Id);
        var history = await _db.IdentityAssociationHistory.AsNoTracking().Where(h => scopeIds.Contains(h.AssociationId))
            .OrderByDescending(h => h.At).ThenByDescending(h => h.Id).Take(30).ToListAsync(ct);
        var histAssoc = history.Select(h => h.AssociationId).Distinct().ToList();
        var hRows = await _db.WorkerTrackAssociations.AsNoTracking().Where(a => histAssoc.Contains(a.Id))
            .Select(a => new { a.Id, a.Number, a.CameraId, a.TrackId, a.IsSimulated }).ToDictionaryAsync(a => a.Id, ct);
        var hCams = hRows.Values.Select(v => v.CameraId).Distinct().ToList();
        var camCodes = await _db.Cameras.AsNoTracking().Where(c => hCams.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Code, ct);
        var changes = history.Select(h =>
        {
            var a = hRows[h.AssociationId];
            return new AssociationChangeDto(h.Id, h.AssociationId, IdentityQueries.Code(a.Number), camCodes.GetValueOrDefault(a.CameraId, "?"), a.TrackId,
                h.Action, h.FromStatus, h.ToStatus, h.Reason, h.IsManual, h.ActorEmail, h.At, a.IsSimulated);
        }).ToList();
        var readers = await _readers.ReadersAsync(caller, factoryId, ct);
        var reads = ReadsScope(caller, factoryId);
        var pendingReads = await reads.CountAsync(r => r.Evaluation == AssociationStatuses.Pending, ct);
        var recentReads = await IdentityQueries.ReadDtosAsync(_db, await reads.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id).Take(15).ToListAsync(ct), ct);
        var counts = new IdentityCounts(verifiedNow, tracks.Count(t => t.Identity == AssociationStatuses.Unverified),
            tracks.Count(t => AssociationStatuses.NeedsReview.Contains(t.Identity)), reviewCount, pendingReads,
            readers.Count(r => r.Health == "Online"), readers.Count);
        return new IdentityOverviewDto(now, counts, tracks.Take(200).ToList(), await IdentityQueries.DtosAsync(_db, review, now, ct), changes, readers,
            recentReads, OverviewNote);
    }

    private IQueryable<RfidReadEvent> ReadsScope(AccessContext caller, Guid? factoryId)
    {
        var q = _db.RfidReadEvents.AsNoTracking();
        if (!caller.HasAny(Permissions.MonitoringView, Permissions.DevicesView)) return q.Where(_ => false);
        if (!caller.IsPlatform) { var mine = caller.FactoryIds.ToList(); q = q.Where(r => mine.Contains(r.FactoryId)); }
        if (caller.IsLineScoped)
        {
            var lines = caller.LineIds.ToList();
            q = q.Where(r => _db.RfidReaderMappings.Any(m => m.Id == r.MappingId && m.LineId != null && lines.Contains(m.LineId)));
        }
        if (factoryId is { } f) q = q.Where(r => r.FactoryId == f);
        return q;
    }

    public async Task<PagedResult<RfidReadDto>> ReadsAsync(AccessContext caller, Guid? factoryId, Guid? deviceId, int page, int pageSize, CancellationToken ct)
    {
        var q = ReadsScope(caller, factoryId);
        if (deviceId is { } d) q = q.Where(r => r.DeviceId == d);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(r => r.OccurredAt).ThenByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<RfidReadDto>(await IdentityQueries.ReadDtosAsync(_db, rows, ct), page, pageSize, total);
    }

    public async Task<PagedResult<AssociationDto>> ListAsync(AccessContext caller, AssociationQuery q, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = IdentityQueries.InScope(_db, caller);
        if (q.FactoryId is { } f) rows = rows.Where(a => a.FactoryId == f);
        if (q.CameraId is { } c) rows = rows.Where(a => a.CameraId == c);
        if (!string.IsNullOrWhiteSpace(q.TrackId)) { var t = q.TrackId.Trim(); rows = rows.Where(a => a.TrackId == t); }
        if (q.WorkerId is { } w) rows = rows.Where(a => a.WorkerId == w);
        if (!string.IsNullOrWhiteSpace(q.LineId)) { var l = q.LineId.Trim(); rows = rows.Where(a => a.LineId == l); }
        if (AssociationStatuses.Parse(q.Status) is { } s) rows = rows.Where(a => a.Status == s);
        if (q.ReviewOnly == true) rows = rows.Where(a => AssociationStatuses.NeedsReview.Contains(a.Status) && a.EndAt == null);
        if (q.From is { } from) { var u = from.ToUniversalTime(); rows = rows.Where(a => a.UpdatedAt >= u); }
        if (q.To is { } to) { var u = to.ToUniversalTime(); rows = rows.Where(a => a.CreatedAt <= u); }
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        var total = await rows.CountAsync(ct);
        var list = await rows.OrderByDescending(a => a.UpdatedAt).ThenByDescending(a => a.Number).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<AssociationDto>(await IdentityQueries.DtosAsync(_db, list, now, ct), page, size, total);
    }

    public async Task<AccessResult<AssociationDetailDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var a = await IdentityQueries.InScope(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return AccessResult<AssociationDetailDto>.NotFound("association_not_found", "Identity association not found.");
        return AccessResult<AssociationDetailDto>.Ok(await DetailAsync(caller, a, ct));
    }

    internal async Task<AssociationDetailDto> DetailAsync(AccessContext caller, WorkerTrackAssociation a, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = IdentityQueries.Guids(a.CandidateWorkers);
        var history = await _db.IdentityAssociationHistory.AsNoTracking().Where(h => h.AssociationId == a.Id).OrderBy(h => h.At).ThenBy(h => h.Id).ToListAsync(ct);
        var names = await IdentityQueries.NamesAsync(_db, new[] { a }, candidates.Concat(history.Where(h => h.WorkerId != null).Select(h => h.WorkerId!.Value)), now, ct);
        var evidence = await _db.IdentityAssociationEvidence.AsNoTracking().Where(e => e.AssociationId == a.Id).OrderBy(e => e.Id)
            .Select(e => new EvidenceDto(e.Id, e.Kind, e.Effect, e.RefType, e.RefId, e.Detail, e.ObservedAt)).ToListAsync(ct);
        var canReview = caller.Has(Permissions.MonitoringEventsReview) && IdentityQueries.CanSee(caller, a.FactoryId, a.LineId);
        MonRef? W(Guid? id) => id is { } w && names.Workers.TryGetValue(w, out var x) ? new MonRef(w, x.Code, x.Name) : null;
        var hist = history.Select(h => new AssociationHistoryDto(h.Id, h.Action, h.FromStatus, h.ToStatus, canReview ? W(h.WorkerId) : null, h.Reason, h.IsManual,
            h.ActorEmail, h.ActorRole, h.At)).ToList();
        RfidReadDto? read = null;
        if (a.RfidReadEventId is { } rid && await _db.RfidReadEvents.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid, ct) is { } r)
            read = (await IdentityQueries.ReadDtosAsync(_db, new[] { r }, ct))[0];
        var cand = new List<CandidateWorkerDto>();
        if (canReview)
        {
            var asg = await _db.Assignments.AsNoTracking().Where(x => candidates.Contains(x.WorkerId) && x.EndAt == null)
                .Select(x => new { x.WorkerId, x.WorkstationId, x.StartAt }).ToListAsync(ct);
            var wsIds = asg.Select(x => x.WorkstationId).Distinct().ToList();
            var wsCodes = await _db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
            cand = candidates.Where(names.Workers.ContainsKey).Select(c =>
            {
                var x = asg.FirstOrDefault(y => y.WorkerId == c);
                return new CandidateWorkerDto(W(c)!, x is null ? null : wsCodes.GetValueOrDefault(x.WorkstationId), x?.StartAt);
            }).ToList();
        }
        var lastEvent = await _db.MonitoringEvents.AsNoTracking().Where(e => e.CameraId == a.CameraId && e.TrackId == a.TrackId)
            .OrderByDescending(e => e.OccurredAt).Select(e => new { e.EventType, e.OccurredAt }).FirstOrDefaultAsync(ct);
        var active = lastEvent is not null && lastEvent.EventType != MonitoringEventTypes.PersonLost;
        var dto = IdentityQueries.ToDto(a, names, now);
        return new AssociationDetailDto(dto, canReview ? evidence : evidence.Select(e => e with { RefId = null }).ToList(), hist, canReview ? read : null, cand,
            IdentityQueries.Strings(a.CandidateTracks), active, lastEvent?.OccurredAt, canReview, canReview ? AllowedDecisions(a.Status) : Array.Empty<string>());
    }

    public static IReadOnlyList<string> AllowedDecisions(string status) => status switch
    {
        AssociationStatuses.Verified => new[] { ReviewDecisions.Revoke, ReviewDecisions.Ambiguous },
        AssociationStatuses.Ambiguous => new[] { ReviewDecisions.Confirm, ReviewDecisions.Reject },
        AssociationStatuses.ManualReview or AssociationStatuses.Pending => new[] { ReviewDecisions.Confirm, ReviewDecisions.Reject, ReviewDecisions.Ambiguous },
        _ => Array.Empty<string>(),            // UNVERIFIED, EXPIRED and REVOKED are closed: a new read starts a new case
    };
}
