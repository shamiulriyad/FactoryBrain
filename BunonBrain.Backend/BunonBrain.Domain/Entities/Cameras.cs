namespace BunonBrain.Domain.Entities;

// Cameras: the registered cameras of a factory, what each one observes
// (coverage: lines and workstations), how its video is reached (source
// versions, with credentials encrypted at rest) and the polygon zones drawn
// over its reference frame for the future computer-vision service.
//
// Nothing here is a detection or a tracking result. A camera record says a
// camera exists and where it looks; whether a stream is actually reachable
// is not checked by the server in this phase (it never connects to camera
// URLs). Health comes from the camera's linked Device, when it has one.

/// <summary>
/// A camera of one factory. <see cref="Code"/> (e.g. <c>CAM-001</c>) is
/// unique within the factory. A camera with zones or device history can't be
/// deleted, only deactivated.
/// </summary>
public class Camera
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>Upper case, unique within the factory (e.g. <c>CAM-001</c>).</summary>
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>One of <c>CameraTypes</c>: FixedCctv, IpCamera, UsbCamera, RtspCamera, Webcam, RecordedVideo.</summary>
    public string CameraType { get; set; } = default!;
    public Guid? DepartmentId { get; set; }
    /// <summary>The production floor the camera is installed on.</summary>
    public Guid FloorId { get; set; }
    /// <summary>The line the camera mainly watches, if any. Coverage may add more.</summary>
    public string? LineId { get; set; }
    /// <summary>Where it hangs / what it looks at, e.g. "Ceiling above T-025, facing the line".</summary>
    public string? Location { get; set; }
    public string? Description { get; set; }
    /// <summary>One of <c>CameraStatuses</c>: Active, Inactive, Maintenance.</summary>
    public string Status { get; set; } = "Active";
    public string? StatusReason { get; set; }
    public DateTimeOffset StatusChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateOnly? InstalledOn { get; set; }

    // Reference frame: the still image zones are drawn on (uploaded, or a frame grabbed from the recorded video).
    public string? ReferenceImageKey { get; set; }
    public string? ReferenceImageContentType { get; set; }
    public int? ReferenceImageWidth { get; set; }
    public int? ReferenceImageHeight { get; set; }
    /// <summary>Upload or VideoFrame.</summary>
    public string? ReferenceImageSource { get; set; }
    /// <summary>For a VideoFrame: the position in the recorded video, in seconds.</summary>
    public double? ReferenceFrameSeconds { get; set; }
    public DateTimeOffset? ReferenceImageUpdatedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One version of a camera's video source configuration. Updating the source
/// adds a new version and retires the previous one (<see cref="RetiredAt"/>),
/// so the configuration history is kept. At most one current version per camera.
/// Credentials are stored only as an AES-GCM ciphertext bound to this row and
/// are never returned by the API.
/// </summary>
public class CameraSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CameraId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>1, 2, 3 … per camera.</summary>
    public int Version { get; set; }
    /// <summary>One of <c>CameraSourceTypes</c>: None, Rtsp, LocalWebcam, RecordedVideo, HttpStream.</summary>
    public string SourceType { get; set; } = "None";
    /// <summary>The stream URL without any user name or password (e.g. <c>rtsp://10.0.4.21:554/stream1</c>).</summary>
    public string? StreamUrl { get; set; }
    /// <summary>Host part of <see cref="StreamUrl"/>, for display and filtering.</summary>
    public string? StreamHost { get; set; }
    /// <summary>Encrypted user name + password (JSON), or null when the stream needs none.</summary>
    public byte[]? CredentialsCiphertext { get; set; }
    /// <summary>Browser webcam source: a label hint for which local camera to pick.</summary>
    public string? WebcamLabel { get; set; }
    // Recorded / uploaded video.
    public string? VideoKey { get; set; }
    public string? VideoContentType { get; set; }
    public string? VideoFileName { get; set; }
    public long? VideoSizeBytes { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    /// <summary>Null while this is the camera's current source.</summary>
    public DateTimeOffset? RetiredAt { get; set; }
}

/// <summary>
/// One production area a camera observes: a whole line, or one workstation
/// (a camera usually covers several). Removing coverage sets
/// <see cref="RemovedAt"/>; rows are kept as history.
/// </summary>
public class CameraCoverage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CameraId { get; set; }
    public Guid FactoryId { get; set; }
    /// <summary>Line or Workstation.</summary>
    public string Kind { get; set; } = default!;
    /// <summary>The covered line; for a workstation, the workstation's line (copied when mapped).</summary>
    public string LineId { get; set; } = default!;
    public Guid? WorkstationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedByEmail { get; set; } = default!;
    public DateTimeOffset? RemovedAt { get; set; }
    public string? RemovedByEmail { get; set; }
}

/// <summary>
/// A polygon over a camera's reference frame, in normalised coordinates
/// (0–1, origin top-left), for the future computer-vision service: a
/// workstation zone, a walkway, a restricted area. Never deleted; zones are
/// deactivated, and every change is written to <see cref="CameraZoneRevision"/>.
/// </summary>
public class CameraZone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CameraId { get; set; }
    public Guid FactoryId { get; set; }
    public string Name { get; set; } = default!;
    /// <summary>One of <c>ZoneTypes</c>: Workstation, ProductionLine, Walkway, Restricted, Other.</summary>
    public string ZoneType { get; set; } = default!;
    public Guid? WorkstationId { get; set; }
    public string? LineId { get; set; }
    /// <summary>JSON array of {"x":…,"y":…} points, each in [0, 1].</summary>
    public string Polygon { get; set; } = "[]";
    /// <summary>Display colour, <c>#RRGGBB</c>.</summary>
    public string? Color { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Incremented on every change (1 at creation).</summary>
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedByEmail { get; set; } = default!;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string UpdatedByEmail { get; set; } = default!;
}

/// <summary>
/// One version of a zone, written whenever it is created or changed.
/// Append-only (a database trigger rejects updates and deletes).
/// </summary>
public class CameraZoneRevision
{
    public long Id { get; set; }
    public Guid ZoneId { get; set; }
    public Guid CameraId { get; set; }
    public Guid FactoryId { get; set; }
    public int Version { get; set; }
    /// <summary>Created, Updated, Deactivated or Reactivated.</summary>
    public string Action { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string ZoneType { get; set; } = default!;
    public Guid? WorkstationId { get; set; }
    public string? LineId { get; set; }
    public string Polygon { get; set; } = "[]";
    public bool IsActive { get; set; }
    /// <summary>The reference frame the polygon was drawn on at the time.</summary>
    public string? ReferenceImageKey { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ChangedByUserId { get; set; }
    public string ChangedByEmail { get; set; } = default!;
}
