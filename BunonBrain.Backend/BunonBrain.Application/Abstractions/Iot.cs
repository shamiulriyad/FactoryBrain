using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Iot;
using BunonBrain.Application.Iot;

namespace BunonBrain.Application.Abstractions.Interfaces;

/// <summary>Who handed a message to the IoT pipeline.</summary>
/// <param name="Caller">A signed-in integration account (HTTP with a token), or null for the MQTT bridge / a device key.</param>
/// <param name="AuthenticatedDeviceId">The device whose key authenticated the request (HTTP device API).</param>
/// <param name="FactoryId">The factory the caller addressed (HTTP); checked against the device's own factory.</param>
public sealed record IotOrigin(string Transport, AccessContext? Caller, Guid? AuthenticatedDeviceId, Guid? FactoryId, IotTopic? Topic)
{
    public string Actor => Caller?.Email ?? Transport switch
    {
        IotTransports.Mqtt => "system:mqtt-bridge",
        IotTransports.Simulator => "system:iot-simulator",
        _ => AuthenticatedDeviceId is { } d ? $"device:{d:N}" : "system:iot",
    };
}

/// <summary>
/// The one IoT pipeline every transport uses: resolve and check the device
/// against the registry, store the raw message once (idempotent), derive
/// the machine state, count production, raise alerts through monitoring and
/// push live updates. Never trusts a factory, device or machine id it did
/// not look up itself.
/// </summary>
public interface IIotIngestionService
{
    Task<AccessResult<IotIngestResult>> IngestAsync(IotMessage message, IotOrigin origin, CancellationToken ct);

    /// <summary>Parse raw bytes and ingest; invalid payloads from a known device are recorded as a device event.</summary>
    Task<AccessResult<IotIngestResult>> IngestRawAsync(byte[] payload, IotOrigin origin, CancellationToken ct);

    /// <summary>The device whose key matches, or null (HTTP device authentication).</summary>
    Task<Guid?> AuthenticateDeviceAsync(Guid deviceId, string? key, CancellationToken ct);
}

/// <summary>Device health, silence, timed alert rules and telemetry retention (the machine monitor runs it every 30 s).</summary>
public interface IMachineMonitorService
{
    Task<MachineMonitorResult> RunAsync(AccessContext? caller, Guid? factoryId, CancellationToken ct);
    Task<MachineStateRefresh> RefreshMachineAsync(Guid machineId, AccessContext? caller, CancellationToken ct);
}

public sealed record MachineMonitorResult(int Machines, int StateChanges, int ConditionChanges, int TelemetryDeleted);
public sealed record MachineStateRefresh(Guid MachineId, string State, bool Changed);

public interface IIotQueryService
{
    Task<MachineOverviewDto> OverviewAsync(AccessContext caller, IotFilter filter, CancellationToken ct);
    Task<PagedResult<MachineDto>> MachinesAsync(AccessContext caller, IotFilter filter, CancellationToken ct);
    Task<AccessResult<MachineDetailDto>> MachineAsync(AccessContext caller, Guid machineId, string? source, CancellationToken ct);
    Task<AccessResult<PagedResult<StateTransitionDto>>> StateHistoryAsync(AccessContext caller, IotFilter filter, CancellationToken ct);
    Task<AccessResult<PagedResult<TelemetryDto>>> TelemetryAsync(AccessContext caller, Guid machineId, IotFilter filter, CancellationToken ct);
    Task<PagedResult<CounterEventDto>> CounterEventsAsync(AccessContext caller, IotFilter filter, CancellationToken ct);
    Task<ProductionReportDto> ProductionAsync(AccessContext caller, IotFilter filter, CancellationToken ct);
    Task<PagedResult<ProductionCorrectionDto>> CorrectionsAsync(AccessContext caller, IotFilter filter, CancellationToken ct);
    Task<IReadOnlyList<DeviceIotDto>> DevicesAsync(AccessContext caller, IotFilter filter, CancellationToken ct);
    Task<AccessResult<DeviceIotDto>> DeviceAsync(AccessContext caller, Guid deviceId, CancellationToken ct);
    IotBridgeStatusDto Bridge();
}

public interface IIotConfigService
{
    Task<AccessResult<DeviceIotDto>> SetDeviceConfigAsync(AccessContext caller, Guid deviceId, DeviceIotConfigRequest r, string? traceId, CancellationToken ct);
    Task<AccessResult<DeviceKeyIssued>> IssueKeyAsync(AccessContext caller, Guid deviceId, string? traceId, CancellationToken ct);
    Task<AccessResult<DeviceIotDto>> RevokeKeyAsync(AccessContext caller, Guid deviceId, string? traceId, CancellationToken ct);
    Task<AccessResult<MachineProfileDto>> SetProfileAsync(AccessContext caller, Guid machineId, MachineProfileRequest r, string? traceId, CancellationToken ct);
    Task<AccessResult<MachineDto>> SetMaintenanceAsync(AccessContext caller, Guid machineId, MaintenanceRequest r, string? traceId, CancellationToken ct);
    Task<AccessResult<SimulatedMachineSetupResult>> SetUpSimulatedMachineAsync(AccessContext caller, SimulatedMachineSetupRequest r, string? traceId, CancellationToken ct);
}

public interface IProductionCorrectionService
{
    Task<AccessResult<CounterEventDto>> ResetCounterAsync(AccessContext caller, CounterResetRequest r, string? traceId, CancellationToken ct);
    Task<AccessResult<ProductionCorrectionDto>> CorrectAsync(AccessContext caller, ProductionCorrectionRequest r, string? traceId, CancellationToken ct);
}

public interface IIotSimulatorService
{
    Task<AccessResult<IotSimulationResult>> SendAsync(AccessContext caller, IotSimulationRequest r, CancellationToken ct);
    Task<AccessResult<IotSimulationRunDto>> StartRunAsync(AccessContext caller, IotSimulationRunRequest r, CancellationToken ct);
    AccessResult<IotSimulationRunDto> StopRun(AccessContext caller, Guid runId);
    IReadOnlyList<IotSimulationRunDto> Runs(AccessContext caller);
}

/// <summary>Live updates to the monitoring hub (SYSTEM groups of the factory and line).</summary>
public interface IIotPublisher
{
    Task MachineAsync(MachineDto machine, string change, CancellationToken ct);
    /// <summary>A production count changed: a ping without numbers (clients reload through the API, which checks production access).</summary>
    Task ProductionAsync(Guid factoryId, string? lineId, Guid? machineId, string sourceType, DateTimeOffset at, CancellationToken ct);
}

public sealed class NullIotPublisher : IIotPublisher
{
    public Task MachineAsync(MachineDto machine, string change, CancellationToken ct) => Task.CompletedTask;
    public Task ProductionAsync(Guid factoryId, string? lineId, Guid? machineId, string sourceType, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Live state of the MQTT bridge (singleton), for the status endpoint.</summary>
public interface IMqttBridgeState
{
    IotBridgeStatusDto Snapshot();
}
