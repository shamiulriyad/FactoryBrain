using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Iot;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BunonBrain.Infrastructure.Iot;

/// <summary>
/// Re-derives every monitored machine's state from device health (OFFLINE /
/// UNKNOWN), runs the timed alert rules (unexpected stoppage, long idle,
/// counter silence, flapping) and enforces the telemetry retention period.
/// </summary>
public sealed class MachineMonitorService : IMachineMonitorService
{
    public const string MonitorEmail = "system:machine-monitor";
    private static DateTimeOffset _lastRetention = DateTimeOffset.MinValue;
    private static readonly object RetentionLock = new();

    private readonly BunonBrainDbContext _db;
    private readonly MachineStateProcessor _states;
    private readonly IotOptions _options;
    private readonly ILogger<MachineMonitorService> _log;

    public MachineMonitorService(BunonBrainDbContext db, MachineStateProcessor states, IOptions<IotOptions> options, ILogger<MachineMonitorService> log)
    {
        _db = db; _states = states; _options = options.Value; _log = log;
    }

    public async Task<MachineMonitorResult> RunAsync(AccessContext? caller, Guid? factoryId, CancellationToken ct)
    {
        var bound = _db.Devices.AsNoTracking().Where(d => d.MachineId != null && d.Status != DeviceStatuses.Disabled).Select(d => d.MachineId!.Value);
        var tracked = _db.MachineStates.AsNoTracking().Select(s => s.MachineId);
        var machines = caller is null ? _db.Machines.AsNoTracking() : IotQueries.MachinesInScope(_db, caller);
        if (factoryId is { } f) machines = machines.Where(m => m.FactoryId == f);
        var list = await machines.Where(m => bound.Contains(m.Id) || tracked.Contains(m.Id)).Select(m => new { m.Id, m.FactoryId }).Take(5000).ToListAsync(ct);

        var calendars = new Dictionary<Guid, ShiftCalendar>();
        int changes = 0, conditions = 0;
        foreach (var m in list)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!calendars.TryGetValue(m.FactoryId, out var cal)) calendars[m.FactoryId] = cal = await IotQueries.CalendarAsync(_db, m.FactoryId, ct);
                var r = await _states.RefreshAsync(m.Id, cal, caller?.Email ?? MonitorEmail, ct);
                if (r.Changed) changes++;
                conditions += r.Conditions;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _log.LogWarning(ex, "Machine monitor: machine {Machine} skipped.", m.Id);
            }
        }
        var deleted = caller is null ? await RetentionAsync(ct) : 0;
        return new MachineMonitorResult(list.Count, changes, conditions, deleted);
    }

    public async Task<MachineStateRefresh> RefreshMachineAsync(Guid machineId, AccessContext? caller, CancellationToken ct)
    {
        var r = await _states.RefreshAsync(machineId, null, caller?.Email ?? MonitorEmail, ct);
        return new MachineStateRefresh(machineId, r.State, r.Changed);
    }

    /// <summary>Delete raw telemetry past the retention period (at most every 10 minutes, in batches). State history and counts are kept.</summary>
    private async Task<int> RetentionAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        lock (RetentionLock)
        {
            if (now - _lastRetention < TimeSpan.FromMinutes(10)) return 0;
            _lastRetention = now;
        }
        var cutoff = now.AddDays(-_options.TelemetryRetentionDays);
        var total = 0;
        for (var i = 0; i < 20; i++)
        {
            var batch = _db.MachineTelemetry.Where(t => t.ReceivedAt < cutoff).OrderBy(t => t.Id).Select(t => t.Id).Take(5000);
            var n = await _db.MachineTelemetry.Where(t => batch.Contains(t.Id)).ExecuteDeleteAsync(ct);
            total += n;
            if (n < 5000) break;
        }
        if (total > 0) _log.LogInformation("Telemetry retention removed {Count} rows older than {Days} days.", total, _options.TelemetryRetentionDays);
        return total;
    }
}

/// <summary>Runs the machine monitor periodically. Not registered in the Testing host (tests call the API).</summary>
public sealed class MachineMonitorWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IotOptions _options;
    private readonly ILogger<MachineMonitorWorker> _log;

    public MachineMonitorWorker(IServiceScopeFactory scopes, IOptions<IotOptions> options, ILogger<MachineMonitorWorker> log)
    {
        _scopes = scopes; _options = options.Value; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_options.MonitorIntervalSeconds);
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IMachineMonitorService>().RunAsync(null, null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Machine monitor run failed; retrying in {Interval}.", interval);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
