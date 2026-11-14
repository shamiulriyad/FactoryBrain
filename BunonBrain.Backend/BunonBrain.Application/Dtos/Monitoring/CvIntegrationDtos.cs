using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Cameras;

namespace BunonBrain.Application.Dtos.Monitoring;

// Camera configuration for the computer-vision service (service key only).
// Unlike every user-facing camera API, the stream URL here carries its
// credentials: the service needs them to open the stream. People can never
// call these endpoints, whatever their permissions.

public sealed record CvZonePoint(double X, double Y);

/// <summary>An active zone: a polygon in normalised (0–1) frame coordinates, and what it stands for.</summary>
public sealed record CvZoneConfig(Guid Id, string Name, string ZoneType, IReadOnlyList<CvZonePoint> Points, Guid? WorkstationId,
    string? WorkstationCode, string? LineId, int Version);

/// <summary>
/// The current video source. <c>StreamUrl</c> (RTSP / HTTP) includes decrypted credentials when some
/// are stored; <c>HasVideo</c> means a recorded video can be downloaded from the integration API.
/// </summary>
public sealed record CvSourceConfig(string SourceType, string? StreamUrl, string? WebcamLabel, bool HasVideo, string? VideoFileName,
    string? VideoContentType, int Version);

public sealed record CvCameraConfig(Guid Id, string Code, string Name, string CameraType, Guid FactoryId, string FactoryCode, string? LineId,
    string Status, CvSourceConfig Source, IReadOnlyList<CvZoneConfig> Zones, IReadOnlyList<string> CoveredWorkstations,
    int? FrameWidth, int? FrameHeight, DateTimeOffset UpdatedAt);

public interface ICvIntegrationService
{
    Task<IReadOnlyList<CvCameraConfig>> CamerasAsync(AccessContext service, CancellationToken ct);
    Task<AccessResult<CvCameraConfig>> CameraAsync(AccessContext service, Guid id, CancellationToken ct);
    Task<AccessResult<CameraFile>> VideoAsync(AccessContext service, Guid id, CancellationToken ct);
}
