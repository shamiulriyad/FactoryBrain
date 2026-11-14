using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Monitoring;

/// <summary>
/// Alert queries and the alert lifecycle (see <see cref="IMonitoringAlertService"/>).
/// Each action checks reach, the current state and (optionally) the version
/// the person was looking at, then writes the new state, a history entry and
/// an audit entry in one transaction, and pushes the change.
/// </summary>
public sealed class MonitoringAlertService : IMonitoringAlertService
{
    private readonly BunonBrainDbContext _db;
    private readonly IMonitoringPublisher _publisher;

    public MonitoringAlertService(BunonBrainDbContext db, IMonitoringPublisher publisher) { _db = db; _publisher = publisher; }

    private static AccessResult<MonitoringAlertDetailDto> NotFound() => AccessResult<MonitoringAlertDetailDto>.NotFound("alert_not_found", "Alert not found.");
    private static AccessResult<MonitoringAlertDetailDto> Invalid(string code, string message) => AccessResult<MonitoringAlertDetailDto>.Invalid(code, message);

    // ─── Queries ───────────────────────────────────────────────────────────

    public async Task<PagedResult<MonitoringAlertDto>> ListAsync(AccessContext caller, MonitoringAlertQuery q, CancellationToken ct)
    {
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, MonitoringRules.MaxPageSize);
        var alerts = Filter(MonitoringQueries.AlertsInScope(_db, caller), q);
        var total = await alerts.CountAsync(ct);
        alerts = q.Sort?.Trim().ToLowerInvariant() switch
        {
            "oldest" => alerts.OrderBy(a => a.CreatedAt).ThenBy(a => a.Number),
            "updated" => alerts.OrderByDescending(a => a.UpdatedAt).ThenByDescending(a => a.Number),
            // CRITICAL first; Postgres orders the CASE before the date.
            "severity" => alerts.OrderByDescending(a => a.Severity == AlertSeverities.Critical ? 4 : a.Severity == AlertSeverities.High ? 3 : a.Severity == AlertSeverities.Medium ? 2 : 1)
                .ThenByDescending(a => a.LastOccurredAt),
            _ => alerts.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Number),
        };
        var rows = await alerts.Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<MonitoringAlertDto>(await MonitoringQueries.AlertDtosAsync(_db, caller, rows, ct), page, size, total);
    }

    internal static IQueryable<MonitoringAlert> Filter(IQueryable<MonitoringAlert> alerts, MonitoringAlertQuery q)
    {
        if (q.FactoryId is { } f) alerts = alerts.Where(a => a.FactoryId == f);
        if (q.DepartmentId is { } d) alerts = alerts.Where(a => a.DepartmentId == d);
        if (q.FloorId is { } fl) alerts = alerts.Where(a => a.FloorId == fl);
        if (!string.IsNullOrWhiteSpace(q.LineId)) { var line = q.LineId.Trim(); alerts = alerts.Where(a => a.LineId == line); }
        if (q.CameraId is { } c) alerts = alerts.Where(a => a.CameraId == c);
        if (q.DeviceId is { } dv) alerts = alerts.Where(a => a.DeviceId == dv);
        if (string.Equals(q.Status?.Trim(), "ACTIVE", StringComparison.OrdinalIgnoreCase))
            alerts = alerts.Where(a => AlertStatuses.Active.Contains(a.Status));
        else if (AlertStatuses.Parse(q.Status) is { } s) alerts = alerts.Where(a => a.Status == s);
        if (AlertSeverities.Parse(q.Severity) is { } sev) alerts = alerts.Where(a => a.Severity == sev);
        if (!string.IsNullOrWhiteSpace(q.AlertType)) { var t = q.AlertType.Trim().ToUpperInvariant(); alerts = alerts.Where(a => a.AlertType == t); }
        if (MonitoringSourceTypes.Parse(q.SourceType) is { } src) alerts = alerts.Where(a => a.SourceType == src);
        if (MonitoringCategories.Parse(q.Category) is { } cat) alerts = alerts.Where(a => a.Category == cat);
        if (q.From is { } from) { var u = from.ToUniversalTime(); alerts = alerts.Where(a => a.LastOccurredAt >= u); }
        if (q.To is { } to) { var u = to.ToUniversalTime(); alerts = alerts.Where(a => a.CreatedAt <= u); }
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim();
            var pattern = "%" + OrganizationService.Like(term) + "%";
            long? number = null;
            var digits = term.ToUpperInvariant().Replace("ALT-", "");
            if (long.TryParse(digits, out var n)) number = n;
            alerts = alerts.Where(a => EF.Functions.ILike(a.Title, pattern, "\\") || EF.Functions.ILike(a.Description, pattern, "\\") || (number != null && a.Number == number));
        }
        return alerts;
    }

    public async Task<AccessResult<MonitoringAlertDetailDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var a = await MonitoringQueries.AlertsInScope(_db, caller).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        var dto = (await MonitoringQueries.AlertDtosAsync(_db, caller, new[] { a }, ct))[0];
        var history = (await _db.MonitoringAlertActions.AsNoTracking().Where(x => x.AlertId == id).OrderBy(x => x.At).ThenBy(x => x.Id).ToListAsync(ct))
            .Select(MonitoringQueries.ActionDto).ToList();
        var eventIds = history.Where(h => h.EventId is not null).Select(h => h.EventId!.Value).Append(a.EventId).Distinct().ToList();
        var events = await MonitoringQueries.EventsInScope(_db, caller).Where(e => eventIds.Contains(e.Id))
            .OrderByDescending(e => e.OccurredAt).Take(20).ToListAsync(ct);
        var dtos = await MonitoringQueries.EventDtosAsync(_db, events, ct);
        return AccessResult<MonitoringAlertDetailDto>.Ok(new MonitoringAlertDetailDto(dto, history, dtos.FirstOrDefault(e => e.Id == a.EventId), dtos));
    }

    // ─── Lifecycle ─────────────────────────────────────────────────────────

    public Task<AccessResult<MonitoringAlertDetailDto>> AcknowledgeAsync(AccessContext caller, Guid id, AcknowledgeAlertRequest r, string? traceId, CancellationToken ct)
    {
        var note = Clean(r.Note);
        if (note is { Length: > MonitoringRules.NoteMax }) return Task.FromResult(Invalid("note_too_long", $"Notes are at most {MonitoringRules.NoteMax} characters."));
        return ActAsync(caller, id, AlertActions.Acknowledge, r.ExpectedVersion, note, traceId, ct, (a, now) =>
        {
            a.AcknowledgedAt = now;
            a.AcknowledgedByUserId = caller.UserId;
            a.AcknowledgedByEmail = caller.Email;
            return (null, null);
        });
    }

    public Task<AccessResult<MonitoringAlertDetailDto>> EscalateAsync(AccessContext caller, Guid id, EscalateAlertRequest r, string? traceId, CancellationToken ct)
    {
        var target = AlertEscalationTargets.Parse(r.ToRole);
        if (target is null)
            return Task.FromResult(Invalid("escalation_target_invalid", $"Escalate to one of: {string.Join(", ", AlertEscalationTargets.All)}."));
        var reason = Clean(r.Reason);
        if (reason is null) return Task.FromResult(Invalid("reason_required", "Say why the alert is being escalated."));
        if (reason.Length > MonitoringRules.NoteMax) return Task.FromResult(Invalid("note_too_long", $"Notes are at most {MonitoringRules.NoteMax} characters."));
        return ActAsync(caller, id, AlertActions.Escalate, r.ExpectedVersion, reason, traceId, ct, (a, now) =>
        {
            a.EscalationLevel++;
            a.EscalatedAt = now;
            a.EscalatedByEmail = caller.Email;
            a.EscalatedToRole = target;
            return (target, null);
        });
    }

    public Task<AccessResult<MonitoringAlertDetailDto>> ResolveAsync(AccessContext caller, Guid id, ResolveAlertRequest r, string? traceId, CancellationToken ct)
    {
        var code = AlertResolutionCodes.Parse(r.ResolutionCode);
        if (code is null) return Task.FromResult(Invalid("resolution_code_invalid", $"Resolution: one of {string.Join(", ", AlertResolutionCodes.All)}."));
        var note = Clean(r.Note);
        if (note is null) return Task.FromResult(Invalid("note_required", "Describe what was found and done."));
        if (note.Length > MonitoringRules.NoteMax) return Task.FromResult(Invalid("note_too_long", $"Notes are at most {MonitoringRules.NoteMax} characters."));
        return ActAsync(caller, id, AlertActions.Resolve, r.ExpectedVersion, note, traceId, ct, (a, now) =>
        {
            a.ResolvedAt = now;
            a.ResolvedByEmail = caller.Email;
            a.ResolutionCode = code;
            a.ResolutionNote = note;
            return (null, code);
        });
    }

    public Task<AccessResult<MonitoringAlertDetailDto>> DismissAsync(AccessContext caller, Guid id, DismissAlertRequest r, string? traceId, CancellationToken ct)
    {
        var reason = Clean(r.Reason);
        if (reason is null || reason.Length < 5) return Task.FromResult(Invalid("reason_required", "Give a reason (at least 5 characters) for dismissing the alert."));
        if (reason.Length > MonitoringRules.NoteMax) return Task.FromResult(Invalid("note_too_long", $"Notes are at most {MonitoringRules.NoteMax} characters."));
        return ActAsync(caller, id, AlertActions.Dismiss, r.ExpectedVersion, reason, traceId, ct, (a, now) =>
        {
            a.DismissedAt = now;
            a.DismissedByEmail = caller.Email;
            a.DismissReason = reason;
            return (null, null);
        });
    }

    /// <param name="apply">Sets the action's own fields; returns (escalated-to role, resolution code) for the history entry.</param>
    private async Task<AccessResult<MonitoringAlertDetailDto>> ActAsync(AccessContext caller, Guid id, string action, int? expectedVersion, string? note,
        string? traceId, CancellationToken ct, Func<MonitoringAlert, DateTimeOffset, (string? EscalatedTo, string? Resolution)> apply)
    {
        if (!await MonitoringQueries.AlertsInScope(_db, caller).AnyAsync(x => x.Id == id, ct)) return NotFound();
        var a = await _db.MonitoringAlerts.FirstAsync(x => x.Id == id, ct);
        if (expectedVersion is { } v && v != a.Version)
            return AccessResult<MonitoringAlertDetailDto>.Conflict("alert_changed", "Someone else changed this alert. Reload it and try again.");
        var t = MonitoringRules.Transition(a.Status, action, a.EscalationLevel);
        if (!t.Allowed) return AccessResult<MonitoringAlertDetailDto>.Conflict(t.Code!, t.Message!);

        var now = DateTimeOffset.UtcNow;
        var from = a.Status;
        var (escalatedTo, resolution) = apply(a, now);
        a.Status = t.ToStatus!;
        a.UpdatedAt = now;
        a.Version++;
        _db.MonitoringAlertActions.Add(new MonitoringAlertAction
        {
            AlertId = a.Id, FactoryId = a.FactoryId, Action = action, FromStatus = from, ToStatus = a.Status, Note = note,
            EscalatedToRole = escalatedTo, ResolutionCode = resolution, ActorUserId = caller.UserId, ActorEmail = caller.Email,
            ActorRole = caller.PrimaryRole, At = now,
        });
        var verb = action.ToLowerInvariant();
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, $"monitoring_alert.{verb}", AccessAudit.EntityMonitoringAlert, a.Id.ToString(), from, a.Status,
            $"{MonitoringQueries.AlertCode(a.Number)} {a.AlertType} ({a.SourceType.ToLowerInvariant()}): {from} → {a.Status}"
            + (escalatedTo is null ? "" : $" to {escalatedTo}") + (resolution is null ? "" : $" [{resolution}]") + (note is null ? "" : $" — {note}"),
            traceId, a.FactoryId));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            _db.ChangeTracker.Clear();
            return AccessResult<MonitoringAlertDetailDto>.Conflict("alert_changed", "Someone else changed this alert at the same moment. Reload it and try again.");
        }
        _db.ChangeTracker.Clear();
        var fresh = await _db.MonitoringAlerts.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        var pushed = (await MonitoringQueries.AlertDtosAsync(_db, null, new[] { fresh }, ct))[0];
        await _publisher.AlertAsync(pushed, verb, ct);
        return await GetAsync(caller, id, ct);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
