using BunonBrain.Api.Hubs;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Dtos.Iot;
using BunonBrain.Application.Monitoring;
using Microsoft.AspNetCore.SignalR;

namespace BunonBrain.Api.Realtime;

/// <summary>
/// Pushes machine state changes (<c>machineState</c>) and production pings
/// (<c>productionChanged</c>) over <see cref="MonitoringHub"/>, only to the
/// SYSTEM groups of the machine's factory and line. The production ping
/// carries no numbers: the dashboard reloads the counts through the API,
/// which checks production access.
/// </summary>
public sealed class SignalRIotPublisher : IIotPublisher
{
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<SignalRIotPublisher> _log;

    public SignalRIotPublisher(IHubContext<MonitoringHub> hub, ILogger<SignalRIotPublisher> log) { _hub = hub; _log = log; }

    public async Task MachineAsync(MachineDto machine, string change, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.Groups(MonitoringSubscriptions.Targets(machine.FactoryId, machine.Line?.Id, MonitoringCategories.System))
                .SendAsync("machineState", new { change, machine }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "realtime machineState push failed");
        }
    }

    public async Task ProductionAsync(Guid factoryId, string? lineId, Guid? machineId, string sourceType, DateTimeOffset at, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.Groups(MonitoringSubscriptions.Targets(factoryId, lineId, MonitoringCategories.System))
                .SendAsync("productionChanged", new { factoryId, lineId, machineId, sourceType, at }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "realtime productionChanged push failed");
        }
    }
}
