using FactoryBrain.Infrastructure.Rag;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Periodic RAG provider health probe. Re-runs <see cref="EmbeddingProviderResolver.ProbeAsync"/>
/// every <c>RAG_HEALTH_INTERVAL_SECONDS</c> (default 300) and triggers a
/// one-shot reindex when a probe recovers the provider from Degraded.
/// </summary>
public sealed class RagHealthMonitor : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<RagHealthMonitor> _log;

    public RagHealthMonitor(IServiceProvider sp, ILogger<RagHealthMonitor> log)
    {
        _sp = sp; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Re-read .env.local before reading the interval so an operator can
        // change RAG_HEALTH_INTERVAL_SECONDS at runtime in Development.
        EnvReloader.Reload(_log);
        var raw = Environment.GetEnvironmentVariable(EmbeddingProviderResolver.EnvHealthInterval);
        int seconds = int.TryParse(raw, out var s) && s > 0 ? s : 300;
        var period = TimeSpan.FromSeconds(seconds);

        // Stagger a little so the first probe doesn't compete with startup.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "RAG health probe loop iteration failed.");
            }

            try
            {
                await Task.Delay(period, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }

    /// <summary>
    /// One probe cycle: re-resolve, build a primary, call ProbeAsync.
    /// If the probe succeeded AND we were degraded AND the configured
    /// provider is now live (not local), trigger a reindex so the stored
    /// vectors catch up to the recovered provider.
    /// </summary>
    public async Task<ProbeOutcome> ProbeOnceAsync(CancellationToken ct)
    {
        // Re-read .env.local at the start of every probe so newly-saved
        // values (e.g. RAG_EMBEDDING_FAKE_FAIL=true, a fresh API key) take
        // effect on the next cycle without a restart. No-op outside
        // Development.
        EnvReloader.Reload(_log);
        using var scope = _sp.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<EmbeddingProviderResolver>();
        var http     = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var lf       = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        var wasDegraded = resolver.IsDegraded;
        var ok = await resolver.ProbeAsync(http, lf, ct).ConfigureAwait(false);
        if (ok && wasDegraded && !resolver.IsDegraded)
        {
            // Recovered — fire one reindex so stored vectors align with
            // the configured provider. The reindex is a no-op when stored
            // rows already match the new active config.
            _log.LogInformation(
                "RAG provider recovered; triggering one-shot reindex to align stored vectors.");
            try
            {
                var admin = scope.ServiceProvider.GetRequiredService<EmbeddingColumnAdmin>();
                var report = await admin.ReindexChangedDocsAsync(ct).ConfigureAwait(false);
                if (report.Documents > 0)
                {
                    _log.LogInformation(
                        "Recovery reindex: {Docs} docs / {Chunks} chunks (provider={Provider} model={Model} dims={Dims}).",
                        report.Documents, report.Chunks, report.Configured.Provider, report.Configured.Model, report.Configured.Dims);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Recovery reindex failed; stored vectors may still drift. Will retry on the next probe cycle.");
            }
        }
        return new ProbeOutcome(ok, resolver.IsDegraded);
    }
}

public sealed record ProbeOutcome(bool Ok, bool Degraded);
