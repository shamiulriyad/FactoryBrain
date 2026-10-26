using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Merchandising;
using FactoryBrain.Application.Settings;
using FactoryBrain.Infrastructure.Qc;

namespace FactoryBrain.Infrastructure.Merchandising;

/// <summary>The deterministic label / table parser (<see cref="RuleBasedFieldExtractor"/>). Not AI; reports no confidence.</summary>
public sealed class RulesExtractionProvider : IDocumentExtractionProvider
{
    public ExtractionProviderStatus Describe() =>
        new(true, MerchOptions.ModeRules, RuleBasedFieldExtractor.ProviderName, RuleBasedFieldExtractor.ModelName, false,
            "Rule-based extraction of labelled values and colour × size tables. It is not AI: unlabelled values are not found.");

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct) =>
        Task.FromResult(new ExtractionResult(RuleBasedFieldExtractor.ProviderName, RuleBasedFieldExtractor.ModelName,
            RuleBasedFieldExtractor.ModelVersion, RuleBasedFieldExtractor.Extract(request.Pages)));
}

/// <summary>
/// Schema-constrained extraction with the language model configured in the
/// AI settings (provider and key from <see cref="IAiSettingsProvider"/>, the
/// same source Ask and QC use; model from <c>MERCH_EXTRACTION_MODEL</c> or the
/// LLM model). The model must answer the JSON contract of
/// <see cref="ExtractionOutputParser"/>; its quotes are then checked against
/// the document text. The confidence is the model's own estimate, not a
/// calibrated probability. Neither the document text nor the response body
/// is logged.
/// </summary>
public sealed class LlmDocumentExtractionProvider : IDocumentExtractionProvider
{
    private const string AnthropicVersion = "2023-06-01";

    private readonly IAiSettingsProvider _settings;
    private readonly IHttpClientFactory _http;
    private readonly MerchOptions _options;
    private readonly ILogger<LlmDocumentExtractionProvider> _log;

    public LlmDocumentExtractionProvider(IAiSettingsProvider settings, IHttpClientFactory http, MerchOptions options, ILogger<LlmDocumentExtractionProvider> log)
    {
        _settings = settings; _http = http; _options = options; _log = log;
    }

    public ExtractionProviderStatus Describe()
    {
        LlmSettings llm;
        try { llm = _settings.GetCurrent().Llm; }
        catch (AiSettingsUnavailableException)
        {
            return new ExtractionProviderStatus(false, MerchOptions.ModeLlm, null, null, true, "The AI settings store could not be read.");
        }
        var model = _options.ExtractionModel ?? llm.EffectiveModel;
        if (llm.Provider is null || !LlmSettings.SupportedProviders.Contains(llm.Provider))
            return new ExtractionProviderStatus(false, MerchOptions.ModeLlm, llm.Provider, model, true,
                "MERCH_EXTRACTION_PROVIDER=llm, but no language-model provider is configured (Settings → AI or LLM_PROVIDER).");
        if (llm.ApiKey is null)
            return new ExtractionProviderStatus(false, MerchOptions.ModeLlm, llm.Provider, model, true,
                "MERCH_EXTRACTION_PROVIDER=llm, but the language-model API key is not set.");
        if (string.IsNullOrWhiteSpace(model))
            return new ExtractionProviderStatus(false, MerchOptions.ModeLlm, llm.Provider, null, true,
                "Set MERCH_EXTRACTION_MODEL (or LLM_MODEL) to a model for this provider.");
        return new ExtractionProviderStatus(true, MerchOptions.ModeLlm, llm.Provider, model, true, null);
    }

    public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct)
    {
        var status = Describe();
        if (!status.Configured) throw new DocumentExtractionException(DocumentExtractionException.NotConfigured, status.Reason!);
        var llm = (await _settings.GetAsync(ct)).Llm;
        var key = llm.ApiKey!.Reveal();
        var provider = status.Provider!;
        var model = status.Model!;
        var prompt = ExtractionOutputParser.BuildPrompt(request, _options.MaxPromptChars, out var truncated);

        using var http = BuildRequest(provider, model, key, prompt);
        var client = _http.CreateClient(nameof(LlmDocumentExtractionProvider));
        client.Timeout = Timeout.InfiniteTimeSpan; // the linked token below enforces MERCH_EXTRACTION_TIMEOUT_SECONDS
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
            throw new DocumentExtractionException(DocumentExtractionException.Timeout,
                $"The {provider} model did not answer within {_options.Timeout.TotalSeconds:F0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("Document extraction call to {Provider} failed before a response ({ErrorType}).", provider, ex.GetType().Name);
            throw new DocumentExtractionException(DocumentExtractionException.ProviderError, $"The {provider} API could not be reached.", ex);
        }

        using (resp)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Status only: provider bodies can echo the request (document text).
                _log.LogWarning("Document extraction call to {Provider} failed: status={Status}.", provider, (int)resp.StatusCode);
                throw resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new DocumentExtractionException(DocumentExtractionException.Unauthorized,
                        $"The {provider} API rejected the configured key ({(int)resp.StatusCode})."),
                    HttpStatusCode.TooManyRequests => new DocumentExtractionException(DocumentExtractionException.RateLimited,
                        $"The {provider} API is rate-limiting requests (429)" +
                        (resp.Headers.RetryAfter?.Delta is { } d ? $"; it asked to wait {d.TotalSeconds:F0} s" : "") + ". Retry later."),
                    _ => new DocumentExtractionException(DocumentExtractionException.ProviderError, $"The {provider} API returned HTTP {(int)resp.StatusCode}."),
                };
            }
            string? content, version;
            try { (content, version) = LlmVisionDefectProvider.ReadContent(provider, raw); }
            catch (DefectDetectionException ex)
            {
                throw new DocumentExtractionException(DocumentExtractionException.InvalidOutput, $"The {provider} response did not have the expected structure.", ex);
            }
            var fields = ExtractionOutputParser.Parse(content);
            _log.LogInformation("Document extraction call to {Provider} model={Model} returned {Count} field(s) in {Ms} ms.",
                provider, model, fields.Count, sw.ElapsedMilliseconds);
            return new ExtractionResult(provider, model, version, fields, truncated);
        }
    }

    private HttpRequestMessage BuildRequest(string provider, string model, string key, string prompt)
    {
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
                    temperature = 0,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new { role = "system", content = ExtractionOutputParser.SystemPrompt },
                        new { role = "user", content = prompt },
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
                    max_tokens = 4096,
                    temperature = 0,
                    system = ExtractionOutputParser.SystemPrompt,
                    messages = new object[] { new { role = "user", content = prompt } },
                };
                break;
            case "gemini":
                // The key goes in a header, never the URL, so it can't reach a log line.
                msg = new HttpRequestMessage(HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
                msg.Headers.Add("x-goog-api-key", key);
                body = new
                {
                    systemInstruction = new { parts = new[] { new { text = ExtractionOutputParser.SystemPrompt } } },
                    contents = new object[] { new { role = "user", parts = new object[] { new { text = prompt } } } },
                    generationConfig = new { responseMimeType = "application/json", temperature = 0 },
                };
                break;
            default:
                throw new DocumentExtractionException(DocumentExtractionException.NotConfigured, $"Provider '{provider}' is not supported for extraction.");
        }
        msg.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return msg;
    }
}

/// <summary>
/// The provider selected by <c>MERCH_EXTRACTION_PROVIDER</c>. With none (or
/// an invalid setting) every call fails with <c>provider_not_configured</c>;
/// it never returns a result.
/// </summary>
public sealed class ConfiguredExtractionProvider : IDocumentExtractionProvider
{
    private readonly MerchOptions _options;
    private readonly IDocumentExtractionProvider? _inner;

    public ConfiguredExtractionProvider(MerchOptions options, IServiceProvider sp)
    {
        _options = options;
        _inner = options.Mode switch
        {
            MerchOptions.ModeLlm => ActivatorUtilities.CreateInstance<LlmDocumentExtractionProvider>(sp),
            MerchOptions.ModeRules => new RulesExtractionProvider(),
            _ => null,
        };
    }

    public ExtractionProviderStatus Describe() => _inner?.Describe()
        ?? new ExtractionProviderStatus(false, MerchOptions.ModeNone, null, null, false,
            _options.ConfigurationError ?? "Automatic extraction is turned off (MERCH_EXTRACTION_PROVIDER=none). Documents are parsed; reviewers enter the values.");

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct) => _inner is null
        ? throw new DocumentExtractionException(DocumentExtractionException.NotConfigured, Describe().Reason!)
        : _inner.ExtractAsync(request, ct);
}
