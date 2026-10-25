namespace FactoryBrain.Domain.Entities;

/// <summary>
/// The single persisted row (Id = 1) of LLM and embedding settings. Each group
/// (LLM, embedding) is authoritative once it has been saved
/// (<see cref="LlmUpdatedAt"/> / <see cref="EmbeddingUpdatedAt"/> not null);
/// until then the startup configuration applies. API keys are stored only as
/// AES-GCM ciphertext; the encryption key is never stored in the database.
/// </summary>
public class AiSettingsRecord
{
    public const short SingletonId = 1;

    public short Id { get; set; } = SingletonId;

    /// <summary>Incremented on every write; used for optimistic checks and plan ids.</summary>
    public long Version { get; set; }

    public string? LlmProvider { get; set; }
    public string? LlmModel { get; set; }
    public byte[]? LlmApiKeyEncrypted { get; set; }
    /// <summary>Null until the LLM group is first saved.</summary>
    public DateTimeOffset? LlmUpdatedAt { get; set; }

    public string? EmbeddingProvider { get; set; }
    public string? EmbeddingModel { get; set; }
    public int? EmbeddingDimensions { get; set; }
    public byte[]? EmbeddingApiKeyEncrypted { get; set; }
    /// <summary>Null until the embedding group is first saved.</summary>
    public DateTimeOffset? EmbeddingUpdatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UpdatedByUserId { get; set; }
    public string? UpdatedByEmail { get; set; }

    /// <summary>Cross-instance reindex lease: held while a reindex computes and writes vectors.</summary>
    public Guid? ReindexLeaseId { get; set; }
    public DateTimeOffset? ReindexLeaseExpiresAt { get; set; }
}
