using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Attendance;
using BunonBrain.Application.Workers;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Attendance;

/// <summary>
/// Overtime: Requested → PendingApproval → Approved / Rejected → InProgress →
/// Completed (or Cancelled before it starts). The requester never decides
/// their own request. Requested, approved, actual (attendance inside the
/// approved window) and payable (set only by an HR review) minutes are
/// separate; time beyond the shift without an approved request is never
/// treated as overtime. Daily / weekly limits from the policy are shown as
/// warnings for HR, to be checked against the applicable Bangladesh labour
/// law; nothing here computes or deducts pay.
/// </summary>
public sealed class OvertimeService : IOvertimeService
{
    private readonly BunonBrainDbContext _db;
    private readonly AttendanceSupport _support;
    private readonly TimeProvider _clock;

    public OvertimeService(BunonBrainDbContext db, TimeProvider? clock = null)
    {
        _db = db; _support = new AttendanceSupport(db); _clock = clock ?? TimeProvider.System;
    }

    private DateTimeOffset Now => _clock.GetUtcNow();
    private static AccessResult<OvertimeDto> NotFound() => AccessResult<OvertimeDto>.NotFound("overtime_not_found", "Overtime request not found.");

    private IQueryable<OvertimeRequest> Visible(AccessContext caller) =>
        caller.HasAny(Permissions.OvertimeView, Permissions.OvertimeApprove)
            ? AttendanceSupport.Scoped(_db.OvertimeRequests.AsNoTracking(), caller,
                (q, f) => q.Where(o => f.Contains(o.FactoryId)),
                (q, f, l) => q.Where(o => f.Contains(o.FactoryId) && o.LineId != null && l.Contains(o.LineId)))
            : _db.OvertimeRequests.AsNoTracking().Where(_ => false);

    public async Task<PagedResult<OvertimeDto>> ListAsync(AccessContext caller, OvertimeListQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 200);
        var rows = Visible(caller);
        if (q.FactoryId is { } f) rows = rows.Where(o => o.FactoryId == f);
        if (!string.IsNullOrWhiteSpace(q.LineId)) rows = rows.Where(o => o.LineId == q.LineId.Trim());
        if (q.WorkerId is { } w) rows = rows.Where(o => o.WorkerId == w);
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            var statuses = q.Status.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(OvertimeStatuses.Parse).Where(s => s is not null).ToList();
            rows = statuses.Count == 0 ? rows.Where(_ => false) : rows.Where(o => statuses.Contains(o.Status));
        }
        if (q.From is { } from) rows = rows.Where(o => o.WorkDate >= from);
        if (q.To is { } to) rows = rows.Where(o => o.WorkDate <= to);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var pattern = "%" + OrganizationService.Like(q.Search.Trim()) + "%";
            rows = rows.Where(o => _db.Workers.Any(x => x.Id == o.WorkerId && (EF.Functions.ILike(x.WorkerCode, pattern, "\\") || EF.Functions.ILike(x.FullName, pattern, "\\"))));
        }
        var total = await rows.CountAsync(ct);
        var list = await rows.OrderByDescending(o => o.Status == OvertimeStatuses.PendingApproval).ThenByDescending(o => o.PlannedStart)
            .Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<OvertimeDto>(await DtosAsync(caller, list, ct), page, size, total);
    }

    public async Task<AccessResult<OvertimeDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var o = await _db.OvertimeRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return NotFound();
        var visible = await Visible(caller).AnyAsync(x => x.Id == id, ct);
        if (!visible)
        {
            var w = await _support.WorkerAsync(o.WorkerId, ct);
            if (w is null || !AttendanceSupport.IsOwn(caller, w)) return NotFound();
        }
        return AccessResult<OvertimeDto>.Ok((await DtosAsync(caller, new[] { o }, ct))[0]);
    }

    public async Task<AccessResult<OvertimeDto>> CreateAsync(AccessContext caller, CreateOvertimeRequest r, string? traceId, CancellationToken ct)
    {
        var w = await _support.WorkerAsync(r.WorkerId, ct);
        if (w is null || !AttendanceSupport.Reaches(caller, w.FactoryId, w.LineId)) return AccessResult<OvertimeDto>.NotFound("worker_not_found", "Worker not found.");
        if (w.Status != WorkerStatuses.Active) return AccessResult<OvertimeDto>.Conflict("worker_not_active", $"The worker is {w.Status}.");
        var policy = await _support.PolicyAsync(w.FactoryId, ct);
        var (start, end) = (r.PlannedStart.ToUniversalTime(), r.PlannedEnd.ToUniversalTime());
        if (start < Now.AddDays(-policy.MaxBackdateDays) || start > Now.AddDays(62))
            return AccessResult<OvertimeDto>.Invalid("overtime_out_of_range", $"Overtime can be requested up to {policy.MaxBackdateDays} days back and 62 days ahead.");

        AccessResult<OvertimeDto>? refusal = null;
        Guid newId = Guid.Empty;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            refusal = null;
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM workers WHERE \"Id\" = {w.Id} FOR UPDATE", ct);
            var open = new[] { OvertimeStatuses.Requested, OvertimeStatuses.PendingApproval, OvertimeStatuses.Approved, OvertimeStatuses.InProgress };
            if (await _db.OvertimeRequests.AnyAsync(o => o.WorkerId == w.Id && open.Contains(o.Status) && o.PlannedStart < end && o.PlannedEnd > start, ct))
            {
                refusal = AccessResult<OvertimeDto>.Conflict("overtime_overlaps", "This worker already has an open overtime request for an overlapping period.");
                await tx.RollbackAsync(ct);
                return;
            }
            // Overtime belongs to the shift it follows: place it by the minute before it starts.
            var place = await _support.PlaceAsync(w.Id, start.AddMinutes(-1), policy, ct);
            var now = Now;
            var o = new OvertimeRequest
            {
                FactoryId = w.FactoryId, WorkerId = w.Id, LineId = w.LineId, WorkDate = place.WorkDate, PlannedStart = start, PlannedEnd = end,
                RequestedMinutes = (int)Math.Floor((end - start).TotalMinutes), Reason = r.Reason.Trim(),
                Status = r.Submit ? OvertimeStatuses.PendingApproval : OvertimeStatuses.Requested,
                RequestedByUserId = caller.UserId, RequestedByEmail = AccessAudit.Clip(caller.Email, 256)!, CreatedAt = now, UpdatedAt = now,
            };
            _db.OvertimeRequests.Add(o);
            History(o, caller, "Requested", null, OvertimeStatuses.Requested, o.RequestedMinutes, o.Reason, now);
            if (r.Submit) History(o, caller, "Submitted", OvertimeStatuses.Requested, OvertimeStatuses.PendingApproval, null, null, now);
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "overtime.requested", AccessAudit.EntityOvertime, o.Id.ToString(), null, o.Status,
                $"overtime for worker {w.WorkerCode}: {o.RequestedMinutes} min from {start:u}: {o.Reason}", traceId, w.FactoryId));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            newId = o.Id;
        });
        _db.ChangeTracker.Clear();
        return refusal ?? AccessResult<OvertimeDto>.Created(await DtoAsync(caller, newId, ct));
    }

    public Task<AccessResult<OvertimeDto>> SubmitAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct) =>
        TransitionAsync(caller, id, traceId, ct, (o, _) =>
        {
            if (o.Status != OvertimeStatuses.Requested)
                return AccessResult<OvertimeDto>.Conflict("overtime_wrong_status", $"Only a Requested request can be submitted (this one is {o.Status}).");
            return Move(o, caller, "Submitted", OvertimeStatuses.PendingApproval, null, null);
        });

    public Task<AccessResult<OvertimeDto>> ApproveAsync(AccessContext caller, Guid id, ApproveOvertimeRequest r, string? traceId, CancellationToken ct) =>
        TransitionAsync(caller, id, traceId, ct, (o, _) =>
        {
            if (o.Status != OvertimeStatuses.PendingApproval)
                return AccessResult<OvertimeDto>.Conflict("overtime_wrong_status", $"Only a request pending approval can be approved (this one is {o.Status}).");
            if (o.RequestedByUserId == caller.UserId)
                return AccessResult<OvertimeDto>.Forbidden("cannot_decide_own", "Someone other than the requester must approve overtime.");
            var minutes = r.ApprovedMinutes ?? o.RequestedMinutes;
            if (minutes > o.RequestedMinutes)
                return AccessResult<OvertimeDto>.Invalid("approved_exceeds_requested", "You can approve at most the requested minutes.");
            o.ApprovedMinutes = minutes;
            o.ApprovedStart = o.PlannedStart;
            o.ApprovedEnd = o.PlannedStart.AddMinutes(minutes);
            return Move(o, caller, "Approved", OvertimeStatuses.Approved, minutes, r.Note);
        }, refreshAfter: true);

    public Task<AccessResult<OvertimeDto>> RejectAsync(AccessContext caller, Guid id, OvertimeReasonRequest r, string? traceId, CancellationToken ct) =>
        TransitionAsync(caller, id, traceId, ct, (o, _) =>
        {
            if (o.Status != OvertimeStatuses.PendingApproval)
                return AccessResult<OvertimeDto>.Conflict("overtime_wrong_status", $"Only a request pending approval can be rejected (this one is {o.Status}).");
            if (o.RequestedByUserId == caller.UserId)
                return AccessResult<OvertimeDto>.Forbidden("cannot_decide_own", "Someone other than the requester must reject overtime; cancel your own request instead.");
            return Move(o, caller, "Rejected", OvertimeStatuses.Rejected, null, r.Reason);
        });

    public Task<AccessResult<OvertimeDto>> CancelAsync(AccessContext caller, Guid id, OvertimeReasonRequest r, string? traceId, CancellationToken ct) =>
        TransitionAsync(caller, id, traceId, ct, (o, now) =>
        {
            if (o.Status is not (OvertimeStatuses.Requested or OvertimeStatuses.PendingApproval or OvertimeStatuses.Approved))
                return AccessResult<OvertimeDto>.Conflict("overtime_wrong_status", $"A request that is {o.Status} can't be cancelled.");
            if (o.Status == OvertimeStatuses.Approved && now >= o.ApprovedStart)
                return AccessResult<OvertimeDto>.Conflict("overtime_started", "Approved overtime that has begun can't be cancelled.");
            return Move(o, caller, "Cancelled", OvertimeStatuses.Cancelled, null, r.Reason);
        });

    public Task<AccessResult<OvertimeDto>> ConfirmPayableAsync(AccessContext caller, Guid id, ConfirmPayableRequest r, string? traceId, CancellationToken ct) =>
        TransitionAsync(caller, id, traceId, ct, (o, now) =>
        {
            if (o.Status != OvertimeStatuses.Completed)
                return AccessResult<OvertimeDto>.Conflict("overtime_wrong_status", "Payable minutes are confirmed only for completed overtime.");
            var cap = Math.Min(o.ApprovedMinutes ?? 0, o.ActualMinutes ?? 0);
            if (r.PayableMinutes > cap)
                return AccessResult<OvertimeDto>.Invalid("payable_exceeds_verified",
                    $"Payable minutes can't exceed the approved and actually attended overtime ({cap} min).");
            o.PayableMinutes = r.PayableMinutes;
            o.PayableNote = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim();
            o.PayableConfirmedByUserId = caller.UserId;
            o.PayableConfirmedByEmail = AccessAudit.Clip(caller.Email, 256);
            o.PayableConfirmedAt = now;
            History(o, caller, "PayableConfirmed", o.Status, o.Status, r.PayableMinutes, o.PayableNote, now);
            return null;
        });

    private AccessResult<OvertimeDto>? Move(OvertimeRequest o, AccessContext caller, string action, string to, int? minutes, string? note)
    {
        var now = Now;
        History(o, caller, action, o.Status, to, minutes, note, now);
        o.Status = to;
        o.UpdatedAt = now;
        return null;
    }

    private void History(OvertimeRequest o, AccessContext caller, string action, string? from, string to, int? minutes, string? note, DateTimeOffset at) =>
        _db.OvertimeApprovals.Add(new OvertimeApproval
        {
            RequestId = o.Id, Action = action, FromStatus = from, ToStatus = to, Minutes = minutes, Note = AccessAudit.Clip(note?.Trim(), 500),
            ActorUserId = caller.UserId, ActorEmail = AccessAudit.Clip(caller.Email, 256)!, OccurredAt = at,
        });

    /// <summary>Loads the request under the worker's lock, applies <paramref name="change"/> (null = OK), audits and commits.</summary>
    private async Task<AccessResult<OvertimeDto>> TransitionAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct,
        Func<OvertimeRequest, DateTimeOffset, AccessResult<OvertimeDto>?> change, bool refreshAfter = false)
    {
        var existing = await Visible(caller).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (existing is null) return NotFound();
        AccessResult<OvertimeDto>? refusal = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            refusal = null;
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM workers WHERE \"Id\" = {existing.WorkerId} FOR UPDATE", ct);
            var o = await _db.OvertimeRequests.FirstAsync(x => x.Id == id, ct);
            var before = o.Status;
            var now = Now;
            refusal = change(o, now);
            if (refusal is not null)
            {
                await tx.RollbackAsync(ct);
                return;
            }
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "overtime." + o.Status.ToLowerInvariant(), AccessAudit.EntityOvertime, o.Id.ToString(), before, o.Status,
                $"overtime request {o.Id}: {before} → {o.Status}" + (o.PayableMinutes is { } p && o.PayableConfirmedAt == now ? $"; payable {p} min confirmed" : ""),
                traceId, o.FactoryId));
            await _db.SaveChangesAsync(ct);
            if (refreshAfter)
            {
                // Approval after the fact: attendance that already happened counts at once.
                await _support.RefreshOvertimeAsync(o.WorkerId, now, AttendanceSupport.SystemActor, ct);
                foreach (var s in await _db.AttendanceSessions.Where(s => s.WorkerId == o.WorkerId && s.WorkDate == o.WorkDate).ToListAsync(ct))
                    await _support.RecomputeAsync(s, ct);
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        });
        _db.ChangeTracker.Clear();
        return refusal ?? AccessResult<OvertimeDto>.Ok(await DtoAsync(caller, id, ct));
    }

    private async Task<OvertimeDto> DtoAsync(AccessContext caller, Guid id, CancellationToken ct) =>
        (await DtosAsync(caller, await _db.OvertimeRequests.AsNoTracking().Where(o => o.Id == id).ToListAsync(ct), ct))[0];

    internal async Task<IReadOnlyList<OvertimeDto>> DtosAsync(AccessContext caller, IReadOnlyList<OvertimeRequest> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<OvertimeDto>();
        var names = await _support.WorkerNamesAsync(rows.Select(r => r.WorkerId), ct);
        var ids = rows.Select(r => r.Id).ToList();
        var history = (await _db.OvertimeApprovals.AsNoTracking().Where(h => ids.Contains(h.RequestId)).OrderBy(h => h.Id).ToListAsync(ct))
            .GroupBy(h => h.RequestId).ToDictionary(g => g.Key, g => g.ToList());
        var workerIds = rows.Select(r => r.WorkerId).Distinct().ToList();
        var dates = rows.Select(r => r.WorkDate).Distinct().ToList();
        var beyond = await _db.AttendanceSessions.AsNoTracking().Where(s => workerIds.Contains(s.WorkerId) && dates.Contains(s.WorkDate))
            .GroupBy(s => new { s.WorkerId, s.WorkDate }).Select(g => new { g.Key.WorkerId, g.Key.WorkDate, Minutes = g.Sum(s => s.BeyondShiftMinutes) })
            .ToListAsync(ct);
        var policies = new Dictionary<Guid, AttendancePolicy>();
        var result = new List<OvertimeDto>();
        foreach (var o in rows)
        {
            if (!policies.TryGetValue(o.FactoryId, out var policy)) policies[o.FactoryId] = policy = await _support.PolicyAsync(o.FactoryId, ct);
            var (code, name) = names.GetValueOrDefault(o.WorkerId, ("?", "Unknown worker"));
            var warnings = await WarningsAsync(o, policy, ct);
            var suggestion = o.Status == OvertimeStatuses.Completed && o.ApprovedMinutes is { } a && o.ActualMinutes is { } act
                ? AttendanceCalculator.PayableSuggestion(a, act, policy.OvertimeRoundingMinutes, policy.MinOvertimeMinutes) : (int?)null;
            var mine = o.RequestedByUserId == caller.UserId;
            var inScope = AttendanceSupport.Reaches(caller, o.FactoryId, o.LineId);
            var can = new OvertimeCapabilities(
                Submit: o.Status == OvertimeStatuses.Requested && inScope && caller.Has(Permissions.OvertimeManage),
                Approve: o.Status == OvertimeStatuses.PendingApproval && !mine && inScope && caller.Has(Permissions.OvertimeApprove),
                Reject: o.Status == OvertimeStatuses.PendingApproval && !mine && inScope && caller.Has(Permissions.OvertimeApprove),
                Cancel: o.Status is OvertimeStatuses.Requested or OvertimeStatuses.PendingApproval or OvertimeStatuses.Approved && inScope && caller.Has(Permissions.OvertimeManage),
                ConfirmPayable: o.Status == OvertimeStatuses.Completed && inScope && caller.Has(Permissions.AttendanceManage));
            result.Add(new OvertimeDto(o.Id, o.WorkerId, code, name, o.FactoryId, o.LineId, o.WorkDate, o.PlannedStart, o.PlannedEnd, o.RequestedMinutes,
                o.Reason, o.Status, o.ApprovedMinutes, o.ApprovedStart, o.ApprovedEnd, o.ActualMinutes,
                beyond.Where(b => b.WorkerId == o.WorkerId && b.WorkDate == o.WorkDate).Sum(b => b.Minutes),
                suggestion, o.PayableMinutes, o.PayableNote, o.PayableConfirmedByEmail, o.RequestedByEmail, warnings,
                (history.GetValueOrDefault(o.Id) ?? new()).Select(h => new OvertimeApprovalDto(h.Id, h.Action, h.FromStatus, h.ToStatus, h.Minutes, h.Note, h.ActorEmail, h.OccurredAt)).ToList(),
                can, o.CreatedAt));
        }
        return result;
    }

    /// <summary>Review aids against the factory's configured limits (not a legal determination).</summary>
    private async Task<IReadOnlyList<string>> WarningsAsync(OvertimeRequest o, AttendancePolicy policy, CancellationToken ct)
    {
        var warnings = new List<string>();
        var counted = new[] { OvertimeStatuses.PendingApproval, OvertimeStatuses.Approved, OvertimeStatuses.InProgress, OvertimeStatuses.Completed };
        var weekStart = o.WorkDate.AddDays(-(int)o.WorkDate.DayOfWeek);
        var week = await _db.OvertimeRequests.AsNoTracking()
            .Where(x => x.WorkerId == o.WorkerId && counted.Contains(x.Status) && x.WorkDate >= weekStart && x.WorkDate < weekStart.AddDays(7))
            .Select(x => new { x.WorkDate, Minutes = x.ApprovedMinutes ?? x.RequestedMinutes }).ToListAsync(ct);
        var day = week.Where(x => x.WorkDate == o.WorkDate).Sum(x => x.Minutes);
        if (day > policy.MaxDailyOvertimeMinutes)
            warnings.Add($"{day} min of overtime on {o.WorkDate:yyyy-MM-dd} exceeds the factory's daily limit of {policy.MaxDailyOvertimeMinutes} min.");
        var weekly = week.Sum(x => x.Minutes);
        if (weekly > policy.MaxWeeklyOvertimeMinutes)
            warnings.Add($"{weekly} min of overtime in the week exceeds the factory's weekly limit of {policy.MaxWeeklyOvertimeMinutes} min.");
        return warnings;
    }
}
