using System.Globalization;
using System.Security.Cryptography;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Merchandising;
using FactoryBrain.Application.Dtos.Production;
using FactoryBrain.Application.Merchandising;
using FactoryBrain.Application.Production;
using FactoryBrain.Application.Validation;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Infrastructure.Production;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Merchandising;

/// <summary>
/// Order documents over <c>merch_documents</c> / <c>merch_document_versions</c>.
/// Uploads are validated by content, stored under a server-generated key and
/// queued for the worker; a per-user <c>Idempotency-Key</c> and the file's
/// SHA-256 stop repeated uploads from creating duplicates. Confirmation is the
/// only way an extracted value becomes order data: it runs in one transaction
/// under the order lock, checks the version's <c>xmin</c>, validates every
/// decision and writes one audit row with what was accepted, corrected,
/// rejected, left unresolved and applied.
/// </summary>
public sealed class MerchDocumentService : IMerchDocumentService
{
    private readonly FactoryBrainDbContext _db;
    private readonly IMerchDocumentStore _store;
    private readonly IDocumentExtractionProvider _provider;
    private readonly MerchOptions _options;
    private readonly ProductionOptions _plant;
    private readonly IMerchWorkSignal _signal;
    private readonly MerchReadModel _read;
    private readonly ILogger<MerchDocumentService> _log;

    public MerchDocumentService(FactoryBrainDbContext db, IMerchDocumentStore store, IDocumentExtractionProvider provider, MerchOptions options,
        ProductionOptions plant, IMerchWorkSignal signal, ILogger<MerchDocumentService> log)
    {
        _db = db; _store = store; _provider = provider; _options = options; _plant = plant; _signal = signal; _log = log;
        _read = new MerchReadModel(db, plant, options, provider);
    }

    public MerchConfigDto Config() => new(
        _read.Extraction(),
        OcrConfigured: false,
        OcrNote: "No OCR integration is configured: scanned PDFs and images are stored and can be reviewed by hand, but their text can't be read automatically.",
        MerchFileInspector.SupportedFormats, MerchFileInspector.AcceptedExtensions, _options.MaxFileBytes, _options.MaxPages,
        _options.ReviewConfidence, _read.ConfidenceNote(),
        MerchWire.DocumentTypes.Select(t => new MerchDocumentTypeDto(t, MerchWire.DocumentTypeLabels[t])).ToList(),
        MerchOrderService.DefaultRequiredDocuments,
        MerchFields.All.Select(f => new MerchFieldDefDto(f.Key, f.Label, f.Type.ToString().ToLowerInvariant(), f.Description, f.RequiredOnPurchaseOrder, f.Compared)).ToList(),
        EmailIntegrationConfigured: false,
        EmailIntegrationNote: "Documents are uploaded by hand. There is no mailbox integration: nothing is read from or sent to buyers by email.");

    // ── Read ────────────────────────────────────────────────────────────────

    public async Task<MerchResult<Paged<MerchDocumentListItem>>> ListAsync(MerchDocumentQuery q, CancellationToken ct)
    {
        var errors = ProductionSupport.Errors();
        MerchVersionStatus? status = null;
        if (q.Status is not null)
        {
            if (MerchWire.TryParseVersionStatus(q.Status, out var s)) status = s;
            else errors.Add("status", $"status must be one of: {string.Join(", ", MerchWire.VersionStatuses)}");
        }
        MerchDocumentType? type = null;
        if (q.Type is not null)
        {
            if (MerchWire.TryParseDocumentType(q.Type, out var t)) type = t;
            else errors.Add("type", $"type must be one of: {string.Join(", ", MerchWire.DocumentTypes)}");
        }
        if (q.Page < 1) errors.Add("page", "page must be 1 or more.");
        if (q.PageSize is < 1 or > 100) errors.Add("pageSize", "pageSize must be from 1 to 100.");
        if (q.Q is { Length: > 100 }) errors.Add("q", "Search text is limited to 100 characters.");
        if (errors.Count > 0) return MerchResult<Paged<MerchDocumentListItem>>.Invalid(errors);

        var query = _db.MerchDocuments.AsNoTracking();
        if (q.OrderId is { } oid) query = query.Where(d => d.OrderId == oid);
        if (type is { } ty) query = query.Where(d => d.Type == ty);
        if (status is { } st) query = query.Where(d => _db.MerchDocumentVersions.Any(v => v.Id == d.CurrentVersionId && v.Status == st));
        if (MerchSupport.Clean(q.Q) is { } text)
        {
            var pattern = "%" + ProductionSupport.EscapeLike(text) + "%";
            query = query.Where(d => EF.Functions.ILike(d.Title, pattern, "\\")
                || _db.MerchOrders.Any(o => o.Id == d.OrderId && (EF.Functions.ILike(o.Reference, pattern, "\\") || EF.Functions.ILike(o.Buyer, pattern, "\\")
                    || EF.Functions.ILike(o.BuyerOrderNumber, pattern, "\\") || (o.StyleRef != null && EF.Functions.ILike(o.StyleRef, pattern, "\\"))))
                || _db.MerchDocumentVersions.Any(v => v.DocumentId == d.Id && v.OriginalFileName != null && EF.Functions.ILike(v.OriginalFileName, pattern, "\\")));
        }
        var total = await query.CountAsync(ct);
        // Awaiting review: oldest first, so nothing waits forever; otherwise most recently changed first.
        var ordered = status == MerchVersionStatus.NeedsReview
            ? query.OrderBy(d => d.UpdatedAt).ThenBy(d => d.Id)
            : query.OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id);
        var docs = await ordered.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        var items = await _read.DocumentItemsAsync(docs, ct);
        return MerchResult<Paged<MerchDocumentListItem>>.Ok(new Paged<MerchDocumentListItem>(items, q.Page, q.PageSize, total, (int)Math.Ceiling(total / (double)q.PageSize)));
    }

    public async Task<MerchDocumentDetail?> GetDocumentAsync(Guid id, CancellationToken ct)
    {
        var d = await _db.MerchDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return null;
        var reference = await _db.MerchOrders.AsNoTracking().Where(o => o.Id == d.OrderId).Select(o => o.Reference).FirstAsync(ct);
        var versions = await _db.MerchDocumentVersions.AsNoTracking().Where(v => v.DocumentId == id).OrderByDescending(v => v.VersionNumber).ToListAsync(ct);
        var counts = await _read.FieldCountsAsync(versions.Select(v => v.Id).ToList(), ct);
        var type = MerchWire.DocumentType(d.Type);
        return new MerchDocumentDetail(d.Id, d.OrderId, reference, type, MerchWire.DocumentTypeLabels[type], d.Title,
            versions.Select(v => MerchReadModel.Summary(v, counts.GetValueOrDefault(v.Id).Fields, counts.GetValueOrDefault(v.Id).Blocking)).ToList(),
            d.CreatedAt, d.CreatedByEmail);
    }

    public Task<MerchVersionDetail?> GetVersionAsync(Guid id, CancellationToken ct) => _read.VersionDetailAsync(id, ct);

    public async Task<MerchFileContent?> OpenFileAsync(Guid versionId, CancellationToken ct)
    {
        var v = await _db.MerchDocumentVersions.AsNoTracking().Where(x => x.Id == versionId)
            .Select(x => new { x.StorageKey, x.ContentType, x.OriginalFileName, x.SizeBytes, x.Sha256, x.Format, x.VersionNumber }).FirstOrDefaultAsync(ct);
        if (v is null) return null;
        var stream = await _store.OpenReadAsync(v.StorageKey, ct);
        var ext = v.Format == "image" ? (v.ContentType == "image/png" ? "png" : "jpg") : v.Format;
        var name = v.OriginalFileName ?? $"document-v{v.VersionNumber}.{ext}";
        return stream is null ? null : new MerchFileContent(stream, v.ContentType, name, v.SizeBytes, v.Sha256, v.Format);
    }

    // ── Upload ──────────────────────────────────────────────────────────────

    private (InspectedFile? File, Dictionary<string, string[]> Errors) ValidateUpload(MerchUploadInput input, bool newDocument)
    {
        var errors = ProductionSupport.Errors();
        if (newDocument)
        {
            if (input.Type is null) errors.Add("type", $"type is required: {string.Join(", ", MerchWire.DocumentTypes)}.");
            else if (!MerchWire.TryParseDocumentType(input.Type, out _)) errors.Add("type", $"type must be one of: {string.Join(", ", MerchWire.DocumentTypes)}.");
            if (input.Title is { Length: > 160 }) errors.Add("title", "The title is limited to 160 characters.");
        }
        if (input.ChangeNote is { Length: > 500 }) errors.Add("changeNote", "The change note is limited to 500 characters.");
        if (input.IdempotencyKey is { Length: > 128 }) errors.Add("idempotencyKey", "The Idempotency-Key is limited to 128 characters.");
        if (!MerchFileInspector.TryInspect(input.Content, input.FileName, _options.MaxFileBytes, out var file, out var error))
            errors.Add("file", MerchFileInspector.Describe(error!, _options.MaxFileBytes));
        return (file, errors);
    }

    public async Task<MerchResult<MerchVersionDetail>> UploadAsync(Guid orderId, MerchUploadInput input, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var order = await _db.MerchOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return MerchResult<MerchVersionDetail>.NotFound("order_not_found");
        var (file, errors) = ValidateUpload(input, newDocument: true);
        if (errors.Count > 0) return MerchResult<MerchVersionDetail>.Invalid(errors);
        if (order.Status == MerchOrderStatus.Cancelled) return MerchResult<MerchVersionDetail>.Conflict("order_cancelled", "Documents can't be added to a cancelled order.");

        var sha = Convert.ToHexString(SHA256.HashData(input.Content)).ToLowerInvariant();
        if (await ReplayAsync(input.IdempotencyKey, actor, sha, ct) is { } replay) return replay;
        var duplicate = await _db.MerchDocumentVersions.AsNoTracking()
            .Where(v => v.OrderId == orderId && v.IsCurrent && v.Sha256 == sha)
            .Join(_db.MerchDocuments, v => v.DocumentId, d => d.Id, (v, d) => new { d.Title, v.VersionNumber }).FirstOrDefaultAsync(ct);
        if (duplicate is not null)
            return MerchResult<MerchVersionDetail>.Conflict("duplicate_content", $"This file is identical to {duplicate.Title} v{duplicate.VersionNumber} of this order.");

        MerchWire.TryParseDocumentType(input.Type, out var type);
        var key = await _store.SaveAsync(input.Content, file!.StorageExtension, ct);
        var now = DateTime.UtcNow;
        var doc = new MerchDocument
        {
            OrderId = orderId, Type = type, Title = MerchSupport.Clean(input.Title) ?? MerchWire.DocumentTypeLabels[MerchWire.DocumentType(type)],
            VersionCount = 1, CreatedAt = now, UpdatedAt = now, CreatedByEmail = actor.Email,
        };
        var version = NewVersion(doc, 1, file, key, sha, input, actor, now);
        doc.CurrentVersionId = version.Id;
        _db.MerchDocuments.Add(doc);
        _db.MerchDocumentVersions.Add(version);
        _db.AuditLog.Add(MerchSupport.Audit(actor, traceId, "merch_document.uploaded", orderId, null, "pending",
            $"{doc.Title} ({MerchWire.DocumentType(type)}) v1 · {version.OriginalFileName ?? file.Format} · {input.Content.Length} bytes · sha256 {sha[..12]}"));
        return await SaveUploadAsync(version, key, input.IdempotencyKey, actor, sha, ct);
    }

    public async Task<MerchResult<MerchVersionDetail>> UploadVersionAsync(Guid documentId, MerchUploadInput input, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var docRow = await _db.MerchDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
        if (docRow is null) return MerchResult<MerchVersionDetail>.NotFound("document_not_found");
        var (file, errors) = ValidateUpload(input, newDocument: false);
        if (errors.Count > 0) return MerchResult<MerchVersionDetail>.Invalid(errors);
        var sha = Convert.ToHexString(SHA256.HashData(input.Content)).ToLowerInvariant();
        if (await ReplayAsync(input.IdempotencyKey, actor, sha, ct) is { } replay) return replay;

        var key = await _store.SaveAsync(input.Content, file!.StorageExtension, ct);
        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            var result = await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await MerchSupport.LockOrderAsync(_db, docRow.OrderId, ct);
                var doc = await _db.MerchDocuments.FirstAsync(d => d.Id == documentId, ct);
                var order = await _db.MerchOrders.FirstAsync(o => o.Id == doc.OrderId, ct);
                if (order.Status == MerchOrderStatus.Cancelled)
                    return MerchResult<Guid>.Conflict("order_cancelled", "Documents can't be added to a cancelled order.");
                var current = doc.CurrentVersionId is { } cid ? await _db.MerchDocumentVersions.FirstOrDefaultAsync(v => v.Id == cid, ct) : null;
                if (current is not null && current.Sha256 == sha)
                    return MerchResult<Guid>.Conflict("duplicate_version", $"This file is identical to the current version (v{current.VersionNumber}).");
                var now = DateTime.UtcNow;
                if (current is not null)
                {
                    current.IsCurrent = false;
                    current.UpdatedAt = now;
                    await _db.SaveChangesAsync(ct); // the one-current-version index is checked per statement
                }
                var number = doc.VersionCount + 1;
                var version = NewVersion(doc, number, file, key, sha, input, actor, now);
                _db.MerchDocumentVersions.Add(version);
                doc.CurrentVersionId = version.Id;
                doc.VersionCount = number;
                doc.UpdatedAt = now;
                _db.AuditLog.Add(MerchSupport.Audit(actor, traceId, "merch_document.version_uploaded", doc.OrderId,
                    current is null ? null : MerchWire.VersionStatus(current.Status), "pending",
                    $"{doc.Title} v{number} replaces v{current?.VersionNumber.ToString(CultureInfo.InvariantCulture) ?? "-"}" +
                    $"{(version.ChangeNote is null ? "" : $": {version.ChangeNote}")} · {version.OriginalFileName ?? file.Format} · sha256 {sha[..12]}"));
                await _db.SaveChangesAsync(ct);
                // The replaced version no longer counts as a source.
                await MerchSupport.SyncConflictsAsync(_db, order, actor, traceId, ct);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return MerchResult<Guid>.Created(version.Id);
            });
            if (result.Outcome != MerchOutcome.Created)
            {
                await _store.DeleteAsync(key, CancellationToken.None);
                return new MerchResult<MerchVersionDetail>(result.Outcome, null, result.Errors, result.Code, result.Message);
            }
            _signal.Notify();
            return MerchResult<MerchVersionDetail>.Created((await _read.VersionDetailAsync(result.Value, ct))!);
        }
        catch (DbUpdateException ex) when (input.IdempotencyKey is not null && ProductionSupport.IsUniqueViolation(ex, "IdempotencyKey"))
        {
            await _store.DeleteAsync(key, CancellationToken.None);
            _db.ChangeTracker.Clear();
            return await ReplayAsync(input.IdempotencyKey, actor, sha, ct) ?? throw new InvalidOperationException("idempotent upload vanished");
        }
        catch
        {
            await _store.DeleteAsync(key, CancellationToken.None);
            throw;
        }
    }

    private static MerchDocumentVersion NewVersion(MerchDocument doc, int number, InspectedFile file, string key, string sha,
        MerchUploadInput input, AuditActor actor, DateTime now) => new()
    {
        DocumentId = doc.Id, OrderId = doc.OrderId, VersionNumber = number, IsCurrent = true, ChangeNote = MerchSupport.Clean(input.ChangeNote),
        StorageKey = key, Format = file.Format, ContentType = file.ContentType, SizeBytes = input.Content.LongLength, Sha256 = sha,
        OriginalFileName = QcCreateInputValidation.SanitizeFileName(input.FileName),
        Status = MerchVersionStatus.Pending, CreatedAt = now, UpdatedAt = now,
        UploadedByUserId = actor.UserId, UploadedByEmail = actor.Email, IdempotencyKey = MerchSupport.Clean(input.IdempotencyKey),
    };

    private async Task<MerchResult<MerchVersionDetail>> SaveUploadAsync(MerchDocumentVersion version, string key, string? idempotencyKey,
        AuditActor actor, string sha, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (idempotencyKey is not null && ProductionSupport.IsUniqueViolation(ex, "IdempotencyKey"))
        {
            // A concurrent request with the same key won the race.
            await _store.DeleteAsync(key, CancellationToken.None);
            _db.ChangeTracker.Clear();
            return await ReplayAsync(idempotencyKey, actor, sha, ct) ?? throw new InvalidOperationException("idempotent upload vanished");
        }
        catch
        {
            await _store.DeleteAsync(key, CancellationToken.None);
            throw;
        }
        _log.LogInformation("Merchandising document version {VersionId} uploaded by user {UserId}.", version.Id, actor.UserId);
        _signal.Notify();
        return MerchResult<MerchVersionDetail>.Created((await _read.VersionDetailAsync(version.Id, ct))!);
    }

    /// <summary>The stored result of an earlier upload with this key: replayed for the same file, a conflict for a different one.</summary>
    private async Task<MerchResult<MerchVersionDetail>?> ReplayAsync(string? key, AuditActor actor, string sha, CancellationToken ct)
    {
        if (MerchSupport.Clean(key) is not { } k || actor.UserId is not { } uid) return null;
        var existing = await _db.MerchDocumentVersions.AsNoTracking().FirstOrDefaultAsync(v => v.UploadedByUserId == uid && v.IdempotencyKey == k, ct);
        if (existing is null) return null;
        return existing.Sha256 == sha
            ? new MerchResult<MerchVersionDetail>(MerchOutcome.Ok, await _read.VersionDetailAsync(existing.Id, ct), Replayed: true)
            : MerchResult<MerchVersionDetail>.Conflict("idempotency_conflict", "This Idempotency-Key was already used for a different file. Use a new key for a new upload.");
    }

    // ── Retry ───────────────────────────────────────────────────────────────

    public async Task<MerchResult<MerchVersionDetail>> RetryAsync(Guid versionId, AuditActor actor, string? traceId, CancellationToken ct)
    {
        var v = await _db.MerchDocumentVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId, ct);
        if (v is null) return MerchResult<MerchVersionDetail>.NotFound("version_not_found");
        if (!MerchLifecycle.CanRetry(v))
            return MerchResult<MerchVersionDetail>.Conflict("not_retryable",
                v.IsCurrent ? "Only a failed run, or a version read without extraction and not yet reviewed, can be retried. A damaged or scanned file must be replaced."
                    : "This version has been replaced by a newer one.");
        if (!_provider.Describe().Configured && v.Status == MerchVersionStatus.NeedsReview)
            return MerchResult<MerchVersionDetail>.Conflict("provider_not_configured", _provider.Describe().Reason ?? "No extraction provider is configured.");

        var now = DateTime.UtcNow;
        var from = v.Status;
        // Conditional on the state just checked: two concurrent retries queue one job.
        var changed = await _db.MerchDocumentVersions
            .Where(x => x.Id == versionId && x.IsCurrent && x.Status == from && x.Version == v.Version)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.Status, MerchVersionStatus.Pending)
                .SetProperty(x => x.FailureCode, (string?)null)
                .SetProperty(x => x.FailureMessage, (string?)null)
                .SetProperty(x => x.ExtractionCode, (string?)null)
                .SetProperty(x => x.ExtractionMessage, (string?)null)
                .SetProperty(x => x.ProcessingStartedAt, (DateTime?)null)
                .SetProperty(x => x.Attempts, 0)
                .SetProperty(x => x.UpdatedAt, now), ct);
        if (changed == 0) return MerchResult<MerchVersionDetail>.Conflict("version_conflict", "The document changed since you loaded it. Reload and try again.");
        _db.AuditLog.Add(MerchSupport.Audit(actor, traceId, "merch_document.retried", v.OrderId, MerchWire.VersionStatus(from), "pending",
            $"version {v.Id} (v{v.VersionNumber}) · was {v.FailureCode ?? v.ExtractionCode}"));
        await _db.SaveChangesAsync(ct);
        _signal.Notify();
        return MerchResult<MerchVersionDetail>.Ok((await _read.VersionDetailAsync(versionId, ct))!);
    }

    // ── Confirm ─────────────────────────────────────────────────────────────

    public async Task<MerchResult<MerchVersionDetail>> ConfirmAsync(Guid versionId, MerchConfirmRequest body, AuditActor actor, string? traceId, CancellationToken ct)
    {
        if (body.ExpectedVersion is null) return MerchResult<MerchVersionDetail>.Invalid("expectedVersion", "expectedVersion is required.");
        if (body.Notes is { Length: > 1000 }) return MerchResult<MerchVersionDetail>.Invalid("notes", "Notes are limited to 1000 characters.");
        var orderId = await _db.MerchDocumentVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => (Guid?)v.OrderId).FirstOrDefaultAsync(ct);
        if (orderId is null) return MerchResult<MerchVersionDetail>.NotFound("version_not_found");
        var today = MerchSupport.Today(_plant);

        var strategy = _db.Database.CreateExecutionStrategy();
        MerchResult<MerchVersionDetail> result;
        try
        {
            result = await strategy.ExecuteAsync(async () =>
            {
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await MerchSupport.LockOrderAsync(_db, orderId.Value, ct);
                var version = await _db.MerchDocumentVersions.FirstAsync(v => v.Id == versionId, ct);
                if (!MerchLifecycle.CanConfirm(version))
                    return MerchResult<MerchVersionDetail>.Conflict("not_reviewable", version.Status switch
                    {
                        MerchVersionStatus.Confirmed => "This version has already been confirmed.",
                        MerchVersionStatus.Pending or MerchVersionStatus.Processing => "The document is still being read. Review it once processing has finished.",
                        _ => "This version has been replaced by a newer one; review the current version.",
                    });
                if (body.ExpectedVersion != version.Version)
                    return MerchResult<MerchVersionDetail>.Conflict("version_conflict", "The document changed since you loaded it. Reload it and review again.");
                var doc = await _db.MerchDocuments.FirstAsync(d => d.Id == version.DocumentId, ct);
                var order = await _db.MerchOrders.FirstAsync(o => o.Id == version.OrderId, ct);
                var fields = await _db.MerchExtractedFields.Where(f => f.VersionId == versionId).ToListAsync(ct);

                var errors = ProductionSupport.Errors();
                var decided = new Dictionary<string, (MerchFieldDecision Decision, string? Value)>();
                var byId = fields.ToDictionary(f => f.Id);
                var decisions = body.Decisions ?? new();
                foreach (var d in decisions)
                {
                    if (!byId.TryGetValue(d.FieldId, out var f)) { errors.Add("decisions", $"Field {d.FieldId} does not belong to this document version."); continue; }
                    if (!MerchWire.TryParseDecision(d.Decision, out var decision) || decision == MerchFieldDecision.Unreviewed)
                    {
                        errors.Add("decisions", $"{MerchFields.Get(f.FieldKey).Label}: decision must be accepted, corrected, rejected or unresolved.");
                        continue;
                    }
                    if (d.Note is { Length: > 500 }) errors.Add("decisions", $"{MerchFields.Get(f.FieldKey).Label}: the note is limited to 500 characters.");
                    string? value = null;
                    if (decision == MerchFieldDecision.Accepted)
                    {
                        var blocking = f.Issues.Where(MerchFields.IsBlocking).ToList();
                        if (f.NormalizedValue is null || blocking.Count > 0)
                            errors.Add("decisions", $"{MerchFields.Get(f.FieldKey).Label} can't be accepted as extracted ({string.Join("; ", blocking.Select(MerchFields.DescribeIssue))}). Correct it or mark it unresolved.");
                        value = f.NormalizedValue;
                    }
                    else if (decision == MerchFieldDecision.Corrected)
                    {
                        var norm = MerchFields.Normalize(f.FieldKey, d.CorrectedValue, f.FieldKey == MerchFields.DeliveryDate ? today : null);
                        if (norm.HasBlockingIssue || norm.Normalized is null)
                            errors.Add("decisions", $"{MerchFields.Get(f.FieldKey).Label}: {string.Join("; ", norm.Issues.Where(MerchFields.IsBlocking).Select(MerchFields.DescribeIssue))}");
                        value = norm.Normalized;
                    }
                    decided[f.FieldKey] = (decision, value);
                }
                var undecided = fields.Where(f => !decisions.Any(d => d.FieldId == f.Id)).ToList();
                if (undecided.Count > 0)
                    errors.Add("decisions", $"Decide every extracted value before confirming ({string.Join(", ", undecided.Select(f => MerchFields.Get(f.FieldKey).Label))} left).");

                var additions = body.Additions ?? new();
                var added = new List<(string Key, MerchFieldDecision Decision, string? Value, string? Raw, string? Note)>();
                foreach (var a in additions)
                {
                    var key = a.FieldKey?.Trim();
                    if (!MerchFields.IsKnown(key)) { errors.Add("additions", $"Unknown field '{a.FieldKey}'."); continue; }
                    if (fields.Any(f => f.FieldKey == key) || added.Any(x => x.Key == key)) { errors.Add("additions", $"{MerchFields.Get(key!).Label} already has a value on this version; correct that one instead."); continue; }
                    var decision = MerchFieldDecision.Accepted;
                    if (a.Decision is not null && !(MerchWire.TryParseDecision(a.Decision, out decision) && decision is MerchFieldDecision.Accepted or MerchFieldDecision.Unresolved))
                    {
                        errors.Add("additions", $"{MerchFields.Get(key!).Label}: an added value is accepted or unresolved.");
                        continue;
                    }
                    if (a.Note is { Length: > 500 }) errors.Add("additions", $"{MerchFields.Get(key!).Label}: the note is limited to 500 characters.");
                    string? value = null;
                    if (decision == MerchFieldDecision.Accepted)
                    {
                        var norm = MerchFields.Normalize(key!, a.Value, key == MerchFields.DeliveryDate ? today : null);
                        if (norm.HasBlockingIssue || norm.Normalized is null)
                        {
                            errors.Add("additions", $"{MerchFields.Get(key!).Label}: {string.Join("; ", norm.Issues.Where(MerchFields.IsBlocking).Select(MerchFields.DescribeIssue))}");
                            continue;
                        }
                        value = norm.Normalized;
                    }
                    added.Add((key!, decision, value, MerchSupport.Truncate(a.Value?.Trim(), 4000), MerchSupport.Clean(a.Note)));
                    decided[key!] = (decision, value);
                }

                var apply = (body.ApplyToOrder ?? new()).Select(k => k.Trim()).Distinct().ToList();
                foreach (var k in apply)
                {
                    if (!MerchFields.IsKnown(k)) errors.Add("applyToOrder", $"Unknown field '{k}'.");
                    else if (!decided.TryGetValue(k, out var dv) || dv.Value is null)
                        errors.Add("applyToOrder", $"{MerchFields.Get(k).Label} can only be applied to the order when it is accepted or corrected here.");
                }

                // Cross-field rule: the confirmed breakdown must add up to the confirmed quantity of the same document.
                if (decided.TryGetValue(MerchFields.Breakdown, out var bd) && bd.Value is not null
                    && decided.TryGetValue(MerchFields.Quantity, out var qd) && qd.Value is not null
                    && MerchFields.ParseBreakdown(bd.Value) is { } rows
                    && MerchFields.BreakdownTotal(rows) != int.Parse(qd.Value, CultureInfo.InvariantCulture))
                    errors.Add("decisions", $"The colour × size breakdown totals {MerchFields.BreakdownTotal(rows):N0} but the quantity is {int.Parse(qd.Value, CultureInfo.InvariantCulture):N0}. Correct one of them or mark one unresolved.");

                if (errors.Count > 0) return MerchResult<MerchVersionDetail>.Invalid(errors);

                var now = DateTime.UtcNow;
                foreach (var d in decisions)
                {
                    var f = byId[d.FieldId];
                    var (decision, value) = decided[f.FieldKey];
                    f.Decision = decision;
                    f.CorrectedValue = decision == MerchFieldDecision.Corrected ? value : null;
                    f.ReviewerNote = MerchSupport.Clean(d.Note);
                    f.ReviewedAt = now;
                    f.ReviewedByEmail = actor.Email;
                }
                foreach (var a in added)
                {
                    _db.MerchExtractedFields.Add(new MerchExtractedField
                    {
                        VersionId = versionId, FieldKey = a.Key, Source = MerchFieldSource.Reviewer, RawValue = a.Raw, NormalizedValue = a.Value,
                        EvidenceStatus = MerchEvidence.None, Decision = a.Decision, ReviewerNote = a.Note, ReviewedAt = now, ReviewedByEmail = actor.Email, CreatedAt = now,
                    });
                }

                var from = version.Status;
                version.Status = MerchVersionStatus.Confirmed;
                version.ConfirmedAt = now;
                version.ConfirmedByUserId = actor.UserId;
                version.ConfirmedByEmail = actor.Email;
                version.ReviewNotes = MerchSupport.Clean(body.Notes);
                version.UpdatedAt = now;
                doc.UpdatedAt = now;

                var sources = MerchSupport.Sources(order);
                var appliedText = new List<string>();
                foreach (var k in apply)
                {
                    var before = MerchSupport.GetValue(order, k);
                    var after = decided[k].Value!;
                    MerchSupport.SetValue(order, k, after);
                    sources[k] = new FieldSourceRecord(FieldSourceRecord.Document, doc.Id, version.Id, version.VersionNumber, doc.Title, null, actor.Email, now);
                    appliedText.Add($"{k}: {MerchFields.Display(k, before)} → {MerchFields.Display(k, after)}");
                }
                if (apply.Count > 0)
                {
                    MerchSupport.SetSources(order, sources);
                    order.UpdatedAt = now;
                }

                string Keys(MerchFieldDecision dec) => string.Join(",", decided.Where(x => x.Value.Decision == dec).Select(x => x.Key));
                var corrections = decisions.Where(d => decided[byId[d.FieldId].FieldKey].Decision == MerchFieldDecision.Corrected)
                    .Select(d => { var f = byId[d.FieldId]; return $"{f.FieldKey}: {MerchFields.Display(f.FieldKey, f.NormalizedValue ?? f.RawValue)} → {MerchFields.Display(f.FieldKey, f.CorrectedValue)}"; });
                var summary = $"{doc.Title} v{version.VersionNumber} confirmed · accepted [{Keys(MerchFieldDecision.Accepted)}] · corrected [{string.Join("; ", corrections)}]" +
                              $" · rejected [{Keys(MerchFieldDecision.Rejected)}] · unresolved [{Keys(MerchFieldDecision.Unresolved)}] · added [{string.Join(",", added.Select(a => a.Key))}]" +
                              $" · applied to order [{string.Join("; ", appliedText)}]";
                _db.AuditLog.Add(MerchSupport.Audit(actor, traceId, "merch_document.confirmed", order.Id, MerchWire.VersionStatus(from), "confirmed",
                    version.ReviewNotes is null ? summary : $"{summary}. {version.ReviewNotes}"));

                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    await tx.RollbackAsync(ct);
                    return MerchResult<MerchVersionDetail>.Conflict("version_conflict", "The document or order changed while you were reviewing. Reload and review again.");
                }
                await MerchSupport.SyncConflictsAsync(_db, order, actor, traceId, ct);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                _log.LogInformation("Merchandising document version {VersionId} confirmed by user {UserId}.", versionId, actor.UserId);
                return MerchResult<MerchVersionDetail>.Ok(null!);
            });
        }
        catch (DbUpdateException ex) when (ProductionSupport.IsUniqueViolation(ex, "BuyerOrderNumber"))
        {
            return MerchResult<MerchVersionDetail>.Conflict("duplicate_order", "Another order of this buyer already has that PO number.");
        }
        return result.Outcome == MerchOutcome.Ok ? MerchResult<MerchVersionDetail>.Ok((await _read.VersionDetailAsync(versionId, ct))!) : result;
    }
}
