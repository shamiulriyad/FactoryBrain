using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pgvector;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// OpenAI <c>text-embedding-3-*</c> embedder. Resolves the output dimensions
/// from the configured model name (1536 for <c>3-small</c>, 3072 for
/// <c>3-large</c>); falls back to 1536 when the model is unknown.
/// </summary>
public sealed class OpenAiEmbeddingService : HostedEmbeddingServiceBase
{
    public const string Provider = "openai";

    public OpenAiEmbeddingService(
        IHttpClientFactory httpFactory,
        ILogger<OpenAiEmbeddingService> logger,
        string apiKey,
        string model)
        : base(httpFactory, logger, apiKey, model)
    {
    }

    public override string ProviderId => Provider;

    public override int Dimensions => Model switch
    {
        var m when m.Contains("3-large", StringComparison.OrdinalIgnoreCase) => 3072,
        var m when m.Contains("3-small", StringComparison.OrdinalIgnoreCase) => 1536,
        var m when m.Contains("ada-002",  StringComparison.OrdinalIgnoreCase) => 1536,
        _ => 1536
    };

    public override async Task<Vector> EmbedAsync(string text, CancellationToken ct = default)
    {
        var client = HttpFactory.CreateClient(nameof(OpenAiEmbeddingService));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        var body = new
        {
            model = Model,
            input = text ?? string.Empty
        };
        // OpenAI passes the API key via the Authorization header, NOT the
        // URL — so the URL we log and re-throw references never contains
        // key material. The body sanitization below strips any accidental
        // echo of the key from the response body before it reaches a log.
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
        var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // Spec (48f): log only the HTTP status and the provider's
            // error type / code. Never log the response body, and
            // never log any key fragment.
            Logger.LogWarning(
                "OpenAI embeddings call failed: status={Status} providerErrorType={ErrorType} providerErrorCode={ErrorCode}.",
                (int)resp.StatusCode,
                ExtractErrorType(raw),
                ExtractErrorCode(raw));
            throw new HttpRequestException($"openai-embed {(int)resp.StatusCode}");
        }

        using var doc = JsonDocument.Parse(raw);
        var values = doc.RootElement
            .GetProperty("data")[0]
            .GetProperty("embedding");
        var arr = new float[values.GetArrayLength()];
        int i = 0;
        foreach (var v in values.EnumerateArray())
            arr[i++] = (float)v.GetDouble();
        return new Vector(arr);
    }

    /// <summary>
    /// Best-effort extractor for the OpenAI JSON error envelope
    /// (<c>error.type</c> / <c>error.code</c>). Returns the supplied
    /// sentinel when the body isn't valid JSON or doesn't carry the
    /// envelope. Never throws and never returns any key material.
    /// </summary>
    private static string ExtractErrorType(string raw)
    {
        try
        {
            using var d = JsonDocument.Parse(raw);
            if (d.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("type", out var t))
                return t.GetString() ?? "unknown";
        }
        catch { /* fall through */ }
        return "unknown";
    }

    private static string ExtractErrorCode(string raw)
    {
        try
        {
            using var d = JsonDocument.Parse(raw);
            if (d.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == JsonValueKind.Object
                && err.TryGetProperty("code", out var c))
            {
                if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "unknown";
                if (c.ValueKind == JsonValueKind.Number) return c.GetRawText();
            }
        }
        catch { /* fall through */ }
        return "unknown";
    }
}
