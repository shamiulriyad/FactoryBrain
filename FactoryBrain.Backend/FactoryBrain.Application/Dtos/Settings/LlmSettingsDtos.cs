using System.Text.Json.Serialization;

namespace FactoryBrain.Application.Dtos.Settings;

/// <summary>
/// Non-secret LLM status. The API key itself is never on this object; only
/// whether one is stored (<see cref="KeySet"/>) and whether it can be read.
/// </summary>
public record LlmSettingsResponse(
    bool Configured,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Provider,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Model,
    string Mode,            // "demo" | "live"
    string Persistence,     // "database" | "startup_config" | "runtime_override" | "none"
    bool KeySet,
    string KeyStatus,       // "set" | "not_set" | "unreadable"
    long Version,
    DateTimeOffset? UpdatedAt,
    bool CanStoreKeys       // false when the server has no encryption key configured
);

public record LlmSettingsRequest(
    string? Provider,
    string? ApiKey,
    string? Model,
    bool? ClearKey,
    bool? ClearAll,
    /// <summary>When set, the update is rejected with 409 unless the stored version still matches.</summary>
    long? ExpectedVersion = null
);

/// <summary>Non-secret embedding status, including how the stored vectors compare with it.</summary>
public record EmbeddingSettingsResponse(
    string Provider,
    string Model,
    int Dimensions,
    string Persistence,
    bool KeySet,
    string KeyStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ColumnDimensions,
    bool DimensionsMatchColumn,
    int Documents,
    int PendingDocuments,
    bool Degraded,
    long Version,
    DateTimeOffset? UpdatedAt,
    bool CanStoreKeys,
    IReadOnlyList<EmbeddingModelOption> Supported
);

public record EmbeddingModelOption(string Provider, string Model, int DefaultDimensions, int MinDimensions, int MaxDimensions);

public record EmbeddingPlanRequest(string? Provider, string? Model, int? Dimensions);

public record EmbeddingSettingsRequest(
    string? Provider,
    string? Model,
    int? Dimensions,
    string? ApiKey,
    bool? ClearKey,
    /// <summary>The <c>planId</c> from the plan the Admin reviewed; required whenever vectors must be recomputed.</summary>
    string? ConfirmPlanId,
    long? ExpectedVersion = null
);

public record ReindexRequest(string? ConfirmPlanId);

public record EmbeddingTargetDto(string Provider, string Model, int Dimensions);

/// <summary>
/// Cost of a reindex. <see cref="EstimateUsd"/> is null when it can't be
/// estimated reliably (no price configured for a hosted provider);
/// <see cref="Note"/> always says which.
/// </summary>
public record ReindexCostDto(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? EstimateUsd,
    bool IsUpperBound, string Currency, string Note);

public record ReindexPlanDto(
    string PlanId,
    EmbeddingTargetDto Current,
    string CurrentPersistence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ColumnDimensions,
    EmbeddingTargetDto Target,
    bool RequiresReindex,
    bool RequiresColumnResize,
    bool ReembedsAllDocuments,
    int Documents,
    int Chunks,
    long Bytes,
    long EstimatedTokensUpperBound,
    ReindexCostDto Cost,
    long SettingsVersion,
    IReadOnlyList<string> Warnings
);

/// <summary>Result of an embedding settings save: the new status, plus the reindex it ran (if any).</summary>
public record EmbeddingUpdateResultDto(EmbeddingSettingsResponse Settings, ReindexOutcomeDto? Reindex);

public record ReindexOutcomeDto(
    string PlanId,
    int Documents,
    int Chunks,
    bool Resized,
    int? ColumnDimensionsBefore,
    int ColumnDimensionsAfter,
    EmbeddingTargetDto Embedding,
    long SettingsVersion
);
