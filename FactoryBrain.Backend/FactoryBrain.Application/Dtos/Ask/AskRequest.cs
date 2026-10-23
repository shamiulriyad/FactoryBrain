namespace FactoryBrain.Application.Dtos.Ask;

/// <summary>POST /api/ask body.</summary>
public record AskRequest(
    string Query,
    string? AgentId,
    RagFilter? Filter
);

public record RagFilter(
    RagDepartment? Department,
    RagCategory? Category
);

public enum RagDepartment { Sewing, Cutting, Finishing, Qc, General }
public enum RagCategory { Manuals, Compliance, Qc, Policy, Sop }
