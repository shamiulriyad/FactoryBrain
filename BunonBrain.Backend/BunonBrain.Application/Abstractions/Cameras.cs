using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Cameras;
using BunonBrain.Application.Dtos.Devices;

namespace BunonBrain.Application.Abstractions.Interfaces;

/// <summary>
/// Cameras, their coverage, video sources, reference frames and zones.
/// Reach: Super Admin every factory; factory roles their factories; a
/// Production Supervisor only cameras covering (or mainly watching) their
/// assigned lines. A camera outside reach answers "not found". Changing
/// anything needs factory-wide reach (checked here) and <c>cameras.manage</c>
/// (checked on the endpoint). Credentials are encrypted at rest and never
/// returned; the server never connects to a camera URL in this phase.
/// </summary>
public interface ICameraService
{
    Task<PagedResult<CameraDto>> ListAsync(AccessContext caller, CameraListQuery query, CancellationToken ct);
    Task<AccessResult<CameraDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<CameraOverviewDto> OverviewAsync(AccessContext caller, Guid? factoryId, CancellationToken ct);

    Task<AccessResult<CameraDto>> CreateAsync(AccessContext caller, CreateCameraRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<CameraDto>> UpdateAsync(AccessContext caller, Guid id, UpdateCameraRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<CameraDto>> SetStatusAsync(AccessContext caller, Guid id, SetCameraStatusRequest request, string? traceId, CancellationToken ct);
    /// <summary>Only a camera without zones and without device history; anything else is deactivated instead.</summary>
    Task<AccessResult<bool>> DeleteAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct);
    Task<AccessResult<CameraDto>> SetCoverageAsync(AccessContext caller, Guid id, SetCameraCoverageRequest request, string? traceId, CancellationToken ct);

    Task<AccessResult<IReadOnlyList<CameraSourceDto>>> SourcesAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<CameraDto>> SetSourceAsync(AccessContext caller, Guid id, SetCameraSourceRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<CameraDto>> SetVideoAsync(AccessContext caller, Guid id, UploadedFile file, string? notes, string? traceId, CancellationToken ct);
    Task<AccessResult<CameraFile>> VideoAsync(AccessContext caller, Guid id, CancellationToken ct);

    Task<AccessResult<CameraDto>> SetReferenceFrameAsync(AccessContext caller, Guid id, byte[] image, string? source, double? frameSeconds, string? traceId, CancellationToken ct);
    Task<AccessResult<CameraFile>> ReferenceFrameAsync(AccessContext caller, Guid id, CancellationToken ct);

    Task<AccessResult<IReadOnlyList<CameraZoneDto>>> ZonesAsync(AccessContext caller, Guid cameraId, bool includeInactive, CancellationToken ct);
    Task<AccessResult<CameraZoneDto>> CreateZoneAsync(AccessContext caller, Guid cameraId, CreateCameraZoneRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<CameraZoneDto>> UpdateZoneAsync(AccessContext caller, Guid zoneId, UpdateCameraZoneRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<IReadOnlyList<CameraZoneRevisionDto>>> ZoneHistoryAsync(AccessContext caller, Guid zoneId, CancellationToken ct);
}

/// <summary>Reference frames and recorded videos of cameras, on the file store.</summary>
public interface ICameraFileStore
{
    long MaxImageBytes { get; }
    long MaxVideoBytes { get; }
    Task<string> SaveImageAsync(byte[] content, string extension, CancellationToken ct);
    /// <summary>Copies at most <see cref="MaxVideoBytes"/>; throws <see cref="FileTooLargeException"/> beyond that.</summary>
    Task<string> SaveVideoAsync(Stream content, string extension, CancellationToken ct);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed class FileTooLargeException : Exception
{
    public FileTooLargeException(long limit) : base($"The file is larger than {limit} bytes.") { Limit = limit; }
    public long Limit { get; }
}

/// <summary>
/// Devices and their health. Reach as for cameras (supervisors: devices on
/// their lines). Registering and changing devices needs factory-wide reach
/// and <c>devices.manage</c>; history needs <c>devices.logs.view</c> or
/// <c>devices.manage</c>.
/// </summary>
public interface IDeviceService
{
    Task<PagedResult<DeviceDto>> ListAsync(AccessContext caller, DeviceListQuery query, CancellationToken ct);
    Task<AccessResult<DeviceDto>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<DeviceDto>> CreateAsync(AccessContext caller, CreateDeviceRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<DeviceDto>> UpdateAsync(AccessContext caller, Guid id, UpdateDeviceRequest request, string? traceId, CancellationToken ct);
    Task<AccessResult<DeviceDto>> SetStatusAsync(AccessContext caller, Guid id, SetDeviceStatusRequest request, string? traceId, CancellationToken ct);
    /// <summary>Only a device that never sent anything (no heartbeats, no events).</summary>
    Task<AccessResult<bool>> DeleteAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct);

    Task<AccessResult<PagedResult<DeviceHeartbeatDto>>> HeartbeatsAsync(AccessContext caller, Guid id, int page, int pageSize, CancellationToken ct);
    Task<PagedResult<DeviceEventDto>> EventsAsync(AccessContext caller, DeviceEventQuery query, CancellationToken ct);
    Task<DeviceHealthOverviewDto> HealthAsync(AccessContext caller, Guid? factoryId, CancellationToken ct);
}

/// <summary>
/// The one pipeline every device message goes through — real hardware
/// (<c>Device</c>) and the simulator (<c>Simulator</c>) alike: row lock,
/// message-id de-duplication, clock checks, heartbeat / event rows,
/// connection transitions. A simulated device accepts only simulator
/// messages and a real device only device messages. The health check
/// (also run every minute in the background) marks devices whose
/// heartbeats stopped as offline.
/// </summary>
public interface IDeviceIngestionService
{
    Task<AccessResult<DeviceMessageResult>> IngestAsync(AccessContext caller, Guid deviceId, DeviceMessage message, string source, CancellationToken ct);
    Task<AccessResult<DeviceMessageResult>> IngestByCodeAsync(AccessContext caller, Guid factoryId, string deviceCode, DeviceMessage message, CancellationToken ct);

    Task<AccessResult<IReadOnlyList<DeviceDto>>> SimulatedDevicesAsync(AccessContext caller, Guid? factoryId, CancellationToken ct);
    Task<AccessResult<SimulatorBatchResult>> SimulateHeartbeatsAsync(AccessContext caller, SimulatorHeartbeatRequest request, CancellationToken ct);
    Task<AccessResult<DeviceMessageResult>> SimulateAsync(AccessContext caller, Guid deviceId, string kind, string? code, string? message, string? severity, CancellationToken ct);

    /// <summary>Mark devices with no message inside their offline window as offline. <paramref name="caller"/> null = the background monitor (every factory).</summary>
    Task<HealthCheckResult> CheckHealthAsync(AccessContext? caller, CancellationToken ct);
}
