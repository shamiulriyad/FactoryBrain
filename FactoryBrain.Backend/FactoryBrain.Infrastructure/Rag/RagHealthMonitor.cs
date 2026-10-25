namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Periodic RAG provider health probe. Re-runs <see cref="EmbeddingProviderResolver.ProbeAsync"/>
/// every <c>RAG_HEALTH_INTERVAL_SECONDS</c> (default 300, read once at startup)
/// against the settings in effect at that moment. It only probes: re-embedding
/// is an Admin-confirmed operation (<see cref="EmbeddingReindexService"/>), so a
/// recovered provider never triggers paid calls on its own.
/// </summary>
public sealed class RagHealthMonitor : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<RagHealthMonitor> _log;
    private readonly TimeSpan _period;

    public RagHealthMonitor(IServiceProvider sp, ILogger<RagHealthMonitor> log)
    {
        _sp = sp; _log = log;
        var raw = Environment.GetEnvironmentVariable(EmbeddingProviderResolver.EnvHealthInterval);
        _period = TimeSpan.FromSeconds(int.TryParse(raw, out var s) && s > 0 ? s : 300);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                _log.LogWarning("RAG health probe loop iteration failed ({ErrorType}).", ex.GetType().Name);
            }

            try
            {
                await Task.Delay(_period, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* shutting down */ }
        }
    }

    /// <summary>One probe of the current embedding settings. Never re-embeds.</summary>
    public async Task<ProbeOutcome> ProbeOnceAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<EmbeddingProviderResolver>();
        var http     = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var lf       = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

        var ok = await resolver.ProbeAsync(http, lf, ct).ConfigureAwait(false);
        return new ProbeOutcome(ok, resolver.IsDegraded);
    }
}

public sealed record ProbeOutcome(bool Ok, bool Degraded);
