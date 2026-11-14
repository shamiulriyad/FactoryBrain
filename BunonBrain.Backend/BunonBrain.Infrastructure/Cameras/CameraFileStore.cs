using System.Globalization;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Infrastructure.Storage;

namespace BunonBrain.Infrastructure.Cameras;

/// <summary>
/// Camera reference frames (JPEG / PNG / WebP) and recorded videos (MP4 /
/// WebM) under <c>CAMERA_FILE_STORAGE_PATH</c> (default
/// <c>App_Data/camera-files</c>), stored by <see cref="SafeFileStore"/>:
/// generated keys, re-validated on every read, atomic writes. Camera images
/// can show workers, so they are served only through the API, to callers
/// who may see the camera. Limits: <c>CAMERA_IMAGE_MAX_MB</c> (default 8,
/// at most 20) and <c>CAMERA_VIDEO_MAX_MB</c> (default 50, at most 200).
/// </summary>
public sealed class CameraFileStore : ICameraFileStore
{
    private readonly SafeFileStore _images;
    private readonly SafeFileStore _videos;

    public CameraFileStore(string storagePath, long maxImageBytes, long maxVideoBytes, ILogger<CameraFileStore> log)
    {
        _images = new SafeFileStore(Path.Combine(storagePath, "frames"), new[] { "jpg", "png", "webp" }, log, "camera reference frame");
        _videos = new SafeFileStore(Path.Combine(storagePath, "videos"), new[] { "mp4", "webm" }, log, "camera video");
        MaxImageBytes = maxImageBytes;
        MaxVideoBytes = maxVideoBytes;
    }

    public static CameraFileStore FromEnvironment(Func<string, string?> env, string contentRoot, ILogger<CameraFileStore> log)
    {
        var configured = env("CAMERA_FILE_STORAGE_PATH");
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(contentRoot, "App_Data", "camera-files")
            : configured.Trim());
        return new CameraFileStore(path, Megabytes(env("CAMERA_IMAGE_MAX_MB"), 8, 20), Megabytes(env("CAMERA_VIDEO_MAX_MB"), 50, 200), log);
    }

    private static long Megabytes(string? raw, double fallback, double max)
    {
        var mb = double.TryParse(raw?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? Math.Min(v, max) : fallback;
        return (long)(mb * 1024 * 1024);
    }

    public long MaxImageBytes { get; }
    public long MaxVideoBytes { get; }

    public Task<string> SaveImageAsync(byte[] content, string extension, CancellationToken ct) => _images.SaveAsync(content, extension, ct);

    public Task<string> SaveVideoAsync(Stream content, string extension, CancellationToken ct) => _videos.SaveAsync(content, extension, MaxVideoBytes, ct);

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => Store(key).OpenReadAsync(key, ct);

    public Task DeleteAsync(string key, CancellationToken ct) => Store(key).DeleteAsync(key, ct);

    private SafeFileStore Store(string key) => key.EndsWith(".mp4", StringComparison.Ordinal) || key.EndsWith(".webm", StringComparison.Ordinal) ? _videos : _images;
}
