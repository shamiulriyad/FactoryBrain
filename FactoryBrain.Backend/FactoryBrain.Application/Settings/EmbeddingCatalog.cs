namespace FactoryBrain.Application.Settings;

/// <summary>One supported embedding model and the output sizes it can produce.</summary>
public sealed record EmbeddingModelSpec(
    string Provider,
    string Model,
    int DefaultDimensions,
    int MinDimensions,
    int MaxDimensions,
    /// <summary>True when the provider accepts a requested output size (OpenAI <c>dimensions</c>).</summary>
    bool AcceptsDimensionsParameter)
{
    public bool Allows(int dims) => dims >= MinDimensions && dims <= MaxDimensions;
}

/// <summary>
/// The embedding providers and models this API knows how to call, with the
/// dimensions each one can actually return. Anything else is rejected rather
/// than guessed: a wrong guess means vectors that don't fit the column.
/// </summary>
public static class EmbeddingCatalog
{
    public const string Local  = "local";
    public const string OpenAi = "openai";
    public const string Gemini = "gemini";
    public const string Stub   = "stub";

    /// <summary>Upper bound for the local hash embedder (pgvector allows up to 16000).</summary>
    public const int LocalMaxDimensions = 4096;
    public const int LocalDefaultDimensions = 384;

    public static readonly IReadOnlyList<EmbeddingModelSpec> Models = new[]
    {
        new EmbeddingModelSpec(Local,  "hash-md5",               LocalDefaultDimensions, 1, LocalMaxDimensions, false),
        // OpenAI text-embedding-3-* accept a "dimensions" request parameter up to their native size.
        new EmbeddingModelSpec(OpenAi, "text-embedding-3-small", 1536, 1, 1536, true),
        new EmbeddingModelSpec(OpenAi, "text-embedding-3-large", 3072, 1, 3072, true),
        // ada-002 has a fixed output size and rejects the dimensions parameter.
        new EmbeddingModelSpec(OpenAi, "text-embedding-ada-002", 1536, 1536, 1536, false),
        new EmbeddingModelSpec(Gemini, "text-embedding-004",     768, 768, 768, false),
        // Development-only deterministic fake (see StubEmbeddingService).
        new EmbeddingModelSpec(Stub,   "stub-deterministic",     768, 1, LocalMaxDimensions, false),
    };

    public static bool IsHosted(string provider) => provider is OpenAi or Gemini;

    public static string? DefaultModel(string provider) =>
        Models.FirstOrDefault(m => m.Provider == provider)?.Model;

    public static EmbeddingModelSpec? Find(string provider, string model) =>
        Models.FirstOrDefault(m => m.Provider == provider && string.Equals(m.Model, model, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Normalise a requested (provider, model, dims) triple, filling the
    /// model and dimensions from the catalogue when omitted. Returns the
    /// errors (keyed by field) when the combination is not supported.
    /// </summary>
    public static (string Provider, string Model, int Dimensions, Dictionary<string, string> Errors) Normalize(
        string? provider, string? model, int? dimensions)
    {
        var errors = new Dictionary<string, string>();
        var p = string.IsNullOrWhiteSpace(provider) ? Local : provider.Trim().ToLowerInvariant();
        if (!Models.Any(m => m.Provider == p))
        {
            errors["provider"] = $"Unsupported embedding provider '{p}'. Supported: {string.Join(", ", Models.Select(m => m.Provider).Distinct())}.";
            return (p, model?.Trim() ?? string.Empty, dimensions ?? 0, errors);
        }

        var m = string.IsNullOrWhiteSpace(model) ? DefaultModel(p)! : model.Trim();
        var spec = Find(p, m);
        if (spec is null)
        {
            errors["model"] = $"Unsupported {p} embedding model '{m}'. Supported: {string.Join(", ", Models.Where(x => x.Provider == p).Select(x => x.Model))}.";
            return (p, m, dimensions ?? 0, errors);
        }

        var d = dimensions ?? spec.DefaultDimensions;
        if (!spec.Allows(d))
        {
            errors["dimensions"] = spec.MinDimensions == spec.MaxDimensions
                ? $"{spec.Model} always returns {spec.MaxDimensions} dimensions."
                : $"{spec.Model} supports {spec.MinDimensions}–{spec.MaxDimensions} dimensions.";
        }
        return (p, spec.Model, d, errors);
    }
}
