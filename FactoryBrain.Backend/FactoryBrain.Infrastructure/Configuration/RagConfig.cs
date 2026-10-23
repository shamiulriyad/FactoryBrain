namespace FactoryBrain.Infrastructure.Configuration;

/// <summary>
/// RAG retrieval tuning. Bound from the <c>Rag</c> section of
/// <c>appsettings.json</c> (mirrored by env vars <c>Rag__*</c>) at startup.
/// Defaults come from <c>docs/rag-research.md</c> and the step-47 brief:
/// the local hash embedder is bag-of-tokens, so BM25 is the signal we trust;
/// a hosted embedder (OpenAI / Gemini / Voyage) returns real semantic
/// vectors, so the dense score is the signal we trust.
/// </summary>
public sealed class RagConfig
{
    /// <summary>How many hits to return per query. Default 4.</summary>
    public int    TopK                 { get; set; } = 4;

    /// <summary>
    /// Legacy floor — kept for backward compatibility with the original
    /// parameter default. New code uses <see cref="MinScore"/> instead.
    /// </summary>
    public double SimilarityThreshold  { get; set; } = 0.0;

    /// <summary>Local / hash embedder BM25 weight. Default 0.70.</summary>
    public double LocalBm25Weight      { get; set; } = 0.70;
    /// <summary>Local / hash embedder vector weight. Default 0.30.</summary>
    public double LocalVectorWeight    { get; set; } = 0.30;

    /// <summary>Hosted embedder BM25 weight. Default 0.30.</summary>
    public double HostedBm25Weight     { get; set; } = 0.30;
    /// <summary>Hosted embedder vector weight. Default 0.70.</summary>
    public double HostedVectorWeight   { get; set; } = 0.70;

    /// <summary>
    /// <c>RAG_MIN_SCORE</c>. Hits whose hybrid score is below this are
    /// dropped. In degraded mode (resolver.IsDegraded or live column dim
    /// mismatch) the cutoff is applied against BM25 alone so keyword
    /// matches still surface when the dense embedder is down — see
    /// <c>RagService.RetrieveAsync</c>. Default 0.10.
    /// </summary>
    public double MinScore             { get; set; } = 0.10;
}
