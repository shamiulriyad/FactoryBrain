namespace FactoryBrain.Application.Abstractions.Interfaces;

/// <summary>
/// Client for the Python RAG service in <c>rag/</c> (LangChain + Qdrant +
/// Gemini, served by <c>rag/main.py</c>). It is the only source of answers
/// for Ask and agent runs: retrieval, generation and citation selection all
/// happen in that service.
/// </summary>
public interface IPyRagClient
{
    /// <summary>
    /// POST /api/kb/{collection}/query. Throws <see cref="PyRagUnavailableException"/>
    /// when the service can't be reached or returns an error.
    /// </summary>
    Task<PyRagAnswer> QueryAsync(string question, CancellationToken ct);

    /// <summary>GET /health. Never throws.</summary>
    Task<PyRagHealth> HealthAsync(CancellationToken ct);

    PyRagOptions Options { get; }
}

/// <summary>
/// <c>Answer</c> is the model's text with the [S1] markers stripped;
/// <c>Sources</c> holds only the chunks the model actually cited.
/// </summary>
public sealed record PyRagAnswer(string Answer, IReadOnlyList<PyRagSource> Sources);

public sealed record PyRagSource(
    string? DocumentId, string? ChunkId, int? Page, string Source, double Score, string Excerpt);

public sealed record PyRagHealth(bool Reachable, bool Ready, string Qdrant, string? Error);

/// <summary>
/// Where the service is and which collection to query, from the repo-root
/// .env shared with <c>rag/config.py</c>. The model names are read only to
/// show them; the Python service is what uses them.
/// </summary>
public sealed record PyRagOptions(
    Uri BaseUrl,
    string? ApiKey,
    string Collection,
    string LlmModel,
    string EmbeddingProvider,
    string EmbeddingModel,
    TimeSpan Timeout)
{
    public const string DefaultUrl = "http://localhost:8000";

    public static PyRagOptions FromEnvironment(Func<string, string?> env)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        var rawUrl = Clean(env("PYRAG_SERVICE_URL")) ?? DefaultUrl;
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            url = new Uri(DefaultUrl);

        var provider = (Clean(env("PYRAG_EMBEDDING_PROVIDER")) ?? "google").ToLowerInvariant();
        var seconds = int.TryParse(Clean(env("PYRAG_TIMEOUT_SECONDS")), out var s) && s > 0 ? s : 90;

        // Defaults mirror rag/config.py so the label matches what the service runs.
        return new PyRagOptions(
            BaseUrl: url,
            ApiKey: Clean(env("PYRAG_API_KEY")),
            Collection: Clean(env("PYRAG_COLLECTION_NAME")) ?? "pdf_rag",
            LlmModel: Clean(env("PYRAG_LLM_MODEL")) ?? "gemini-2.5-flash",
            EmbeddingProvider: provider,
            EmbeddingModel: Clean(env("PYRAG_EMBEDDING_MODEL"))
                ?? (provider == "huggingface" ? "sentence-transformers/all-MiniLM-L6-v2" : "gemini-embedding-001"),
            Timeout: TimeSpan.FromSeconds(seconds));
    }
}

/// <summary>
/// The RAG service answered an agent's question without citing any indexed
/// chunk, so there is no document-backed finding to store. <c>Answer</c> is
/// the service's reply (usually that the documents don't cover it).
/// </summary>
public sealed class AgentNoSourceException : Exception
{
    public string Answer { get; }
    public AgentNoSourceException(string answer)
        : base("The indexed documents do not cover this agent's question, so no insight was stored.") { Answer = answer; }
}

/// <summary>The Python RAG service could not answer (down, misconfigured, or returned an error).</summary>
public sealed class PyRagUnavailableException : Exception
{
    public int? Status { get; }
    public PyRagUnavailableException(string message, int? status = null, Exception? inner = null)
        : base(message, inner) { Status = status; }
}
