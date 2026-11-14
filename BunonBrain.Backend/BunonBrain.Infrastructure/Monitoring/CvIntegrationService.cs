using System.Text.Json;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Application.Cameras;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Cameras;
using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Cameras;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Monitoring;

/// <summary>
/// Camera configuration for the computer-vision service (see
/// <see cref="ICvIntegrationService"/>). Reach is the service's configured
/// factories. Zones come from the cameras module (never duplicated in Python).
/// </summary>
public sealed class CvIntegrationService : ICvIntegrationService
{
    private readonly BunonBrainDbContext _db;
    private readonly ICameraService _cameras;
    private readonly AiSecretProtector _secrets;
    private readonly ILogger<CvIntegrationService> _log;

    public CvIntegrationService(BunonBrainDbContext db, ICameraService cameras, AiSecretProtector secrets, ILogger<CvIntegrationService> log)
    {
        _db = db; _cameras = cameras; _secrets = secrets; _log = log;
    }

    private static AccessResult<T> NotFound<T>() => AccessResult<T>.NotFound("camera_not_found", "Camera not found.");

    private static bool IsService(AccessContext caller) => caller.IsService;

    public async Task<IReadOnlyList<CvCameraConfig>> CamerasAsync(AccessContext service, CancellationToken ct)
    {
        if (!IsService(service)) return Array.Empty<CvCameraConfig>();
        var rows = await CameraService.InScope(_db, service).OrderBy(c => c.Code).Take(500).ToListAsync(ct);
        return await ConfigsAsync(rows, ct);
    }

    public async Task<AccessResult<CvCameraConfig>> CameraAsync(AccessContext service, Guid id, CancellationToken ct)
    {
        if (!IsService(service)) return NotFound<CvCameraConfig>();
        var cam = await CameraService.InScope(_db, service).FirstOrDefaultAsync(c => c.Id == id, ct);
        return cam is null ? NotFound<CvCameraConfig>() : AccessResult<CvCameraConfig>.Ok((await ConfigsAsync(new[] { cam }, ct))[0]);
    }

    public async Task<AccessResult<CameraFile>> VideoAsync(AccessContext service, Guid id, CancellationToken ct) =>
        IsService(service) ? await _cameras.VideoAsync(service, id, ct) : NotFound<CameraFile>();

    private async Task<IReadOnlyList<CvCameraConfig>> ConfigsAsync(IReadOnlyList<Camera> cams, CancellationToken ct)
    {
        var ids = cams.Select(c => c.Id).ToList();
        var codes = await _db.Factories.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Code, ct);
        var sources = await _db.CameraSources.AsNoTracking().Where(s => s.RetiredAt == null && ids.Contains(s.CameraId)).ToDictionaryAsync(s => s.CameraId, ct);
        var zones = await _db.CameraZones.AsNoTracking().Where(z => z.IsActive && ids.Contains(z.CameraId)).OrderBy(z => z.Name).ToListAsync(ct);
        var coverage = await _db.CameraCoverage.AsNoTracking().Where(v => v.RemovedAt == null && v.WorkstationId != null && ids.Contains(v.CameraId))
            .Select(v => new { v.CameraId, WorkstationId = v.WorkstationId!.Value }).ToListAsync(ct);
        var wsIds = zones.Where(z => z.WorkstationId != null).Select(z => z.WorkstationId!.Value).Concat(coverage.Select(v => v.WorkstationId)).Distinct().ToList();
        var wsCodes = await _db.Workstations.AsNoTracking().Where(w => wsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        return cams.Select(c => new CvCameraConfig(
            c.Id, c.Code, c.Name, c.CameraType, c.FactoryId, codes.GetValueOrDefault(c.FactoryId, "?"), c.LineId, c.Status,
            Source(sources.GetValueOrDefault(c.Id)),
            zones.Where(z => z.CameraId == c.Id).Select(z => new CvZoneConfig(z.Id, z.Name, z.ZoneType,
                ZoneGeometry.Deserialize(z.Polygon).Select(p => new CvZonePoint(p.X, p.Y)).ToList(), z.WorkstationId,
                z.WorkstationId is { } w ? wsCodes.GetValueOrDefault(w) : null, z.LineId, z.Version)).ToList(),
            coverage.Where(v => v.CameraId == c.Id).Select(v => wsCodes.GetValueOrDefault(v.WorkstationId, "?")).OrderBy(x => x).ToList(),
            c.ReferenceImageWidth, c.ReferenceImageHeight, c.UpdatedAt)).ToList();
    }

    private CvSourceConfig Source(CameraSource? s)
    {
        if (s is null) return new CvSourceConfig(CameraSourceTypes.None, null, null, false, null, null, 0);
        string? url = s.StreamUrl;
        if (url is not null && s.CredentialsCiphertext is { } blob)
        {
            if (_secrets.TryUnprotect(blob, CameraRules.CredentialsPurposeFor(s.Id), out var json) && json is not null)
            {
                var creds = JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? new();
                url = WithCredentials(url, creds.GetValueOrDefault("u"), creds.GetValueOrDefault("p"));
            }
            else _log.LogWarning("Stored credentials of camera source {Source} could not be decrypted; the stream URL is sent without them.", s.Id);
        }
        return new CvSourceConfig(s.SourceType, url, s.WebcamLabel, s.VideoKey is not null, s.VideoFileName, s.VideoContentType, s.Version);
    }

    public static string WithCredentials(string url, string? user, string? password)
    {
        if (string.IsNullOrEmpty(user) && string.IsNullOrEmpty(password)) return url;
        var b = new UriBuilder(url) { UserName = Uri.EscapeDataString(user ?? ""), Password = Uri.EscapeDataString(password ?? "") };
        // UriBuilder adds a default port and trailing slash differently per scheme; keep the original path and query.
        return b.Uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
    }
}
