using System.Text.Json;
using BunonBrain.Application.Access;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Planning;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BunonBrain.Infrastructure.Planning;

/// <summary>Who may read or change which planning records. Every planning query starts here.</summary>
internal static class PlanningScope
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Orders in the caller's factories (orders recorded before factories existed have none and stay visible).</summary>
    public static IQueryable<ProductionOrder> Orders(BunonBrainDbContext db, AccessContext c, bool tracking = false)
    {
        var q = tracking ? db.ProductionOrders.AsQueryable() : db.ProductionOrders.AsNoTracking();
        if (c.IsPlatform) return q;
        var f = c.FactoryIds.ToList();
        return q.Where(o => o.FactoryId == null || f.Contains(o.FactoryId.Value));
    }

    public static IQueryable<ProductionAllocation> Allocations(BunonBrainDbContext db, AccessContext c, bool tracking = false)
    {
        var q = tracking ? db.ProductionAllocations.AsQueryable() : db.ProductionAllocations.AsNoTracking();
        if (c.IsPlatform) return q;
        var f = c.FactoryIds.ToList();
        q = q.Where(a => f.Contains(a.FactoryId));
        if (c.IsLineScoped) { var l = c.LineIds.ToList(); q = q.Where(a => l.Contains(a.LineId)); }
        return q;
    }

    public static IQueryable<ProductionTarget> Targets(BunonBrainDbContext db, AccessContext c, bool tracking = false)
    {
        var q = tracking ? db.ProductionTargets.AsQueryable() : db.ProductionTargets.AsNoTracking();
        if (c.IsPlatform) return q;
        var f = c.FactoryIds.ToList();
        q = q.Where(t => f.Contains(t.FactoryId));
        if (c.IsLineScoped) { var l = c.LineIds.ToList(); q = q.Where(t => t.LineId == null || l.Contains(t.LineId)); }
        return q;
    }

    public static IQueryable<DailyProductionPlan> Plans(BunonBrainDbContext db, AccessContext c, bool tracking = false)
    {
        var q = tracking ? db.DailyProductionPlans.AsQueryable() : db.DailyProductionPlans.AsNoTracking();
        if (!c.IsPlatform) { var f = c.FactoryIds.ToList(); q = q.Where(p => f.Contains(p.FactoryId)); }
        // People who only follow the plan see what is in force (and what it replaced), never drafts.
        if (!SeesDrafts(c)) q = q.Where(p => p.Status == PlanStatuses.Published || p.Status == PlanStatuses.Revised);
        return q;
    }

    public static IQueryable<ProductionException> Exceptions(BunonBrainDbContext db, AccessContext c, bool tracking = false)
    {
        var q = tracking ? db.ProductionExceptions.AsQueryable() : db.ProductionExceptions.AsNoTracking();
        if (c.IsPlatform) return q;
        var f = c.FactoryIds.ToList();
        q = q.Where(e => f.Contains(e.FactoryId));
        if (c.IsLineScoped)
        {
            var l = c.LineIds.ToList();
            q = q.Where(e => (e.LineId != null && l.Contains(e.LineId))
                             || (e.LineId == null && e.OrderId != null && db.ProductionAllocations.Any(a => a.OrderId == e.OrderId && l.Contains(a.LineId))));
        }
        return q;
    }

    public static bool SeesDrafts(AccessContext c) => c.HasAny(Permissions.PlanningPlansManage, Permissions.PlanningPlansApprove, Permissions.PlanningPlansPublish);

    /// <summary>The caller reaches this factory and (for line-scoped callers) this line.</summary>
    public static bool CanActOnLine(AccessContext c, string lineId, Guid? lineFactoryId) => c.CanAccessLine(lineId, lineFactoryId);

    public static bool Reaches(AccessContext c, Guid factoryId) => c.CanAccessFactory(factoryId);

    /// <summary>Loads a line and confirms it belongs to <paramref name="factoryId"/>.</summary>
    public static async Task<ProductionLine?> LineInFactoryAsync(BunonBrainDbContext db, string lineId, Guid factoryId, CancellationToken ct) =>
        await db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(l => l.Id == lineId && l.FactoryId == factoryId, ct);

    /// <summary>Serialises writes about one subject (an order's allocations, a factory's targets) for the rest of the transaction.</summary>
    public static Task LockAsync(BunonBrainDbContext db, string key, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"planning:" + key}, 0))", ct);

    public static bool IsUniqueViolation(DbUpdateException ex, string? constraint = null) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && (constraint is null || (pg.ConstraintName?.Contains(constraint, StringComparison.Ordinal) ?? false));

    public static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd");

    public static bool TryDate(string? raw, out DateOnly d) =>
        DateOnly.TryParseExact(raw?.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d);

    public static string Describe<T>(T value) => JsonSerializer.Serialize(value, Json);
}

/// <summary>A factory's operational time: its zone, working days and shifts, loaded once per request.</summary>
internal sealed class FactoryClock
{
    public Guid FactoryId { get; }
    public TimeZoneInfo Zone { get; }
    public string ZoneId { get; }
    public IReadOnlyList<PlanShift> Shifts { get; }

    private FactoryClock(Guid factoryId, TimeZoneInfo zone, string zoneId, IReadOnlyList<PlanShift> shifts) { FactoryId = factoryId; Zone = zone; ZoneId = zoneId; Shifts = shifts; }

    public static async Task<FactoryClock> LoadAsync(BunonBrainDbContext db, Guid factoryId, CancellationToken ct)
    {
        var tzId = await db.AttendancePolicies.AsNoTracking().Where(p => p.FactoryId == factoryId).Select(p => p.TimeZoneId).FirstOrDefaultAsync(ct) ?? AttendanceRules.DefaultTimeZone;
        // Route every lookup through the attendance rules: the API runs with invariant globalization, where IANA ids don't resolve by themselves.
        var zone = AttendanceRules.ResolveTimeZone(tzId) ?? AttendanceRules.ResolveTimeZone(AttendanceRules.DefaultTimeZone) ?? TimeZoneInfo.Utc;
        var shifts = await db.Shifts.AsNoTracking().Include(s => s.Breaks).Where(s => s.FactoryId == factoryId && s.IsActive).OrderBy(s => s.StartLocal).ToListAsync(ct);
        return new FactoryClock(factoryId, zone, tzId, shifts.Select(s => new PlanShift(s.Id, s.Code, s.Name, s.StartLocal, s.EndLocal, s.WorkingDays,
            s.Breaks.Select(b => new ScheduleBreak(b.StartLocal, b.DurationMinutes)).ToList())).ToList());
    }

    public DateOnly Today(DateTimeOffset now) => AttendanceCalculator.LocalDate(now, Zone);

    /// <summary>A day the factory produces: some active shift works that weekday.</summary>
    public bool IsWorkingDay(DateOnly d) => Shifts.Any(s => s.WorksOn(d.DayOfWeek));

    public PlanShift? Shift(Guid id) => Shifts.FirstOrDefault(s => s.Id == id);

    public ShiftSchedule? Schedule(Guid shiftId, DateOnly date) => Shift(shiftId) is { } s ? new ShiftSchedule(s, date, Zone) : null;

    /// <summary>Productive minutes of the factory on a date (all shifts that work that day).</summary>
    public double ProductiveMinutes(DateOnly date) =>
        Shifts.Where(s => s.WorksOn(date.DayOfWeek)).Sum(s => new ShiftSchedule(s, date, Zone).TotalProductiveMinutes);

    /// <summary>UTC window of the factory-local day.</summary>
    public (DateTimeOffset Start, DateTimeOffset End) DayWindow(DateOnly date) =>
        (AttendanceCalculator.ToUtc(date, TimeOnly.MinValue, Zone), AttendanceCalculator.ToUtc(date.AddDays(1), TimeOnly.MinValue, Zone));
}

/// <summary>What a line's output counters (plus audited corrections) say for one line, date and shift.</summary>
internal sealed record CounterReading(int Pieces, int Events, int CorrectionPieces, bool Alive, DateTimeOffset? LastEventAt)
{
    public bool HasEvidence => Events > 0 || CorrectionPieces != 0 || Alive;
    public int Total => Pieces + CorrectionPieces;
}

/// <summary>
/// Actual production for planning, read from the IoT production counters
/// (the existing append-only events, stored once per device and message id,
/// with counter resets already handled) and audited corrections. Only
/// counter devices designated as a line's output point count: a line's other
/// machine counters measure operations, not finished garments. REAL and
/// SIMULATED are never mixed. Where a line has no output point, output
/// recorded by people (the production module's output records) is used and
/// labelled RECORDED; otherwise there is no evidence and the actual is empty,
/// not zero.
/// </summary>
internal sealed class PlanningActuals
{
    private readonly BunonBrainDbContext _db;

    public PlanningActuals(BunonBrainDbContext db) { _db = db; }

    public sealed record Point(Guid Id, Guid DeviceId, Guid? MachineId, Guid? WorkstationId, bool Simulated);

    /// <summary>Active output points per line for one source (REAL devices or SIMULATED devices).</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Point>>> PointsAsync(Guid factoryId, string source, CancellationToken ct)
    {
        var simulated = source == IotSourceTypes.Simulated;
        var rows = await (from p in _db.LineOutputPoints.AsNoTracking()
                          join d in _db.Devices.AsNoTracking() on p.DeviceId equals d.Id
                          where p.FactoryId == factoryId && p.IsActive && d.IsSimulated == simulated && d.Status != "Disabled"
                          select new { p.LineId, Point = new Point(p.Id, d.Id, d.MachineId, d.WorkstationId, d.IsSimulated) }).ToListAsync(ct);
        return rows.GroupBy(r => r.LineId).ToDictionary(g => g.Key, g => (IReadOnlyList<Point>)g.Select(x => x.Point).ToList());
    }

    /// <summary>Counter readings by (line, production date, shift) over a date range; shift is null outside every shift.</summary>
    public async Task<IReadOnlyDictionary<(string Line, DateOnly Date, Guid? Shift), CounterReading>> ReadAsync(
        Guid factoryId, DateOnly fromDate, DateOnly toDate, string source, IReadOnlyDictionary<string, IReadOnlyList<Point>> points, FactoryClock clock, DateTimeOffset now,
        IReadOnlyCollection<string>? onlyLines, CancellationToken ct)
    {
        var result = new Dictionary<(string, DateOnly, Guid?), CounterReading>();
        var lines = points.Keys.Where(l => onlyLines is null || onlyLines.Contains(l)).ToList();
        if (lines.Count == 0) return result;
        var pairs = points.Where(kv => lines.Contains(kv.Key)).SelectMany(kv => kv.Value.Select(p => new { Line = kv.Key, p.DeviceId })).ToList();
        var deviceIds = pairs.Select(p => p.DeviceId).Distinct().ToList();

        // Counter events of the designated devices, on the line they are designated for.
        var events = await (from e in _db.ProductionCounterEvents.AsNoTracking()
                            join p in _db.LineOutputPoints.AsNoTracking() on new { e.DeviceId, e.LineId } equals new { p.DeviceId, p.LineId }
                            where p.IsActive && e.FactoryId == factoryId && e.SourceType == source && e.ProductionDate >= fromDate && e.ProductionDate <= toDate && lines.Contains(e.LineId)
                            group e by new { e.LineId, e.ProductionDate, e.ShiftId } into g
                            select new { g.Key.LineId, g.Key.ProductionDate, g.Key.ShiftId, Pieces = g.Sum(x => x.Pieces), Events = g.Count(), Last = g.Max(x => x.OccurredAt) }).ToListAsync(ct);

        // Corrections to those counters (or to the line as a whole), audited with a reason.
        var machineIds = points.Where(kv => lines.Contains(kv.Key)).SelectMany(kv => kv.Value).Where(p => p.MachineId != null).Select(p => p.MachineId!.Value).Distinct().ToList();
        var wsIds = points.Where(kv => lines.Contains(kv.Key)).SelectMany(kv => kv.Value).Where(p => p.WorkstationId != null).Select(p => p.WorkstationId!.Value).Distinct().ToList();
        var corrections = await _db.ProductionCorrections.AsNoTracking()
            .Where(c => c.FactoryId == factoryId && c.SourceType == source && c.ProductionDate >= fromDate && c.ProductionDate <= toDate && lines.Contains(c.LineId)
                        && ((c.MachineId == null && c.WorkstationId == null) || (c.MachineId != null && machineIds.Contains(c.MachineId.Value))
                            || (c.WorkstationId != null && wsIds.Contains(c.WorkstationId.Value))))
            .Select(c => new { c.LineId, c.ProductionDate, c.ShiftId, c.PiecesDelta }).ToListAsync(ct);

        var keys = events.Select(e => (e.LineId, e.ProductionDate, e.ShiftId)).Concat(corrections.Select(c => (c.LineId, c.ProductionDate, c.ShiftId))).Distinct().ToList();
        foreach (var k in keys)
        {
            var ev = events.FirstOrDefault(e => e.LineId == k.LineId && e.ProductionDate == k.ProductionDate && e.ShiftId == k.ShiftId);
            var corr = corrections.Where(c => c.LineId == k.LineId && c.ProductionDate == k.ProductionDate && c.ShiftId == k.ShiftId).Sum(c => c.PiecesDelta);
            result[(k.LineId, k.ProductionDate, k.ShiftId)] = new CounterReading(ev?.Pieces ?? 0, ev?.Events ?? 0, corr, false, ev?.Last);
        }

        // "No counts" is only zero production when the counter was alive: any heartbeat or device event of an output device in the window.
        var window = (From: clock.DayWindow(fromDate).Start, To: clock.DayWindow(toDate).End < now ? clock.DayWindow(toDate).End : now);
        var beats = await _db.DeviceHeartbeats.AsNoTracking().Where(h => deviceIds.Contains(h.DeviceId) && h.OccurredAt >= window.From && h.OccurredAt < window.To)
            .Select(h => new { h.DeviceId, h.OccurredAt }).ToListAsync(ct);
        AliveBeats = beats.Select(b => (b.DeviceId, b.OccurredAt)).ToList();
        AliveMap = pairs.GroupBy(p => p.Line).ToDictionary(g => g.Key, g => g.Select(x => x.DeviceId).ToHashSet());
        return result;
    }

    // Heartbeat evidence from the last Read, for Alive checks per shift window.
    internal IReadOnlyList<(Guid DeviceId, DateTimeOffset At)> AliveBeats { get; private set; } = Array.Empty<(Guid, DateTimeOffset)>();
    internal IReadOnlyDictionary<string, HashSet<Guid>> AliveMap { get; private set; } = new Dictionary<string, HashSet<Guid>>();

    /// <summary>Whether an output device of the line reported (heartbeat) between two instants.</summary>
    public bool WasAlive(string lineId, DateTimeOffset from, DateTimeOffset to) =>
        AliveMap.TryGetValue(lineId, out var devices) && AliveBeats.Any(b => devices.Contains(b.DeviceId) && b.At >= from && b.At < to);

    /// <summary>Good pieces recorded by people (production output records) per (order, line, date, factory shift via the production shift code).</summary>
    public async Task<IReadOnlyList<RecordedOutput>> RecordedAsync(IReadOnlyCollection<Guid> orderIds, DateOnly fromDate, DateOnly toDate, IReadOnlyCollection<string>? lines, Guid factoryId,
        CancellationToken ct)
    {
        if (orderIds.Count == 0) return Array.Empty<RecordedOutput>();
        var q = _db.ProductionOutputEvents.AsNoTracking()
            .Where(e => orderIds.Contains(e.OrderId) && !e.IsSuperseded && e.Stage == ProductionStage.SewingOutput && e.ProductionDate >= fromDate && e.ProductionDate <= toDate);
        if (lines is not null) q = q.Where(e => lines.Contains(e.LineId));
        var rows = await q.GroupBy(e => new { e.OrderId, e.LineId, e.ProductionDate, e.ShiftCode })
            .Select(g => new { g.Key.OrderId, g.Key.LineId, g.Key.ProductionDate, g.Key.ShiftCode, Good = g.Sum(x => x.Quantity) }).ToListAsync(ct);
        var shifts = await _db.Shifts.AsNoTracking().Where(s => s.FactoryId == factoryId && s.ProductionShiftCode != null).Select(s => new { s.Id, s.ProductionShiftCode }).ToListAsync(ct);
        return rows.Select(r => new RecordedOutput(r.OrderId, r.LineId, r.ProductionDate,
            r.ShiftCode is null ? null : shifts.FirstOrDefault(s => s.ProductionShiftCode == r.ShiftCode)?.Id, r.Good)).ToList();
    }
}

internal sealed record RecordedOutput(Guid OrderId, string LineId, DateOnly Date, Guid? ShiftId, int Good);
