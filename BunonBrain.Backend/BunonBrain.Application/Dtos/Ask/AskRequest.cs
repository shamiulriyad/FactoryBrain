namespace BunonBrain.Application.Dtos.Ask;

/// <summary>POST /api/ask body.</summary>
public record AskRequest(
    string Query,
    string? AgentId,
    RagFilter? Filter,
    /// <summary>Saved chat to add this question to; null starts a new chat.</summary>
    Guid? ConversationId = null,
    /// <summary>True: don't reuse an earlier answer, ask the model again.</summary>
    bool? Fresh = null
);

public record RagFilter(
    RagDepartment? Department,
    RagCategory? Category
);

public enum RagDepartment { Sewing, Cutting, Finishing, Qc, General }
public enum RagCategory { Manuals, Compliance, Qc, Policy, Sop }
