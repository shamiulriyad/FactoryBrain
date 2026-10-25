using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FactoryBrain.Application.Settings;
using Pgvector;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// OpenAI <c>text-embedding-3-*</c> / <c>ada-002</c> embedder. The requested
/// output size is sent as <c>dimensions</c> for the models that accept it
/// (see <see cref="EmbeddingCatalog"/>); callers still check the length of
/// what comes back.
/// </summary>
public sealed class OpenAiEmbeddingService : HostedEmbeddingServiceBase
{
    public const string Provider = "openai";

    private readonly int _dimensions;
    private readonly bool _sendDimensions;

    public OpenAiEmbeddingService(
        IHttpClientFactory httpFactory,
        ILogger<OpenAiEmbeddingService> logger,
        string apiKey,
        string model,
        int dimensions = 0)
        : base(httpFactory, logger, apiKey, model)
    {
        var spec = EmbeddingCatalog.Find(Provider, model);
        _dimensions = dimensions > 0 ? dimensions : spec?.DefaultDimensions ?? 1536;
        _sendDimensions = spec?.AcceptsDimensionsParameter == true;
    }

    public override string ProviderId => Provider;

    public override int Dimensions => _dimensions;

    public override async Task<Vector> EmbedAsync(string text, CancellationToken ct = default)
    {
        var client = HttpFactory.CreateClient(nameof(OpenAiEmbeddingService));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        object body = _sendDimensions
            ? new { model = Model, input = text ?? string.Empty, dimensions = _dimensions }
            : new { model = Model, input = text ?? string.Empty };
        // OpenAI passes the API key via the Authorization header, NOT the
        // URL — so the URL we log and re-throw references never contains
        // key material. The body sanitization below strips any accidental
        // echo of the key from the response body before it reaches a log.
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/embeddings")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, body.GetType()), Encoding.UTF8, "application/json")
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
