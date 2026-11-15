using System.Globalization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Iot;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Devices;
using BunonBrain.Infrastructure.Monitoring;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BunonBrain.Infrastructure.Iot;

/// <summary>
/// Machine, production and device views. Simulated and real data are never
/// added together: every production figure is for one source (REAL by
/// default). Machine running time is reported as machine data only.
/// </summary>
public sealed class IotQueryService : IIotQueryService
{
    public const int MaxRangeDays = 93;
    public const int MaxHourlyDays = 3;

    public const string SeparationNote =
        "Machine data only: running time is the machine's, not a worker's working time; counts are machine output, not an individual's output. " +
        "Attendance, predicted activity and verified workstation identity are separate records.";

    private readonly BunonBrainDbContext _db;
    private readonly IMqttBridgeState _bridge;
    private readonly IotOptions _options;

    public IotQueryService(BunonBrainDbContext db, IMqttBridgeState bridge, IOptions<IotOptions> options)
    {
        _db = db; _bridge = bridge; _options = options.Value;
    }

    public IotBridgeStatusDto Bridge() => _bridge.Snapshot();

    // ─── Machines ──────────────────────────────────────────────────────────

    private IQueryable<Machine> Machines(AccessContext caller, IotFilter f)
    {
        var q = IotQueries.MachinesInScope(_db, caller).Where(m => m.IsActive);
        if (f.FactoryId is { } fid) q = q.Where(m => m.FactoryId == fid);
        if (f.MachineId is { } mid) q = q.Where(m => m.Id == mid);
        if (f.WorkstationId is { } wid) q = q.Where(m => m.WorkstationId == wid);
        if (!string.IsNullOrWhiteSpace(f.LineId)) { var l = f.LineId.Trim(); q = q.Where(m => _db.Workstations.Any(w => w.Id == m.WorkstationId && w.LineId == l)); }
        if (f.DepartmentId is { } dep) q = q.Where(m => _db.Workstations.Any(w => w.Id == m.WorkstationId && w.DepartmentId == dep));
        if (f.FloorId is { } fl) q = q.Where(m => _db.Workstations.Any(w => w.Id == m.WorkstationId && w.FloorId == fl));
        if (f.DeviceId is { } dev) q = q.Where(m => _db.Devices.Any(d => d.Id == dev && d.MachineId == m.Id));
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = "%" + f.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(m => EF.Functions.ILike(m.Code, s, "\\") || (m.MachineType != null && EF.Functions.ILike(m.MachineType, s, "\\")));
        }
        var source = IotSourceTypes.Parse(f.Source);
        if (source == IotSourceTypes.Simulated)
            q = q.Where(m => _db.Devices.Any(d => d.MachineId == m.Id && d.IsSimulated) && !_db.Devices.Any(d => d.MachineId == m.Id && !d.IsSimulated && d.Status != DeviceStatuses.Disabled));
        else if (source == IotSourceTypes.Real)
            q = q.Where(m => !_db.Devices.Any(d => d.MachineId == m.Id && d.IsSimulated) || _db.Devices.Any(d => d.MachineId == m.Id && !d.IsSimulated && d.Status != DeviceStatuses.Disabled));
        if (MachineStates.Parse(f.State) is { } st)
            q = st == MachineStates.Unknown
                ? q.Where(m => !_db.MachineStates.Any(s => s.MachineId == m.Id) || _db.MachineStates.Any(s => s.MachineId == m.Id && s.State == st))
                : q.Where(m => _db.MachineStates.Any(s => s.MachineId == m.Id && s.State == st));
        return q;
    }

    public async Task<PagedResult<MachineDto>> MachinesAsync(AccessContext caller, IotFilter f, CancellationToken ct)
    {
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 1, 500);
        var q = Machines(caller, f);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(m => m.Code).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<MachineDto>(await IotQueries.MachineDtosAsync(_db, rows, ct), page, size, total);
    }

    public async Task<MachineOverviewDto> OverviewAsync(AccessContext caller, IotFilter f, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var source = IotQueries.SourceOf(f.Source);
        var rows = await Machines(caller, f with { Source = source, State = null, Search = null }).OrderBy(m => m.Code).Take(500).ToListAsync(ct);
        var machines = await IotQueries.MachineDtosAsync(_db, rows, ct);
        var monitored = machines.Where(m => m.Monitored).ToList();
        int N(string s) => monitored.Count(m => m.State == s);
        var counts = new MachineCounts(machines.Count, N(MachineStates.Running), N(MachineStates.Stopped), N(MachineStates.Idle), N(MachineStates.Offline),
            N(MachineStates.Error), N(MachineStates.Maintenance), N(MachineStates.Unknown), machines.Count - monitored.Count);

        var canProduction = IotQueries.CanSeeProduction(caller);
        ProductionReportDto? today = null;
        if (canProduction)
        {
            var cal = await CalendarForAsync(caller, f.FactoryId, ct);
            var day = DateOnly.FromDateTime(cal.Local(now));
            today = await ProductionAsync(caller, f with { Source = source, From = cal.DayStartUtc(day), To = now, ShiftId = null }, ct);
        }

        var machineIds = rows.Select(m => m.Id).ToList();
        var deviceIds = await _db.Devices.AsNoTracking().Where(d => d.MachineId != null && machineIds.Contains(d.MachineId.Value)).Select(d => d.Id).ToListAsync(ct);
        var deviceEvents = await _db.DeviceEvents.AsNoTracking().Where(e => deviceIds.Contains(e.DeviceId))
            .OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(15).ToListAsync(ct);
        var codes = await _db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Code, ct);
        var recent = deviceEvents.Select(e => new RecentDeviceEventDto(e.Id, e.DeviceId, codes.GetValueOrDefault(e.DeviceId, "?"), e.EventType, e.Severity,
            e.Message, e.IsSimulated, e.OccurredAt)).ToList();

        var alertRows = await MonitoringQueries.AlertsInScope(_db, caller)
            .Where(a => AlertTypes.Machine.Contains(a.AlertType) && AlertStatuses.Active.Contains(a.Status) && a.SourceType == source)
            .Where(a => f.FactoryId == null || a.FactoryId == f.FactoryId)
            .OrderByDescending(a => a.LastOccurredAt).Take(20).ToListAsync(ct);
        var alerts = await MonitoringQueries.AlertDtosAsync(_db, caller, alertRows, ct);

        return new MachineOverviewDto(now, source, counts, machines, today?.TotalPieces ?? 0, today?.ByLine ?? Array.Empty<GroupCount>(),
            today?.ByShift ?? Array.Empty<GroupCount>(), today?.DowntimeSeconds ?? 0, recent, alerts, _bridge.Snapshot(), canProduction,
            (source == IotSourceTypes.Simulated ? "SIMULATED data from the IoT simulator, not from real machines. " : "") + SeparationNote);
    }

    public async Task<AccessResult<MachineDetailDto>> MachineAsync(AccessContext caller, Guid machineId, string? source, CancellationToken ct)
    {
        var m = await IotQueries.MachinesInScope(_db, caller).FirstOrDefaultAsync(x => x.Id == machineId, ct);
        if (m is null) return AccessResult<MachineDetailDto>.NotFound("machine_not_found", "Machine not found.");
        var dto = (await IotQueries.MachineDtosAsync(_db, new[] { m }, ct))[0];
        var profile = await _db.MachineMonitoringProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.MachineId == m.Id, ct);
        var src = IotSourceTypes.Parse(source) ?? (dto.IsSimulated ? IotSourceTypes.Simulated : IotSourceTypes.Real);
        var cal = await IotQueries.CalendarAsync(_db, m.FactoryId, ct);
        var now = DateTimeOffset.UtcNow;
        var start = cal.DayStartUtc(DateOnly.FromDateTime(cal.Local(now)));
        var canProduction = IotQueries.CanSeeProduction(caller);
        var today = (await MachineMetricsAsync(new[] { m }, start, now, src, canProduction, ct))[0];

        var transitions = await _db.MachineStateTransitions.AsNoTracking().Where(t => t.MachineId == m.Id).OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.Id)
            .Take(50).ToListAsync(ct);
        var telemetry = await _db.MachineTelemetry.AsNoTracking().Where(t => t.MachineId == m.Id).OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.Id)
            .Take(50).ToListAsync(ct);
        var counts = canProduction
            ? await IotQueries.CountsInScope(_db, caller).Where(e => e.MachineId == m.Id).OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Take(50).ToListAsync(ct)
            : new List<ProductionCounterEvent>();
        var alertRows = await MonitoringQueries.AlertsInScope(_db, caller).Where(a => a.MachineId == m.Id)
            .OrderByDescending(a => a.LastOccurredAt).Take(30).ToListAsync(ct);

        return AccessResult<MachineDetailDto>.Ok(new MachineDetailDto(dto, IotQueries.ProfileDto(m, profile), today,
            await TransitionDtosAsync(transitions, ct), await TelemetryDtosAsync(telemetry, ct), await CounterDtosAsync(counts, ct),
            await MonitoringQueries.AlertDtosAsync(_db, caller, alertRows, ct), await ContextAsync(caller, m, ct),
            IotQueries.CanConfigure(caller, m.FactoryId), IotQueries.CanCorrect(caller, m.FactoryId), canProduction));
    }

    /// <summary>The workstation's scheduled assignment and verified identity, side by side with (never merged into) the machine data.</summary>
    private async Task<MachineContextDto?> ContextAsync(AccessContext caller, Machine m, CancellationToken ct)
    {
        if (m.WorkstationId is not { } ws) return null;
        MonRef? assigned = null;
        DateTimeOffset? since = null;
        if (caller.Has(Permissions.WorkersView))
        {
            var a = await _db.Assignments.AsNoTracking().Where(x => x.WorkstationId == ws && x.EndAt == null).OrderByDescending(x => x.StartAt).FirstOrDefaultAsync(ct);
            if (a is not null)
            {
                var w = await _db.Workers.AsNoTracking().Where(x => x.Id == a.WorkerId).Select(x => new { x.Id, x.WorkerCode, x.FullName }).FirstOrDefaultAsync(ct);
                if (w is not null) { assigned = new MonRef(w.Id, w.WorkerCode, w.FullName); since = a.StartAt; }
            }
        }
        MonRef? verified = null;
        string? camera = null, track = null;
        DateTimeOffset? verifiedAt = null;
        if (caller.Has(Permissions.MonitoringView))
        {
            var now = DateTimeOffset.UtcNow;
            var v = await _db.WorkerTrackAssociations.AsNoTracking()
                .Where(x => x.WorkstationId == ws && x.Status == "VERIFIED" && x.EndAt == null && x.ExpiresAt > now && x.WorkerId != null)
                .OrderByDescending(x => x.StartAt).FirstOrDefaultAsync(ct);
            if (v is not null)
            {
                var w = await _db.Workers.AsNoTracking().Where(x => x.Id == v.WorkerId).Select(x => new { x.Id, x.WorkerCode, x.FullName }).FirstOrDefaultAsync(ct);
                camera = await _db.Cameras.AsNoTracking().Where(c => c.Id == v.CameraId).Select(c => c.Code).FirstOrDefaultAsync(ct);
                if (w is not null) { verified = new MonRef(w.Id, w.WorkerCode, w.FullName); track = v.TrackId; verifiedAt = v.StartAt; }
            }
        }
        return new MachineContextDto(assigned, since, verified, camera, track, verifiedAt,
            "Kept separate from the machine data. An assignment is a schedule; a verified identity says who was at the workstation. " +
            "Neither proves who operated the machine, and a RUNNING machine doesn't prove anyone is working on it.");
    }

    // ─── History ───────────────────────────────────────────────────────────

    public async Task<AccessResult<PagedResult<StateTransitionDto>>> StateHistoryAsync(AccessContext caller, IotFilter f, CancellationToken ct)
    {
        var machines = Machines(caller, f with { Source = null, State = null }).Select(m => m.Id);
        var q = _db.MachineStateTransitions.AsNoTracking().Where(t => machines.Contains(t.MachineId));
        if (IotSourceTypes.Parse(f.Source) is { } s) q = q.Where(t => t.SourceType == s);
        if (MachineStates.Parse(f.State) is { } st) q = q.Where(t => t.ToState == st);
        if (f.From is { } from) q = q.Where(t => t.OccurredAt >= from);
        if (f.To is { } to) q = q.Where(t => t.OccurredAt <= to);
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return AccessResult<PagedResult<StateTransitionDto>>.Ok(new PagedResult<StateTransitionDto>(await TransitionDtosAsync(rows, ct), page, size, total));
    }

    public async Task<AccessResult<PagedResult<TelemetryDto>>> TelemetryAsync(AccessContext caller, Guid machineId, IotFilter f, CancellationToken ct)
    {
        if (!await IotQueries.MachinesInScope(_db, caller).AnyAsync(m => m.Id == machineId, ct))
            return AccessResult<PagedResult<TelemetryDto>>.NotFound("machine_not_found", "Machine not found.");
        var q = _db.MachineTelemetry.AsNoTracking().Where(t => t.MachineId == machineId);
        if (f.DeviceId is { } d) q = q.Where(t => t.DeviceId == d);
        if (f.From is { } from) q = q.Where(t => t.OccurredAt >= from);
        if (f.To is { } to) q = q.Where(t => t.OccurredAt <= to);
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return AccessResult<PagedResult<TelemetryDto>>.Ok(new PagedResult<TelemetryDto>(await TelemetryDtosAsync(rows, ct), page, size, total));
    }

    private IQueryable<ProductionCounterEvent> Counts(AccessContext caller, IotFilter f)
    {
        var q = IotQueries.CountsInScope(_db, caller);
        if (f.FactoryId is { } fid) q = q.Where(e => e.FactoryId == fid);
        if (!string.IsNullOrWhiteSpace(f.LineId)) { var l = f.LineId.Trim(); q = q.Where(e => e.LineId == l); }
        if (f.DepartmentId is { } dep) q = q.Where(e => e.DepartmentId == dep);
        if (f.FloorId is { } fl) q = q.Where(e => e.FloorId == fl);
        if (f.WorkstationId is { } w) q = q.Where(e => e.WorkstationId == w);
        if (f.MachineId is { } m) q = q.Where(e => e.MachineId == m);
        if (f.DeviceId is { } d) q = q.Where(e => e.DeviceId == d);
        if (f.ShiftId is { } s) q = q.Where(e => e.ShiftId == s);
        var source = IotQueries.SourceOf(f.Source);
        return q.Where(e => e.SourceType == source);
    }

    public async Task<PagedResult<CounterEventDto>> CounterEventsAsync(AccessContext caller, IotFilter f, CancellationToken ct)
    {
        var q = Counts(caller, f);
        if (f.From is { } from) q = q.Where(e => e.OccurredAt >= from);
        if (f.To is { } to) q = q.Where(e => e.OccurredAt <= to);
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<CounterEventDto>(await CounterDtosAsync(rows, ct), page, size, total);
    }

    public async Task<PagedResult<ProductionCorrectionDto>> CorrectionsAsync(AccessContext caller, IotFilter f, CancellationToken ct)
    {
        var q = IotQueries.CorrectionsInScope(_db, caller).Where(c => c.SourceType == IotQueries.SourceOf(f.Source));
        if (f.FactoryId is { } fid) q = q.Where(c => c.FactoryId == fid);
        if (!string.IsNullOrWhiteSpace(f.LineId)) { var l = f.LineId.Trim(); q = q.Where(c => c.LineId == l); }
        if (f.MachineId is { } m) q = q.Where(c => c.MachineId == m);
        var page = Math.Max(1, f.Page);
        var size = Math.Clamp(f.PageSize, 1, 200);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(c => c.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var names = await NamesAsync(rows.Select(r => r.LineId), rows.Select(r => r.WorkstationId), rows.Select(r => r.MachineId), ct);
        return new PagedResult<ProductionCorrectionDto>(rows.Select(c => new ProductionCorrectionDto(c.Id, c.FactoryId, names.Line(c.LineId), names.Ws(c.WorkstationId),
            names.Machine(c.MachineId), c.ProductionDate, c.ShiftCode, c.HourStart, c.PiecesDelta, c.Reason, c.SourceType, c.CreatedByEmail, c.CreatedByRole,
            c.CreatedAt)).ToList(), page, size, total);
    }

    // ─── Production report ─────────────────────────────────────────────────

    public async Task<ProductionReportDto> ProductionAsync(AccessContext caller, IotFilter f, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cal = await CalendarForAsync(caller, f.FactoryId, ct);
        var to = f.To ?? now;
        var from = f.From ?? cal.DayStartUtc(DateOnly.FromDateTime(cal.Local(to)));
        if (to <= from) to = from.AddMinutes(1);
        if (to - from > TimeSpan.FromDays(MaxRangeDays)) from = to.AddDays(-MaxRangeDays);
        var source = IotQueries.SourceOf(f.Source);

        var events = Counts(caller, f).Where(e => e.OccurredAt >= from && e.OccurredAt < to && e.Pieces > 0);
        var byLine = await events.GroupBy(e => e.LineId).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byWs = await events.Where(e => e.WorkstationId != null).GroupBy(e => e.WorkstationId!.Value).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byMachine = await events.Where(e => e.MachineId != null).GroupBy(e => e.MachineId!.Value).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byShift = await events.GroupBy(e => e.ShiftCode).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var byDay = await events.GroupBy(e => e.ProductionDate).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToListAsync(ct);
        var counted = byLine.Sum(x => x.N);

        // Hourly buckets only for short ranges (the rows are read and bucketed here).
        var hourly = new List<BucketCount>();
        if (to - from <= TimeSpan.FromDays(MaxHourlyDays))
        {
            var points = await events.Select(e => new { e.OccurredAt, e.Pieces }).ToListAsync(ct);
            var buckets = new SortedDictionary<DateTimeOffset, long>();
            for (var h = Hour(from); h < to; h = h.AddHours(1)) buckets[h] = 0;
            foreach (var p in points) { var h = Hour(p.OccurredAt); buckets[h] = buckets.GetValueOrDefault(h) + p.Pieces; }
            hourly = buckets.Select(b => new BucketCount(b.Key, cal.Local(b.Key).ToString("HH:00", CultureInfo.InvariantCulture), b.Value)).ToList();
        }

        // Corrections: by production date (and hour, when given) inside the range.
        var fromDay = DateOnly.FromDateTime(cal.Local(from));
        var toDay = DateOnly.FromDateTime(cal.Local(to));
        var corrections = IotQueries.CorrectionsInScope(_db, caller).Where(c => c.SourceType == source && c.ProductionDate >= fromDay && c.ProductionDate <= toDay);
        if (f.FactoryId is { } fid) corrections = corrections.Where(c => c.FactoryId == fid);
        if (!string.IsNullOrWhiteSpace(f.LineId)) { var l = f.LineId.Trim(); corrections = corrections.Where(c => c.LineId == l); }
        if (f.WorkstationId is { } wsf) corrections = corrections.Where(c => c.WorkstationId == wsf);
        if (f.MachineId is { } mf) corrections = corrections.Where(c => c.MachineId == mf);
        if (f.ShiftId is { } sf) corrections = corrections.Where(c => c.ShiftId == sf);
        if (f.DeviceId is not null) corrections = corrections.Where(_ => false); // corrections aren't per device
        var corr = await corrections.ToListAsync(ct);
        var correctionPieces = corr.Sum(c => (long)c.PiecesDelta);

        Dictionary<TK, long> Merge<TK>(IEnumerable<(TK Key, long N)> a, IEnumerable<(TK Key, long N)> b) where TK : notnull
        {
            var d = new Dictionary<TK, long>();
            foreach (var (k, n) in a.Concat(b)) d[k] = d.GetValueOrDefault(k) + n;
            return d;
        }
        var lines = Merge(byLine.Select(x => (x.Key, x.N)), corr.Select(c => (c.LineId, (long)c.PiecesDelta)));
        var wss = Merge(byWs.Select(x => (x.Key, x.N)), corr.Where(c => c.WorkstationId != null).Select(c => (c.WorkstationId!.Value, (long)c.PiecesDelta)));
        var machinesCount = Merge(byMachine.Select(x => (x.Key, x.N)), corr.Where(c => c.MachineId != null).Select(c => (c.MachineId!.Value, (long)c.PiecesDelta)));
        var shifts = Merge(byShift.Select(x => (x.Key ?? "", x.N)), corr.Select(c => (c.ShiftCode ?? "", (long)c.PiecesDelta)));
        var days = Merge(byDay.Select(x => (x.Key, x.N)), corr.Select(c => (c.ProductionDate, (long)c.PiecesDelta)));
        foreach (var c in corr.Where(c => c.HourStart is not null))
        {
            var h = Hour(c.HourStart!.Value);
            var i = hourly.FindIndex(b => b.Start == h);
            if (i >= 0) hourly[i] = hourly[i] with { Pieces = hourly[i].Pieces + c.PiecesDelta };
        }

        var seconds = (Min(to, now) - from).TotalSeconds;
        var names = await NamesAsync(lines.Keys, wss.Keys.Select(x => (Guid?)x), machinesCount.Keys.Select(x => (Guid?)x), ct);
        var shiftNames = await _db.Shifts.AsNoTracking().Where(s => shifts.Keys.Contains(s.Code)).Select(s => new { s.Code, s.Name }).ToListAsync(ct);

        // Machine running time and downtime for the machines in the filter.
        var machineRows = await Machines(caller, f with { Source = source, State = null, Search = null }).OrderBy(m => m.Code).Take(500).ToListAsync(ct);
        var metrics = await MachineMetricsAsync(machineRows, from, Min(to, now), source, true, ct, machinesCount);

        var total = counted + correctionPieces;
        return new ProductionReportDto(from, to, source, cal.TimeZone.Id, total, counted, correctionPieces, IotMetrics.RatePerHour(total, seconds),
            lines.OrderByDescending(x => x.Value).Select(x => new GroupCount(x.Key, names.Line(x.Key)?.Name ?? x.Key, x.Value, IotMetrics.RatePerHour(x.Value, seconds))).ToList(),
            wss.OrderByDescending(x => x.Value).Select(x => new GroupCount(x.Key.ToString(), names.Ws(x.Key)?.Code ?? "?", x.Value, IotMetrics.RatePerHour(x.Value, seconds))).ToList(),
            machinesCount.OrderByDescending(x => x.Value).Select(x => new GroupCount(x.Key.ToString(), names.Machine(x.Key)?.Code ?? "?", x.Value, IotMetrics.RatePerHour(x.Value, seconds))).ToList(),
            shifts.OrderBy(x => x.Key).Select(x => new GroupCount(x.Key, x.Key == "" ? "Outside shifts" : shiftNames.FirstOrDefault(s => s.Code == x.Key)?.Name ?? x.Key,
                x.Value, null)).ToList(),
            hourly,
            days.OrderBy(x => x.Key).Select(x => new BucketCount(cal.DayStartUtc(x.Key), x.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x.Value)).ToList(),
            metrics, metrics.Sum(m => m.RunningSeconds), metrics.Sum(m => m.IdleSeconds), metrics.Sum(m => m.DowntimeSeconds),
            (source == IotSourceTypes.Simulated ? "SIMULATED production from the IoT simulator, not real output. " : "") + SeparationNote +
            " Counts come from production counters under each machine's counting rule; camera activity predictions are never used as production quantity.");
    }

    private async Task<IReadOnlyList<MachineMetricsDto>> MachineMetricsAsync(IReadOnlyList<Machine> machines, DateTimeOffset from, DateTimeOffset to, string source,
        bool withPieces, CancellationToken ct, Dictionary<Guid, long>? pieces = null)
    {
        if (machines.Count == 0) return Array.Empty<MachineMetricsDto>();
        var ids = machines.Select(m => m.Id).ToList();
        var inRange = await _db.MachineStateTransitions.AsNoTracking()
            .Where(t => ids.Contains(t.MachineId) && t.SourceType == source && t.OccurredAt > from && t.OccurredAt < to)
            .Select(t => new { t.MachineId, t.OccurredAt, t.ToState }).ToListAsync(ct);
        var before = await _db.MachineStateTransitions.AsNoTracking()
            .Where(t => ids.Contains(t.MachineId) && t.SourceType == source && t.OccurredAt <= from)
            .GroupBy(t => t.MachineId)
            .Select(g => g.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).Select(x => new { x.MachineId, x.ToState }).First())
            .ToListAsync(ct);
        if (withPieces && pieces is null)
            pieces = await _db.ProductionCounterEvents.AsNoTracking()
                .Where(e => e.MachineId != null && ids.Contains(e.MachineId.Value) && e.SourceType == source && e.OccurredAt >= from && e.OccurredAt < to)
                .GroupBy(e => e.MachineId!.Value).Select(g => new { g.Key, N = g.Sum(x => (long)x.Pieces) }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var names = await NamesAsync(Array.Empty<string>(), machines.Select(m => m.WorkstationId), Array.Empty<Guid?>(), ct);
        return machines.Select(m =>
        {
            var start = before.FirstOrDefault(b => b.MachineId == m.Id)?.ToState;
            var totals = IotMetrics.Durations(start, inRange.Where(t => t.MachineId == m.Id).Select(t => (t.OccurredAt, t.ToState)), from, to);
            var n = withPieces ? pieces!.GetValueOrDefault(m.Id) : 0;
            var ws = names.WsRow(m.WorkstationId);
            return new MachineMetricsDto(m.Id, m.Code, ws is null ? null : names.Line(ws.LineId), ws is null ? null : new MonRef(ws.Id, ws.Code, ws.Name), n,
                totals.Running, totals.Idle, totals.Downtime, totals.Of(MachineStates.Stopped), totals.Of(MachineStates.Error), totals.Of(MachineStates.Offline),
                totals.Of(MachineStates.Unknown), totals.Of(MachineStates.Maintenance), withPieces ? IotMetrics.RatePerRunningHour(n, totals.Running) : null,
                totals.CoveredSeconds < 1 ? null : Math.Round(totals.Running / totals.CoveredSeconds, 3));
        }).ToList();
    }

    // ─── Devices ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeviceIotDto>> DevicesAsync(AccessContext caller, IotFilter f, CancellationToken ct)
    {
        var q = DeviceQueries.InScope(_db, caller).Where(d => d.Category != DeviceCategories.Camera);
        if (f.FactoryId is { } fid) q = q.Where(d => d.FactoryId == fid);
        if (f.MachineId is { } m) q = q.Where(d => d.MachineId == m);
        if (f.WorkstationId is { } w) q = q.Where(d => d.WorkstationId == w || _db.Machines.Any(x => x.Id == d.MachineId && x.WorkstationId == w));
        if (!string.IsNullOrWhiteSpace(f.LineId)) { var l = f.LineId.Trim(); q = q.Where(d => d.LineId == l); }
        if (IotSourceTypes.Parse(f.Source) is { } s) q = q.Where(d => d.IsSimulated == (s == IotSourceTypes.Simulated));
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var p = "%" + f.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(d => EF.Functions.ILike(d.Code, p, "\\") || EF.Functions.ILike(d.Name, p, "\\"));
        }
        var rows = await q.OrderBy(d => d.Code).Take(500).ToListAsync(ct);
        return await IotQueries.DeviceDtosAsync(_db, rows, _options.Mqtt.TopicPrefix, ct);
    }

    public async Task<AccessResult<DeviceIotDto>> DeviceAsync(AccessContext caller, Guid deviceId, CancellationToken ct)
    {
        var d = await DeviceQueries.InScope(_db, caller).FirstOrDefaultAsync(x => x.Id == deviceId, ct);
        if (d is null) return AccessResult<DeviceIotDto>.NotFound("device_not_found", "Device not found.");
        return AccessResult<DeviceIotDto>.Ok((await IotQueries.DeviceDtosAsync(_db, new[] { d }, _options.Mqtt.TopicPrefix, ct))[0]);
    }

    // ─── Mapping ───────────────────────────────────────────────────────────

    private async Task<ShiftCalendar> CalendarForAsync(AccessContext caller, Guid? factoryId, CancellationToken ct)
    {
        var fid = factoryId ?? caller.FactoryIds.OrderBy(x => x).FirstOrDefault();
        return await IotQueries.CalendarAsync(_db, fid, ct);
    }

    private static DateTimeOffset Hour(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private sealed class NameSet
    {
        public Dictionary<string, string> Lines = new();
        public Dictionary<Guid, Workstation> Workstations = new();
        public Dictionary<Guid, (string Code, string? Type)> Machines = new();
        public MonLine? Line(string? id) => id is null ? null : new MonLine(id, Lines.GetValueOrDefault(id, id));
        public Workstation? WsRow(Guid? id) => id is { } g ? Workstations.GetValueOrDefault(g) : null;
        public MonRef? Ws(Guid? id) => WsRow(id) is { } w ? new MonRef(w.Id, w.Code, w.Name) : null;
        public MonRef? Machine(Guid? id) => id is { } g && Machines.TryGetValue(g, out var m) ? new MonRef(g, m.Code, m.Type) : null;
    }

    private async Task<NameSet> NamesAsync(IEnumerable<string?> lineIds, IEnumerable<Guid?> wsIds, IEnumerable<Guid?> machineIds, CancellationToken ct)
    {
        var n = new NameSet();
        var w = wsIds.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        if (w.Count > 0) n.Workstations = await _db.Workstations.AsNoTracking().Where(x => w.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var l = lineIds.Where(x => x is not null).Select(x => x!).Concat(n.Workstations.Values.Select(x => x.LineId)).Distinct().ToList();
        if (l.Count > 0) n.Lines = await _db.ProductionLines.AsNoTracking().Where(x => l.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var m = machineIds.Where(x => x is not null).Select(x => x!.Value).Distinct().ToList();
        if (m.Count > 0)
            n.Machines = (await _db.Machines.AsNoTracking().Where(x => m.Contains(x.Id)).Select(x => new { x.Id, x.Code, x.MachineType }).ToListAsync(ct))
                .ToDictionary(x => x.Id, x => (x.Code, x.MachineType));
        return n;
    }

    private async Task<IReadOnlyList<StateTransitionDto>> TransitionDtosAsync(IReadOnlyList<MachineStateTransition> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<StateTransitionDto>();
        var machineIds = rows.Select(r => r.MachineId).Distinct().ToList();
        var oldest = rows.Min(r => r.OccurredAt);
        // The next transition of each machine ends the state: read the following changes once.
        var following = await _db.MachineStateTransitions.AsNoTracking().Where(t => machineIds.Contains(t.MachineId) && t.OccurredAt >= oldest)
            .Select(t => new { t.Id, t.MachineId, t.OccurredAt }).ToListAsync(ct);
        var devIds = rows.Where(r => r.DeviceId != null).Select(r => r.DeviceId!.Value).Distinct().ToList();
        var devices = await _db.Devices.AsNoTracking().Where(d => devIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Code, ct);
        var names = await NamesAsync(Array.Empty<string>(), Array.Empty<Guid?>(), machineIds.Select(x => (Guid?)x), ct);
        var now = DateTimeOffset.UtcNow;
        return rows.Select(r =>
        {
            var next = following.Where(x => x.MachineId == r.MachineId && (x.OccurredAt > r.OccurredAt || (x.OccurredAt == r.OccurredAt && x.Id > r.Id)))
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).FirstOrDefault();
            var end = next?.OccurredAt ?? now;
            return new StateTransitionDto(r.Id, r.MachineId, names.Machine(r.MachineId)?.Code ?? "?", r.FromState, r.ToState, r.Reason, r.Trigger, r.SourceType,
                r.DeviceId is { } d ? devices.GetValueOrDefault(d) : null, r.OccurredAt, Math.Max(0, (end - r.OccurredAt).TotalSeconds));
        }).ToList();
    }

    private async Task<IReadOnlyList<TelemetryDto>> TelemetryDtosAsync(IReadOnlyList<MachineTelemetry> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<TelemetryDto>();
        var devIds = rows.Select(r => r.DeviceId).Distinct().ToList();
        var devices = await _db.Devices.AsNoTracking().Where(d => devIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Code, ct);
        return rows.Select(t => new TelemetryDto(t.Id, t.DeviceId, devices.GetValueOrDefault(t.DeviceId, "?"), t.ReportedState, t.PowerWatts, t.CurrentAmps,
            t.TemperatureC, t.VibrationMmS, t.ErrorCode, t.Metrics is null ? null : MonitoringQueries.Json(t.Metrics), t.SourceType, t.Transport, t.Outcome,
            t.MessageId, t.OccurredAt, t.ReceivedAt)).ToList();
    }

    private async Task<IReadOnlyList<CounterEventDto>> CounterDtosAsync(IReadOnlyList<ProductionCounterEvent> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return Array.Empty<CounterEventDto>();
        var devIds = rows.Select(r => r.DeviceId).Distinct().ToList();
        var devices = await _db.Devices.AsNoTracking().Where(d => devIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Code, ct);
        var names = await NamesAsync(rows.Select(r => (string?)r.LineId), rows.Select(r => r.WorkstationId), rows.Select(r => r.MachineId), ct);
        return rows.Select(e => new CounterEventDto(e.Id, e.DeviceId, devices.GetValueOrDefault(e.DeviceId, "?"), names.Machine(e.MachineId), names.Ws(e.WorkstationId),
            names.Line(e.LineId), e.CounterMode, e.RawValue, e.Pulses, e.Pieces, e.Status, e.Note, e.ShiftCode, e.ProductionDate, e.SourceType, e.Transport,
            e.MessageId, e.OccurredAt, e.ReceivedAt)).ToList();
    }
}
