using System.Text;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Cameras;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Cameras;
using BunonBrain.Application.Qc;
using BunonBrain.Application.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Cameras;

// Video sources and reference frames. A source change always adds a version
// (the previous one is retired, never edited). Credentials are encrypted with
// AES-256-GCM (the installation's settings key) bound to the new row's id, so
// a ciphertext can't be copied to another camera; they are never logged,
// audited or returned.

public sealed partial class CameraService
{
    public async Task<AccessResult<IReadOnlyList<CameraSourceDto>>> SourcesAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        if (await FindAsync(caller, id, ct) is null) return NotFound<IReadOnlyList<CameraSourceDto>>();
        var rows = await _db.CameraSources.AsNoTracking().Where(s => s.CameraId == id).OrderByDescending(s => s.Version).ToListAsync(ct);
        return AccessResult<IReadOnlyList<CameraSourceDto>>.Ok(rows.Select(SourceDto).ToList());
    }

    public async Task<AccessResult<CameraDto>> SetSourceAsync(AccessContext caller, Guid id, SetCameraSourceRequest r, string? traceId, CancellationToken ct)
    {
        var camera = await FindAsync(caller, id, ct);
        if (camera is null) return NotFound<CameraDto>();
        if (!CanConfigure(caller, camera.FactoryId)) return NoConfigure<CameraDto>();
        var type = CameraSourceTypes.Parse(r.SourceType)!;

        var current = await _db.CameraSources.AsNoTracking().FirstOrDefaultAsync(s => s.CameraId == id && s.RetiredAt == null, ct);
        var next = new CameraSource
        {
            CameraId = id, FactoryId = camera.FactoryId, SourceType = type, Notes = Clean(r.Notes),
            CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email, CreatedAt = DateTimeOffset.UtcNow,
        };
        string summary;
        switch (type)
        {
            case CameraSourceTypes.Rtsp:
            case CameraSourceTypes.HttpStream:
            {
                if (!StreamUrls.TryParse(r.StreamUrl, type, out var parsed, out var error, out var message))
                    return AccessResult<CameraDto>.Invalid(error!, message!);
                next.StreamUrl = parsed!.Url;
                next.StreamHost = parsed.Host;
                var user = Clean(r.UserName) ?? parsed.UserName;
                var password = string.IsNullOrEmpty(r.Password) ? parsed.Password : r.Password;
                if (user is not null || password is not null)
                {
                    if (!_secrets.CanEncrypt)
                        return AccessResult<CameraDto>.Invalid("credential_encryption_unavailable",
                            $"Camera credentials can't be stored: {AiSecretProtector.EnvKey} is not configured on the API server. Ask an administrator to set it, or use a stream without a password.");
                    next.CredentialsCiphertext = _secrets.Protect(StreamUrls.CredentialsJson(user, password), CameraRules.CredentialsPurposeFor(next.Id));
                }
                else if (r.KeepCredentials && current?.CredentialsCiphertext is { } old)
                {
                    // Re-encrypt for the new row: the ciphertext is bound to the row it was written for.
                    if (!_secrets.TryUnprotect(old, CameraRules.CredentialsPurposeFor(current.Id), out var plain) || !_secrets.CanEncrypt)
                        return AccessResult<CameraDto>.Invalid("credentials_unreadable",
                            "The stored credentials can't be read with the server's current key. Enter the user name and password again.");
                    next.CredentialsCiphertext = _secrets.Protect(plain!, CameraRules.CredentialsPurposeFor(next.Id));
                }
                summary = $"{type} {next.StreamUrl}" + (next.CredentialsCiphertext is null ? "" : " (credentials stored encrypted)");
                break;
            }
            case CameraSourceTypes.LocalWebcam:
                next.WebcamLabel = Clean(r.WebcamLabel);
                summary = "local webcam" + (next.WebcamLabel is null ? "" : $" ({next.WebcamLabel})");
                break;
            case CameraSourceTypes.RecordedVideo:
                if (current is not { SourceType: CameraSourceTypes.RecordedVideo, VideoKey: not null })
                    return AccessResult<CameraDto>.Invalid("video_required", "Upload the video file for a recorded-video source.");
                next.VideoKey = current.VideoKey;
                next.VideoContentType = current.VideoContentType;
                next.VideoFileName = current.VideoFileName;
                next.VideoSizeBytes = current.VideoSizeBytes;
                summary = $"recorded video {current.VideoFileName} (kept)";
                break;
            default:
                summary = "no source";
                break;
        }
        await AddSourceVersionAsync(caller, camera, next, summary, traceId, ct);
        return await GetAsync(caller, id, ct);
    }

    public async Task<AccessResult<CameraDto>> SetVideoAsync(AccessContext caller, Guid id, UploadedFile file, string? notes, string? traceId, CancellationToken ct)
    {
        var camera = await FindAsync(caller, id, ct);
        if (camera is null) return NotFound<CameraDto>();
        if (!CanConfigure(caller, camera.FactoryId)) return NoConfigure<CameraDto>();
        if (file.Length > _files.MaxVideoBytes)
            return AccessResult<CameraDto>.Invalid(VideoFileInspector.ErrTooLarge, $"The video is larger than {_files.MaxVideoBytes / (1024 * 1024)} MB.");

        // Identify the file by its first bytes, never by its name or declared type.
        var header = new byte[16];
        var read = await ReadHeaderAsync(file.Content, header, ct);
        if (!VideoFileInspector.TryInspect(header.AsSpan(0, read), out var info, out var error))
            return AccessResult<CameraDto>.Invalid(error!, VideoFileInspector.Describe(error!));
        Stream body = file.Content.CanSeek ? file.Content : new PrefixedStream(header.AsMemory(0, read), file.Content);
        if (file.Content.CanSeek) file.Content.Seek(0, SeekOrigin.Begin);

        string key;
        try { key = await _files.SaveVideoAsync(body, info!.Extension, ct); }
        catch (FileTooLargeException) { return AccessResult<CameraDto>.Invalid(VideoFileInspector.ErrTooLarge, $"The video is larger than {_files.MaxVideoBytes / (1024 * 1024)} MB."); }

        var next = new CameraSource
        {
            CameraId = id, FactoryId = camera.FactoryId, SourceType = CameraSourceTypes.RecordedVideo, Notes = Clean(notes),
            VideoKey = key, VideoContentType = info.ContentType, VideoFileName = SafeFileName(file.FileName, info.Extension), VideoSizeBytes = file.Length,
            CreatedByUserId = caller.UserId, CreatedByEmail = caller.Email, CreatedAt = DateTimeOffset.UtcNow,
        };
        try
        {
            await AddSourceVersionAsync(caller, camera, next, $"recorded video {next.VideoFileName} ({file.Length / 1024} KB) uploaded", traceId, ct);
        }
        catch
        {
            await _files.DeleteAsync(key, CancellationToken.None);
            throw;
        }
        return await GetAsync(caller, id, ct);
    }

    private async Task AddSourceVersionAsync(AccessContext caller, Camera camera, CameraSource next, string summary, string? traceId, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await CameraLocks.CameraAsync(_db, camera.Id, ct);
            var current = await _db.CameraSources.FirstOrDefaultAsync(s => s.CameraId == camera.Id && s.RetiredAt == null, ct);
            next.Version = (await _db.CameraSources.Where(s => s.CameraId == camera.Id).MaxAsync(s => (int?)s.Version, ct) ?? 0) + 1;
            if (current is not null)
            {
                current.RetiredAt = DateTimeOffset.UtcNow;
                await _db.SaveChangesAsync(ct); // retire first: one current source per camera (unique index)
            }
            _db.CameraSources.Add(next);
            var cam = await _db.Cameras.FirstAsync(c => c.Id == camera.Id, ct);
            cam.UpdatedAt = DateTimeOffset.UtcNow;
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "camera.source_updated", AccessAudit.EntityCamera, camera.Id.ToString(),
                current?.SourceType ?? CameraSourceTypes.None, next.SourceType, $"camera {camera.Code}: source v{next.Version}: {summary}", traceId, camera.FactoryId));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
        _db.ChangeTracker.Clear();
    }

    public async Task<AccessResult<CameraFile>> VideoAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        if (await FindAsync(caller, id, ct) is null) return NotFound<CameraFile>();
        var source = await _db.CameraSources.AsNoTracking().FirstOrDefaultAsync(s => s.CameraId == id && s.RetiredAt == null, ct);
        if (source is not { SourceType: CameraSourceTypes.RecordedVideo, VideoKey: not null, VideoContentType: not null })
            return AccessResult<CameraFile>.NotFound("video_not_found", "This camera's current source is not a recorded video.");
        var stream = await _files.OpenReadAsync(source.VideoKey, ct);
        return stream is null
            ? AccessResult<CameraFile>.NotFound("video_not_found", "The video file is missing from storage.")
            : AccessResult<CameraFile>.Ok(new CameraFile(stream, source.VideoContentType, source.VideoFileName));
    }

    public async Task<AccessResult<CameraDto>> SetReferenceFrameAsync(AccessContext caller, Guid id, byte[] image, string? source, double? frameSeconds,
        string? traceId, CancellationToken ct)
    {
        var camera = await FindAsync(caller, id, ct);
        if (camera is null) return NotFound<CameraDto>();
        if (!CanConfigure(caller, camera.FactoryId)) return NoConfigure<CameraDto>();
        var kind = ReferenceFrameSources.Parse(source ?? ReferenceFrameSources.Upload);
        if (kind is null) return AccessResult<CameraDto>.Invalid("reference_source_invalid", "Source: Upload or VideoFrame.");
        if (frameSeconds is { } s && (!double.IsFinite(s) || s < 0 || s > 86_400))
            return AccessResult<CameraDto>.Invalid("frame_seconds_invalid", "The video position must be between 0 and 86400 seconds.");
        if (!QcImageInspector.TryInspect(image, _files.MaxImageBytes, out var info, out var error))
            return AccessResult<CameraDto>.Invalid(error!, error == QcImageInspector.ErrTooLarge
                ? $"The image is larger than {_files.MaxImageBytes / (1024 * 1024.0):0.#} MB."
                : QcImageInspector.Describe(error!));

        var key = await _files.SaveImageAsync(image, info!.Extension, ct);
        string? old;
        try
        {
            var cam = await _db.Cameras.FirstAsync(c => c.Id == id, ct);
            old = cam.ReferenceImageKey;
            cam.ReferenceImageKey = key;
            cam.ReferenceImageContentType = info.ContentType;
            cam.ReferenceImageWidth = info.Width;
            cam.ReferenceImageHeight = info.Height;
            cam.ReferenceImageSource = kind;
            cam.ReferenceFrameSeconds = kind == ReferenceFrameSources.VideoFrame ? frameSeconds : null;
            cam.ReferenceImageUpdatedAt = DateTimeOffset.UtcNow;
            cam.UpdatedAt = cam.ReferenceImageUpdatedAt.Value;
            _db.AuditLog.Add(AccessAudit.Entry(caller.Actor, "camera.reference_frame_updated", AccessAudit.EntityCamera, cam.Id.ToString(), null, kind,
                $"camera {cam.Code}: reference frame {(old is null ? "added" : "replaced")} ({info.Width}×{info.Height}, {kind})", traceId, cam.FactoryId));
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            await _files.DeleteAsync(key, CancellationToken.None);
            throw;
        }
        _db.ChangeTracker.Clear();
        // Zone revisions name the frame they were drawn on; keep a frame that history points at.
        if (old is not null && !await _db.CameraZoneRevisions.AnyAsync(z => z.ReferenceImageKey == old, ct))
            await _files.DeleteAsync(old, CancellationToken.None);
        return await GetAsync(caller, id, ct);
    }

    public async Task<AccessResult<CameraFile>> ReferenceFrameAsync(AccessContext caller, Guid id, CancellationToken ct)
    {
        var camera = await FindAsync(caller, id, ct);
        if (camera is null) return NotFound<CameraFile>();
        if (camera.ReferenceImageKey is null || camera.ReferenceImageContentType is null)
            return AccessResult<CameraFile>.NotFound("reference_frame_not_found", "This camera has no reference frame yet.");
        var stream = await _files.OpenReadAsync(camera.ReferenceImageKey, ct);
        return stream is null
            ? AccessResult<CameraFile>.NotFound("reference_frame_not_found", "The reference frame file is missing from storage.")
            : AccessResult<CameraFile>.Ok(new CameraFile(stream, camera.ReferenceImageContentType, null));
    }

    private static async Task<int> ReadHeaderAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>The file name to show: no path, no control characters, the real extension.</summary>
    internal static string SafeFileName(string? name, string extension)
    {
        var baseName = Path.GetFileNameWithoutExtension(Path.GetFileName(name ?? "")) ?? "";
        var sb = new StringBuilder();
        foreach (var ch in baseName)
            if (!char.IsControl(ch) && ch is not ('"' or '\\' or '/' or ':' or '*' or '?' or '<' or '>' or '|')) sb.Append(ch);
        var clean = sb.ToString().Trim();
        if (clean.Length == 0) clean = "video";
        if (clean.Length > CameraRules.FileNameMax - 6) clean = clean[..(CameraRules.FileNameMax - 6)];
        return $"{clean}.{extension}";
    }
}

/// <summary>A read-only stream that replays a few already-read bytes before the rest of the inner stream.</summary>
internal sealed class PrefixedStream : Stream
{
    private readonly ReadOnlyMemory<byte> _prefix;
    private readonly Stream _inner;
    private int _offset;

    public PrefixedStream(ReadOnlyMemory<byte> prefix, Stream inner) { _prefix = prefix; _inner = inner; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_offset < _prefix.Length)
        {
            var n = Math.Min(buffer.Length, _prefix.Length - _offset);
            _prefix.Slice(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }
        return await _inner.ReadAsync(buffer, ct);
    }
}
