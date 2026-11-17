using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Persistence.Configurations;
using BunonBrain.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Monitoring;

/// <summary>
/// The monitoring event pipeline (see <see cref="IMonitoringEventService"/>).
/// One path for every producer: validate the contract and every reference
/// against the database, verify any worker identity (RFID card only), store
/// the event once per idempotency key, apply the alert rules in the same
/// transaction, then push the event and alert to the groups allowed to see
/// them. Stored events are never changed.
/// </summary>
public sealed class MonitoringEventService : IMonitoringEventService
{
    private readonly BunonBrainDbContext _db;
    private readonly IMonitoringPublisher _publisher;
    private readonly ITrackObservationProcessor _tracks;
    private readonly ILogger<MonitoringEventService> _log;

    public MonitoringEventService(BunonBrainDbContext db, IMonitoringPublisher publisher, ITrackObservationProcessor tracks, ILogger<MonitoringEventService> log)
    {
        _db = db; _publisher = publisher; _tracks = tracks; _log = log;
    }

    private static AccessResult<MonitoringIngestResult> Invalid(string code, string message) => AccessResult<MonitoringIngestResult>.Invalid(code, message);

    public static string SystemEmail(string producer) => producer switch
    {
        MonitoringProducers.DevicePipeline => "system:device-pipeline",
        MonitoringProducers.CameraService => "system:camera-service",
        MonitoringProducers.Simulator => "system:monitoring-simulator",
        MonitoringProducers.CvService => "service:computer-vision",
        _ => "system",
    };

    // ─── Ingestion ─────────────────────────────────────────────────────────

    public async Task<AccessResult<MonitoringIngestResult>> IngestAsync(AccessContext? caller, MonitoringEventInput input, string producer, CancellationToken ct)
    {
        if (!MonitoringProducers.All.Contains(producer)) throw new ArgumentOutOfRangeException(nameof(producer));
        var type = MonitoringEventTypes.Parse(input.EventType);
        if (type is null) return Invalid("event_type_invalid", $"Event type: one of {string.Join(", ", MonitoringEventTypes.All)}.");
        var source = MonitoringSourceTypes.Parse(input.SourceType);
        if (source is null) return Invalid("source_type_invalid", "Source type: SIMULATED or REAL.");
        if (MonitoringProducers.IsExternal(producer) && source != MonitoringSourceTypes.Real)
            return Invalid("source_type_not_allowed", "The ingestion API accepts REAL events only. Simulated events come from the monitoring simulator, so they can never pass as real.");
        if (producer == MonitoringProducers.CvService && !MonitoringProducers.CvEventTypes.Contains(type))
            return Invalid("event_type_not_allowed_for_service", $"The computer-vision service reports {string.Join(", ", MonitoringProducers.CvEventTypes)} only.");
        if (producer == MonitoringProducers.CvService && (input.WorkerId is not null || input.Identity is not null))
            return Invalid("cv_cannot_identify",
                "The computer-vision service never names a worker: a track id is a temporary camera number, and appearance is not an identity.");
        if (producer == MonitoringProducers.Simulator && source != MonitoringSourceTypes.Simulated)
            return Invalid("source_type_not_allowed", "The simulator only produces SIMULATED events.");
        if (MonitoringEventTypes.Machine.Contains(type) != (producer == MonitoringProducers.IotPipeline))
            return Invalid("machine_events_are_derived",
                "Machine state and condition events are derived by the IoT pipeline from machine sensor data; send the telemetry instead (see /api/iot).");

        if (caller is not null && (caller.Scope < RoleScope.Factory || !caller.CanAccessFactory(input.FactoryId)))
            return AccessResult<MonitoringIngestResult>.Forbidden("factory_out_of_scope", "You can only send events for a factory you administer or operate.");
        var factory = await _db.Factories.AsNoTracking().Where(f => f.Id == input.FactoryId).Select(f => new { f.Id, f.Code, f.IsActive }).FirstOrDefaultAsync(ct);
        if (factory is null) return AccessResult<MonitoringIngestResult>.NotFound("factory_not_found", "Factory not found.");
        if (!factory.IsActive) return AccessResult<MonitoringIngestResult>.Conflict("factory_inactive", "The factory is deactivated; its events are not accepted.");

        // ── Idempotency ──
        string key;
        if (MonitoringProducers.IsExternal(producer))
        {
            var raw = input.IdempotencyKey?.Trim();
            if (!MonitoringRules.IsIdempotencyKey(raw))
                return Invalid("idempotency_key_required",
                    $"idempotencyKey is required: 1–{MonitoringRules.IdempotencyKeyMax} characters (letters, digits, '.', '_', ':', '/', '-'). Send the same key when retrying.");
            key = raw!;
        }
        else key = string.IsNullOrWhiteSpace(input.IdempotencyKey) ? $"{Guid.NewGuid():N}" : input.IdempotencyKey.Trim();
        // Keys are namespaced by producer, so an API client can never collide with a system key.
        var storedKey = $"{producer.ToLowerInvariant()}/{key}";
        var existing = await _db.MonitoringEvents.AsNoTracking().FirstOrDefaultAsync(e => e.FactoryId == input.FactoryId && e.IdempotencyKey == storedKey, ct);
        if (existing is not null) return await DuplicateAsync(existing, type);

        // ── Time ──
        var now = DateTimeOffset.UtcNow;
        var occurredAt = input.OccurredAt?.ToUniversalTime() ?? now;
        if (occurredAt > now + MonitoringRules.MaxClockSkew)
            return Invalid("occurred_in_future", "The event is dated in the future. Check the producer's clock (timestamps are UTC).");
        if (occurredAt < now - MonitoringRules.MaxBackdate)
            return Invalid("occurred_too_old", "The event is more than 7 days old and was not recorded.");

        // ── References (all must belong to the event's factory) ──
        var fid = input.FactoryId;
        Camera? cam = null;
        if (input.CameraId is { } cid)
        {
            cam = await _db.Cameras.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.FactoryId == fid, ct);
            if (cam is null) return Invalid("camera_not_in_factory", "The camera is not registered in this factory.");
            if (producer == MonitoringProducers.CvService && cam.Status != "Active")
                return AccessResult<MonitoringIngestResult>.Conflict("camera_not_active", $"{cam.Code} is {cam.Status.ToLowerInvariant()}; its video is not being analysed.");
        }
        Device? dev = null;
        if (input.DeviceId is { } did)
        {
            dev = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == did && d.FactoryId == fid, ct);
            if (dev is null) return Invalid("device_not_in_factory", "The device is not registered in this factory.");
        }
        CameraZone? zone = null;
        if (input.ZoneId is { } zid)
        {
            zone = await _db.CameraZones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == zid && z.FactoryId == fid, ct);
            if (zone is null) return Invalid("zone_not_in_factory", "The zone is not configured in this factory.");
            if (cam is not null && zone.CameraId != cam.Id) return Invalid("zone_not_on_camera", "The zone belongs to another camera.");
            if (!zone.IsActive) return Invalid("zone_inactive", "The zone is deactivated.");
            cam ??= await _db.Cameras.AsNoTracking().FirstAsync(c => c.Id == zone.CameraId, ct);
        }
        Machine? machine = null;
        if (input.MachineId is { } mid)
        {
            machine = await _db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == mid && m.FactoryId == fid, ct);
            if (machine is null) return Invalid("machine_not_in_factory", "The machine is not registered in this factory.");
            if (dev?.MachineId is { } devMachine && devMachine != machine.Id) return Invalid("device_not_on_machine", "The device belongs to another machine.");
        }
        var wsId = input.WorkstationId ?? machine?.WorkstationId ?? zone?.WorkstationId ?? dev?.WorkstationId;
        Workstation? ws = null;
        if (wsId is { } w)
        {
            ws = await _db.Workstations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == w && x.FactoryId == fid, ct);
            if (ws is null) return Invalid("workstation_not_in_factory", "The workstation is not in this factory.");
        }
        var lineId = string.IsNullOrWhiteSpace(input.LineId) ? null : input.LineId.Trim();
        if (lineId is not null)
        {
            if (!await _db.ProductionLines.AnyAsync(l => l.Id == lineId && l.FactoryId == fid, ct))
                return Invalid("line_not_in_factory", "The production line is not in this factory.");
            if (ws is not null && ws.LineId != lineId) return Invalid("workstation_not_on_line", "The workstation is on another line.");
        }
        lineId ??= ws?.LineId ?? zone?.LineId ?? cam?.LineId ?? dev?.LineId;
        var floorId = ws?.FloorId ?? cam?.FloorId;
        var departmentId = ws?.DepartmentId ?? cam?.DepartmentId;

        // Simulated and real equipment never mix.
        if (dev is not null)
        {
            if (source == MonitoringSourceTypes.Real && dev.IsSimulated)
                return AccessResult<MonitoringIngestResult>.Conflict("device_is_simulated", $"{dev.Code} is a simulated device; it can't report REAL events.");
            if (source == MonitoringSourceTypes.Simulated && !dev.IsSimulated && type == MonitoringEventTypes.DeviceStatusChanged)
                return AccessResult<MonitoringIngestResult>.Conflict("device_not_simulated", $"{dev.Code} is a real device; simulated status changes are only for simulated devices.");
        }

        // ── Contract per event type ──
        var req = MonitoringRules.Requirements(type);
        if (req.Camera && cam is null) return Invalid("camera_required", $"{type} needs cameraId.");
        if (req.Device && dev is null) return Invalid("device_required", $"{type} needs deviceId.");
        if (req.Machine && machine is null) return Invalid("machine_required", $"{type} needs machineId.");
        var condition = MonitoringRules.PayloadString(input.Payload, "condition")?.ToUpperInvariant();
        if (type == MonitoringEventTypes.MachineCondition && MonitoringRules.MachineConditionAlert(condition) is null)
            return Invalid("condition_invalid", "MACHINE_CONDITION needs payload.condition (UNEXPECTED_STOP, LONG_IDLE, ABNORMAL_TEMPERATURE, HIGH_VIBRATION, COUNTER_DISCONNECTED, UNUSUAL_TRANSITION).");
        if (req.Zone && zone is null) return Invalid("zone_required", $"{type} needs zoneId (a zone of the camera).");
        var trackId = string.IsNullOrWhiteSpace(input.TrackId) ? null : input.TrackId.Trim();
        if (req.Track && trackId is null) return Invalid("track_id_required", $"{type} needs trackId (the camera-local track number).");
        if (trackId is not null)
        {
            if (!MonitoringRules.IsTrackId(trackId))
                return Invalid("track_id_invalid", $"trackId: 1–{MonitoringRules.TrackIdMax} characters (letters, digits, '.', '_', ':', '-').");
            if (cam is null) return Invalid("track_requires_camera", "A track id only makes sense with the camera that tracks it.");
        }
        if (input.Confidence is { } conf && (double.IsNaN(conf) || conf < 0 || conf > 1))
            return Invalid("confidence_invalid", "confidence is a number from 0 to 1.");
        if (req.Confidence && input.Confidence is null) return Invalid("confidence_required", $"{type} needs a confidence from 0 to 1.");
        if (!MonitoringRules.TryPayload(input.Payload, out var payloadJson, out var payloadError))
            return Invalid("payload_invalid", payloadError!);
        var toStatus = MonitoringRules.PayloadString(input.Payload, "toStatus")?.ToUpperInvariant();
        var fromStatus = MonitoringRules.PayloadString(input.Payload, "fromStatus")?.ToUpperInvariant();
        if (req.ToStatus && (toStatus is null || !StatusValues.For(type).Contains(toStatus)))
            return Invalid("to_status_invalid", $"{type} needs payload.toStatus: one of {string.Join(", ", StatusValues.For(type))}.");
        var modelName = (input.ModelName ?? MonitoringRules.PayloadString(input.Payload, "model"))?.Trim();
        if (modelName is { Length: > MonitoringRules.ModelNameMax }) return Invalid("model_name_too_long", $"Model names are at most {MonitoringRules.ModelNameMax} characters.");
        if (req.Model && string.IsNullOrEmpty(modelName)) return Invalid("model_required", $"{type} needs modelName (or payload.model).");
        var activity = MonitoringRules.PayloadString(input.Payload, "activity");
        if (req.Activity && string.IsNullOrEmpty(activity)) return Invalid("activity_required", $"{type} needs payload.activity.");
        if (activity is { Length: > 40 }) return Invalid("activity_too_long", "payload.activity is at most 40 characters.");
        var previousActivity = MonitoringRules.PayloadString(input.Payload, "previousActivity");
        var garment = MonitoringEventTypes.GarmentActivity.Contains(type);
        if (garment)
        {
            if (GarmentActivityLabels.Check(type, activity, previousActivity, input.ModelVersion) is { } badGarment) return Invalid(badGarment.Code, badGarment.Message);
            activity = GarmentActivityLabels.Parse(activity);
            previousActivity = GarmentActivityLabels.Parse(previousActivity);
        }
        else if (req.Activity)
        {
            if (ActivityLabels.Check(type, activity, previousActivity, producer) is { } bad) return Invalid(bad.Code, bad.Message);
            activity = ActivityLabels.Parse(activity);
            previousActivity = ActivityLabels.Parse(previousActivity);
        }
        // Store the validated labels in their canonical form: filters and activity views match on them.
        if (activity is not null || previousActivity is not null)
            payloadJson = MonitoringRules.WithCanonicalLabels(payloadJson, activity, previousActivity);
        var modelVersion = input.ModelVersion?.Trim();
        if (modelVersion is { Length: > 40 }) return Invalid("model_version_too_long", "modelVersion is at most 40 characters.");

        // ── Worker identity: only verified, never visual, never uncertain, never simulated ──
        Guid? workerId = null;
        Guid? cardId = null;
        string? identityMethod = null;
        if (input.WorkerId is not null || input.Identity is not null)
        {
            if (type == MonitoringEventTypes.UncertainDetection)
                return Invalid("uncertain_detection_cannot_identify", "An uncertain detection stays uncertain: it can't carry a worker identity.");
            if (type is MonitoringEventTypes.ActivityUncertain or MonitoringEventTypes.GarmentActivityUncertain)
                return Invalid("uncertain_activity_cannot_identify", "An uncertain activity stays anonymous: it can't carry a worker identity.");
            if (!req.WorkerAllowed) return Invalid("worker_not_applicable", $"{type} is about equipment and can't carry a worker identity.");
            if (source == MonitoringSourceTypes.Simulated) return Invalid("simulated_events_cannot_identify", "Simulated events never name a worker.");
            var method = IdentityMethods.Normalise(input.Identity?.Method);
            if (IdentityMethods.Visual.Contains(method))
                return Invalid("identity_method_not_accepted",
                    "A worker is never identified from how a person looks (face, appearance, re-identification). Send the track id without a worker id.");
            if (input.Identity is null || !IdentityMethods.Verifiable.Contains(method))
                return Invalid("worker_identity_unverified",
                    "A worker id needs a verified identity: identity.method RFID_BADGE with identity.cardUid. Without it, send the track id only.");
            var uid = input.Identity.CardUid?.Trim();
            if (string.IsNullOrEmpty(uid) || uid.Length > MonitoringRules.CardUidMax)
                return Invalid("worker_identity_unverified", "identity.cardUid is required for RFID_BADGE.");
            var upper = uid.ToUpperInvariant();
            var card = await _db.WorkerCards.AsNoTracking().FirstOrDefaultAsync(c => c.FactoryId == fid && c.IsActive && c.CardUid.ToUpper() == upper, ct);
            if (card is null) return Invalid("worker_identity_unverified", "No active RFID card with that UID in this factory, so the identity can't be verified.");
            if (input.WorkerId is { } claimed && claimed != card.WorkerId)
                return Invalid("worker_identity_mismatch", "That RFID card belongs to another worker; the identity was not recorded.");
            if (!await _db.Workers.AnyAsync(x => x.Id == card.WorkerId && x.FactoryId == fid, ct))
                return Invalid("worker_identity_unverified", "The card's worker is not in this factory.");
            workerId = card.WorkerId;
            cardId = card.Id;
            identityMethod = IdentityMethods.RfidBadge;
        }

        // ── Summary, severity, alert rule ──
        var simulated = source == MonitoringSourceTypes.Simulated;
        var detail = MonitoringRules.PayloadString(input.Payload, "reason") ?? MonitoringRules.PayloadString(input.Payload, "message");
        var ctx = new AlertContext(type, simulated, toStatus, fromStatus, cam?.Id, cam?.Code, dev?.Id, dev?.Code, zone?.Id, zone?.Name, zone?.ZoneType,
            trackId, input.Confidence, modelName, detail, machine?.Id, machine?.Code, condition);
        var spec = MonitoringRules.AlertFor(ctx);
        var recovery = spec is null ? MonitoringRules.RecoveryDedupKey(ctx) : null;
        var summary = MonitoringRules.Summary(type, simulated, cam?.Code, dev?.Code, zone?.Name, trackId, input.Confidence, toStatus, activity, modelName,
            workerId is not null, previousActivity,
            type is MonitoringEventTypes.ActivityUncertain or MonitoringEventTypes.GarmentActivityUncertain or MonitoringEventTypes.MachineCondition ? detail : null,
            machine?.Code, condition, fromStatus);
        var severity = MonitoringRules.EventSeverity(type, toStatus, zone?.ZoneType);
        var actorEmail = caller?.Email ?? SystemEmail(producer);
        var actorRole = caller?.PrimaryRole;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var strategy = _db.Database.CreateExecutionStrategy();
                var saved = await strategy.ExecuteAsync(async () =>
                {
                    _db.ChangeTracker.Clear();
                    await using var tx = await _db.Database.BeginTransactionAsync(ct);
                    var ev = new MonitoringEvent
                    {
                        FactoryId = fid, EventType = type, Category = MonitoringEventTypes.CategoryOf(type), SourceType = source, Producer = producer,
                        IdempotencyKey = storedKey, DepartmentId = departmentId, FloorId = floorId, LineId = lineId, WorkstationId = ws?.Id,
                        CameraId = cam?.Id, DeviceId = dev?.Id, MachineId = machine?.Id, ZoneId = zone?.Id, TrackId = trackId, WorkerId = workerId, IdentityMethod = identityMethod,
                        WorkerCardId = cardId, Confidence = input.Confidence is { } c ? Math.Round(c, 4) : null, Severity = severity, Summary = summary,
                        Payload = payloadJson, ModelName = string.IsNullOrEmpty(modelName) ? null : modelName, ModelVersion = string.IsNullOrEmpty(modelVersion) ? null : modelVersion,
                        SimulationRunId = input.SimulationRunId, OccurredAt = occurredAt, ReceivedAt = now, RecordedByUserId = caller?.UserId,
                        RecordedByEmail = actorEmail,
                    };
                    _db.MonitoringEvents.Add(ev);

                    MonitoringAlert? alert = null;
                    string? change = null;
                    var dedup = spec?.DedupKey ?? recovery;
                    var open = dedup is null ? null : await _db.MonitoringAlerts
                        .FirstOrDefaultAsync(a => a.FactoryId == fid && a.DedupKey == dedup && AlertStatuses.Active.Contains(a.Status), ct);
                    if (spec is not null && open is null)
                    {
                        alert = new MonitoringAlert
                        {
                            FactoryId = fid, AlertType = spec.AlertType, Category = AlertTypes.CategoryOf(spec.AlertType), Severity = spec.Severity,
                            Title = spec.Title, Description = spec.Description, DepartmentId = departmentId, FloorId = floorId, LineId = lineId,
                            WorkstationId = ws?.Id, CameraId = cam?.Id, DeviceId = dev?.Id, MachineId = machine?.Id, ZoneId = zone?.Id, EventId = ev.Id, LastEventId = ev.Id,
                            SourceType = source, DedupKey = spec.DedupKey, FirstOccurredAt = occurredAt, LastOccurredAt = occurredAt,
                            Status = AlertStatuses.Open, Evidence = Evidence(ev, zone, 1), CreatedAt = now, UpdatedAt = now,
                        };
                        _db.MonitoringAlerts.Add(alert);
                        _db.MonitoringAlertActions.Add(new MonitoringAlertAction
                        {
                            AlertId = alert.Id, FactoryId = fid, Action = AlertActions.Created, ToStatus = AlertStatuses.Open, EventId = ev.Id,
                            Note = $"Raised by {type} ({source.ToLowerInvariant()}, {producer.ToLowerInvariant()}).", ActorUserId = caller?.UserId,
                            ActorEmail = actorEmail, ActorRole = actorRole, At = now,
                        });
                        change = "created";
                    }
                    else if (open is not null)
                    {
                        var isRepeat = spec is not null;
                        _db.MonitoringAlertActions.Add(new MonitoringAlertAction
                        {
                            AlertId = open.Id, FactoryId = fid, Action = isRepeat ? AlertActions.Repeated : AlertActions.SourceRecovered,
                            FromStatus = open.Status, ToStatus = open.Status, EventId = ev.Id,
                            Note = isRepeat
                                ? $"Happened again ({open.OccurrenceCount + 1} times)."
                                : "The source is back to normal. The alert stays open until a person resolves it.",
                            ActorUserId = caller?.UserId, ActorEmail = actorEmail, ActorRole = actorRole, At = now,
                        });
                        if (isRepeat)
                        {
                            open.OccurrenceCount++;
                            if (occurredAt > open.LastOccurredAt) open.LastOccurredAt = occurredAt;
                            if (AlertSeverities.Rank(spec!.Severity) > AlertSeverities.Rank(open.Severity)) open.Severity = spec.Severity;
                            open.Evidence = Evidence(ev, zone, open.OccurrenceCount);
                        }
                        open.LastEventId = ev.Id;
                        open.UpdatedAt = now;
                        open.Version++;
                        alert = open;
                        change = isRepeat ? "repeated" : "recovered";
                    }
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return (Event: ev, Alert: alert, Change: change);
                });
                _db.ChangeTracker.Clear();
                var eventDto = (await MonitoringQueries.EventDtosAsync(_db, new[] { saved.Event }, ct))[0];
                MonitoringAlertDto? alertDto = null;
                MonitoringAlertDto? broadcast = null;
                if (saved.Alert is not null)
                {
                    var fresh = await _db.MonitoringAlerts.AsNoTracking().FirstAsync(a => a.Id == saved.Alert.Id, ct);
                    alertDto = (await MonitoringQueries.AlertDtosAsync(_db, caller, new[] { fresh }, ct))[0];
                    // Receivers hold different permissions: the pushed copy carries none; clients ask for their own.
                    broadcast = alertDto with { Can = MonitoringQueries.Can(null, fresh) };
                }
                await _publisher.EventAsync(eventDto, ct);
                if (broadcast is not null) await _publisher.AlertAsync(broadcast, saved.Change!, ct);
                // Track observations feed the identity association engine (zone entries, lost tracks). It never changes the event.
                if (type is MonitoringEventTypes.ZoneEntered or MonitoringEventTypes.PersonLost)
                    await _tracks.OnVisionEventAsync(eventDto, ct);
                return AccessResult<MonitoringIngestResult>.Ok(new MonitoringIngestResult("Accepted", eventDto, alertDto));
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg)
            {
                _db.ChangeTracker.Clear();
                if (pg.ConstraintName == MonitoringEventConfiguration.IdempotencyIndex)
                {
                    // Same key sent twice at the same moment: the other request stored it.
                    var winner = await _db.MonitoringEvents.AsNoTracking().FirstAsync(e => e.FactoryId == fid && e.IdempotencyKey == storedKey, ct);
                    return await DuplicateAsync(winner, type);
                }
                if (pg.ConstraintName == MonitoringAlertConfiguration.OneActiveIndex && attempt == 0) continue; // another event opened the alert first: join it
                throw;
            }
        }
    }

    private async Task<AccessResult<MonitoringIngestResult>> DuplicateAsync(MonitoringEvent existing, string type)
    {
        if (existing.EventType != type)
            return AccessResult<MonitoringIngestResult>.Conflict("idempotency_key_reused", "This idempotency key was already used for a different event type.");
        var dto = (await MonitoringQueries.EventDtosAsync(_db, new[] { existing }, CancellationToken.None))[0];
        return AccessResult<MonitoringIngestResult>.Ok(new MonitoringIngestResult("Duplicate", dto, null));
    }

    /// <summary>Evidence metadata: which event, how sure, where. No images and no identities.</summary>
    private static string Evidence(MonitoringEvent ev, CameraZone? zone, int occurrences) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["eventId"] = ev.Id,
        ["eventType"] = ev.EventType,
        ["occurredAt"] = ev.OccurredAt,
        ["sourceType"] = ev.SourceType,
        ["confidence"] = ev.Confidence,
        ["trackId"] = ev.TrackId,
        ["zone"] = zone is null ? null : new { zone.Id, zone.Name, zone.ZoneType },
        ["model"] = ev.ModelName,
        ["occurrences"] = occurrences,
        ["note"] = ev.SourceType == MonitoringSourceTypes.Simulated ? MonitoringRules.SimulatedNote : null,
    });

    // ─── Queries ───────────────────────────────────────────────────────────

    public async Task<PagedResult<MonitoringEventDto>> ListAsync(AccessContext caller, MonitoringEventQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MonitoringRules.MaxPageSize);
        var events = Filter(MonitoringQueries.EventsInScope(_db, caller), q);
        var total = await events.CountAsync(ct);
        var rows = await events.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.ReceivedAt).ThenBy(e => e.Id)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<MonitoringEventDto>(await MonitoringQueries.EventDtosAsync(_db, rows, ct), page, size, total);
    }

    internal IQueryable<MonitoringEvent> Filter(IQueryable<MonitoringEvent> events, MonitoringEventQuery q)
    {
        if (q.FactoryId is { } f) events = events.Where(e => e.FactoryId == f);
        if (q.DepartmentId is { } d) events = events.Where(e => e.DepartmentId == d);
        if (q.FloorId is { } fl) events = events.Where(e => e.FloorId == fl);
        if (!string.IsNullOrWhiteSpace(q.LineId)) { var line = q.LineId.Trim(); events = events.Where(e => e.LineId == line); }
        if (q.CameraId is { } c) events = events.Where(e => e.CameraId == c);
        if (q.DeviceId is { } dv) events = events.Where(e => e.DeviceId == dv);
        if (q.MachineId is { } mc) events = events.Where(e => e.MachineId == mc);
        if (q.WorkstationId is { } w) events = events.Where(e => e.WorkstationId == w);
        if (MonitoringEventTypes.Parse(q.EventType) is { } t) events = events.Where(e => e.EventType == t);
        if (MonitoringCategories.Parse(q.Category) is { } cat) events = events.Where(e => e.Category == cat);
        if (MonitoringSourceTypes.Parse(q.SourceType) is { } s) events = events.Where(e => e.SourceType == s);
        if (!string.IsNullOrWhiteSpace(q.TrackId)) { var tr = q.TrackId.Trim(); events = events.Where(e => e.TrackId == tr); }
        if (q.VerifiedWorker is { } vw) events = vw ? events.Where(e => e.WorkerId != null) : events.Where(e => e.WorkerId == null);
        if (GarmentActivityLabels.Parse(q.Activity) is { } act)
        {
            // Matches the activity in either family (generic posture or garment).
            var activityJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["activity"] = act });
            var activityTypes = MonitoringEventTypes.Activity.Concat(MonitoringEventTypes.GarmentActivity).ToArray();
            events = events.Where(e => activityTypes.Contains(e.EventType) && EF.Functions.JsonContains(e.Payload, activityJson));
        }
        if (q.From is { } from) { var u = from.ToUniversalTime(); events = events.Where(e => e.OccurredAt >= u); }
        if (q.To is { } to) { var u = to.ToUniversalTime(); events = events.Where(e => e.OccurredAt <= u); }
        if (q.SimulationRunId is { } run) events = events.Where(e => e.SimulationRunId == run);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            events = events.Where(e => EF.Functions.ILike(e.Summary, pattern, "\\") || (e.TrackId != null && EF.Functions.ILike(e.TrackId, pattern, "\\"))
                                       || EF.Functions.ILike(e.IdempotencyKey, pattern, "\\"));
        }
        return events;
    }

    public async Task<AccessResult<MonitoringEventDetailDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var e = await MonitoringQueries.EventsInScope(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return AccessResult<MonitoringEventDetailDto>.NotFound("event_not_found", "Event not found.");
        var dto = (await MonitoringQueries.EventDtosAsync(_db, new[] { e }, ct))[0];
        var reviews = await _db.MonitoringEventReviews.AsNoTracking().Where(r => r.EventId == id).OrderBy(r => r.ReviewedAt).ThenBy(r => r.Id)
            .Select(r => new EventReviewDto(r.Id, r.Decision, r.Note, r.ReviewedByEmail, r.ReviewedByRole, r.ReviewedAt)).ToListAsync(ct);
        var alertIds = _db.MonitoringAlertActions.Where(a => a.EventId == id).Select(a => a.AlertId);
        var alerts = await MonitoringQueries.AlertsInScope(_db, caller)
            .Where(a => a.EventId == id || a.LastEventId == id || alertIds.Contains(a.Id))
            .OrderByDescending(a => a.CreatedAt).Take(20).ToListAsync(ct);
        return AccessResult<MonitoringEventDetailDto>.Ok(new MonitoringEventDetailDto(dto, reviews,
            alerts.Select(a => new AlertLinkDto(a.Id, MonitoringQueries.AlertCode(a.Number), a.Title, a.Status, a.Severity)).ToList(),
            caller.Has(Permissions.MonitoringEventsReview)));
    }

    public async Task<AccessResult<MonitoringEventDetailDto>> ReviewAsync(AccessContext caller, Guid id, ReviewMonitoringEventRequest r, string? traceId, CancellationToken ct)
    {
        var e = await MonitoringQueries.EventsInScope(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return AccessResult<MonitoringEventDetailDto>.NotFound("event_not_found", "Event not found.");
        var decision = EventReviewDecisions.Parse(r.Decision);
        if (decision is null)
            return AccessResult<MonitoringEventDetailDto>.Invalid("review_decision_invalid", $"Decision: one of {string.Join(", ", EventReviewDecisions.All)}.");
        var note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
        if (note is { Length: > MonitoringRules.NoteMax })
            return AccessResult<MonitoringEventDetailDto>.Invalid("note_too_long", $"Notes are at most {MonitoringRules.NoteMax} characters.");
        if (note is null && decision is EventReviewDecisions.FalsePositive or EventReviewDecisions.NeedsFollowUp)
            return AccessResult<MonitoringEventDetailDto>.Invalid("note_required", "Say what was wrong or what needs following up.");
        var now = DateTimeOffset.UtcNow;
        _db.MonitoringEventReviews.Add(new MonitoringEventReview
        {
            EventId = e.Id, FactoryId = e.FactoryId, Decision = decision, Note = note, ReviewedByUserId = caller.UserId,
            ReviewedByEmail = caller.Email, ReviewedByRole = caller.PrimaryRole, ReviewedAt = now,
        });
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "monitoring_event.reviewed", AccessAudit.EntityMonitoringEvent, e.Id.ToString(), null, decision,
            $"{e.EventType} ({e.SourceType.ToLowerInvariant()}) reviewed: {decision}" + (note is null ? "" : $" — {note}"), traceId, e.FactoryId));
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return await GetAsync(caller, id, ct);
    }

    public MonitoringContractDto Contract() => new(
        MonitoringEventTypes.All.Select(t => new EventTypeContract(t, MonitoringEventTypes.Label(t), MonitoringEventTypes.CategoryOf(t),
            MonitoringRules.Requirements(t), StatusValues.For(t))).ToList(),
        MonitoringSourceTypes.All, IdentityMethods.Verifiable, IdentityMethods.Visual, AlertStatuses.All, AlertSeverities.All,
        AlertResolutionCodes.All, AlertEscalationTargets.All, EventReviewDecisions.All,
        MonitoringRules.IdempotencyKeyMax, MonitoringRules.TrackIdMax, MonitoringRules.PayloadMaxBytes, MonitoringRules.PayloadMaxDepth,
        "Timestamps are UTC (ISO 8601). The same idempotencyKey is stored once per factory; resending returns the stored event with outcome Duplicate. " +
        "A trackId is a temporary camera-local number, never a worker id. workerId is accepted only with identity.method RFID_BADGE and the card's UID, " +
        "checked against the factory's active cards; visual identification is refused and UNCERTAIN_DETECTION never carries a worker. " +
        "The ingestion API accepts REAL events only; the simulator's events are SIMULATED. Payloads are JSON objects up to 8 KB without secrets or identity fields.");
}
