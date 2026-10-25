using FactoryBrain.Application.Dtos.Ask;
using FactoryBrain.Application.Abstractions.Interfaces;

namespace FactoryBrain.Infrastructure.Services;

/// <summary>
/// POST /api/ask. Every answer comes from the Python RAG service
/// (<see cref="IPyRagClient"/>): it retrieves from Qdrant, has Gemini answer
/// from those chunks only, and returns the chunks the answer cites. Nothing
/// here writes answer text, figures or advice of its own; when the service
/// can't answer, <see cref="PyRagUnavailableException"/> reaches the caller
/// instead of a substitute answer.
/// </summary>
public sealed class AskService : IAskService
{
    private readonly IPyRagClient _rag;
    private readonly ILogger<AskService> _log;

    public AskService(IPyRagClient rag, ILogger<AskService> log)
    { _rag = rag; _log = log; }

    public async Task<AskAnswerResponse> AskAsync(AskRequest req, CancellationToken ct)
    {
        // The Python service has no department/category metadata, so a filter can't narrow it.
        if (req.Filter is not null)
            _log.LogInformation("Ask filter ignored: the RAG service does not support metadata filters.");

        var result = await _rag.QueryAsync(req.Query, ct);
        var citations = BuildCitations(result.Sources, _rag.Options.Collection);

        // The service only returns sources the answer actually cited; an
        // answer citing nothing is the model saying the documents don't cover it
        // (or replying to small talk), so it gets no score.
        var cited = citations.Count > 0;
        return new AskAnswerResponse(
            TaskId: $"ask-{Guid.NewGuid():N}",
            Analyzed: Array.Empty<AskAnalyzedDomain>(),
            Finding: result.Answer,
            // Gemini replies in the language of the question, so one text serves both locales.
            FindingBn: result.Answer,
            Factors: Array.Empty<AskFactor>(),
            Evidence: Array.Empty<EvidenceRefDto>(),
            Citations: citations,
            Recommendation: null,
            CreatedAt: DateTime.UtcNow,
            Confidence: cited ? citations.Max(c => c.Confidence) : 0.0,
            RagHits: Array.Empty<AskRagHit>(),
            Mode: cited ? AskModes.Live : AskModes.NoSource,
            Warning: null);
    }

    /// <summary>
    /// One citation per cited chunk. <c>Confidence</c> is the chunk's
    /// similarity score from Qdrant, clamped to [0,1].
    /// </summary>
    internal static IReadOnlyList<CitationDto> BuildCitations(IReadOnlyList<PyRagSource> sources, string collection)
        => sources.Select((s, i) => new CitationDto(
            // Chunk ids restart at 0 in every document, so qualify them.
            Id: $"{s.DocumentId ?? s.Source}:{s.ChunkId ?? (i + 1).ToString()}",
            Title: s.Page is { } page ? $"{s.Source} · page {page}" : s.Source,
            Snippet: s.Excerpt,
            Confidence: Math.Clamp(s.Score, 0.0, 1.0),
            Source: collection,
            Url: string.Empty
        )).ToList();
}
