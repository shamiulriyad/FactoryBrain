using BunonBrain.Application.Access;
using BunonBrain.Application.Analytics;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Analytics;
using BunonBrain.Application.Iot;
using BunonBrain.Application.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Attendance;
using BunonBrain.Infrastructure.Iot;
using BunonBrain.Infrastructure.Monitoring;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Analytics;

/// <summary>Which analytics sections a caller may see (existing permissions only).</summary>
public static class AnalyticsSections
{
    public const string Production = "production";
    public const string Machines = "machines";
    public const string Attendance = "attendance";
    public const string Overtime = "overtime";
    public const string Assignments = "assignments";
    public const string Activity = "activity";
    public const string Devices = "devices";
    public const string Alerts = "alerts";
    public static readonly IReadOnlyList<string> All = new[] { Production, Machines, Attendance, Overtime, Assignments, Activity, Devices, Alerts };

    public static IReadOnlyList<string> For(AccessContext c)
    {
        var s = new List<string>();
        if (c.HasAny(Permissions.ProductionView, Permissions.MonitoringView)) s.Add(Production);
        if (c.HasAny(Permissions.MonitoringView, Permissions.DevicesView, Permissions.ProductionView)) s.Add(Machines);
        if (c.Has(Permissions.AttendanceView)) s.Add(Attendance);
        if (c.HasAny(Permissions.OvertimeView, Permissions.AttendanceView)) s.Add(Overtime);
        if (c.Has(Permissions.WorkersView)) s.Add(Assignments);
        if (c.Has(Permissions.MonitoringView)) s.Add(Activity);
        if (c.HasAny(Permissions.DevicesView, Permissions.MonitoringView)) s.Add(Devices);
        if (c.HasAny(Permissions.MonitoringView, Permissions.DevicesView)) s.Add(Alerts);
        return s;
    }

    public static string PermissionFor(string section) => section switch
    {
        Production => "production.view or monitoring.view",
        Machines => "monitoring.view, devices.view or production.view",
        Attendance => "attendance.view",
        Overtime => "overtime.view or attendance.view",
        Assignments => "workers.view",
        Activity => "monitoring.view",
        Devices => "devices.view or monitoring.view",
        _ => "monitoring.view or devices.view",
    };
}

/// <summary>
/// One analytics request, resolved: the caller's reach, the validated
/// filters, the factory time zone and period, and the source. Every query
/// starts from the existing modules' scope helpers (factory and line reach),
/// so no page or export can see another factory's data.
/// </summary>
public sealed class AnalyticsContext
{
    private readonly BunonBrainDbContext _db;
    private readonly AttendanceSupport _attendance;

    public AccessContext Caller { get; }
    public AnalyticsFilter Filter { get; }
    public AnalyticsPeriod Period { get; }
    public TimeZoneInfo Zone { get; }
    public string Source { get; }
    public string? EventSource { get; }
    public string ScopeLabel { get; }
    public IReadOnlyList<string> Sections { get; }
    public IReadOnlyList<ReportFilter> Filters { get; }
    public DateTimeOffset Now { get; }
    public bool Simulated => Source == IotSourceTypes.Simulated;

    private AnalyticsContext(BunonBrainDbContext db, AccessContext caller, AnalyticsFilter filter, AnalyticsPeriod period, TimeZoneInfo zone, string source,
        string? eventSource, string scope, IReadOnlyList<ReportFilter> filters, DateTimeOffset now)
    {
        _db = db;
        _attendance = new AttendanceSupport(db);
        Caller = caller; Filter = filter; Period = period; Zone = zone; Source = source; EventSource = eventSource; ScopeLabel = scope; Filters = filters; Now = now;
        Sections = AnalyticsSections.For(caller);
    }

    public bool Can(string section) => Sections.Contains(section);

    public static readonly IReadOnlyList<string> EventSources = new[] { "DEVICE", "MANUAL", "SIMULATOR" };

    public static async Task<AccessResult<AnalyticsContext>> CreateAsync(BunonBrainDbContext db, AccessContext caller, AnalyticsFilter f, CancellationToken ct)
    {
        static AccessResult<AnalyticsContext> Bad(string code, string message) => AccessResult<AnalyticsContext>.Invalid(code, message);
        if (AnalyticsSections.For(caller).Count == 0)
            return AccessResult<AnalyticsContext>.Forbidden("analytics_access_required", "Your role doesn't include any analytics data.");
        var filters = new List<ReportFilter>();
        Factory? factory = null;
        if (f.FactoryId is { } fid)
        {
            if (!caller.CanAccessFactory(fid)) return AccessResult<AnalyticsContext>.Forbidden("factory_out_of_scope", "You can only see analytics of your own factories.");
            factory = await db.Factories.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fid, ct);
            if (factory is null) return AccessResult<AnalyticsContext>.NotFound("factory_not_found", "Factory not found.");
            filters.Add(new ReportFilter("Factory", $"{factory.Code} · {factory.Name}"));
        }
        var source = f.Source is null ? IotSourceTypes.Real : IotSourceTypes.Parse(f.Source);
        if (source is null) return Bad("source_invalid", "Source: REAL or SIMULATED.");
        string? eventSource = null;
        if (!string.IsNullOrWhiteSpace(f.EventSource))
        {
            eventSource = EventSources.FirstOrDefault(s => string.Equals(s, f.EventSource.Trim(), StringComparison.OrdinalIgnoreCase));
            if (eventSource is null) return Bad("event_source_invalid", "Event source: DEVICE, MANUAL or SIMULATOR.");
            filters.Add(new ReportFilter("Event source", eventSource));
        }

        // Every organisational filter must belong to the chosen factory (or one the caller reaches).
        bool InReach(Guid factoryId) => caller.CanAccessFactory(factoryId) && (f.FactoryId is null || f.FactoryId == factoryId);
        if (f.DepartmentId is { } dep)
        {
            var d = await db.Departments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == dep, ct);
            if (d is null || !InReach(d.FactoryId)) return Bad("department_not_in_factory", "The department is not in the selected factory.");
            filters.Add(new ReportFilter("Department", d.Name));
        }
        if (f.FloorId is { } fl)
        {
            var d = await db.ProductionFloors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fl, ct);
            if (d is null || !InReach(d.FactoryId)) return Bad("floor_not_in_factory", "The floor is not in the selected factory.");
            filters.Add(new ReportFilter("Floor", d.Name));
        }
        if (!string.IsNullOrWhiteSpace(f.LineId))
        {
            var line = await db.ProductionLines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == f.LineId.Trim(), ct);
            if (line?.FactoryId is not { } lf || !InReach(lf)) return Bad("line_not_in_factory", "The line is not in the selected factory.");
            if (caller.Scope == RoleScope.Line && !caller.LineIds.Contains(line.Id))
                return AccessResult<AnalyticsContext>.Forbidden("line_out_of_scope", "You can only see analytics of your own lines.");
            filters.Add(new ReportFilter("Line", line.Name));
        }
        if (f.WorkstationId is { } wsId)
        {
            var w = await db.Workstations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == wsId, ct);
            if (w is null || !InReach(w.FactoryId) || (!string.IsNullOrWhiteSpace(f.LineId) && w.LineId != f.LineId.Trim()))
                return Bad("workstation_not_in_scope", "The workstation is not in the selected factory / line.");
            filters.Add(new ReportFilter("Workstation", w.Code));
        }
        if (f.MachineId is { } mId)
        {
            var m = await db.Machines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == mId, ct);
            if (m is null || !InReach(m.FactoryId)) return Bad("machine_not_in_scope", "The machine is not in the selected factory.");
            filters.Add(new ReportFilter("Machine", m.Code));
        }
        if (f.ShiftId is { } sId)
        {
            var s = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sId, ct);
            if (s is null || !InReach(s.FactoryId)) return Bad("shift_not_in_factory", "The shift is not in the selected factory.");
            filters.Add(new ReportFilter("Shift", s.Name));
        }

        // Time zone of the factory (attendance policy), else the caller's only factory, else Asia/Dhaka.
        var tzFactory = f.FactoryId ?? (caller.FactoryIds.Count == 1 ? caller.FactoryIds.First() : (Guid?)null);
        var tzId = tzFactory is { } tf ? await db.AttendancePolicies.AsNoTracking().Where(p => p.FactoryId == tf).Select(p => p.TimeZoneId).FirstOrDefaultAsync(ct) : null;
        var zone = AttendanceRules.ResolveTimeZone(tzId ?? AttendanceRules.DefaultTimeZone) ?? TimeZoneInfo.Utc;
        if (f.From is { } a && f.To is { } b && b < a) return Bad("invalid_period", "The end date can't be before the start date.");
        var now = DateTimeOffset.UtcNow;
        var period = AnalyticsPeriod.Resolve(f.From, f.To, zone, now) with { TimeZone = tzId ?? AttendanceRules.DefaultTimeZone };
        var scope = factory is not null ? $"{factory.Code} · {factory.Name}"
            : caller.Scope == RoleScope.Platform ? "All factories"
            : caller.FactoryIds.Count == 1 ? (await db.Factories.AsNoTracking().Where(x => x.Id == caller.FactoryIds.First()).Select(x => x.Code + " · " + x.Name).FirstOrDefaultAsync(ct) ?? "Your factory")
            : "Your factories";
        if (caller.Scope == RoleScope.Line) scope += $" · your lines ({string.Join(", ", caller.LineIds.OrderBy(x => x))})";
        return AccessResult<AnalyticsContext>.Ok(new AnalyticsContext(db, caller, f, period, zone, source, eventSource, scope, filters, now));
    }

    public AnalyticsScopeDto Dto() => new(Filter.FactoryId, ScopeLabel, Period.From, Period.To, Period.FromUtc, Period.ToUtc, Period.TimeZone, Period.Truncated, Source,
        EventSource, Filters, Sections, Now);

    public IReadOnlyList<string> CommonWarnings()
    {
        var w = new List<string>();
        if (Period.Truncated) w.Add($"The range was shortened to the last {AnalyticsPeriod.MaxDays} days.");
        if (Simulated) w.Add("SIMULATED data: not real production, machines or attendance.");
        return w;
    }

    // ─── Scoped queries (reach + filters + source + period) ────────────────

    private string? LineFilter => string.IsNullOrWhiteSpace(Filter.LineId) ? null : Filter.LineId.Trim();

    public IQueryable<ProductionCounterEvent> Counts(bool inPeriod = true)
    {
        var q = IotQueries.CountsInScope(_db, Caller).Where(e => e.SourceType == Source);
        if (Filter.FactoryId is { } f) q = q.Where(e => e.FactoryId == f);
        if (Filter.DepartmentId is { } d) q = q.Where(e => e.DepartmentId == d);
        if (Filter.FloorId is { } fl) q = q.Where(e => e.FloorId == fl);
        if (LineFilter is { } l) q = q.Where(e => e.LineId == l);
        if (Filter.WorkstationId is { } w) q = q.Where(e => e.WorkstationId == w);
        if (Filter.MachineId is { } m) q = q.Where(e => e.MachineId == m);
        if (Filter.ShiftId is { } s) q = q.Where(e => e.ShiftId == s);
        q = EventSource switch
        {
            "DEVICE" => q.Where(e => e.Transport == IotTransports.Mqtt || e.Transport == IotTransports.Http),
            "MANUAL" => q.Where(e => e.Transport == IotTransports.Manual),
            "SIMULATOR" => q.Where(e => e.Transport == IotTransports.Simulator),
            _ => q,
        };
        if (inPeriod) { var (a, b) = (Period.FromUtc, Period.ToUtc); q = q.Where(e => e.OccurredAt >= a && e.OccurredAt < b); }
        return q;
    }

    public IQueryable<ProductionCorrection> Corrections()
    {
        var q = IotQueries.CorrectionsInScope(_db, Caller).Where(c => c.SourceType == Source && c.ProductionDate >= Period.From && c.ProductionDate <= Period.To);
        if (Filter.FactoryId is { } f) q = q.Where(c => c.FactoryId == f);
        if (LineFilter is { } l) q = q.Where(c => c.LineId == l);
        if (Filter.WorkstationId is { } w) q = q.Where(c => c.WorkstationId == w);
        if (Filter.MachineId is { } m) q = q.Where(c => c.MachineId == m);
        if (Filter.ShiftId is { } s) q = q.Where(c => c.ShiftId == s);
        if (Filter.DepartmentId is { } d) q = q.Where(c => _db.Workstations.Any(x => x.Id == c.WorkstationId && x.DepartmentId == d));
        if (Filter.FloorId is { } fl) q = q.Where(c => _db.Workstations.Any(x => x.Id == c.WorkstationId && x.FloorId == fl));
        if (EventSource is "DEVICE" or "SIMULATOR") q = q.Where(_ => false); // corrections are manual
        return q;
    }

    public IQueryable<Machine> Machines()
    {
        var q = IotQueries.MachinesInScope(_db, Caller).Where(m => m.IsActive);
        if (Filter.FactoryId is { } f) q = q.Where(m => m.FactoryId == f);
        if (Filter.MachineId is { } mid) q = q.Where(m => m.Id == mid);
        if (Filter.WorkstationId is { } w) q = q.Where(m => m.WorkstationId == w);
        if (LineFilter is { } l) q = q.Where(m => _db.Workstations.Any(x => x.Id == m.WorkstationId && x.LineId == l));
        if (Filter.DepartmentId is { } d) q = q.Where(m => _db.Workstations.Any(x => x.Id == m.WorkstationId && x.DepartmentId == d));
        if (Filter.FloorId is { } fl) q = q.Where(m => _db.Workstations.Any(x => x.Id == m.WorkstationId && x.FloorId == fl));
        // A machine belongs to the source of its devices (REAL as soon as one real device is bound).
        q = Simulated
            ? q.Where(m => _db.Devices.Any(d => d.MachineId == m.Id && d.IsSimulated) && !_db.Devices.Any(d => d.MachineId == m.Id && !d.IsSimulated && d.Status != "Disabled"))
            : q.Where(m => !_db.Devices.Any(d => d.MachineId == m.Id && d.IsSimulated) || _db.Devices.Any(d => d.MachineId == m.Id && !d.IsSimulated && d.Status != "Disabled"));
        return q;
    }

    public IQueryable<AttendanceSession> Sessions()
    {
        var q = _attendance.Sessions(Caller).Where(s => s.WorkDate >= Period.From && s.WorkDate <= Period.To);
        q = Simulated ? q.Where(s => s.HasSimulatedEvents) : q.Where(s => !s.HasSimulatedEvents);
        if (Filter.FactoryId is { } f) q = q.Where(s => s.FactoryId == f);
        if (LineFilter is { } l) q = q.Where(s => s.LineId == l);
        if (Filter.ShiftId is { } sh) q = q.Where(s => s.ShiftId == sh);
        if (Filter.DepartmentId is { } d) q = q.Where(s => _db.Workers.Any(w => w.Id == s.WorkerId && w.DepartmentId == d));
        if (Filter.FloorId is { } fl) q = q.Where(s => _db.Workers.Any(w => w.Id == s.WorkerId && w.FloorId == fl));
        if (Filter.WorkstationId is { } ws) q = q.Where(s => _db.Assignments.Any(a => a.WorkerId == s.WorkerId && a.WorkstationId == ws && a.EndAt == null));
        q = EventSource switch
        {
            "DEVICE" => q.Where(s => s.CheckInSource == AttendanceSources.Device),
            "MANUAL" => q.Where(s => s.CheckInSource == AttendanceSources.Manual || s.CheckInSource == AttendanceSources.Correction),
            "SIMULATOR" => q.Where(s => s.CheckInSource == AttendanceSources.Simulator),
            _ => q,
        };
        return q;
    }

    public IQueryable<WorkerProfile> Workers()
    {
        var q = _attendance.Workers(Caller).Where(w => w.Status == "Active");
        if (Filter.FactoryId is { } f) q = q.Where(w => w.FactoryId == f);
        if (LineFilter is { } l) q = q.Where(w => w.LineId == l);
        if (Filter.DepartmentId is { } d) q = q.Where(w => w.DepartmentId == d);
        if (Filter.FloorId is { } fl) q = q.Where(w => w.FloorId == fl);
        if (Filter.WorkstationId is { } ws) q = q.Where(w => _db.Assignments.Any(a => a.WorkerId == w.Id && a.WorkstationId == ws && a.EndAt == null));
        return q;
    }

    public IQueryable<OvertimeRequest> Overtime()
    {
        var q = AttendanceSupport.Scoped(_db.OvertimeRequests.AsNoTracking(), Caller, (x, fs) => x.Where(o => fs.Contains(o.FactoryId)),
                (x, fs, ls) => x.Where(o => fs.Contains(o.FactoryId) && o.LineId != null && ls.Contains(o.LineId)))
            .Where(o => o.WorkDate >= Period.From && o.WorkDate <= Period.To);
        if (Filter.FactoryId is { } f) q = q.Where(o => o.FactoryId == f);
        if (LineFilter is { } l) q = q.Where(o => o.LineId == l);
        if (Filter.DepartmentId is { } d) q = q.Where(o => _db.Workers.Any(w => w.Id == o.WorkerId && w.DepartmentId == d));
        if (Filter.FloorId is { } fl) q = q.Where(o => _db.Workers.Any(w => w.Id == o.WorkerId && w.FloorId == fl));
        return q;
    }

    public IQueryable<WorkerShiftAssignment> ShiftAssignments(IQueryable<WorkerProfile> workers)
    {
        var ids = workers.Select(w => w.Id);
        var q = _db.ShiftAssignments.AsNoTracking().Where(a => ids.Contains(a.WorkerId) && a.EffectiveFrom <= Period.To && (a.EffectiveTo == null || a.EffectiveTo >= Period.From));
        if (Filter.ShiftId is { } s) q = q.Where(a => a.ShiftId == s);
        return q;
    }

    public IQueryable<MonitoringEvent> Events(bool inPeriod = true)
    {
        var src = Simulated ? MonitoringSourceTypes.Simulated : MonitoringSourceTypes.Real;
        var q = MonitoringQueries.EventsInScope(_db, Caller).Where(e => e.SourceType == src);
        if (Filter.FactoryId is { } f) q = q.Where(e => e.FactoryId == f);
        if (LineFilter is { } l) q = q.Where(e => e.LineId == l);
        if (Filter.DepartmentId is { } d) q = q.Where(e => e.DepartmentId == d);
        if (Filter.FloorId is { } fl) q = q.Where(e => e.FloorId == fl);
        if (Filter.WorkstationId is { } w) q = q.Where(e => e.WorkstationId == w);
        q = EventSource switch
        {
            "DEVICE" => q.Where(e => e.Producer != MonitoringProducers.Simulator),
            "SIMULATOR" => q.Where(e => e.Producer == MonitoringProducers.Simulator),
            "MANUAL" => q.Where(_ => false),
            _ => q,
        };
        if (inPeriod) { var (a, b) = (Period.FromUtc, Period.ToUtc); q = q.Where(e => e.OccurredAt >= a && e.OccurredAt < b); }
        return q;
    }

    public IQueryable<MonitoringAlert> Alerts()
    {
        var src = Simulated ? MonitoringSourceTypes.Simulated : MonitoringSourceTypes.Real;
        var q = MonitoringQueries.AlertsInScope(_db, Caller).Where(a => a.SourceType == src);
        if (Filter.FactoryId is { } f) q = q.Where(a => a.FactoryId == f);
        if (LineFilter is { } l) q = q.Where(a => a.LineId == l);
        if (Filter.MachineId is { } m) q = q.Where(a => a.MachineId == m);
        if (Filter.WorkstationId is { } w) q = q.Where(a => a.WorkstationId == w);
        return q;
    }

    public IQueryable<Device> Devices()
    {
        var q = BunonBrain.Infrastructure.Devices.DeviceQueries.InScope(_db, Caller).Where(d => d.IsSimulated == Simulated && d.Status != "Disabled");
        if (Filter.FactoryId is { } f) q = q.Where(d => d.FactoryId == f);
        if (LineFilter is { } l) q = q.Where(d => d.LineId == l || _db.Workstations.Any(w => w.Id == d.WorkstationId && w.LineId == l));
        if (Filter.MachineId is { } m) q = q.Where(d => d.MachineId == m);
        return q;
    }

    public IQueryable<WorkerTrackAssociation> Associations()
    {
        var (a, b) = (Period.FromUtc, Period.ToUtc);
        var q = _db.WorkerTrackAssociations.AsNoTracking().Where(x => x.IsSimulated == Simulated && x.StartAt < b && (x.EndAt == null || x.EndAt >= a));
        var factories = Caller.FactoryIds.ToList();
        var lines = Caller.LineIds.ToList();
        q = Caller.Scope switch
        {
            RoleScope.Platform => q,
            RoleScope.Factory => q.Where(x => factories.Contains(x.FactoryId)),
            RoleScope.Line => q.Where(x => factories.Contains(x.FactoryId) && x.LineId != null && lines.Contains(x.LineId)),
            _ => q.Where(_ => false),
        };
        if (Filter.FactoryId is { } f) q = q.Where(x => x.FactoryId == f);
        if (LineFilter is { } l) q = q.Where(x => x.LineId == l);
        if (Filter.WorkstationId is { } w) q = q.Where(x => x.WorkstationId == w);
        return q;
    }

    /// <summary>Line names (and factory) for ids.</summary>
    public async Task<Dictionary<string, string>> LineNamesAsync(IEnumerable<string?> ids, CancellationToken ct)
    {
        var list = ids.Where(x => x is not null).Select(x => x!).Distinct().ToList();
        return list.Count == 0 ? new() : await _db.ProductionLines.AsNoTracking().Where(l => list.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
    }
}
