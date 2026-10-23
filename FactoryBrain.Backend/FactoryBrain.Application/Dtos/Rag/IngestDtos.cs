namespace FactoryBrain.Application.Dtos.Rag;

/// <summary>
/// POST /api/rag/ingest body. The C# RAG ingest pipeline replaces the
/// frontend seed path: every doc the API receives here becomes a
/// <see cref="FactoryBrain.Domain.Entities.ManualDocument"/> row
/// with one or more <see cref="FactoryBrain.Domain.Entities.DocumentChunk"/>
/// children.
/// </summary>
public record RagIngestRequest(
    string Title,
    string? TitleBn,
    /// <summary>One of <c>manual | sop | compliance | faq</c>.</summary>
    string Source,
    List<string>? Tags,
    /// <summary>Plain text or markdown body. Max 200 KB.</summary>
    string Content,
    /// <summary>Optional canonical URL where this content is sourced from.</summary>
    string? Url
);

/// <summary>
/// 201 Created body for POST /api/rag/ingest. In degraded mode the
/// chunks are stored with <c>Embedding = null</c> /
/// <c>EmbeddingProvider = "pending"</c> and the response surfaces
/// <c>embedded = false</c> plus a <c>warning</c> field. The next
/// non-degraded startup or POST /api/rag/reindex embeds every
/// "pending" row.
/// </summary>
public record RagIngestResponse(
    string DocumentId,
    int Chunks,
    string Provider,
    string Model,
    int Dims,
    bool Embedded,
    string? Warning
);

/// <summary>
/// One row of GET /api/rag/documents. Deliberately omits the
/// embedding vector — the column is a 384/–1536-d pgvector payload
/// and would balloon the list response for no UI use.
/// </summary>
public record DocumentListItem(
    string Id,
    string Title,
    string? TitleBn,
    string Source,
    List<string> Tags,
    int Chunks,
    string EmbeddingProvider,
    string EmbeddingModel,
    int Dims,
    bool IsDemo,
    DateTime CreatedAt
);

public static class RagSources
{
    public const string Manual     = "manual";
    public const string Sop        = "sop";
    public const string Compliance = "compliance";
    public const string Faq        = "faq";

    public static bool IsValid(string? source) =>
        source is Manual or Sop or Compliance or Faq;
}