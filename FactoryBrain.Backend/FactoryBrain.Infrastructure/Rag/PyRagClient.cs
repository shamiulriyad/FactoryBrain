using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FactoryBrain.Application.Abstractions.Interfaces;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// HTTP client for the Python RAG service (<c>rag/main.py</c>). Sends
/// <c>X-Rag-Api-Key</c> when <c>PYRAG_API_KEY</c> is set; the key is never logged.
/// </summary>
public sealed class PyRagClient : IPyRagClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<PyRagClient> _log;

    public PyRagOptions Options { get; }

    public PyRagClient(IHttpClientFactory http, PyRagOptions options, ILogger<PyRagClient> log)
    { _http = http; Options = options; _log = log; }

    public async Task<PyRagAnswer> QueryAsync(string question, CancellationToken ct)
    {
        using var client = Client(Options.Timeout);
        var path = $"api/kb/{Uri.EscapeDataString(Options.Collection)}/query";
        HttpResponseMessage resp;
        try
        {
            resp = await client.PostAsJsonAsync(path, new { question }, Json, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PyRagUnavailableException($"The RAG service at {Options.BaseUrl} did not answer within {Options.Timeout.TotalSeconds:F0} seconds.", null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PyRagUnavailableException($"The RAG service at {Options.BaseUrl} is not reachable. Start it with `uvicorn main:app --port 8000` in rag/.", null, ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var detail = await DetailAsync(resp, ct);
                _log.LogWarning("RAG service query failed: status={Status}.", (int)resp.StatusCode);
                throw new PyRagUnavailableException($"The RAG service returned {(int)resp.StatusCode}: {detail}", (int)resp.StatusCode);
            }

            var body = await resp.Content.ReadFromJsonAsync<QueryOut>(Json, ct)
                ?? throw new PyRagUnavailableException("The RAG service returned an empty response.");
            var sources = (body.Sources ?? new())
                .Select(s => new PyRagSource(s.DocumentId, s.ChunkId, s.Page, s.Source ?? "document", s.Score, s.Excerpt ?? ""))
                .ToList();
            return new PyRagAnswer(body.Answer?.Trim() ?? "", sources);
        }
    }

    public async Task<PyRagHealth> HealthAsync(CancellationToken ct)
    {
        try
        {
            using var client = Client(TimeSpan.FromSeconds(3));
            using var resp = await client.GetAsync("health", ct);
            if (!resp.IsSuccessStatusCode) return new PyRagHealth(true, false, "unknown", $"health returned {(int)resp.StatusCode}");
            var body = await resp.Content.ReadFromJsonAsync<HealthOut>(Json, ct);
            return new PyRagHealth(true, body?.Ready ?? false, body?.Qdrant ?? "unknown", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new PyRagHealth(false, false, "unknown", "not reachable");
        }
    }

    private HttpClient Client(TimeSpan timeout)
    {
        var client = _http.CreateClient(nameof(PyRagClient));
        client.BaseAddress = Options.BaseUrl;
        client.Timeout = timeout;
        if (Options.ApiKey is { } key) client.DefaultRequestHeaders.TryAddWithoutValidation("X-Rag-Api-Key", key);
        return client;
    }

    /// <summary>FastAPI's <c>{"detail": "..."}</c>, or the start of a plain-text body.</summary>
    private static async Task<string> DetailAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                text = d.GetString() ?? text;
        }
        catch (JsonException) { }
        text = text.Trim();
        return text.Length == 0 ? resp.ReasonPhrase ?? "error" : text.Length > 300 ? text[..300] + "…" : text;
    }

    private sealed class QueryOut
    {
        public string? Answer { get; set; }
        public List<SourceOut>? Sources { get; set; }
    }

    private sealed class SourceOut
    {
        [JsonPropertyName("document_id")] public string? DocumentId { get; set; }
        [JsonPropertyName("chunk_id")] public string? ChunkId { get; set; }
        public int? Page { get; set; }
        public string? Source { get; set; }
        public double Score { get; set; }
        public string? Excerpt { get; set; }
    }

    private sealed class HealthOut
    {
        public bool Ready { get; set; }
        public string? Qdrant { get; set; }
    }
}
