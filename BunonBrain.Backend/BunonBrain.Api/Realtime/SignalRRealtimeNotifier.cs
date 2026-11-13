using BunonBrain.Api.Hubs;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Domain.Entities;
using Microsoft.AspNetCore.SignalR;

namespace BunonBrain.Api.Realtime;

/// <summary>
/// SignalR-backed <see cref="IRealtimeNotifier"/>. Wraps every broadcast
/// in a try/catch — a notifier outage is logged and dropped so the
/// REST write that triggered it (sensor ingest, line-board refresh,
/// floor-alert push) never fails because no clients are listening or
/// the transport hiccupped.
/// </summary>
public sealed class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<FactoryHub> _hub;
    private readonly ILogger<SignalRRealtimeNotifier> _log;

    public SignalRRealtimeNotifier(IHubContext<FactoryHub> hub, ILogger<SignalRRealtimeNotifier> log)
    {
        _hub = hub; _log = log;
    }

    public async Task SensorReadingAsync(BunonBrain.Application.Dtos.Sensors.IngestResponse payload, CancellationToken ct)
    {
        try { await _hub.Clients.All.SendAsync("sensorReading", payload, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "realtime sensorReading broadcast failed"); }
    }

    public async Task LineBoardUpdatedAsync(BunonBrain.Application.Dtos.LineBoard.LineBoardResponse payload, CancellationToken ct)
    {
        try { await _hub.Clients.All.SendAsync("lineBoardUpdated", payload, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "realtime lineBoardUpdated broadcast failed"); }
    }

    public async Task FloorAlertAsync(FloorAlert payload, CancellationToken ct)
    {
        try { await _hub.Clients.All.SendAsync("floorAlert", payload, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "realtime floorAlert broadcast failed"); }
    }
}