using Pgvector;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Pluggable embedder used by <see cref="RagService"/> and the seed pipeline.
/// Three concrete implementations live alongside this interface:
/// <list type="bullet">
///   <item><see cref="HashEmbeddingService"/> — offline / demo fallback (no key).</item>
///   <item><see cref="OpenAiEmbeddingService"/> — OpenAI <c>text-embedding-3-*</c>.</item>
///   <item><see cref="GeminiEmbeddingService"/> — Google <c>text-embedding-004</c>.</item>
/// </list>
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Stable provider identifier persisted on <c>ManualDocument</c> rows.</summary>
    string ProviderId { get; }

    /// <summary>Model identifier persisted on <c>ManualDocument</c> rows.</summary>
    string ModelId { get; }

    /// <summary>Output vector dimensionality.</summary>
    int Dimensions { get; }

    /// <summary>Embed a single piece of text.</summary>
    Vector Embed(string text, int dimensions = 0);

    /// <summary>Embed many texts in one batch when the provider supports it.</summary>
    Task<Vector> EmbedAsync(string text, CancellationToken ct = default);
}
