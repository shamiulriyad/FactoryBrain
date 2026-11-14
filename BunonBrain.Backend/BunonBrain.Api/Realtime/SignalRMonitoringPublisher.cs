using BunonBrain.Api.Hubs;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using Microsoft.AspNetCore.SignalR;

namespace BunonBrain.Api.Realtime;

/// <summary>
/// Pushes monitoring changes over <see cref="MonitoringHub"/> to the groups
/// allowed to see them only: the item's factory group for its category, the
/// platform group, and its line's group. Never to all connections. Failures
/// are logged and dropped so the write that caused them still succeeds.
/// </summary>
public sealed class SignalRMonitoringPublisher : IMonitoringPublisher
{
    private readonly IHubContext<MonitoringHub> _hub;
    private readonly ILogger<SignalRMonitoringPublisher> _log;

    public SignalRMonitoringPublisher(IHubContext<MonitoringHub> hub, ILogger<SignalRMonitoringPublisher> log) { _hub = hub; _log = log; }

    public async Task EventAsync(MonitoringEventDto e, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.Groups(MonitoringSubscriptions.Targets(e.FactoryId, e.Line?.Id, e.Category)).SendAsync("monitoringEvent", e, ct);
            var status = MonitoringRules.PayloadString(e.Payload, "toStatus");
            if (e.EventType == MonitoringEventTypes.CameraStatusChanged && e.Camera is not null)
                await _hub.Clients.Groups(MonitoringSubscriptions.Targets(e.FactoryId, e.Line?.Id, MonitoringCategories.System))
                    .SendAsync("cameraStatus", new { id = e.Camera.Id, code = e.Camera.Code, status, e.SourceType, e.OccurredAt, eventId = e.Id }, ct);
            if (e.EventType == MonitoringEventTypes.DeviceStatusChanged && e.Device is not null)
                await _hub.Clients.Groups(MonitoringSubscriptions.Targets(e.FactoryId, e.Line?.Id, MonitoringCategories.System))
                    .SendAsync("deviceStatus", new { id = e.Device.Id, code = e.Device.Code, status, e.SourceType, e.OccurredAt, eventId = e.Id }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "realtime monitoringEvent push failed");
        }
    }

    public async Task AlertAsync(MonitoringAlertDto alert, string change, CancellationToken ct)
    {
        try
        {
            await _hub.Clients.Groups(MonitoringSubscriptions.Targets(alert.FactoryId, alert.Line?.Id, alert.Category))
                .SendAsync("monitoringAlert", new { change, alert }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "realtime monitoringAlert push failed");
        }
    }
}
