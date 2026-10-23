using Pgvector;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Shared base for HTTP-backed embedders (OpenAI / Gemini). Provides a
/// sync <see cref="Embed"/> wrapper around the network call so the existing
/// RagService / seeder pipeline can stay synchronous, plus a helper to redact
/// the API key from the logger.
/// </summary>
public abstract class HostedEmbeddingServiceBase : IEmbeddingService
{
    protected readonly IHttpClientFactory HttpFactory;
    protected readonly ILogger Logger;
    protected readonly string ApiKey;
    protected readonly string Model;

    protected HostedEmbeddingServiceBase(
        IHttpClientFactory httpFactory,
        ILogger logger,
        string apiKey,
        string model)
    {
        HttpFactory = httpFactory;
        Logger = logger;
        ApiKey = apiKey;
        Model = model;
    }

    public abstract string ProviderId { get; }
    public string ModelId => Model;
    public abstract int Dimensions { get; }

    /// <summary>
    /// The sync call site is used by the seeding pipeline; we bridge through
    /// <see cref="EmbedAsync"/> with a short wait so we don't deadlock under
    /// ASP.NET sync-over-async. Hosting callers should prefer
    /// <see cref="EmbedAsync"/>.
    /// </summary>
    public Vector Embed(string text, int dimensions = 0)
    {
        var task = EmbedAsync(text, CancellationToken.None);
        if (task.IsCompleted)
            return task.GetAwaiter().GetResult();
        // Use Task.Run to avoid ASP.NET sync-over-async deadlocks for the
        // small initial seed write — this path runs once at startup.
        return Task.Run(async () => await task.ConfigureAwait(false))
                   .GetAwaiter().GetResult();
    }

    public abstract Task<Vector> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>Strip the API key from a message before logging.</summary>
    protected string Redact(string message)
    {
        if (string.IsNullOrEmpty(ApiKey) || string.IsNullOrEmpty(message))
            return message;
        return message.Replace(ApiKey, "[redacted]", StringComparison.Ordinal);
    }
}
