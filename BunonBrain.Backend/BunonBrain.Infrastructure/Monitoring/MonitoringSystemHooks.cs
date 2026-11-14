using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Monitoring;

/// <summary>
/// Turns stored device events and camera status changes into monitoring
/// events through the normal pipeline (see <see cref="IMonitoringSystemHooks"/>).
/// A camera's health device going online / offline is also reported as the
/// camera's status. Keys derive from the stored rows, so a retry is a duplicate.
/// </summary>
public sealed class MonitoringSystemHooks : IMonitoringSystemHooks
{
    private readonly BunonBrainDbContext _db;
    private readonly IMonitoringEventService _events;
    private readonly ILogger<MonitoringSystemHooks> _log;

    public MonitoringSystemHooks(BunonBrainDbContext db, IMonitoringEventService events, ILogger<MonitoringSystemHooks> log)
    {
        _db = db; _events = events; _log = log;
    }

    /// <summary>The status a device event means, or null when it isn't a status change (firmware).</summary>
    public static string? DeviceStatus(string eventType, string? toState) => eventType switch
    {
        DeviceEventTypes.ConnectionOnline => "ONLINE",
        DeviceEventTypes.ConnectionOffline or DeviceEventTypes.HeartbeatMissed => "OFFLINE",
        DeviceEventTypes.Error => "ERROR",
        DeviceEventTypes.StatusChanged => toState?.ToUpperInvariant(),
        _ => null,
    };

    private static string? FromStatus(string eventType, string? fromState) => eventType switch
    {
        DeviceEventTypes.ConnectionOnline or DeviceEventTypes.ConnectionOffline or DeviceEventTypes.HeartbeatMissed or DeviceEventTypes.StatusChanged
            => fromState?.ToUpperInvariant(),
        _ => null,
    };

    public async Task DeviceEventsAsync(IReadOnlyCollection<long> deviceEventIds, AccessContext? caller, CancellationToken ct)
    {
        if (deviceEventIds.Count == 0) return;
        try
        {
            var ids = deviceEventIds.ToList();
            var rows = await _db.DeviceEvents.AsNoTracking().Where(e => ids.Contains(e.Id)).OrderBy(e => e.Id).ToListAsync(ct);
            foreach (var e in rows)
            {
                var to = DeviceStatus(e.EventType, e.ToState);
                if (to is null || !StatusValues.Device.Contains(to)) continue;
                var device = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == e.DeviceId, ct);
                if (device is null) continue;
                var source = MonitoringSourceTypes.Of(device.IsSimulated);
                var payload = new Dictionary<string, object?>
                {
                    ["toStatus"] = to, ["fromStatus"] = FromStatus(e.EventType, e.FromState), ["deviceEventId"] = e.Id, ["deviceEventType"] = e.EventType,
                    ["deviceSource"] = e.Source, ["code"] = e.Code, ["message"] = e.Message, ["category"] = device.Category,
                };
                if (device.IsSimulated) payload["note"] = "Simulated device: this status comes from the device simulator, not from hardware.";
                var json = JsonSerializer.SerializeToElement(payload);
                var result = await _events.IngestAsync(caller, new MonitoringEventInput(
                    MonitoringEventTypes.DeviceStatusChanged, device.FactoryId, $"device-event:{e.Id}", e.OccurredAt, source,
                    DeviceId: device.Id, CameraId: device.CameraId, Payload: json), MonitoringProducers.DevicePipeline, ct);
                if (result.Outcome != Application.Dtos.Access.AccessOutcome.Ok)
                    _log.LogWarning("Device event {Id} not mirrored to monitoring: {Code} {Message}", e.Id, result.Code, result.Message);

                // A camera's health device going on / offline is the camera going on / offline.
                if (device.CameraId is { } cameraId && to is "ONLINE" or "OFFLINE")
                {
                    var camPayload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["toStatus"] = to, ["fromStatus"] = to == "ONLINE" ? "OFFLINE" : "ONLINE", ["via"] = $"health device {device.Code}",
                        ["deviceEventId"] = e.Id, ["reason"] = e.Message,
                        ["note"] = device.IsSimulated ? "Simulated health device: not a real camera connection." : null,
                    });
                    var camResult = await _events.IngestAsync(caller, new MonitoringEventInput(
                        MonitoringEventTypes.CameraStatusChanged, device.FactoryId, $"device-event:{e.Id}:camera", e.OccurredAt, source,
                        CameraId: cameraId, Payload: camPayload), MonitoringProducers.DevicePipeline, ct);
                    if (camResult.Outcome != Application.Dtos.Access.AccessOutcome.Ok)
                        _log.LogWarning("Camera status from device event {Id} not recorded: {Code} {Message}", e.Id, camResult.Code, camResult.Message);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The device change is already committed; monitoring catches up on the next change.
            _log.LogWarning(ex, "Mirroring device events to monitoring failed.");
        }
    }

    public async Task CameraStatusAsync(Guid cameraId, string fromStatus, string toStatus, string? reason, AccessContext caller, CancellationToken ct)
    {
        try
        {
            var cam = await _db.Cameras.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cameraId, ct);
            if (cam is null) return;
            var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["toStatus"] = toStatus.ToUpperInvariant(), ["fromStatus"] = fromStatus.ToUpperInvariant(), ["reason"] = reason, ["kind"] = "ADMINISTRATIVE",
            });
            var result = await _events.IngestAsync(caller, new MonitoringEventInput(
                MonitoringEventTypes.CameraStatusChanged, cam.FactoryId, $"camera-status:{cam.Id}:{cam.StatusChangedAt.UtcTicks}", cam.StatusChangedAt,
                MonitoringSourceTypes.Real, CameraId: cam.Id, Payload: payload), MonitoringProducers.CameraService, ct);
            if (result.Outcome != Application.Dtos.Access.AccessOutcome.Ok)
                _log.LogWarning("Camera status change of {Camera} not recorded in monitoring: {Code} {Message}", cam.Code, result.Code, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Recording a camera status change in monitoring failed.");
        }
    }
}
