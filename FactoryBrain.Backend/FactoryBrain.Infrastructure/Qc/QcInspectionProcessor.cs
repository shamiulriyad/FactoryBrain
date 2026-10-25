using System.Data;
using System.Diagnostics;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Qc;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FactoryBrain.Infrastructure.Qc;

/// <summary>
/// Analyses one queued inspection at a time. Claiming is a single
/// <c>UPDATE … WHERE Id = (SELECT … FOR UPDATE SKIP LOCKED)</c>, so several
/// API instances can run workers without picking the same row. A claim is a
/// lease: if a worker dies mid-call, the row is reclaimed once the lease is
/// older than the provider timeout plus a margin, up to
/// <see cref="QcOptions.MaxAttempts"/>; after that it is marked failed.
/// Results are written only if the row is still the one this worker
/// claimed (xmin concurrency check), and every outcome is audited.
/// </summary>
public sealed class QcInspectionProcessor : IQcInspectionProcessor
{
    /// <summary>The audit actor for work done by the background worker.</summary>
    public static readonly AuditActor SystemActor = new(null, "system:qc-worker", "System");

    private static readonly TimeSpan LeaseMargin = TimeSpan.FromMinutes(2);

    private readonly FactoryBrainDbContext _db;
    private readonly IQcImageStore _store;
    private readonly IDefectDetectionProvider _provider;
    private readonly QcOptions _options;
    private readonly ILogger<QcInspectionProcessor> _log;

    public QcInspectionProcessor(FactoryBrainDbContext db, IQcImageStore store, IDefectDetectionProvider provider,
        QcOptions options, ILogger<QcInspectionProcessor> log)
    {
        _db = db; _store = store; _provider = provider; _options = options; _log = log;
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var leaseCutoff = now - _options.Timeout - LeaseMargin;

        // Leases that ran out of attempts: fail them so they stop being reclaimed.
        var abandoned = await _db.QcInspections
            .Where(i => i.Status == QcInspectionStatus.Processing && i.ProcessingStartedAt < leaseCutoff && i.Attempts >= _options.MaxAttempts)
            .Select(i => i.Id).ToListAsync(ct);
        foreach (var id in abandoned)
            await FailAsync(id, null, "processing_abandoned",
                $"Analysis did not finish after {_options.MaxAttempts} attempt(s). Retry once the vision provider is healthy.", ct);

        var claimed = await ClaimAsync(now, leaseCutoff, ct);
        if (claimed is null) return false;
        await AnalyseAsync(claimed.Value, ct);
        return true;
    }

    private async Task<Guid?> ClaimAsync(DateTime now, DateTime leaseCutoff, CancellationToken ct)
    {
        const string sql = """
            UPDATE qc_inspections
               SET "Status" = 'Processing', "ProcessingStartedAt" = @now, "Attempts" = "Attempts" + 1, "UpdatedAt" = @now
             WHERE "Id" = (
                   SELECT "Id" FROM qc_inspections
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

    private async Task AnalyseAsync(Guid id, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var inspection = await _db.QcInspections.FirstAsync(i => i.Id == id, ct);
        var leaseStartedAt = inspection.ProcessingStartedAt;
        var categories = await _db.QcDefectCategories.AsNoTracking()
            .Where(c => inspection.RequestedCategoryCodes.Contains(c.Code))
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);

        byte[] image;
        await using (var stream = await _store.OpenReadAsync(inspection.ImageStorageKey, ct))
        {
            if (stream is null)
            {
                await FailAsync(id, leaseStartedAt, "image_missing", "The stored image could not be found, so it can't be analysed.", ct);
                return;
            }
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            image = ms.ToArray();
        }

        var request = new DefectDetectionRequest(image, inspection.ImageContentType, inspection.ImageWidth, inspection.ImageHeight,
            categories.Select(c => new DetectionCategory(c.Code, c.Name, c.Description)).ToList(),
            inspection.ProductType, QcWire.Stage(inspection.Stage));

        var sw = Stopwatch.StartNew();
        DefectDetectionResult result;
        try
        {
            result = await _provider.DetectAsync(request, ct);
        }
        catch (DefectDetectionException ex)
        {
            _log.LogWarning("QC inspection {InspectionId} analysis failed: {Code}.", id, ex.Code);
            await FailAsync(id, leaseStartedAt, ex.Code, ex.Message, ct);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Unexpected: log the type (not the message, which may carry provider text) and keep it retryable.
            _log.LogError("QC inspection {InspectionId} analysis threw {ErrorType}.", id, ex.GetType().Name);
            await FailAsync(id, leaseStartedAt, "processing_error", "Analysis failed unexpectedly. See the API log and retry.", ct);
            return;
        }
        sw.Stop();

        var mapped = QcDetectionNormalizer.Map(result.Defects, request.Categories, _options.MinConfidence);
        var defaults = categories.ToDictionary(c => c.Code, c => c.DefaultSeverity);
        var now = DateTime.UtcNow;
        foreach (var m in mapped)
        {
            var severity = QcWire.TryParseSeverity(m.Severity, out var s) ? s
                : m.CategoryCode is not null && defaults.TryGetValue(m.CategoryCode, out var d) ? d
                : QcDefectSeverity.Major;
            _db.QcInspectionFindings.Add(new QcInspectionFinding
            {
                InspectionId = id,
                Source = QcFindingSource.Model,
                CategoryCode = m.CategoryCode,
                RawLabel = m.RawLabel,
                Confidence = m.Confidence,
                BoxX = m.Box?.X, BoxY = m.Box?.Y, BoxWidth = m.Box?.Width, BoxHeight = m.Box?.Height,
                Polygon = m.Polygon,
                Severity = severity,
                Description = m.Description,
                CreatedAt = now,
            });
        }

        inspection.Status = QcInspectionStatus.Completed;
        inspection.Provider = QcInspectionService.Truncate(result.Provider, 32);
        inspection.Model = QcInspectionService.Truncate(result.Model, 128);
        inspection.ModelVersion = QcInspectionService.Truncate(result.ModelVersion, 128);
        inspection.LocalizationSupported = result.LocalizationSupported;
        inspection.ConfidenceThreshold = _options.MinConfidence;
        inspection.InferenceMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
        inspection.AnalyzedAt = now;
        inspection.FailureCode = null;
        inspection.FailureMessage = null;
        inspection.UpdatedAt = now;
        _db.AuditLog.Add(QcInspectionService.Audit(SystemActor, null, "qc_inspection.analyzed", id,
            QcWire.Status(QcInspectionStatus.Processing), QcWire.Status(QcInspectionStatus.Completed),
            $"{result.Provider}/{result.Model}: {mapped.Count} prediction(s) at threshold {_options.MinConfidence:0.##} " +
            $"({result.Defects.Count - mapped.Count} below threshold or invalid)"));

        try
        {
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("QC inspection {InspectionId} analysed: {Count} prediction(s).", id, mapped.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The lease expired and another worker reclaimed the row; its result wins.
            _log.LogWarning("QC inspection {InspectionId}: result discarded, the row changed while analysing.", id);
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>Processing → Failed, only while the row is still under the given lease.</summary>
    private async Task FailAsync(Guid id, DateTime? leaseStartedAt, string code, string message, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var now = DateTime.UtcNow;
        var q = _db.QcInspections.Where(i => i.Id == id && i.Status == QcInspectionStatus.Processing);
        if (leaseStartedAt is { } lease) q = q.Where(i => i.ProcessingStartedAt == lease);
        var changed = await q.ExecuteUpdateAsync(set => set
            .SetProperty(i => i.Status, QcInspectionStatus.Failed)
            .SetProperty(i => i.FailureCode, QcInspectionService.Truncate(code, 64))
            .SetProperty(i => i.FailureMessage, QcInspectionService.Truncate(message, 500))
            .SetProperty(i => i.UpdatedAt, now), ct);
        if (changed == 0) return;
        _db.AuditLog.Add(QcInspectionService.Audit(SystemActor, null, "qc_inspection.failed", id,
            QcWire.Status(QcInspectionStatus.Processing), QcWire.Status(QcInspectionStatus.Failed), $"{code}: {message}"));
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>In-process wake-up for the worker; the 5-second poll covers other instances.</summary>
public sealed class QcWorkSignal : IQcWorkSignal
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
/// Runs <see cref="IQcInspectionProcessor"/> until the queue is empty, then
/// waits for a signal or the next poll. Disabled with
/// <c>QC_WORKER_ENABLED=false</c> (for instances that should only serve
/// requests). A processing error never stops the loop.
/// </summary>
public sealed class QcInspectionWorker : BackgroundService
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly QcOptions _options;
    private readonly IQcWorkSignal _signal;
    private readonly ILogger<QcInspectionWorker> _log;

    public QcInspectionWorker(IServiceScopeFactory scopes, QcOptions options, IQcWorkSignal signal, ILogger<QcInspectionWorker> log)
    {
        _scopes = scopes; _options = options; _signal = signal; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.WorkerEnabled)
        {
            _log.LogInformation("QC inspection worker disabled (QC_WORKER_ENABLED=false).");
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
                    processed = await scope.ServiceProvider.GetRequiredService<IQcInspectionProcessor>().ProcessNextAsync(stoppingToken);
                } while (processed && !stoppingToken.IsCancellationRequested);
                await _signal.WaitAsync(Poll, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError("QC inspection worker iteration failed ({ErrorType}); retrying in {Seconds} s.", ex.GetType().Name, Poll.TotalSeconds);
                try { await Task.Delay(Poll, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
