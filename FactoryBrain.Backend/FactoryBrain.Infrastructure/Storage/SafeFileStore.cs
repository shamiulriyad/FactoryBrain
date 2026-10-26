using System.Text.RegularExpressions;

namespace FactoryBrain.Infrastructure.Storage;

/// <summary>
/// Files on the local file system (or a mounted volume) under one root.
/// Keys are generated here as <c>yyyy/MM/&lt;32 hex&gt;.&lt;ext&gt;</c>; every
/// read re-validates the key against that exact shape and an allowed
/// extension, and checks the resolved path stays under the root, so a
/// tampered database value can't reach any other file. Files are written to
/// a temporary name and moved into place, so a crash never leaves a
/// half-written file behind a stored key. Shared by the QC image store and
/// the merchandising document store.
/// </summary>
public sealed class SafeFileStore
{
    private readonly string _root;
    private readonly HashSet<string> _extensions;
    private readonly Regex _keyPattern;
    private readonly ILogger _log;
    private readonly string _what;

    public SafeFileStore(string root, IEnumerable<string> extensions, ILogger log, string what)
    {
        _root = Path.GetFullPath(root);
        _extensions = new HashSet<string>(extensions, StringComparer.Ordinal);
        _keyPattern = new Regex($@"^[0-9]{{4}}/[0-9]{{2}}/[0-9a-f]{{32}}\.({string.Join("|", _extensions.Select(Regex.Escape))})$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        _log = log;
        _what = what;
    }

    public bool IsValidKey(string? key) => key is not null && _keyPattern.IsMatch(key);

    public async Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct)
    {
        if (!_extensions.Contains(extension)) throw new ArgumentException($"unsupported {_what} extension", nameof(extension));
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
            _log.LogWarning("Rejected an invalid {What} storage key.", _what);
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
        catch (IOException ex) { _log.LogWarning("Could not delete {What} file ({ErrorType}).", _what, ex.GetType().Name); }
        catch (UnauthorizedAccessException ex) { _log.LogWarning("Could not delete {What} file ({ErrorType}).", _what, ex.GetType().Name); }
    }
}
