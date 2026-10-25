using System.Text.RegularExpressions;
using FactoryBrain.Application.Abstractions.Interfaces;

namespace FactoryBrain.Infrastructure.Qc;

/// <summary>
/// Inspection images on the local file system (or a mounted volume) under
/// <see cref="QcOptions.StoragePath"/>. Keys are generated here as
/// <c>yyyy/MM/&lt;32 hex&gt;.&lt;ext&gt;</c>; every read re-validates the key
/// against that exact shape and checks the resolved path stays under the
/// root, so a tampered database value can't reach any other file. Files are
/// written to a temporary name and moved into place, so a crash never leaves
/// a half-written image behind a stored key.
/// </summary>
public sealed partial class LocalFileQcImageStore : IQcImageStore
{
    private static readonly HashSet<string> Extensions = new(StringComparer.Ordinal) { "jpg", "png", "webp" };

    [GeneratedRegex(@"^[0-9]{4}/[0-9]{2}/[0-9a-f]{32}\.(jpg|png|webp)$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private readonly string _root;
    private readonly ILogger<LocalFileQcImageStore> _log;

    public LocalFileQcImageStore(QcOptions options, ILogger<LocalFileQcImageStore> log)
    {
        _root = Path.GetFullPath(options.StoragePath);
        _log = log;
    }

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public async Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct)
    {
        if (!Extensions.Contains(extension)) throw new ArgumentException("unsupported image extension", nameof(extension));
        var now = DateTime.UtcNow;
        var key = $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}.{extension}";
        var path = Resolve(key) ?? throw new InvalidOperationException("generated key failed validation");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await fs.WriteAsync(content, ct);
                await fs.FlushAsync(ct);
            }
            File.Move(temp, path, overwrite: false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return key;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (path is null)
        {
            _log.LogWarning("Rejected an invalid QC image storage key.");
            return Task.FromResult<Stream?>(null);
        }
        if (!File.Exists(path)) return Task.FromResult<Stream?>(null);
        Stream s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<Stream?>(s);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = Resolve(key);
        if (path is not null) TryDelete(path);
        return Task.CompletedTask;
    }

    /// <summary>Full path for a valid key under the root, else null.</summary>
    private string? Resolve(string key)
    {
        if (!IsValidKey(key)) return null;
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.Ordinal) ? full : null;
    }

    private void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException ex) { _log.LogWarning("Could not delete QC image file ({ErrorType}).", ex.GetType().Name); }
        catch (UnauthorizedAccessException ex) { _log.LogWarning("Could not delete QC image file ({ErrorType}).", ex.GetType().Name); }
    }
}
