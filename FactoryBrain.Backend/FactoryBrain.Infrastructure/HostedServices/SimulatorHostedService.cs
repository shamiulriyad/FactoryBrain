using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Sensors;
using FactoryBrain.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FactoryBrain.Infrastructure.HostedServices;

/// <summary>
/// Optional background service that drives the live-event demo. Gated
/// on <c>SIMULATOR_ENABLED=true</c>; when unset or false the service
/// short-circuits and the timer never starts. Behaviour:
///   - every 5 s : <see cref="ISensorService.IngestAsync"/> (Tick=null
///                 → server chooses next tick). This path runs the
///                 normal notifier, so connected SignalR clients see
///                 a `sensorReading` event.
///   - every 30 s: <see cref="ILineBoardService.RefreshAsync"/>. Same
///                 pattern — clients see a `lineBoardUpdated` event.
/// SIMULATOR_SEED (optional int) makes the sensor RNG deterministic so
/// the same seed gives the same first N readings across runs. Invalid
/// values fail startup with a clear message.
/// </summary>
public sealed class SimulatorHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SimulatorHostedService> _log;
    private readonly bool _enabled;
    private readonly int _seed;

    public SimulatorHostedService(
        IServiceScopeFactory scopes,
        ILogger<SimulatorHostedService> log)
    {
        _scopes = scopes;
        _log = log;

        var raw = Environment.GetEnvironmentVariable("SIMULATOR_ENABLED");
        _enabled = string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);

        if (!_enabled)
        {
            _seed = 0;
            return;
        }

        var seedRaw = Environment.GetEnvironmentVariable("SIMULATOR_SEED");
        if (!string.IsNullOrWhiteSpace(seedRaw))
        {
            if (!int.TryParse(seedRaw, out _seed))
            {
                throw new InvalidOperationException(
                    $"SIMULATOR_SEED='{seedRaw}' is not a valid integer.");
            }
        }
        else
        {
            _seed = Random.Shared.Next();
            _log.LogInformation("Simulator using random seed {Seed}. Set SIMULATOR_SEED to reproduce.", _seed);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;

        // Apply the seed so the sensor RNG produces identical readings
        // across runs with the same SIMULATOR_SEED. StepTick XORs this
        // into its tick-scoped seed.
        SensorService.SimulatorSeedOverride = _seed;
        _log.LogInformation("Simulator started (sensor every 5s, line-board every 30s, seed={Seed}).", _seed);

        // Stagger the two timers so the first line-board refresh lands
        // 30s after start, not at start+0.
        var sensorTick = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var lineBoardTick = new PeriodicTimer(TimeSpan.FromSeconds(30));

        // Run them concurrently; one CancellationToken cancels both.
        var sensorTask = RunSensorLoopAsync(sensorTick, stoppingToken);
        var lineBoardTask = RunLineBoardLoopAsync(lineBoardTick, stoppingToken);
        await Task.WhenAll(sensorTask, lineBoardTask);
    }

    private async Task RunSensorLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        // Apply the seed to the next sensor tick by routing through the
        // same path an authenticated ingest would. The tick value is
        // computed inside SensorService so a deterministic seed
        // produces the same readings.
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var sensors = scope.ServiceProvider.GetRequiredService<ISensorService>();
                    await sensors.IngestAsync(new IngestRequest(Tick: null), ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Simulator sensor tick failed.");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task RunLineBoardLoopAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    using var scope = _scopes.CreateScope();
                    var lb = scope.ServiceProvider.GetRequiredService<ILineBoardService>();
                    await lb.RefreshAsync(null, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Simulator line-board tick failed.");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }
}
