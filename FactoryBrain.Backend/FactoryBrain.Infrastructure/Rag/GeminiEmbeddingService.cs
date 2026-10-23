using System.Text;
using System.Text.Json;
using Pgvector;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Google Gemini <c>text-embedding-004</c> embedder. Native multilingual
/// model — preferred over OpenAI when Bangla quality matters more than
/// cost. Output is 768-d.
/// </summary>
public sealed class GeminiEmbeddingService : HostedEmbeddingServiceBase
{
    public const string Provider = "gemini";

    public GeminiEmbeddingService(
        IHttpClientFactory httpFactory,
        ILogger<GeminiEmbeddingService> logger,
        string apiKey,
        string model)
        : base(httpFactory, logger, apiKey, model)
    {
    }

    public override string ProviderId => Provider;
    public override int Dimensions => 768;

    public override async Task<Vector> EmbedAsync(string text, CancellationToken ct = default)
    {
        var client = HttpFactory.CreateClient(nameof(GeminiEmbeddingService));
        var model = string.IsNullOrWhiteSpace(Model) ? "text-embedding-004" : Model;
        // Gemini uses query-string auth. We hold the key in this URL only
        // for the duration of the call; it is NEVER logged, and any
        // exception message that escapes is passed through Redact() so
        // the substring can never reach a log line.
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:embedContent?key={Uri.EscapeDataString(ApiKey)}";

        var body = new
        {
            content = new { parts = new[] { new { text = text ?? string.Empty } } }
        };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
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
                    "Gemini embeddings call failed: status={Status} providerErrorType={ErrorType} providerErrorCode={ErrorCode}.",
                    (int)resp.StatusCode,
                    ExtractErrorType(raw),
                    ExtractErrorCode(raw));
                throw new HttpRequestException($"gemini-embed {(int)resp.StatusCode}");
            }

            using var doc = JsonDocument.Parse(raw);
            var values = doc.RootElement.GetProperty("embedding").GetProperty("values");
            var arr = new float[values.GetArrayLength()];
            int i = 0;
            foreach (var v in values.EnumerateArray())
                arr[i++] = (float)v.GetDouble();
            return new Vector(arr);
        }
        catch (Exception ex) when (ex.Message.Contains(ApiKey, StringComparison.Ordinal))
        {
            // Network/DNS errors from HttpClient sometimes include the
            // request URL in their message — scrub any key fragment
            // before re-throwing so it can't reach a log line.
            throw new HttpRequestException(Redact(ex.Message), ex);
        }
    }

    /// <summary>
    /// Best-effort extractor for the Gemini JSON error envelope
    /// (<c>error.status</c> / <c>error.code</c>). Returns the supplied
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
                && err.TryGetProperty("status", out var t))
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
                if (c.ValueKind == JsonValueKind.Number) return c.GetRawText();
                if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "unknown";
            }
        }
        catch { /* fall through */ }
        return "unknown";
    }
}
