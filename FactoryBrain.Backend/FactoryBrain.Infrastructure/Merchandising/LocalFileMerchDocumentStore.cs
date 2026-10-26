using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Infrastructure.Storage;

namespace FactoryBrain.Infrastructure.Merchandising;

/// <summary>
/// Original order documents under <see cref="MerchOptions.StoragePath"/>,
/// through <see cref="SafeFileStore"/> (server-generated keys, re-validated
/// on every read, atomic writes). Files are never modified: a correction is
/// recorded in the database, a replacement is a new version with a new key.
/// </summary>
public sealed class LocalFileMerchDocumentStore : IMerchDocumentStore
{
    public static readonly string[] Extensions = { "pdf", "txt", "csv", "docx", "jpg", "png" };

    private readonly SafeFileStore _files;

    public LocalFileMerchDocumentStore(MerchOptions options, ILogger<LocalFileMerchDocumentStore> log)
    {
        _files = new SafeFileStore(options.StoragePath, Extensions, log, "merchandising document");
    }

    public bool IsValidKey(string? key) => _files.IsValidKey(key);

    public Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct) => _files.SaveAsync(content, extension, ct);

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) => _files.OpenReadAsync(key, ct);

    public Task DeleteAsync(string key, CancellationToken ct) => _files.DeleteAsync(key, ct);
}
