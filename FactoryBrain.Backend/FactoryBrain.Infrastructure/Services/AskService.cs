using System.Net.Http.Json;
using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Domain.Enums;
using FactoryBrain.Application.Dtos.Ask;
using FactoryBrain.Application.Abstractions.Interfaces;

namespace FactoryBrain.Infrastructure.Services;

/// <summary>
/// Equivalent of <c>src/services/ask.service.ts</c> + the SSE /api/ask handler.
/// If <c>LLM_PROVIDER</c> + <c>LLM_API_KEY</c> are set, forwards to the model;
/// otherwise returns the deterministic demo payload assembled from RAG hits +
/// the agent roster.
/// </summary>
public sealed class AskService : IAskService
{
    private readonly IRagService _rag;
    private readonly IConfiguration _cfg;
    private readonly IHttpClientFactory _http;
    private readonly FactoryBrainDbContext _db;
    private readonly ILogger<AskService> _log;

    public AskService(
        IRagService rag, IConfiguration cfg, IHttpClientFactory http,
        FactoryBrainDbContext db, ILogger<AskService> log)
    { _rag = rag; _cfg = cfg; _http = http; _db = db; _log = log; }

    public async Task<AskAnswerResponse> AskAsync(AskRequest req, CancellationToken ct)
    {
        var provider  = _cfg["LLM_PROVIDER"] ?? Environment.GetEnvironmentVariable("LLM_PROVIDER");
        var apiKey    = _cfg["LLM_API_KEY"]  ?? Environment.GetEnvironmentVariable("LLM_API_KEY");
        var model     = _cfg["LLM_MODEL"]    ?? Environment.GetEnvironmentVariable("LLM_MODEL");
        if (string.IsNullOrWhiteSpace(model) && string.Equals(provider?.Trim(), "openai", StringComparison.OrdinalIgnoreCase))
            model = "gpt-6-luna";

        var ragHits = await _rag.RetrieveAsync(req.Query, req.Filter, topK: 4, ct: ct);

        // Step 47 (47add.txt): when the RAG service has no confident source
        // for the query, skip the LLM entirely and surface a fixed EN/BN
        // "no confident source was found" message. We must NOT feed an
        // empty context to the model — every model hallucinates
        // something, and the user sees that hallucination as an answer.
        if (ragHits.Count == 0)
        {
            _log.LogInformation("LLM skipped: no confident sources for query={Query}", req.Query);
            return NoConfidentSourceAsync(req);
        }

        if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                return await LiveAsync(req, ragHits, provider!, apiKey!, model!, ct);
            }
            catch (Exception ex)
            {
                // Spec (48f): log only the HTTP status + provider's
                // error type / code. Never log the response body or
                // any part of a key. Most exceptions raised by the LLM
                // helpers are HttpRequestException built from the status
                // alone, so we surface the status and the exception
                // type (which is the "error type" the operator wants)
                // — and nothing else.
                var status = TryExtractStatus(ex);
                _log.LogWarning(
                    "Live ask failed, falling back to demo: status={Status} providerErrorType={ErrorType} providerErrorCode={ErrorCode}.",
                    status, ex.GetType().Name, ex.Message is null ? "unknown" : "see-status");
                return DemoAsync(req, ragHits, "Live model unavailable, showing demo answer.", ct);
            }
        }
        return DemoAsync(req, ragHits, null, ct);
    }

    /// <summary>
    /// Fixed-shape answer for "no confident source found". Citations are
    /// empty (per brief: never invent a citation). The fixed EN/BN
    /// strings are not interpolated with the user query — that would be a
    /// surface that downstream templates might treat as a hit.
    /// </summary>
    private static AskAnswerResponse NoConfidentSourceAsync(AskRequest req)
    {
        return new AskAnswerResponse(
            TaskId: $"ask-{Guid.NewGuid():N}",
            Analyzed: Array.Empty<AskAnalyzedDomain>(),
            Finding: "No confident source was found for your query.",
            FindingBn: "আপনার প্রশ্নের জন্য কোনো নির্ভরযোগ্য উৎস পাওয়া যায়নি।",
            Factors: Array.Empty<AskFactor>(),
            Evidence: new List<EvidenceRefDto>(),
            Citations: new List<CitationDto>(),
            Recommendation: new AskRecommendation(
                "Try rephrasing or broadening the query",
                "প্রশ্নটি পুনর্বিন্যাস বা প্রসারিত করুন",
                "Use one or two keywords, or check the manuals page directly.",
                "এক বা দুটি কীওয়ার্ড ব্যবহার করুন, অথবা সরাসরি ম্যানুয়াল পেজ দেখুন।",
                "low"),
            CreatedAt: DateTime.UtcNow,
            Confidence: 0.0,
            RagHits: Array.Empty<AskRagHit>());
    }

    /// <summary>
    /// Step 47: project an <see cref="ReadOnlyCollection{AskRagHit}"/> onto
    /// the new <see cref="CitationDto"/> shape with id, title, snippet,
    /// confidence (hybrid score clamped to [0,1]), source, and url. The
    /// URL is empty for now because the per-chunk URL is not stored on the
    /// chunk row — a future step can join through <c>ManualDocument.Url</c>.
    /// </summary>
    private static IReadOnlyList<CitationDto> BuildCitations(IReadOnlyList<AskRagHit> hits)
        => hits.Select(h => new CitationDto(
            Id: h.Id,
            Title: h.Title,
            Snippet: h.Snippet,
            Confidence: Math.Clamp(h.HybridScore, 0.0, 1.0),
            Source: "rag",
            Url: string.Empty
        )).ToList();

    private AskAnswerResponse DemoAsync(AskRequest req, IReadOnlyList<AskRagHit> hits, string? warning, CancellationToken ct)
    {
        var factors = new List<AskFactor>
        {
            new("Line efficiency vs target", "লক্ষ্যের বিপরীতে লাইনের দক্ষতা", "−12%", "−১২%"),
            new("Helper allocation", "সহায়ক বরাদ্দ", "watch", "নজরে")
        };
        var rec = new AskRecommendation(
            "Open the line board and review the bottleneck",
            "লাইন বোর্ড খুলুন এবং বটলনেক পর্যালোচনা করুন",
            "Pull helpers from Finishing → Sewing, validate SAH against target.",
            "ফিনিশিং থেকে সেলাইতে সহায়ক পুনর্বণ্টন করুন।",
            "medium");

        var evidence = new List<EvidenceRefDto>();
        if (hits.Count > 0)
            evidence.Add(new EvidenceRefDto("manuals", hits.Count,
                new Dictionary<string, string> { ["source"] = "rag" },
                hits.Select(h => h.SourceId).ToList()));

        return new AskAnswerResponse(
            TaskId: $"ask-{Guid.NewGuid():N}",
            Analyzed: evidence.Select(e => new AskAnalyzedDomain(e.Domain, e.Count, null)).ToList(),
            Finding: $"Demo answer for: {req.Query}",
            FindingBn: $"ডেমো উত্তর: {req.Query}",
            Factors: factors,
            Evidence: evidence,
            Citations: BuildCitations(hits),
            Recommendation: rec,
            CreatedAt: DateTime.UtcNow,
            Confidence: 0.7,
            RagHits: hits.ToList()
        );
    }

    private async Task<AskAnswerResponse> LiveAsync(
        AskRequest req, IReadOnlyList<AskRagHit> hits,
        string provider, string apiKey, string model, CancellationToken ct)
    {
        var prompt = string.Join("\n\n", new[]
        {
            "You are BunonBrain, the factory-floor operations assistant for a Bangladeshi RMG factory.",
            "Return ONLY JSON with: taskId, finding, findingBn, factors[] {label,labelBn,magnitude,magnitudeBn},",
            "evidence[] { domain: manuals|orders|customers|products|inventory|conversations|policies|suppliers, count },",
            "recommendation { title,titleBn,action,actionBn,riskTier: low|medium|high }, confidence.",
            "Question: " + req.Query,
            "RAG hits: " + System.Text.Json.JsonSerializer.Serialize(hits.Select(h => new { h.SourceId, h.Title, h.Snippet, h.HybridScore }))
        });

        var raw = provider switch
        {
            "openai"    => await OpenAiAsync(apiKey, model, prompt, ct),
            "anthropic" => await AnthropicAsync(apiKey, model, prompt, ct),
            "gemini"    => await GeminiAsync(apiKey, model, prompt, ct),
            _           => throw new InvalidOperationException($"Unknown provider: {provider}")
        };

        string finding = raw;
        try
        {
            var m = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(MatchJson(raw));
            finding = m.GetProperty("finding").GetString() ?? finding;
        }
        catch { /* swallow, keep raw as finding */ }

        return new AskAnswerResponse(
            TaskId: $"live-{Guid.NewGuid():N}",
            Analyzed: Array.Empty<AskAnalyzedDomain>(),
            Finding: finding,
            FindingBn: finding,
            Factors: new List<AskFactor>(),
            Evidence: new List<EvidenceRefDto>(),
            Citations: BuildCitations(hits),
            Recommendation: new AskRecommendation("Review the raw answer", "কাঁচা উত্তর পর্যালোচনা", raw.Length > 240 ? raw[..240] : raw, raw.Length > 240 ? raw[..240] : raw, "low"),
            CreatedAt: DateTime.UtcNow,
            Confidence: 0.65,
            RagHits: hits.ToList());
    }

    private static string MatchJson(string raw)
    {
        var s = raw.IndexOf('{'); var e = raw.LastIndexOf('}');
        return s >= 0 && e > s ? raw.Substring(s, e - s + 1) : raw;
    }

    private async Task<string> OpenAiAsync(string apiKey, string model, string prompt, CancellationToken ct)
    {
        using var http = _http.CreateClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", apiKey);
        using var resp = await http.PostAsync("https://api.openai.com/v1/chat/completions",
            new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                model,
                messages = new[] { new { role = "user", content = prompt } },
                temperature = 0.2
            }), System.Text.Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Spec (48f): log only the HTTP status + provider's error
            // type / code. Never log the response body or any part of a key.
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogWarning(
                "OpenAI LLM call failed: status={Status} providerErrorType={ErrorType} providerErrorCode={ErrorCode}.",
                (int)resp.StatusCode,
                ExtractOpenAiErrorType(body),
                ExtractOpenAiErrorCode(body));
            throw new HttpRequestException($"openai-llm {(int)resp.StatusCode}");
        }
        var j = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
        return j.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private async Task<string> AnthropicAsync(string apiKey, string model, string prompt, CancellationToken ct)
    {
        using var http = _http.CreateClient();
        http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);
        http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        using var resp = await http.PostAsync("https://api.anthropic.com/v1/messages",
            new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                model, max_tokens = 1024,
                messages = new[] { new { role = "user", content = prompt } }
            }), System.Text.Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Spec (48f): log only the HTTP status + provider's error
            // type / code. Never log the response body or any part of a key.
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogWarning(
                "Anthropic LLM call failed: status={Status} providerErrorType={ErrorType} providerErrorCode={ErrorCode}.",
                (int)resp.StatusCode,
                ExtractAnthropicErrorType(body),
                ExtractAnthropicErrorCode(body));
            throw new HttpRequestException($"anthropic-llm {(int)resp.StatusCode}");
        }
        var j = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
        return j.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private async Task<string> GeminiAsync(string apiKey, string model, string prompt, CancellationToken ct)
    {
        using var http = _http.CreateClient();
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        using var resp = await http.PostAsync(url,
            new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } },
                generationConfig = new { temperature = 0.2 }
            }), System.Text.Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
        {
            // Spec (48f): log only the HTTP status + provider's error
            // type / code. Never log the response body or any part of a key.
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogWarning(
                "Gemini LLM call failed: status={Status} providerErrorType={ErrorType} providerErrorCode={ErrorCode}.",
                (int)resp.StatusCode,
                ExtractGeminiErrorType(body),
                ExtractGeminiErrorCode(body));
            throw new HttpRequestException($"gemini-llm {(int)resp.StatusCode}");
        }
        var j = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
        return j.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
    }

    /// <summary>
    /// Pull an HTTP status code out of an exception chain without logging
    /// any body. Returns 0 when no status is present.
    /// </summary>
    private static int TryExtractStatus(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException hre)
            {
                // The exception messages the embedding/LLM helpers
                // throw are pure "provider-tag <status>" strings; we
                // pull the trailing integer instead of relying on
                // <see cref="System.Net.Http.HttpRequestException.StatusCode"/>.
                var digits = new string(hre.Message.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var n) && n >= 100 && n < 600) return n;
            }
        }
        return 0;
    }

    private static string ExtractOpenAiErrorType(string body)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == System.Text.Json.JsonValueKind.Object
                && err.TryGetProperty("type", out var t))
                return t.GetString() ?? "unknown";
        }
        catch { }
        return "unknown";
    }

    private static string ExtractOpenAiErrorCode(string body)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == System.Text.Json.JsonValueKind.Object
                && err.TryGetProperty("code", out var c))
            {
                if (c.ValueKind == System.Text.Json.JsonValueKind.String) return c.GetString() ?? "unknown";
                if (c.ValueKind == System.Text.Json.JsonValueKind.Number) return c.GetRawText();
            }
        }
        catch { }
        return "unknown";
    }

    private static string ExtractAnthropicErrorType(string body)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == System.Text.Json.JsonValueKind.Object
                && err.TryGetProperty("type", out var t))
                return t.GetString() ?? "unknown";
        }
        catch { }
        return "unknown";
    }

    private static string ExtractAnthropicErrorCode(string body)
    {
        // Spec (48h): log only the HTTP status, error type and error code —
        // never error.message or the response body. Anthropic's error
        // envelope puts the user-facing human message in `error.message`,
        // which can contain secrets, internal trace details, or whatever
        // the operator typed. The structured code lives in `error.type` /
        // `error.code`, which is what the operator actually wants to see.
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == System.Text.Json.JsonValueKind.Object
                && err.TryGetProperty("code", out var c))
            {
                if (c.ValueKind == System.Text.Json.JsonValueKind.String) return c.GetString() ?? "unknown";
                if (c.ValueKind == System.Text.Json.JsonValueKind.Number) return c.GetRawText();
            }
            if (err.TryGetProperty("type", out var t))
                return t.GetString() ?? "unknown";
        }
        catch { }
        return "unknown";
    }

    private static string ExtractGeminiErrorType(string body)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == System.Text.Json.JsonValueKind.Object
                && err.TryGetProperty("status", out var t))
                return t.GetString() ?? "unknown";
        }
        catch { }
        return "unknown";
    }

    private static string ExtractGeminiErrorCode(string body)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var err)
                && err.ValueKind == System.Text.Json.JsonValueKind.Object
                && err.TryGetProperty("code", out var c))
            {
                if (c.ValueKind == System.Text.Json.JsonValueKind.Number) return c.GetRawText();
                if (c.ValueKind == System.Text.Json.JsonValueKind.String) return c.GetString() ?? "unknown";
            }
        }
        catch { }
        return "unknown";
    }
}
