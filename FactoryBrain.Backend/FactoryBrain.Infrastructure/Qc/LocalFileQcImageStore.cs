using System.Text.RegularExpressions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Infrastructure.Storage;

namespace FactoryBrain.Infrastructure.Qc;

/// <summary>
/// Inspection images under <see cref="QcOptions.StoragePath"/>, stored by
/// <see cref="SafeFileStore"/>: keys are generated as
/// <c>yyyy/MM/&lt;32 hex&gt;.&lt;ext&gt;</c>, re-validated on every read and
/// must resolve inside the root; writes are atomic.
/// </summary>
public sealed partial class LocalFileQcImageStore : IQcImageStore
{
    private static readonly string[] Extensions = { "jpg", "png", "webp" };

    [GeneratedRegex(@"^[0-9]{4}/[0-9]{2}/[0-9a-f]{32}\.(jpg|png|webp)$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private readonly SafeFileStore _files;

    public LocalFileQcImageStore(QcOptions options, ILogger<LocalFileQcImageStore> log)
    {
        _files = new SafeFileStore(options.StoragePath, Extensions, log, "QC image");
    }

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct) => _files.SaveAsync(content, extension, ct);

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => _files.OpenReadAsync(key, ct);

    public Task DeleteAsync(string key, CancellationToken ct) => _files.DeleteAsync(key, ct);
}
