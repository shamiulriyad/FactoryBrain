using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Attendance;

/// <summary>
/// The factory holiday calendar. HR adds public and factory holidays; attendance
/// statistics, analytics and the HR documents then expect nobody on those days.
/// Holidays on or before today can't be removed: absence already reported for them
/// must stay as reported (history is not rewritten).
/// </summary>
public sealed class HolidayService : IHolidayService
{
    private static readonly string[] Kinds = { "PUBLIC", "FACTORY" };
    private readonly BunonBrainDbContext _db;
    public HolidayService(BunonBrainDbContext db) { _db = db; }

    private async Task<Factory?> FactoryAsync(AccessContext c, Guid? factoryId, CancellationToken ct)
    {
        if (factoryId is { } id) return c.CanAccessFactory(id) ? await _db.Factories.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct) : null;
        var mine = c.FactoryIds.ToList();
        var q = _db.Factories.AsNoTracking().Where(f => f.IsActive);
        if (!c.IsPlatform) q = q.Where(f => mine.Contains(f.Id));
        return await q.OrderBy(f => f.Code).FirstOrDefaultAsync(ct);
    }

    private async Task<DateOnly> TodayAsync(Guid factoryId, CancellationToken ct)
    {
        var tzId = await _db.AttendancePolicies.AsNoTracking().Where(p => p.FactoryId == factoryId).Select(p => p.TimeZoneId).FirstOrDefaultAsync(ct) ?? AttendanceRules.DefaultTimeZone;
        var zone = AttendanceRules.ResolveTimeZone(tzId) ?? AttendanceRules.ResolveTimeZone(AttendanceRules.DefaultTimeZone) ?? TimeZoneInfo.Utc;
        return AttendanceCalculator.LocalDate(DateTimeOffset.UtcNow, zone);
    }

    private static bool DocScopeUnique(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation };

    private static HolidayDto Dto(FactoryHoliday h, DateOnly today) => new(h.Id, h.FactoryId, h.Date, h.Name, h.Kind, h.CreatedAt, h.CreatedByEmail, h.Date > today);

    public async Task<AccessResult<IReadOnlyList<HolidayDto>>> ListAsync(AccessContext caller, Guid? factoryId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, factoryId, ct);
        if (factory is null) return AccessResult<IReadOnlyList<HolidayDto>>.NotFound("factory_not_found", "No factory in your scope.");
        var today = await TodayAsync(factory.Id, ct);
        var f = from ?? new DateOnly(today.Year, 1, 1);
        var t = to ?? new DateOnly(today.Year, 12, 31);
        if (t < f || t.DayNumber - f.DayNumber > 800) return AccessResult<IReadOnlyList<HolidayDto>>.Invalid("invalid_period", "Choose a period of at most about two years.");
        var rows = await _db.FactoryHolidays.AsNoTracking().Where(h => h.FactoryId == factory.Id && h.Date >= f && h.Date <= t).OrderBy(h => h.Date).ToListAsync(ct);
        return AccessResult<IReadOnlyList<HolidayDto>>.Ok(rows.Select(h => Dto(h, today)).ToList());
    }

    public async Task<AccessResult<HolidayDto>> CreateAsync(AccessContext caller, HolidayWrite body, string? traceId, CancellationToken ct)
    {
        var factory = await FactoryAsync(caller, body.FactoryId, ct);
        if (factory is null) return AccessResult<HolidayDto>.NotFound("factory_not_found", "No factory in your scope.");
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120) return AccessResult<HolidayDto>.Invalid("invalid_name", "Give the holiday a name (at most 120 characters).");
        var kind = (body.Kind ?? "PUBLIC").Trim().ToUpperInvariant();
        if (!Kinds.Contains(kind)) return AccessResult<HolidayDto>.Invalid("invalid_kind", "Kind must be PUBLIC or FACTORY.");
        var today = await TodayAsync(factory.Id, ct);
        if (body.Date.DayNumber < today.DayNumber - 400 || body.Date.DayNumber > today.DayNumber + 800)
            return AccessResult<HolidayDto>.Invalid("invalid_date", "The date must be within about a year back and two years ahead.");
        if (await _db.FactoryHolidays.AnyAsync(h => h.FactoryId == factory.Id && h.Date == body.Date, ct))
            return AccessResult<HolidayDto>.Conflict("holiday_exists", $"{body.Date:yyyy-MM-dd} is already a holiday.");
        var h = new FactoryHoliday { FactoryId = factory.Id, Date = body.Date, Name = name, Kind = kind, CreatedByEmail = caller.Email };
        _db.FactoryHolidays.Add(h);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "attendance.holiday_added", "factory_holiday", h.Id.ToString(), null, $"{h.Date:yyyy-MM-dd} {h.Name} ({h.Kind})",
            body.Date <= today ? "Holiday added for a past day: attendance of that day is recalculated" : "Holiday added", traceId, factory.Id));
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (DocScopeUnique(ex))
        {
            return AccessResult<HolidayDto>.Conflict("holiday_exists", $"{body.Date:yyyy-MM-dd} is already a holiday.");
        }
        return AccessResult<HolidayDto>.Created(Dto(h, today));
    }

    public async Task<AccessResult<HolidayDto>> RemoveAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct)
    {
        var h = await _db.FactoryHolidays.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is null || !caller.CanAccessFactory(h.FactoryId)) return AccessResult<HolidayDto>.NotFound("holiday_not_found", "Holiday not found.");
        var today = await TodayAsync(h.FactoryId, ct);
        if (h.Date <= today)
            return AccessResult<HolidayDto>.Conflict("holiday_in_past", "A holiday that has started can't be removed: attendance already reported for it would change.");
        _db.FactoryHolidays.Remove(h);
        _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "attendance.holiday_removed", "factory_holiday", h.Id.ToString(), $"{h.Date:yyyy-MM-dd} {h.Name} ({h.Kind})", null,
            "Holiday removed", traceId, h.FactoryId));
        await _db.SaveChangesAsync(ct);
        return AccessResult<HolidayDto>.Ok(Dto(h, today));
    }
}
