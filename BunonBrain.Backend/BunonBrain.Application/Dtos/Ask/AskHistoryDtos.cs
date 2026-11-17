namespace BunonBrain.Application.Dtos.Ask;

/// <summary>A saved chat in the caller's chat list.</summary>
public sealed record AskConversationSummary(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    /// <summary>The latest question in the chat (the title is the first one).</summary>
    string? LastQuestion);

/// <summary>One question and the answer it got, as shown at the time.</summary>
public sealed record AskMessageDto(
    Guid Id,
    string Question,
    string Answer,
    string Mode,
    double Confidence,
    IReadOnlyList<CitationDto> Citations,
    bool Cached,
    string? CachedQuestion,
    DateTimeOffset CreatedAt);

public sealed record AskConversationDetail(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AskMessageDto> Messages);
