using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Identity;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Identity;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BunonBrain.Infrastructure.Identity;

/// <summary>
/// Camera track observations that matter for identity, from the monitoring
/// events the computer-vision service (or the monitoring simulator) sends:
/// a track entering a workstation zone re-evaluates reads waiting for it; a
/// lost track ends its verified association at once.
/// </summary>
public sealed class TrackObservationProcessor : ITrackObservationProcessor
{
    private readonly BunonBrainDbContext _db;
    private readonly IIdentityAssociationService _identity;
    private readonly IdentityOptions _o;
    private readonly ILogger<TrackObservationProcessor> _log;

    public TrackObservationProcessor(BunonBrainDbContext db, IIdentityAssociationService identity, IOptions<IdentityOptions> options,
        ILogger<TrackObservationProcessor> log)
    {
        _db = db; _identity = identity; _o = options.Value; _log = log;
    }

    public async Task OnVisionEventAsync(MonitoringEventDto e, CancellationToken ct)
    {
        if (e.Camera is null || e.TrackId is null) return;
        try
        {
            if (e.EventType == MonitoringEventTypes.PersonLost && _identity is IdentityAssociationService svc)
                await svc.TrackLostAsync(e.FactoryId, e.Camera.Id, e.TrackId, ct);
            else if (e.EventType == MonitoringEventTypes.ZoneEntered && e.Zone is not null)
                await ZoneEnteredAsync(e, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Identity is evaluated again by the maintenance worker; the event itself is already stored.
            _log.LogWarning(ex, "Identity follow-up of {Type} {Id} failed.", e.EventType, e.Id);
        }
    }

    private async Task ZoneEnteredAsync(MonitoringEventDto e, CancellationToken ct)
    {
        var zone = await _db.CameraZones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == e.Zone!.Id, ct);
        if (zone is null) return;
        var mappingIds = await _db.RfidReaderMappings.AsNoTracking()
            .Where(m => m.CameraZoneId == zone.Id || (zone.WorkstationId != null && m.WorkstationId == zone.WorkstationId))
            .Select(m => m.Id).ToListAsync(ct);
        if (mappingIds.Count == 0) return;
        var lo = e.OccurredAt - _o.After;
        var hi = e.OccurredAt + _o.Before;
        var reads = await _db.RfidReadEvents.AsNoTracking()
            .Where(r => r.FactoryId == e.FactoryId && r.Evaluation == AssociationStatuses.Pending && r.MappingId != null && mappingIds.Contains(r.MappingId.Value)
                        && r.OccurredAt >= lo && r.OccurredAt <= hi)
            .OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(ct);
        foreach (var id in reads)
            await _identity.EvaluateReadAsync(id, final: false, ct);
    }
}

/// <summary>
/// Manual identity decisions. Each one needs a written reason, respects the
/// reviewer's factory and line scope and the association's current version,
/// is recorded in the association's history (previous and new status, who,
/// when, why) and in the audit log, and never overwrites an earlier decision.
/// A CONFIRM must name a worker who is a candidate of the case (evidence-backed)
/// and whose track is still visible; the database's one-to-one rules still apply.
/// </summary>
public sealed class AssociationReviewService : IAssociationReviewService
{
    private readonly BunonBrainDbContext _db;
    private readonly IdentityAssociationService _identity;
    private readonly IdentityOptions _o;

    public AssociationReviewService(BunonBrainDbContext db, IIdentityAssociationService identity, IOptions<IdentityOptions> options)
    {
        _db = db; _identity = (IdentityAssociationService)identity; _o = options.Value;
    }

    private static AccessResult<AssociationDetailDto> Bad(string code, string message) => AccessResult<AssociationDetailDto>.Invalid(code, message);

    public async Task<AccessResult<AssociationDetailDto>> ReviewAsync(AccessContext caller, Guid id, AssociationReviewRequest r, string? traceId, CancellationToken ct)
    {
        var visible = await IdentityQueries.InScope(_db, caller).FirstOrDefaultAsync(a => a.Id == id, ct);
        if (visible is null) return AccessResult<AssociationDetailDto>.NotFound("association_not_found", "Identity association not found.");
        if (!caller.Has(Permissions.MonitoringEventsReview) || !IdentityQueries.CanSee(caller, visible.FactoryId, visible.LineId))
            return AccessResult<AssociationDetailDto>.Forbidden("review_not_allowed", "You may not review identity cases in this scope.");
        var decision = ReviewDecisions.Parse(r.Decision);
        if (decision is null) return Bad("decision_invalid", $"Decision: one of {string.Join(", ", ReviewDecisions.All)}.");
        var reason = r.Reason?.Trim() ?? "";
        if (reason.Length < 5) return Bad("reason_required", "Give a reason (at least 5 characters): what you checked and why.");
        if (reason.Length > 1000) return Bad("reason_too_long", "Reasons are at most 1000 characters.");
        if (!IdentityAssociationService.AllowedDecisions(visible.Status).Contains(decision))
            return AccessResult<AssociationDetailDto>.Conflict("decision_not_allowed",
                $"A {visible.Status} association can't be {decision.ToLowerInvariant()}ed (allowed: {string.Join(", ", IdentityAssociationService.AllowedDecisions(visible.Status))}).");
        if (r.ExpectedVersion is { } v && v != visible.Version)
            return AccessResult<AssociationDetailDto>.Conflict("association_changed", "The case changed since you opened it. Reload it and decide again.");

        if (decision == ReviewDecisions.Confirm)
        {
            if (r.WorkerId is not { } w) return Bad("worker_required", "Confirming needs the worker, chosen from the case's candidates.");
            if (!IdentityQueries.Guids(visible.CandidateWorkers).Contains(w))
                return Bad("worker_not_candidate", "Only a worker the evidence points to (a candidate of this case) can be confirmed.");
            if (!await _db.Workers.AnyAsync(x => x.Id == w && x.FactoryId == visible.FactoryId && x.Status == "Active", ct))
                return Bad("worker_not_active", "The worker is not an active worker of this factory.");
            var last = await _db.MonitoringEvents.AsNoTracking().Where(e => e.CameraId == visible.CameraId && e.TrackId == visible.TrackId)
                .OrderByDescending(e => e.OccurredAt).Select(e => e.EventType).FirstOrDefaultAsync(ct);
            if (last is null || last == MonitoringEventTypes.PersonLost)
                return AccessResult<AssociationDetailDto>.Conflict("track_not_active", "The camera no longer sees this track: there is nothing current to confirm.");
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await IdentityAssociationService.LockFactoryAsync(_db, visible.FactoryId, ct);
                var a = await _db.WorkerTrackAssociations.FirstAsync(x => x.Id == id, ct);
                if (a.Version != visible.Version) throw new DbUpdateConcurrencyException("changed");
                var from = a.Status;
                switch (decision)
                {
                    case ReviewDecisions.Confirm:
                        a.WorkerId = r.WorkerId;
                        a.VerificationMethod = VerificationMethods.ManualReview;
                        a.ExpiresAt = now + _o.Validity;
                        a.EndAt = null;
                        _identity.Transition(a, AssociationStatuses.Verified, "CONFIRMED", AssociationReasons.ManualConfirmed, now, caller, reason);
                        break;
                    case ReviewDecisions.Reject:
                        _identity.Transition(a, AssociationStatuses.Unverified, "REJECTED", AssociationReasons.ManualRejected, now, caller, reason);
                        break;
                    case ReviewDecisions.Ambiguous:
                        _identity.Transition(a, AssociationStatuses.Ambiguous, "MARKED_AMBIGUOUS", AssociationReasons.ManualAmbiguous, now, caller, reason);
                        break;
                    case ReviewDecisions.Revoke:
                        _identity.Transition(a, AssociationStatuses.Revoked, "REVOKED", AssociationReasons.ManualRevoked, now, caller, reason);
                        break;
                }
                a.ReviewedByUserId = caller.UserId;
                a.ReviewedByEmail = caller.Email;
                a.ReviewedAt = now;
                _identity.Evidence(a, new EvidenceItem(EvidenceKinds.Manual, EvidenceEffects.Context, "user", caller.UserId.ToString(),
                    $"{decision} by {caller.Email}: {reason}", now), now);
                _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, $"identity_association.{decision.ToLowerInvariant()}", AccessAudit.EntityIdentityAssociation,
                    a.Id.ToString(), from, a.Status, $"{IdentityQueries.Code(a.Number)} track {a.TrackId}: {reason}", traceId, a.FactoryId));
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (DbUpdateConcurrencyException)
        {
            return AccessResult<AssociationDetailDto>.Conflict("association_changed", "The case changed since you opened it. Reload it and decide again.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg)
        {
            return AccessResult<AssociationDetailDto>.Conflict("identity_conflict", pg.ConstraintName == WorkerTrackAssociationConfiguration.OneVerifiedPerTrack
                ? "This track is already verified as a worker. Revoke that association first."
                : "This worker is already verified on another track of this camera. Revoke that association first.");
        }
        _db.ChangeTracker.Clear();
        var fresh = await _db.WorkerTrackAssociations.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        await _identity.PublishAsync(new List<(WorkerTrackAssociation, string)> { (fresh, "reviewed") }, ct);
        return AccessResult<AssociationDetailDto>.Ok(await _identity.DetailAsync(caller, fresh, ct));
    }
}

/// <summary>
/// Development-only RFID scenarios. Every read goes through the real
/// ingestion and identity pipeline from a simulated reader and is stored
/// SIMULATED; a SIMULATED read only ever combines with SIMULATED track
/// observations. Optionally a SIMULATED track observation is first sent
/// through the monitoring simulator into the reader's workstation zone. It
/// never writes associations directly.
/// </summary>
public sealed class RfidSimulatorService : IRfidSimulatorService
{
    public static readonly string[] Scenarios = { "READ", "DUPLICATE", "MULTIPLE", "INVALID", "INACTIVE", "CONFLICT" };
    private readonly BunonBrainDbContext _db;
    private readonly IRfidEventProcessor _reads;
    private readonly IMonitoringSimulatorService _monitoring;
    private readonly IdentityEvidenceEvaluator _evidence;

    public RfidSimulatorService(BunonBrainDbContext db, IRfidEventProcessor reads, IMonitoringSimulatorService monitoring, IdentityEvidenceEvaluator evidence)
    {
        _db = db; _reads = reads; _monitoring = monitoring; _evidence = evidence;
    }

    public async Task<AccessResult<RfidSimulationResult>> RunAsync(AccessContext caller, RfidSimulationRequest r, CancellationToken ct)
    {
        var scenario = Scenarios.FirstOrDefault(s => string.Equals(s, r.Scenario?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (scenario is null) return AccessResult<RfidSimulationResult>.Invalid("scenario_invalid", $"Scenario: one of {string.Join(", ", Scenarios)}.");
        var notes = new List<string>();
        var results = new List<RfidReadResult>();

        async Task<string?> ActiveCard(Guid? worker) => worker is { } w
            ? await _db.WorkerCards.AsNoTracking().Where(c => c.WorkerId == w && c.FactoryId == r.FactoryId && c.IsActive).Select(c => c.CardUid).FirstOrDefaultAsync(ct)
            : null;
        async Task<AccessResult<RfidSimulationResult>?> Read(string uid, Guid device, DateTimeOffset at, string? key = null)
        {
            var res = await _reads.IngestAsync(caller, new RfidReadRequest(r.FactoryId, uid, key ?? $"sim-{Guid.NewGuid():N}", device, null, at),
                RfidReadSources.Simulator, ct);
            if (res.Value is null) return new AccessResult<RfidSimulationResult>(res.Outcome, null, res.Code, res.Message);
            results.Add(res.Value);
            return null;
        }

        if (r.WithSimulatedTrack)
        {
            var mappingId = await _db.RfidReaderMappings.AsNoTracking().Where(m => m.DeviceId == r.DeviceId && m.EffectiveTo == null).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);
            var (_, zones) = await _evidence.MappingAsync(mappingId, ct);
            if (zones.Count == 0) notes.Add("No camera zone of a workstation for this reader: no simulated track was sent.");
            else
            {
                var z = zones[0];
                var track = $"sim-id-{Random.Shared.Next(1000, 9999)}";
                var trig = await _monitoring.TriggerAsync(caller, new TriggerSimulatedEventRequest(r.FactoryId, MonitoringEventTypes.ZoneEntered, z.CameraId, z.Id,
                    TrackId: track), ct);
                notes.Add(trig.Value is not null ? $"SIMULATED track {track} entered zone {z.Name} (monitoring simulator)."
                    : $"The simulated track observation was not sent: {trig.Message}");
            }
        }

        var now = DateTimeOffset.UtcNow;
        switch (scenario)
        {
            case "READ":
            case "DUPLICATE":
            {
                var uid = await ActiveCard(r.WorkerId);
                if (uid is null) return AccessResult<RfidSimulationResult>.Invalid("card_required", "Choose a worker with an active card.");
                var key = $"sim-{Guid.NewGuid():N}";
                if (await Read(uid, r.DeviceId, now, key) is { } e1) return e1;
                if (scenario == "DUPLICATE")
                {
                    if (await Read(uid, r.DeviceId, now, key) is { } e2) return e2;
                    notes.Add("The same read was sent twice with one idempotency key: the second is a duplicate and changes nothing.");
                }
                break;
            }
            case "MULTIPLE":
            {
                var a = await ActiveCard(r.WorkerId);
                var b = await ActiveCard(r.SecondWorkerId);
                if (a is null || b is null || r.WorkerId == r.SecondWorkerId)
                    return AccessResult<RfidSimulationResult>.Invalid("two_cards_required", "Choose two different workers with active cards.");
                if (await Read(a, r.DeviceId, now) is { } e1) return e1;
                if (await Read(b, r.DeviceId, now.AddSeconds(1)) is { } e2) return e2;
                notes.Add("Two workers' cards at one reader within seconds: the reader can't tell who is on which track.");
                break;
            }
            case "INVALID":
            {
                if (await Read($"SIM{Guid.NewGuid():N}"[..16].ToUpperInvariant(), r.DeviceId, now) is { } e) return e;
                break;
            }
            case "INACTIVE":
            {
                var uid = r.WorkerId is { } w
                    ? await _db.WorkerCards.AsNoTracking().Where(c => c.WorkerId == w && c.FactoryId == r.FactoryId && !c.IsActive)
                        .OrderByDescending(c => c.DeactivatedAt).Select(c => c.CardUid).FirstOrDefaultAsync(ct)
                    : null;
                if (uid is null) return AccessResult<RfidSimulationResult>.Invalid("inactive_card_required", "Choose a worker who has a deactivated card.");
                if (await _db.WorkerCards.AnyAsync(c => c.CardUid == uid && c.IsActive, ct))
                    notes.Add("That card UID has since been issued again: the read goes to its current holder.");
                if (await Read(uid, r.DeviceId, now) is { } e) return e;
                break;
            }
            case "CONFLICT":
            {
                var uid = await ActiveCard(r.WorkerId);
                if (uid is null || r.OtherDeviceId is not { } other || other == r.DeviceId)
                    return AccessResult<RfidSimulationResult>.Invalid("conflict_needs_two_readers", "Choose a worker with an active card and a second, different reader.");
                if (await Read(uid, r.DeviceId, now) is { } e1) return e1;
                if (await Read(uid, other, now.AddSeconds(5)) is { } e2) return e2;
                notes.Add("The same card at two readers seconds apart: contradictory location evidence.");
                break;
            }
        }
        return AccessResult<RfidSimulationResult>.Ok(new RfidSimulationResult(scenario, results, notes));
    }
}

/// <summary>Every 30 seconds: close pending reads whose window passed, expire elapsed / lost / reassigned associations.</summary>
public sealed class IdentityAssociationWorker : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<IdentityAssociationWorker> _log;

    public IdentityAssociationWorker(IServiceScopeFactory scopes, ILogger<IdentityAssociationWorker> log) { _scopes = scopes; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IIdentityAssociationService>().MaintainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Identity maintenance failed; retrying in {Interval}.", Interval);
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
