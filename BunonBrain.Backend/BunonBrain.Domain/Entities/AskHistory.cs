namespace BunonBrain.Domain.Entities;

/// <summary>
/// One saved Ask BunonBrain chat of one user. Private to that user: nobody
/// else (administrators included) lists or opens it through the API.
/// </summary>
public class AskConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    /// <summary>The first question, shortened; shown in the chat list.</summary>
    public string Title { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Time of the latest question; the chat list is ordered by it.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<AskMessage> Messages { get; set; } = new();
}

/// <summary>A question and the answer it got, exactly as shown at the time.</summary>
public class AskMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public string Question { get; set; } = default!;
    public string Answer { get; set; } = default!;
    /// <summary><c>live</c> or <c>no_source</c> (see AskModes).</summary>
    public string Mode { get; set; } = default!;
    /// <summary>Similarity of the best cited passage, 0 when nothing was cited.</summary>
    public double Confidence { get; set; }
    /// <summary>The cited passages as JSON (CitationDto list).</summary>
    public string CitationsJson { get; set; } = "[]";
    /// <summary>True when the answer was reused from an earlier, equivalent question (no model call).</summary>
    public bool Cached { get; set; }
    public string? CachedQuestion { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
