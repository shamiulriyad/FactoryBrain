using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Qc;
using FactoryBrain.Application.Settings;

namespace FactoryBrain.Infrastructure.Qc;

/// <summary>
/// Defect detection with the vision-capable language model configured in
/// the AI settings (provider and key from <see cref="IAiSettingsProvider"/>,
/// the same source Ask uses; model from <c>QC_VISION_MODEL</c> or the LLM
/// model). The model is asked for JSON with a label, its own confidence and
/// a <c>box_2d</c> per visible defect.
/// <para>
/// Limits, stated to users: the confidence is the model's self-reported
/// score, not a calibrated probability, and boxes from a general
/// vision-language model are approximate. A purpose-trained detector behind
/// <see cref="HttpDefectDetectionProvider"/> is the better choice for
/// production thresholds.
/// </para>
/// </summary>
public sealed class LlmVisionDefectProvider : IDefectDetectionProvider
{
    private const string AnthropicVersion = "2023-06-01";

    private readonly IAiSettingsProvider _settings;
    private readonly IHttpClientFactory _http;
    private readonly QcOptions _options;
    private readonly ILogger<LlmVisionDefectProvider> _log;

    public LlmVisionDefectProvider(IAiSettingsProvider settings, IHttpClientFactory http, QcOptions options, ILogger<LlmVisionDefectProvider> log)
    {
        _settings = settings; _http = http; _options = options; _log = log;
    }

    public DefectProviderStatus Describe()
    {
        LlmSettings llm;
        try { llm = _settings.GetCurrent().Llm; }
        catch (AiSettingsUnavailableException)
        {
            return new DefectProviderStatus(false, QcOptions.ModeLlm, null, null, null, "The AI settings store could not be read.");
        }
        var model = _options.VisionModel ?? llm.EffectiveModel;
        if (llm.Provider is null || !LlmSettings.SupportedProviders.Contains(llm.Provider))
            return new DefectProviderStatus(false, QcOptions.ModeLlm, llm.Provider, model, null,
                "QC_VISION_PROVIDER=llm, but no language-model provider is configured (Settings → AI or LLM_PROVIDER).");
        if (llm.ApiKey is null)
            return new DefectProviderStatus(false, QcOptions.ModeLlm, llm.Provider, model, null,
                "QC_VISION_PROVIDER=llm, but the language-model API key is not set.");
        if (string.IsNullOrWhiteSpace(model))
            return new DefectProviderStatus(false, QcOptions.ModeLlm, llm.Provider, null, null,
                "Set QC_VISION_MODEL to a vision-capable model for this provider.");
        return new DefectProviderStatus(true, QcOptions.ModeLlm, llm.Provider, model, true, null);
    }

    public async Task<DefectDetectionResult> DetectAsync(DefectDetectionRequest request, CancellationToken ct)
    {
        var status = Describe();
        if (!status.Configured) throw new DefectDetectionException(DefectDetectionException.NotConfigured, status.Reason!);
        var llm = (await _settings.GetAsync(ct)).Llm;
        var key = llm.ApiKey!.Reveal();
        var provider = status.Provider!;
        var model = status.Model!;

        using var http = BuildRequest(provider, model, key, request);
        var client = _http.CreateClient(nameof(LlmVisionDefectProvider));
        client.Timeout = Timeout.InfiniteTimeSpan; // the linked token below enforces QC_VISION_TIMEOUT_SECONDS
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        var sw = Stopwatch.StartNew();
        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(http, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DefectDetectionException(DefectDetectionException.Timeout,
                $"The {provider} vision model did not answer within {_options.Timeout.TotalSeconds:F0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("QC vision call to {Provider} failed before a response ({ErrorType}).", provider, ex.GetType().Name);
            throw new DefectDetectionException(DefectDetectionException.ProviderError, $"The {provider} API could not be reached.", ex);
        }

        using (resp)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Status only: provider bodies can echo request details.
                _log.LogWarning("QC vision call to {Provider} failed: status={Status}.", provider, (int)resp.StatusCode);
                throw resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? new DefectDetectionException(DefectDetectionException.Unauthorized, $"The {provider} API rejected the configured key ({(int)resp.StatusCode}).")
                    : new DefectDetectionException(DefectDetectionException.ProviderError, $"The {provider} API returned HTTP {(int)resp.StatusCode}.");
            }
            var (content, version) = ReadContent(provider, raw);
            var defects = QcDetectionNormalizer.ParseVisionModelJson(content);
            _log.LogInformation("QC vision call to {Provider} model={Model} returned {Count} defect(s) in {Ms} ms.",
                provider, model, defects.Count, sw.ElapsedMilliseconds);
            return new DefectDetectionResult(provider, model, version, LocalizationSupported: true, defects);
        }
    }

    public static string BuildPrompt(DefectDetectionRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Product type: {r.ProductType}. Inspection stage: {r.Stage}.");
        sb.AppendLine("Look for these defect categories (use the code as the label):");
        foreach (var c in r.Categories)
            sb.AppendLine($"- {c.Code}: {c.Name}{(string.IsNullOrWhiteSpace(c.Description) ? "" : " — " + c.Description)}");
        sb.AppendLine("If you see a defect that fits none of them, use a short snake_case label of your own.");
        sb.AppendLine();
        sb.AppendLine("Respond with JSON only, exactly this shape:");
        sb.AppendLine("{\"defects\":[{\"label\":\"<code>\",\"confidence\":<0..1>,\"box_2d\":[ymin,xmin,ymax,xmax],\"severity\":\"minor|major|critical\",\"description\":\"<what you see, max 25 words>\"}]}");
        sb.AppendLine("box_2d uses integers 0-1000 relative to the image height (y) and width (x). Omit box_2d if you cannot locate the defect.");
        sb.AppendLine("confidence is your own estimate that the defect is really there.");
        sb.AppendLine("Report only defects you can actually see. If the piece has no visible defect, return {\"defects\":[]}.");
        return sb.ToString();
    }

    private const string SystemPrompt =
        "You are a garment quality-control inspector examining a photo from a production line. " +
        "You report visible manufacturing defects precisely and never invent defects that are not visible.";

    private HttpRequestMessage BuildRequest(string provider, string model, string key, DefectDetectionRequest r)
    {
        var b64 = Convert.ToBase64String(r.Image);
        var prompt = BuildPrompt(r);
        HttpRequestMessage msg;
        object body;
        switch (provider)
        {
            case "openai":
            {
                var baseUrl = (_settings.Startup.LlmBaseUrl ?? "https://api.openai.com").TrimEnd('/');
                msg = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions");
                msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                body = new
                {
                    model,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new { role = "system", content = SystemPrompt },
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "text", text = prompt },
                                new { type = "image_url", image_url = new { url = $"data:{r.ContentType};base64,{b64}", detail = "high" } },
                            },
                        },
                    },
                };
                break;
            }
            case "anthropic":
                msg = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                msg.Headers.Add("x-api-key", key);
                msg.Headers.Add("anthropic-version", AnthropicVersion);
                body = new
                {
                    model,
                    max_tokens = 2048,
                    system = SystemPrompt,
                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "image", source = new { type = "base64", media_type = r.ContentType, data = b64 } },
                                new { type = "text", text = prompt },
                            },
                        },
                    },
                };
                break;
            case "gemini":
                // The key goes in a header, never the URL, so it can't reach a log line.
                msg = new HttpRequestMessage(HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
                msg.Headers.Add("x-goog-api-key", key);
                body = new
                {
                    systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
                    contents = new object[]
                    {
                        new
                        {
                            role = "user",
                            parts = new object[]
                            {
                                new { inline_data = new { mime_type = r.ContentType, data = b64 } },
                                new { text = prompt },
                            },
                        },
                    },
                    generationConfig = new { responseMimeType = "application/json" },
                };
                break;
            default:
                throw new DefectDetectionException(DefectDetectionException.NotConfigured, $"Provider '{provider}' is not supported for vision.");
        }
        msg.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return msg;
    }

    /// <summary>The model's text and the provider-reported model version, per provider envelope.</summary>
    public static (string? Content, string? Version) ReadContent(string provider, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            switch (provider)
            {
                case "openai":
                {
                    var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                    return (content, root.TryGetProperty("model", out var m) ? m.GetString() : null);
                }
                case "anthropic":
                {
                    var text = string.Concat(root.GetProperty("content").EnumerateArray()
                        .Where(p => p.TryGetProperty("type", out var t) && t.GetString() == "text")
                        .Select(p => p.GetProperty("text").GetString()));
                    return (text, root.TryGetProperty("model", out var m) ? m.GetString() : null);
                }
                default:
                {
                    var parts = root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
                    var text = string.Concat(parts.EnumerateArray()
                        .Where(p => p.TryGetProperty("text", out _))
                        .Select(p => p.GetProperty("text").GetString()));
                    return (text, root.TryGetProperty("modelVersion", out var m) ? m.GetString() : null);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new DefectDetectionException(DefectDetectionException.InvalidOutput,
                $"The {provider} response did not have the expected structure.", ex);
        }
    }
}
