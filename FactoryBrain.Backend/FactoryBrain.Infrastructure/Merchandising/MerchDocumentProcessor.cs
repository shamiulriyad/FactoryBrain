using System.Data;
using System.Diagnostics;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Merchandising;
using FactoryBrain.Application.Production;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FactoryBrain.Infrastructure.Merchandising;

/// <summary>
/// Reads one queued document version at a time: parse the stored file into
/// page text, run the configured extractor, validate every value and check
/// its quoted evidence, store the result as <c>NeedsReview</c> (or
/// <c>Failed</c> with a stable code), and re-check the order's conflicts.
/// Claiming is a single <c>UPDATE … WHERE Id = (SELECT … FOR UPDATE SKIP LOCKED)</c>,
/// so several API instances can run workers; a claim is a lease reclaimed
/// after the provider timeout plus a margin, up to <see cref="MerchOptions.MaxAttempts"/>.
/// Re-processing replaces the version's pages and extracted fields (one row
/// per field key, enforced by a unique index), so retries never duplicate.
/// Document text is never logged.
/// </summary>
public sealed class MerchDocumentProcessor : IMerchDocumentProcessor
{
    private static readonly TimeSpan LeaseMargin = TimeSpan.FromMinutes(2);

    private readonly FactoryBrainDbContext _db;
    private readonly IMerchDocumentStore _store;
    private readonly IDocumentExtractionProvider _provider;
    private readonly MerchOptions _options;
    private readonly ProductionOptions _plant;
    private readonly ILogger<MerchDocumentProcessor> _log;

    public MerchDocumentProcessor(FactoryBrainDbContext db, IMerchDocumentStore store, IDocumentExtractionProvider provider,
        MerchOptions options, ProductionOptions plant, ILogger<MerchDocumentProcessor> log)
    {
        _db = db; _store = store; _provider = provider; _options = options; _plant = plant; _log = log;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseCutoff = now - _options.Timeout - LeaseMargin;

        var abandoned = await _db.MerchDocumentVersions
            .Where(v => v.Status == MerchVersionStatus.Processing && v.ProcessingStartedAt < leaseCutoff && v.Attempts >= _options.MaxAttempts)
            .Select(v => v.Id).ToListAsync(ct);
        foreach (var id in abandoned)
            await FailAsync(id, null, "processing_abandoned",
                $"Processing did not finish after {_options.MaxAttempts} attempt(s). Retry once the extraction provider is healthy.", ct);

        var claimed = await ClaimAsync(now, leaseCutoff, ct);
        if (claimed is null) return false;
        await ProcessAsync(claimed.Value, ct);
        return true;
    }

    private async Task<Guid?> ClaimAsync(DateTime now, DateTime leaseCutoff, CancellationToken ct)
    {
        const string sql = """
            UPDATE merch_document_versions
               SET "Status" = 'Processing', "ProcessingStartedAt" = @now, "Attempts" = "Attempts" + 1, "UpdatedAt" = @now
             WHERE "Id" = (
                   SELECT "Id" FROM merch_document_versions
                    WHERE "Status" = 'Pending'
                       OR ("Status" = 'Processing' AND "ProcessingStartedAt" < @cutoff AND "Attempts" < @maxAttempts)
                    ORDER BY "CreatedAt"
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
            RETURNING "Id";
            """;
        var conn = (NpgsqlConnection)_db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("now", now);
            cmd.Parameters.AddWithValue("cutoff", leaseCutoff);
            cmd.Parameters.AddWithValue("maxAttempts", _options.MaxAttempts);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is Guid g ? g : null;
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    private async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var v = await _db.MerchDocumentVersions.AsNoTracking().FirstAsync(x => x.Id == id, ct);
        var lease = v.ProcessingStartedAt;
        var doc = await _db.MerchDocuments.AsNoTracking().FirstAsync(d => d.Id == v.DocumentId, ct);

        byte[] content;
        await using (var stream = await _store.OpenReadAsync(v.StorageKey, ct))
        {
            if (stream is null)
            {
                await FailAsync(id, lease, "file_missing", "The stored file could not be found, so it can't be read. Upload it again.", ct);
                return;
            }
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            content = ms.ToArray();
        }

        ParsedDocument parsed;
        try
        {
            parsed = DocumentTextParser.Parse(content, v.Format, _options.MaxPages, MerchOptions.MaxStoredTextChars);
        }
        catch (DocumentParseException ex)
        {
            _log.LogWarning("Document version {VersionId} could not be parsed: {Code}.", id, ex.Code);
            await FailAsync(id, lease, ex.Code, ex.Message, ct);
            return;
        }

        var status = _provider.Describe();
        var sw = Stopwatch.StartNew();
        ExtractionResult? result = null;
        string? extractionCode = null, extractionMessage = null, failure = null, failureMessage = null;
        if (!status.Configured)
        {
            extractionCode = DocumentExtractionException.NotConfigured;
            extractionMessage = status.Reason;
        }
        else
        {
            try
            {
                result = await _provider.ExtractAsync(new ExtractionRequest(MerchWire.DocumentType(doc.Type), parsed.Pages), ct);
            }
            catch (DocumentExtractionException ex) when (ex.Code == DocumentExtractionException.NotConfigured)
            {
                extractionCode = ex.Code;
                extractionMessage = ex.Message;
            }
            catch (DocumentExtractionException ex)
            {
                _log.LogWarning("Document version {VersionId} extraction failed: {Code}.", id, ex.Code);
                failure = ex.Code;
                failureMessage = ex.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Log the type only: the message may carry provider text.
                _log.LogError("Document version {VersionId} extraction threw {ErrorType}.", id, ex.GetType().Name);
                failure = "processing_error";
                failureMessage = "Extraction failed unexpectedly. See the API log and retry.";
            }
        }
        sw.Stop();

        var today = MerchSupport.Today(_plant);
        var source = result?.Provider == RuleBasedFieldExtractor.ProviderName ? MerchFieldSource.Rules : MerchFieldSource.Model;
        var drafts = result is null ? Array.Empty<FieldDraft>()
            : MerchExtractionNormalizer.Build(result.Fields, parsed.Pages, source, _options.ReviewConfidence, today);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await MerchSupport.LockOrderAsync(_db, v.OrderId, ct);
            var version = await _db.MerchDocumentVersions.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (version is null || version.Status != MerchVersionStatus.Processing || version.ProcessingStartedAt != lease)
            {
                _log.LogWarning("Document version {VersionId}: result discarded, the row changed while processing.", id);
                await tx.RollbackAsync(ct);
                return;
            }

            await _db.MerchDocumentPages.Where(p => p.VersionId == id).ExecuteDeleteAsync(ct);
            await _db.MerchExtractedFields.Where(f => f.VersionId == id && f.Source != MerchFieldSource.Reviewer).ExecuteDeleteAsync(ct);
            var reviewerKeys = await _db.MerchExtractedFields.Where(f => f.VersionId == id).Select(f => f.FieldKey).ToListAsync(ct);
            foreach (var p in parsed.Pages) _db.MerchDocumentPages.Add(new MerchDocumentPage { VersionId = id, PageNumber = p.PageNumber, Text = p.Text });
            var now = DateTime.UtcNow;
            foreach (var d in drafts.Where(d => !reviewerKeys.Contains(d.Key)))
            {
                _db.MerchExtractedFields.Add(new MerchExtractedField
                {
                    VersionId = id, FieldKey = d.Key, Source = d.Source, RawValue = d.RawValue, NormalizedValue = d.NormalizedValue,
                    Confidence = d.Confidence, PageNumber = d.Page, Snippet = d.Snippet, EvidenceStatus = d.EvidenceStatus,
                    Issues = d.Issues.Distinct().ToArray(), CreatedAt = now,
                });
            }

            version.PageCount = parsed.PageCount;
            version.TextLength = parsed.TextLength;
            version.TextTruncated = parsed.Truncated || (result?.InputTruncated ?? false);
            version.ProcessedAt = now;
            version.UpdatedAt = now;
            version.ExtractionMs = result is null ? null : (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
            version.Provider = MerchSupport.Truncate(result?.Provider ?? status.Provider ?? status.Mode, 32);
            version.Model = MerchSupport.Truncate(result?.Model ?? status.Model, 128);
            version.ModelVersion = MerchSupport.Truncate(result?.ModelVersion, 128);
            string audit;
            if (failure is not null)
            {
                version.Status = MerchVersionStatus.Failed;
                version.FailureCode = MerchSupport.Truncate(failure, 64);
                version.FailureMessage = MerchSupport.Truncate(failureMessage, 500);
                version.ExtractionCode = null;
                version.ExtractionMessage = null;
                audit = $"{doc.Title} v{version.VersionNumber}: text read ({parsed.PageCount} page(s)); extraction failed {failure}: {failureMessage}";
            }
            else
            {
                version.Status = MerchVersionStatus.NeedsReview;
                version.FailureCode = null;
                version.FailureMessage = null;
                version.ExtractionCode = MerchSupport.Truncate(extractionCode, 64);
                version.ExtractionMessage = MerchSupport.Truncate(extractionMessage, 500);
                audit = result is null
                    ? $"{doc.Title} v{version.VersionNumber}: text read ({parsed.PageCount} page(s)); no automatic extraction ({extractionCode})"
                    : $"{doc.Title} v{version.VersionNumber}: {result.Provider}/{result.Model} extracted {drafts.Count} field(s) " +
                      $"({drafts.Count(d => d.EvidenceStatus == MerchEvidence.Verified)} with verified evidence, {drafts.Count(d => d.Issues.Any(MerchFields.IsBlocking))} invalid)";
            }
            _db.AuditLog.Add(MerchSupport.Audit(MerchSupport.SystemActor, null, failure is null ? "merch_document.processed" : "merch_document.failed",
                v.OrderId, "processing", MerchWire.VersionStatus(version.Status), audit));

            await _db.SaveChangesAsync(ct);
            var order = await _db.MerchOrders.FirstAsync(o => o.Id == v.OrderId, ct);
            await MerchSupport.SyncConflictsAsync(_db, order, MerchSupport.SystemActor, null, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _log.LogInformation("Document version {VersionId} processed: status={Status}, fields={Count}.", id, version.Status, drafts.Count);
        });
    }

    /// <summary>Processing → Failed, only while the row is still under the given lease.</summary>
    private async Task FailAsync(Guid id, DateTime? lease, string code, string message, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var now = DateTime.UtcNow;
        var q = _db.MerchDocumentVersions.Where(v => v.Id == id && v.Status == MerchVersionStatus.Processing);
        if (lease is { } l) q = q.Where(v => v.ProcessingStartedAt == l);
        var changed = await q.ExecuteUpdateAsync(set => set
            .SetProperty(v => v.Status, MerchVersionStatus.Failed)
            .SetProperty(v => v.FailureCode, MerchSupport.Truncate(code, 64))
            .SetProperty(v => v.FailureMessage, MerchSupport.Truncate(message, 500))
            .SetProperty(v => v.ProcessedAt, now)
            .SetProperty(v => v.UpdatedAt, now), ct);
        if (changed == 0) return;
        var orderId = await _db.MerchDocumentVersions.Where(v => v.Id == id).Select(v => v.OrderId).FirstAsync(ct);
        _db.AuditLog.Add(MerchSupport.Audit(MerchSupport.SystemActor, null, "merch_document.failed", orderId, "processing", "failed",
            $"version {id}: {code}: {message}"));
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>In-process wake-up for the worker; the 5-second poll covers other instances.</summary>
public sealed class MerchWorkSignal : IMerchWorkSignal
{
    private readonly SemaphoreSlim _gate = new(0, 1);

    public void Notify()
    {
        try { _gate.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _gate.WaitAsync(timeout, ct);
}

/// <summary>
/// Runs <see cref="IMerchDocumentProcessor"/> until the queue is empty, then
/// waits for a signal or the next poll. Disabled with <c>MERCH_WORKER_ENABLED=false</c>.
/// A processing error never stops the loop.
/// </summary>
public sealed class MerchDocumentWorker : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly MerchOptions _options;
    private readonly IMerchWorkSignal _signal;
    private readonly ILogger<MerchDocumentWorker> _log;

    public MerchDocumentWorker(IServiceScopeFactory scopes, MerchOptions options, IMerchWorkSignal signal, ILogger<MerchDocumentWorker> log)
    {
        _scopes = scopes; _options = options; _signal = signal; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled)
        {
            _log.LogInformation("Merchandising document worker disabled (MERCH_WORKER_ENABLED=false).");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool processed;
                do
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    processed = await scope.ServiceProvider.GetRequiredService<IMerchDocumentProcessor>().ProcessNextAsync(stoppingToken);
                } while (processed && !stoppingToken.IsCancellationRequested);
                await _signal.WaitAsync(Poll, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError("Merchandising document worker iteration failed ({ErrorType}); retrying in {Seconds} s.", ex.GetType().Name, Poll.TotalSeconds);
                try { await Task.Delay(Poll, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
