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
    /// Step 47: per-hit citation parity with the brief's
    /// <c>{id, title, snippet, confidence, source, url}</c> shape.
    /// <c>Confidence</c> is the hybrid score clamped to <c>[0,1]</c>.
    /// <c>Url</c> is empty today — the parent <c>ManualDocument.Url</c> is
    /// not propagated through to chunk-level hits yet.
    /// </summary>
    IReadOnlyList<CitationDto> Citations,
    AskRecommendation Recommendation,
    DateTime CreatedAt,
    double Confidence,
    IReadOnlyList<AskRagHit> RagHits
);

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

