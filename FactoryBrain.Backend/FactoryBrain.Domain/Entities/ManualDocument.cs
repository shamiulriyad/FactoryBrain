using Pgvector;

namespace FactoryBrain.Domain.Entities;

/// <summary>
/// Manual knowledge doc surfaced to Ask BunonBrain. Mirrors
/// <c>src/data/manuals.ts::ManualDocT</c>.
/// </summary>
public class ManualDocument
{
    public string Id { get; set; } = default!;       // "doc-1"
    public string TitleEn { get; set; } = default!;
    public string TitleBn { get; set; } = default!;
    public List<string> Tags { get; set; } = new();
    public string BodyEn { get; set; } = default!;
    public string BodyBn { get; set; } = default!;
    public string Source { get; set; } = "manual";  // "manual" | "sensor_log" | "sop" | "compliance" | "faq"
    public string Department { get; set; } = "general";
    public string Category { get; set; } = "manuals";

    /// <summary>
    /// Canonical URL where the doc was sourced from (RAG ingest payload).
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// True when the row was inserted by <c>DbInitializer</c> as part of
    /// the demo corpus. The UI uses this to label "Demo" tiles so the
    /// operator can distinguish them from docs that came in via
    /// POST /api/rag/ingest.
    /// </summary>
    public bool IsDemo { get; set; } = false;

    /// <summary>UTC wall-clock ingestion timestamp.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Embedding metadata columns are added by the AddEmbeddingMetadata
    // migration. They're required in the C# model (with safe defaults) so
    // RagService + DbInitializer can stamp them after every reindex.
    public string EmbeddingProvider { get; set; } = "local";
    public string EmbeddingModel { get; set; } = "hash-md5";
    public int Dims { get; set; } = 384;

    public List<DocumentChunk> Chunks { get; set; } = new();
}

public class DocumentChunk
{
    public string Id { get; set; } = default!;       // "doc-1::chunk-1"
    public string DocumentId { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string Department { get; set; } = "general";
    public string Category { get; set; } = "manuals";
    public List<string> Tags { get; set; } = new();
    public string Text { get; set; } = default!;
    // Null when the chunk hasn't been embedded yet — either because the
    // ingest ran in degraded mode (EmbeddingProvider="pending") or because
    // the embedding call failed mid-batch. The reindex pipeline, hybrid
    // search, and /api/rag/status pendingCount all tolerate null. Storing
    // null here keeps pgvector from receiving the zero-length empty vector
    // the old code used to fill in as a sentinel.
    public Vector? Embedding { get; set; }
    public int Ordinal { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}