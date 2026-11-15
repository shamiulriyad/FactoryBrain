using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Monitoring;

namespace BunonBrain.Application.Abstractions.Interfaces;

/// <summary>
/// The monitoring event pipeline and its queries. Every producer (the HTTP
/// ingestion API, the simulator, the device pipeline, the camera service)
/// goes through <see cref="IngestAsync"/>: validation, idempotency, alert
/// rules, storage and the real-time push all happen in one place.
/// Reach: Super Admin every factory; factory roles their factories; line
/// roles (supervisors) only events on their lines. VISION events (people,
/// tracks, zones, activity) also need <c>monitoring.view</c>; callers with
/// only <c>devices.view</c> see SYSTEM events (camera, device, model status).
/// </summary>
public interface IMonitoringEventService
{
    /// <param name="caller">The producer's identity, or null for the server itself (health monitor).</param>
    /// <param name="producer">One of <see cref="Monitoring.MonitoringProducers"/>.</param>
    Task<AccessResult<MonitoringIngestResult>> IngestAsync(AccessContext? caller, MonitoringEventInput input, string producer, CancellationToken ct);

    Task<PagedResult<MonitoringEventDto>> ListAsync(AccessContext caller, MonitoringEventQuery query, CancellationToken ct);
    Task<AccessResult<MonitoringEventDetailDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<MonitoringEventDetailDto>> ReviewAsync(AccessContext caller, Guid id, ReviewMonitoringEventRequest request, string? traceId, CancellationToken ct);
    MonitoringContractDto Contract();
}

/// <summary>
/// Alerts raised from monitoring events and their lifecycle. Every action is
/// validated (state, scope, permission held by the endpoint) and recorded in
/// the alert's history and the audit log. Nothing here changes pay,
/// attendance or discipline.
/// </summary>
public interface IMonitoringAlertService
{
    Task<PagedResult<MonitoringAlertDto>> ListAsync(AccessContext caller, MonitoringAlertQuery query, CancellationToken ct);
    Task<AccessResult<MonitoringAlertDetailDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<MonitoringAlertDetailDto>> AcknowledgeAsync(AccessContext caller, Guid id, AcknowledgeAlertRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<MonitoringAlertDetailDto>> EscalateAsync(AccessContext caller, Guid id, EscalateAlertRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<MonitoringAlertDetailDto>> ResolveAsync(AccessContext caller, Guid id, ResolveAlertRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<MonitoringAlertDetailDto>> DismissAsync(AccessContext caller, Guid id, DismissAlertRequest request, string? traceId, CancellationToken ct);
}

/// <summary>The central monitoring room: counts, line and factory status, and the camera monitoring views.</summary>
public interface IMonitoringRoomService
{
    Task<MonitoringOverviewDto> OverviewAsync(AccessContext caller, MonitoringOverviewQuery query, CancellationToken ct);
    Task<IReadOnlyList<MonitoringCameraDto>> CamerasAsync(AccessContext caller, MonitoringCameraQuery query, CancellationToken ct);
    Task<AccessResult<MonitoringCameraDetailDto>> CameraAsync(AccessContext caller, Guid id, CancellationToken ct);

    /// <summary>Predicted physical activity of temporary tracks (current tracks, distributions, transitions). Needs <c>monitoring.view</c>.</summary>
    Task<MonitoringActivityDto> ActivityAsync(AccessContext caller, MonitoringActivityQuery query, CancellationToken ct);
}

/// <summary>
/// The monitoring simulator (<c>monitoring.simulate</c>). Its events use the
/// same pipeline as real producers and are always SIMULATED.
/// </summary>
public interface IMonitoringSimulatorService
{
    Task<AccessResult<SimulatorStatusDto>> StatusAsync(AccessContext caller, Guid? factoryId, CancellationToken ct);
    Task<AccessResult<SimulationRunDto>> StartAsync(AccessContext caller, StartSimulationRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<SimulationRunDto>> StopAsync(AccessContext caller, Guid runId, StopSimulationRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<SimulatorTriggerResult>> TriggerAsync(AccessContext caller, TriggerSimulatedEventRequest request, CancellationToken ct);

    /// <summary>Generates the events that are due for every running run (called every second by the worker; by tests with a chosen time).</summary>
    Task<SimulatorTickResult> TickAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// How equipment changes reach the monitoring pipeline: device connection,
/// error and status changes, and administrative camera status changes. Called
/// after the change is committed; failures are logged and never undo it.
/// Retries are safe (keys derive from the stored device event / change).
/// </summary>
public interface IMonitoringSystemHooks
{
    /// <param name="deviceEventIds">Ids of stored device events (connection, error, status, heartbeat missed).</param>
    /// <param name="caller">Who sent the device message, or null for the server's health monitor.</param>
    Task DeviceEventsAsync(IReadOnlyCollection<long> deviceEventIds, AccessContext? caller, CancellationToken ct);

    Task CameraStatusAsync(Guid cameraId, string fromStatus, string toStatus, string? reason, AccessContext caller, CancellationToken ct);
}

/// <summary>
/// Real-time delivery of monitoring changes (SignalR in the API). Messages go
/// only to the groups that may see them: the factory group of the item's
/// category and the line group of its line. Failures are logged, never thrown.
/// </summary>
public interface IMonitoringPublisher
{
    Task EventAsync(MonitoringEventDto e, CancellationToken ct);
    Task AlertAsync(MonitoringAlertDto alert, string change, CancellationToken ct);
}

/// <summary>Used where nothing listens (tests of services without the API host).</summary>
public sealed class NullMonitoringPublisher : IMonitoringPublisher
{
    public Task EventAsync(MonitoringEventDto e, CancellationToken ct) => Task.CompletedTask;
    public Task AlertAsync(MonitoringAlertDto alert, string change, CancellationToken ct) => Task.CompletedTask;
}
