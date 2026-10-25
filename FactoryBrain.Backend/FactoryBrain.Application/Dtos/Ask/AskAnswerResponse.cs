namespace FactoryBrain.Application.Dtos.Ask;

/// <summary>
/// Response from POST /api/ask. Mirrors the
/// <c>AskAnswer</c> + <c>EvidenceRefPublic</c> contract in
/// <c>src/services/types.ts</c>.
/// </summary>
public record AskAnswerResponse(
    string TaskId,
    IReadOnlyList<AskAnalyzedDomain> Analyzed,
    string Finding,
    string FindingBn,
    IReadOnlyList<AskFactor> Factors,
    IReadOnlyList<EvidenceRefDto> Evidence,
    /// <summary>
    /// The chunks the answer cites, as returned by the Python RAG service.
    /// <c>Confidence</c> is each chunk's similarity score clamped to <c>[0,1]</c>.
    /// </summary>
    IReadOnlyList<CitationDto> Citations,
    /// <summary>Not produced by the RAG service, so always null (kept for wire compatibility).</summary>
    AskRecommendation? Recommendation,
    DateTime CreatedAt,
    double Confidence,
    IReadOnlyList<AskRagHit> RagHits,
    /// <summary>
    /// How this answer was produced. <c>live</c>: the RAG service answered and
    /// cited at least one indexed chunk. <c>no_source</c>: it answered without
    /// citing anything (the documents don't cover the question, nothing is
    /// indexed yet, or the message was small talk). <c>demo</c> is no longer
    /// produced; it stays a known value so older clients keep parsing.
    /// </summary>
    string Mode,
    /// <summary>Optional note about the answer; null when there is nothing to add.</summary>
    string? Warning
);

public static class AskModes
{
    public const string Live     = "live";
    public const string Demo     = "demo";
    public const string NoSource = "no_source";
}

public record AskAnalyzedDomain(string Domain, int Count, Dictionary<string, object>? Filter);

public record AskFactor(
    string Label, string LabelBn,
    string Magnitude, string MagnitudeBn);

public record AskRecommendation(
    string Title, string TitleBn,
    string Action, string ActionBn,
    string RiskTier);

public record AskRagHit(
    string Id,
    string SourceId,
    string Title,
    string Department,
    string Category,
    IReadOnlyList<string> Tags,
    double HybridScore,
    double DenseScore,
    double Bm25Score,
    string Snippet
);

public record EvidenceRefDto(
    string Domain,
    int Count,
    Dictionary<string, string>? Filter,
    IReadOnlyList<string>? PreviewIds
);

/// <summary>
/// Step 47: per-citation entry that exposes the exact six fields the
/// <c>EvidenceBlock.tsx</c> brief expects (id, title, snippet, confidence,
/// source, url). Empty when no confident source was found.
/// </summary>
public record CitationDto(
    string Id,
    string Title,
    string Snippet,
    double Confidence,
    string Source,
    string Url
);

