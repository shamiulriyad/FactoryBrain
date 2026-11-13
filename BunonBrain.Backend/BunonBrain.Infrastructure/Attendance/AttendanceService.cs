using BunonBrain.Application.Abstractions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Attendance;
using BunonBrain.Application.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Attendance;

/// <summary>
/// Attendance (see <see cref="IAttendanceService"/>). Every punch, from a
/// person, the RFID simulator or a physical reader, goes through
/// <see cref="ProcessAsync"/>:
/// <list type="number">
///   <item>the worker's row is locked, so concurrent punches for one worker queue;</item>
///   <item>a known idempotency key returns the first result (nothing is applied twice);</item>
///   <item>the raw event is always stored, with its outcome (Accepted / Duplicate / Rejected);</item>
///   <item>the session and breaks are derived, recomputed, and overtime is brought up to date;</item>
///   <item>everything commits together.</item>
/// </list>
/// Simulator events are marked simulated at every level and never counted as device data.
/// </summary>
public sealed partial class AttendanceService : IAttendanceService
{
    private readonly BunonBrainDbContext _db;
    private readonly AttendanceSupport _support;
    private readonly TimeProvider _clock;
    private readonly ILogger<AttendanceService> _log;

    public AttendanceService(BunonBrainDbContext db, ILogger<AttendanceService> log, TimeProvider? clock = null)
    {
        _db = db; _log = log; _support = new AttendanceSupport(db); _clock = clock ?? TimeProvider.System;
    }

    private DateTimeOffset Now => _clock.GetUtcNow();

    /// <summary>Allowed minutes per break type when the shift doesn't configure one.</summary>
    private static int DefaultAllowance(string type) => type switch
    {
        BreakTypes.Lunch => 60, BreakTypes.Tea => 15, BreakTypes.Prayer => 20, BreakTypes.Personal => 10, _ => 15,
    };

    internal sealed record EventInput(
        Guid WorkerId, string EventType, DateTimeOffset OccurredAt, string Source, Guid? ReaderId, string? CardUid,
        string? IdempotencyKey, AuditActor Actor, string? Note, string? BreakType = null, Guid? ShiftBreakId = null);

    // ─── Entry points ──────────────────────────────────────────────────────

    public async Task<AccessResult<EventResult>> PunchAsync(AccessContext caller, string eventType, PunchRequest r, CancellationToken ct)
    {
        var w = await _support.WorkerAsync(r.WorkerId, ct);
        if (w is null || !AttendanceSupport.Reaches(caller, w.FactoryId, w.LineId)) return WorkerNotFound();
        var result = await ProcessAsync(new EventInput(w.Id, eventType, r.OccurredAt ?? Now, AttendanceSources.Manual, null, null,
            r.IdempotencyKey, caller.Actor, r.Note), ct);
        if (result.Value is { Replayed: false } v && v.Event.Outcome == EventOutcomes.Accepted)
        {
            // People typing punches is worth an audit line; readers and the simulator are recorded in attendance_events.
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "attendance.manual_" + (eventType == AttendanceEventTypes.CheckIn ? "check_in" : "check_out"),
                AccessAudit.EntityAttendanceSession, v.Session?.Id.ToString() ?? "-", null, eventType,
                $"{eventType} of worker {w.WorkerCode} recorded by hand at {v.Event.OccurredAt:u}{(r.Note is null ? "" : $": {r.Note}")}", null, w.FactoryId));
            await _db.SaveChangesAsync(ct);
        }
        return result;
    }

    public async Task<AccessResult<EventResult>> BreakAsync(AccessContext caller, string eventType, BreakPunchRequest r, CancellationToken ct)
    {
        var w = await _support.WorkerAsync(r.WorkerId, ct);
        if (w is null || !AttendanceSupport.Reaches(caller, w.FactoryId, w.LineId)) return WorkerNotFound();
        return await ProcessAsync(new EventInput(w.Id, eventType, r.OccurredAt ?? Now, AttendanceSources.Manual, null, null, r.IdempotencyKey,
            caller.Actor, r.Note, BreakTypes.Parse(r.BreakType) ?? BreakTypes.Other, r.ShiftBreakId), ct);
    }

    public async Task<AccessResult<EventResult>> SimulateAsync(AccessContext caller, string eventType, SimulatorPunchRequest r, CancellationToken ct)
    {
        var w = await _support.WorkerAsync(r.WorkerId, ct);
        if (w is null || !AttendanceSupport.Reaches(caller, w.FactoryId, w.LineId)) return WorkerNotFound();
        AttendanceReader? reader;
        if (r.ReaderId is { } rid)
        {
            reader = await _db.AttendanceReaders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rid, ct);
            if (reader is null || reader.FactoryId != w.FactoryId)
                return AccessResult<EventResult>.Invalid("reader_not_found", "That reader doesn't exist in the worker's factory.");
            if (!reader.IsSimulated)
                return AccessResult<EventResult>.Invalid("reader_not_simulated", "The simulator only uses simulated readers; real readers send their own events.");
            if (!reader.IsActive) return AccessResult<EventResult>.Conflict("reader_inactive", $"Reader {reader.Code} is deactivated.");
        }
        else reader = await EnsureSimulatedReaderAsync(w.FactoryId, eventType == AttendanceEventTypes.CheckOut ? "Exit" : "Entry", ct);
        // The worker's active card, if any, is what a real reader would report.
        var card = await _db.WorkerCards.AsNoTracking().Where(c => c.WorkerId == w.Id && c.IsActive).Select(c => c.CardUid).FirstOrDefaultAsync(ct);
        return await ProcessAsync(new EventInput(w.Id, eventType, r.OccurredAt ?? Now, AttendanceSources.Simulator, reader.Id, card,
            r.IdempotencyKey, caller.Actor, "SIMULATED", BreakTypes.Parse(r.BreakType) ?? (eventType is AttendanceEventTypes.BreakStart ? BreakTypes.Other : null)), ct);
    }

    public async Task<AccessResult<EventResult>> DeviceEventAsync(AccessContext caller, DeviceEventRequest r, CancellationToken ct)
    {
        var reader = await _db.AttendanceReaders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.ReaderId, ct);
        if (reader is null || !caller.CanAccessFactory(reader.FactoryId))
            return AccessResult<EventResult>.NotFound("reader_not_found", "Reader not found.");
        if (reader.IsSimulated)
            return AccessResult<EventResult>.Invalid("reader_simulated", "A simulated reader can't send device events. Use the simulator.");
        if (!reader.IsActive) return AccessResult<EventResult>.Conflict("reader_inactive", $"Reader {reader.Code} is deactivated.");
        var uid = WorkerRules.NormaliseCardUid(r.CardUid);
        var workerId = await _db.WorkerCards.AsNoTracking().Where(c => c.CardUid == uid && c.IsActive && c.FactoryId == reader.FactoryId)
            .Select(c => (Guid?)c.WorkerId).FirstOrDefaultAsync(ct);
        if (workerId is null) return AccessResult<EventResult>.NotFound("card_not_recognised", "No active card with this UID in the reader's factory.");
        return await ProcessAsync(new EventInput(workerId.Value, AttendanceEventTypes.Parse(r.EventType)!, r.OccurredAt, AttendanceSources.Device,
            reader.Id, uid, $"dev:{reader.Id:N}:{r.IdempotencyKey.Trim()}", caller.Actor, null,
            AttendanceEventTypes.Parse(r.EventType) == AttendanceEventTypes.BreakStart ? BreakTypes.Other : null), ct);
    }

    private static AccessResult<EventResult> WorkerNotFound() => AccessResult<EventResult>.NotFound("worker_not_found", "Worker not found.");

    private async Task<AttendanceReader> EnsureSimulatedReaderAsync(Guid factoryId, string direction, CancellationToken ct)
    {
        var code = direction == "Exit" ? "SIM-EXIT" : "SIM-ENTRY";
        var existing = await _db.AttendanceReaders.AsNoTracking().FirstOrDefaultAsync(r => r.FactoryId == factoryId && r.Code == code, ct);
        if (existing is not null) return existing;
        var reader = new AttendanceReader
        {
            FactoryId = factoryId, Code = code, Name = direction == "Exit" ? "Simulated exit reader" : "Simulated entry reader",
            Direction = direction, Kind = "Simulated", IsSimulated = true, Location = "Software simulator (no hardware)",
        };
        _db.AttendanceReaders.Add(reader);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            _db.ChangeTracker.Clear();
            return await _db.AttendanceReaders.AsNoTracking().FirstAsync(r => r.FactoryId == factoryId && r.Code == code, ct);
        }
        _db.Entry(reader).State = EntityState.Detached;
        return reader;
    }

    // ─── The pipeline ──────────────────────────────────────────────────────

    private sealed record Applied(string Outcome, string? Code, string? Message, AttendanceSession? Session, BreakSession? Break);

    internal async Task<AccessResult<EventResult>> ProcessAsync(EventInput input, CancellationToken ct)
    {
        var now = Now;
        var w = await _support.WorkerAsync(input.WorkerId, ct);
        if (w is null) return WorkerNotFound();
        var policy = await _support.PolicyAsync(w.FactoryId, ct);
        if (input.OccurredAt > now + TimeSpan.FromMinutes(5))
            return AccessResult<EventResult>.Invalid("event_in_future", "The event time can't be in the future.");
        if (input.OccurredAt < now - TimeSpan.FromDays(policy.MaxBackdateDays))
            return AccessResult<EventResult>.Invalid("event_too_old", $"Events older than {policy.MaxBackdateDays} days can't be recorded; request a correction instead.");

        var key = string.IsNullOrWhiteSpace(input.IdempotencyKey)
            ? $"{input.Source.ToLowerInvariant()}:{Guid.NewGuid():N}"
            : input.Source == AttendanceSources.Device ? input.IdempotencyKey! : $"{input.Source.ToLowerInvariant()}:{input.IdempotencyKey!.Trim()}";

        long eventId = 0;
        var replayed = false;
        Applied? applied = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM workers WHERE \"Id\" = {w.Id} FOR UPDATE", ct);

                var seen = await _db.AttendanceEvents.AsNoTracking().FirstOrDefaultAsync(e => e.FactoryId == w.FactoryId && e.IdempotencyKey == key, ct);
                if (seen is not null)
                {
                    await tx.RollbackAsync(ct);
                    eventId = seen.Id;
                    replayed = true;
                    return;
                }

                applied = await ApplyAsync(w, policy, input, now, ct);
                var ev = new AttendanceEvent
                {
                    FactoryId = w.FactoryId, WorkerId = w.Id, EventType = input.EventType, OccurredAt = input.OccurredAt.ToUniversalTime(), ReceivedAt = now,
                    Source = input.Source, IsSimulated = input.Source == AttendanceSources.Simulator, ReaderId = input.ReaderId, CardUid = input.CardUid,
                    IdempotencyKey = key, Outcome = applied.Outcome, OutcomeCode = applied.Code, OutcomeMessage = AccessAudit.Clip(applied.Message, 300),
                    SessionId = applied.Session?.Id, BreakSessionId = applied.Break?.Id,
                    RecordedByUserId = input.Actor.UserId, RecordedByEmail = AccessAudit.Clip(input.Actor.Email, 256)!, Note = AccessAudit.Clip(input.Note, 500),
                };
                _db.AttendanceEvents.Add(ev);
                await _db.SaveChangesAsync(ct);
                if (applied.Outcome == EventOutcomes.Accepted)
                {
                    await _support.RefreshOvertimeAsync(w.Id, now, AttendanceSupport.SystemActor, ct);
                    if (applied.Session is { } s) await _support.RecomputeAsync(s, ct);
                    await _db.SaveChangesAsync(ct);
                }
                await tx.CommitAsync(ct);
                eventId = ev.Id;
            });
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg)
        {
            // Another request with the same key (for another worker, so it held another lock) won the race.
            _db.ChangeTracker.Clear();
            var seen = await _db.AttendanceEvents.AsNoTracking().FirstOrDefaultAsync(e => e.FactoryId == w.FactoryId && e.IdempotencyKey == key, ct);
            if (seen is null || seen.WorkerId != w.Id)
                return AccessResult<EventResult>.Conflict(pg.ConstraintName?.Contains("one_open") == true ? "already_checked_in" : "idempotency_key_reused",
                    "This idempotency key was already used for another worker, or the punch conflicts with one being processed.");
            eventId = seen.Id;
            replayed = true;
        }
        _db.ChangeTracker.Clear();

        var stored = await _db.AttendanceEvents.AsNoTracking().FirstAsync(e => e.Id == eventId, ct);
        if (replayed && stored.WorkerId != w.Id)
            return AccessResult<EventResult>.Conflict("idempotency_key_reused", "This idempotency key was already used for another worker.");
        var result = await EventResultAsync(stored, replayed, ct);
        if (stored.Outcome == EventOutcomes.Rejected)
            return new AccessResult<EventResult>(stored.OutcomeCode is "checkout_before_checkin" or "break_before_check_in" ? AccessOutcome.Invalid : AccessOutcome.Conflict,
                result, stored.OutcomeCode, stored.OutcomeMessage);
        return AccessResult<EventResult>.Ok(result);
    }

    private async Task<EventResult> EventResultAsync(AttendanceEvent e, bool replayed, CancellationToken ct)
    {
        var dto = (await _support.EventDtosAsync(new[] { e }, ct))[0];
        AttendanceSessionDto? session = null;
        if (e.SessionId is { } sid)
            session = (await _support.SessionDtosAsync(await _db.AttendanceSessions.AsNoTracking().Where(s => s.Id == sid).ToListAsync(ct), ct)).FirstOrDefault();
        BreakSessionDto? brk = null;
        if (e.BreakSessionId is { } bid)
            brk = (await _support.BreakDtosAsync(await _db.BreakSessions.AsNoTracking().Where(b => b.Id == bid).ToListAsync(ct), Now, ct)).FirstOrDefault();
        return new EventResult(dto, session, brk, replayed);
    }

    /// <summary>What an event does to the worker's sessions and breaks (inside the worker lock).</summary>
    private async Task<Applied> ApplyAsync(AttendanceSupport.WorkerRow w, AttendancePolicy policy, EventInput input, DateTimeOffset now, CancellationToken ct)
    {
        var at = input.OccurredAt.ToUniversalTime();
        var dup = TimeSpan.FromSeconds(policy.DuplicateWindowSeconds);
        var simulated = input.Source == AttendanceSources.Simulator;
        var open = await _db.AttendanceSessions.FirstOrDefaultAsync(s => s.WorkerId == w.Id && s.Status == SessionStatuses.Open, ct);

        // A session that should long have ended by the time of this event is closed for review first.
        // Judged by the event's own time, so a late-delivered (offline-buffered or backdated) event
        // that happened inside the session still belongs to it. The review sweep closes stale
        // sessions by the wall clock, so none stays open indefinitely.
        if (open is not null && IsStale(open, policy, at))
        {
            await FlagMissingCheckOutAsync(open, policy, now, ct);
            open = null;
        }

        switch (input.EventType)
        {
            case AttendanceEventTypes.CheckIn:
            {
                if (open is not null)
                {
                    if (open.CheckInAt is { } ci && (at - ci).Duration() <= dup)
                        return new(EventOutcomes.Duplicate, "duplicate_check_in", "The same check-in was already recorded.", open, null);
                    return new(EventOutcomes.Rejected, "already_checked_in", $"The worker is already checked in since {open.CheckInAt:u}.", open, null);
                }
                if (w.Status != WorkerStatuses.Active)
                    return new(EventOutcomes.Rejected, "worker_not_active", $"The worker is {w.Status}.", null, null);
                var last = await _db.AttendanceSessions.Where(s => s.WorkerId == w.Id && s.CheckOutAt != null)
                    .OrderByDescending(s => s.CheckOutAt).FirstOrDefaultAsync(ct);
                if (last is not null && at < last.CheckOutAt)
                    return new(EventOutcomes.Rejected, "overlaps_previous_session", $"The worker was already checked out at {last.CheckOutAt:u}, after this time.", last, null);

                var place = await _support.PlaceAsync(w.Id, at, policy, ct);
                var session = new AttendanceSession
                {
                    WorkerId = w.Id, FactoryId = w.FactoryId, LineId = w.LineId, WorkDate = place.WorkDate,
                    ShiftAssignmentId = place.Context?.Assignment.Id, ShiftId = place.Context?.Shift.Id,
                    ScheduledStart = place.Occurrence?.Start, ScheduledEnd = place.Occurrence?.End,
                    CheckInAt = at, CheckInSource = input.Source, Status = SessionStatuses.Open, HasSimulatedEvents = simulated,
                    CreatedAt = now, UpdatedAt = now,
                };
                _db.AttendanceSessions.Add(session);
                await _support.RecomputeAsync(session, ct);
                return new(EventOutcomes.Accepted, null, place.Occurrence is null ? "Checked in (no shift scheduled at this time)." : "Checked in.", session, null);
            }

            case AttendanceEventTypes.CheckOut:
            {
                if (open is not null)
                {
                    if (at <= open.CheckInAt)
                        return new(EventOutcomes.Rejected, "checkout_before_checkin", "The check-out time is before the check-in.", open, null);
                    await CloseAsync(open, at, input.Source, simulated, now, ct);
                    return new(EventOutcomes.Accepted, null, "Checked out.", open, null);
                }
                var recent = await _db.AttendanceSessions.Where(s => s.WorkerId == w.Id)
                    .OrderByDescending(s => s.CheckOutAt ?? s.CheckInAt).FirstOrDefaultAsync(ct);
                if (recent?.CheckOutAt is { } co && (at - co).Duration() <= dup)
                    return new(EventOutcomes.Duplicate, "duplicate_check_out", "The same check-out was already recorded.", recent, null);
                // A late check-out for a session the review closed: it completes that session.
                if (recent is { Status: SessionStatuses.MissingCheckOut, CheckInAt: { } rci } && at > rci && at - rci <= TimeSpan.FromHours(policy.MaxOpenSessionHours * 2))
                {
                    recent.Status = SessionStatuses.Open; // reopen to close properly
                    await CloseAsync(recent, at, input.Source, simulated, now, ct);
                    recent.NeedsReview = false;
                    recent.ReviewReason = "Check-out arrived after the session was flagged; completed.";
                    return new(EventOutcomes.Accepted, null, "Checked out (late check-out completed a flagged session).", recent, null);
                }
                // No check-in: keep the punch, flag the day for a correction.
                var place = await _support.PlaceAsync(w.Id, at.AddMinutes(-1), policy, ct);
                var orphan = new AttendanceSession
                {
                    WorkerId = w.Id, FactoryId = w.FactoryId, LineId = w.LineId, WorkDate = place.WorkDate,
                    ShiftAssignmentId = place.Context?.Assignment.Id, ShiftId = place.Context?.Shift.Id,
                    ScheduledStart = place.Occurrence?.Start, ScheduledEnd = place.Occurrence?.End,
                    CheckOutAt = at, CheckOutSource = input.Source, Status = SessionStatuses.MissingCheckIn, NeedsReview = true,
                    ReviewReason = "Check-out without a check-in.", HasSimulatedEvents = simulated, CreatedAt = now, UpdatedAt = now,
                };
                _db.AttendanceSessions.Add(orphan);
                await _support.RecomputeAsync(orphan, ct);
                return new(EventOutcomes.Accepted, "missing_check_in", "Check-out without a check-in: recorded and flagged for review.", orphan, null);
            }

            case AttendanceEventTypes.BreakStart:
            {
                if (open is null)
                    return new(EventOutcomes.Rejected, "not_checked_in", "The worker isn't checked in, so a break can't start.", null, null);
                if (at < open.CheckInAt)
                    return new(EventOutcomes.Rejected, "break_before_check_in", "A break can't start before the check-in.", open, null);
                var active = await _db.BreakSessions.FirstOrDefaultAsync(b => b.WorkerId == w.Id && b.Status == BreakStatuses.Active, ct);
                if (active is not null)
                {
                    if ((at - active.StartAt).Duration() <= dup)
                        return new(EventOutcomes.Duplicate, "duplicate_break_start", "The same break start was already recorded.", open, active);
                    return new(EventOutcomes.Rejected, "already_on_break", $"The worker is already on a {active.BreakType} break.", open, active);
                }
                var type = input.BreakType ?? BreakTypes.Other;
                var allowed = DefaultAllowance(type);
                Guid? shiftBreakId = null;
                if (open.ShiftId is { } sid)
                {
                    var configured = await _db.ShiftBreaks.AsNoTracking().Where(b => b.ShiftId == sid).OrderBy(b => b.SortOrder).ToListAsync(ct);
                    var match = input.ShiftBreakId is { } want ? configured.FirstOrDefault(b => b.Id == want) : configured.FirstOrDefault(b => b.BreakType == type);
                    if (match is not null) { allowed = match.DurationMinutes; shiftBreakId = match.Id; type = match.BreakType; }
                }
                var brk = new BreakSession
                {
                    WorkerId = w.Id, FactoryId = w.FactoryId, AttendanceSessionId = open.Id, ShiftBreakId = shiftBreakId, BreakType = type,
                    StartAt = at, AllowedMinutes = allowed, Status = BreakStatuses.Active, StartSource = input.Source, IsSimulated = simulated,
                    CreatedAt = now, UpdatedAt = now,
                };
                _db.BreakSessions.Add(brk);
                if (simulated) open.HasSimulatedEvents = true;
                return new(EventOutcomes.Accepted, null, $"{type} break started ({allowed} min allowed).", open, brk);
            }

            case AttendanceEventTypes.BreakEnd:
            {
                var active = await _db.BreakSessions.FirstOrDefaultAsync(b => b.WorkerId == w.Id && b.Status == BreakStatuses.Active, ct);
                if (active is null)
                {
                    var lastBreak = await _db.BreakSessions.Where(b => b.WorkerId == w.Id && b.EndAt != null).OrderByDescending(b => b.EndAt).FirstOrDefaultAsync(ct);
                    if (lastBreak?.EndAt is { } be && (at - be).Duration() <= dup)
                        return new(EventOutcomes.Duplicate, "duplicate_break_end", "The same break end was already recorded.", open, lastBreak);
                    return new(EventOutcomes.Rejected, "not_on_break", "The worker isn't on a break.", open, null);
                }
                if (at < active.StartAt)
                    return new(EventOutcomes.Rejected, "break_end_before_start", "The break can't end before it started.", open, active);
                active.EndAt = at;
                active.EndSource = input.Source;
                active.Status = BreakStatuses.Completed;
                active.UpdatedAt = now;
                var session = open ?? await _db.AttendanceSessions.FirstAsync(s => s.Id == active.AttendanceSessionId, ct);
                if (simulated) session.HasSimulatedEvents = true;
                return new(EventOutcomes.Accepted, null, $"{active.BreakType} break ended.", session, active);
            }
        }
        return new(EventOutcomes.Rejected, "unknown_event", "Unknown event type.", null, null);
    }

    private static bool IsStale(AttendanceSession s, AttendancePolicy p, DateTimeOffset reference) =>
        s.ScheduledEnd is { } end
            ? reference > end.AddMinutes(p.MissingCheckOutAfterMinutes)
            : s.CheckInAt is { } ci && reference > ci.AddHours(p.MaxOpenSessionHours);

    /// <summary>Check-out: the session completes; a break still running ends with it.</summary>
    private async Task CloseAsync(AttendanceSession s, DateTimeOffset at, string source, bool simulated, DateTimeOffset now, CancellationToken ct)
    {
        s.CheckOutAt = at;
        s.CheckOutSource = source;
        s.Status = SessionStatuses.Completed;
        if (simulated) s.HasSimulatedEvents = true;
        s.UpdatedAt = now;
        foreach (var b in await _db.BreakSessions.Where(b => b.AttendanceSessionId == s.Id && b.Status == BreakStatuses.Active).ToListAsync(ct))
        {
            b.EndAt = at < b.StartAt ? b.StartAt : at;
            b.EndSource = source;
            b.Status = BreakStatuses.AutoClosed;
            b.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        await _support.RecomputeAsync(s, ct);
    }

    /// <summary>
    /// No check-out came: the session stops being active and is flagged for a
    /// correction. Its attendance is not guessed (no check-out time is invented).
    /// </summary>
    private async Task FlagMissingCheckOutAsync(AttendanceSession s, AttendancePolicy p, DateTimeOffset now, CancellationToken ct)
    {
        s.Status = SessionStatuses.MissingCheckOut;
        s.NeedsReview = true;
        s.ReviewReason = s.ScheduledEnd is { } end
            ? $"No check-out within {p.MissingCheckOutAfterMinutes} minutes after the shift ended ({end:u})."
            : $"No check-out within {p.MaxOpenSessionHours} hours of the check-in.";
        s.UpdatedAt = now;
        foreach (var b in await _db.BreakSessions.Where(b => b.AttendanceSessionId == s.Id && b.Status == BreakStatuses.Active).ToListAsync(ct))
        {
            // The break's real end is unknown: it is closed at its allowance and the session is under review anyway.
            b.EndAt = b.StartAt.AddMinutes(b.AllowedMinutes);
            b.EndSource = AttendanceSources.System;
            b.Status = BreakStatuses.AutoClosed;
            b.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        await _support.RecomputeAsync(s, ct);
    }

    // ─── Review sweep ──────────────────────────────────────────────────────

    public async Task<ReviewSweepResult> ReviewAsync(AccessContext? caller, CancellationToken ct)
    {
        var now = Now;
        var open = _db.AttendanceSessions.AsNoTracking().Where(s => s.Status == SessionStatuses.Open);
        if (caller is not null && !caller.IsPlatform)
        {
            var mine = caller.FactoryIds.ToList();
            open = open.Where(s => mine.Contains(s.FactoryId));
        }
        var candidates = await open.Select(s => new { s.Id, s.WorkerId, s.FactoryId }).ToListAsync(ct);
        var policies = new Dictionary<Guid, AttendancePolicy>();
        int flagged = 0, breaks = 0;
        foreach (var c in candidates)
        {
            if (!policies.TryGetValue(c.FactoryId, out var policy)) policies[c.FactoryId] = policy = await _support.PolicyAsync(c.FactoryId, ct);
            var strategy = _db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM workers WHERE \"Id\" = {c.WorkerId} FOR UPDATE", ct);
                var s = await _db.AttendanceSessions.FirstOrDefaultAsync(x => x.Id == c.Id && x.Status == SessionStatuses.Open, ct);
                if (s is not null && IsStale(s, policy, now))
                {
                    breaks += await _db.BreakSessions.CountAsync(b => b.AttendanceSessionId == s.Id && b.Status == BreakStatuses.Active, ct);
                    await FlagMissingCheckOutAsync(s, policy, now, ct);
                    _db.AuditLog.Add(AccessAudit.Entry(caller?.Actor ?? AttendanceSupport.SystemActor, "attendance.missing_check_out",
                        AccessAudit.EntityAttendanceSession, s.Id.ToString(), SessionStatuses.Open, SessionStatuses.MissingCheckOut, s.ReviewReason!, null, s.FactoryId));
                    await _db.SaveChangesAsync(ct);
                    flagged++;
                }
                await tx.CommitAsync(ct);
            });
        }

        // Overtime whose approved window has passed, or that is under way.
        var otWorkers = _db.OvertimeRequests.AsNoTracking()
            .Where(o => (o.Status == OvertimeStatuses.Approved || o.Status == OvertimeStatuses.InProgress) && o.ApprovedStart <= now);
        if (caller is not null && !caller.IsPlatform)
        {
            var mine = caller.FactoryIds.ToList();
            otWorkers = otWorkers.Where(o => mine.Contains(o.FactoryId));
        }
        var updated = 0;
        foreach (var workerId in await otWorkers.Select(o => o.WorkerId).Distinct().ToListAsync(ct))
        {
            _db.ChangeTracker.Clear();
            updated += await _support.RefreshOvertimeAsync(workerId, now, caller?.Actor ?? AttendanceSupport.SystemActor, ct);
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
        if (flagged > 0) _log.LogInformation("Attendance review flagged {Count} session(s) with a missing check-out.", flagged);
        return new ReviewSweepResult(flagged, breaks, updated);
    }
}
