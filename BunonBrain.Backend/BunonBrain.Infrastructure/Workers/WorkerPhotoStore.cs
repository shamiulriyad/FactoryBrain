using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Infrastructure.Storage;

namespace BunonBrain.Infrastructure.Workers;

/// <summary>
/// Worker profile photographs on the local file system (or a mounted
/// volume) under <c>WORKER_PHOTO_STORAGE_PATH</c> (default
/// <c>App_Data/worker-photos</c>), stored by <see cref="SafeFileStore"/>:
/// generated keys, re-validated on every read, atomic writes. Photos are
/// personal data: they are served only through the API, to callers who may
/// see the worker.
/// </summary>
public sealed class WorkerPhotoStore : IWorkerPhotoStore
{
    /// <summary>A profile photo is small; 3 MB leaves room for a phone camera JPEG.</summary>
    public const long DefaultMaxBytes = 3 * 1024 * 1024;

    private static readonly string[] Extensions = { "jpg", "png", "webp" };
    private readonly SafeFileStore _files;

    public WorkerPhotoStore(string storagePath, ILogger<WorkerPhotoStore> log)
    {
        _files = new SafeFileStore(storagePath, Extensions, log, "worker photo");
    }

    public static string ResolvePath(Func<string, string?> env, string contentRoot)
    {
        var configured = env("WORKER_PHOTO_STORAGE_PATH");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(contentRoot, "App_Data", "worker-photos")
            : configured.Trim());
    }

    public long MaxBytes => DefaultMaxBytes;

    public Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct) => _files.SaveAsync(content, extension, ct);

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => _files.OpenReadAsync(key, ct);

    public Task DeleteAsync(string key, CancellationToken ct) => _files.DeleteAsync(key, ct);
}
