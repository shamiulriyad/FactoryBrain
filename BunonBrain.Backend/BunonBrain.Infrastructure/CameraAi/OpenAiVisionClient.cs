using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BunonBrain.Application.CameraAi;
using BunonBrain.Application.Settings;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Documents;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace BunonBrain.Infrastructure.CameraAi;

/// <summary>Whether camera AI can call OpenAI, and with which model (never the key).</summary>
public sealed record CameraAiStatus(bool Configured, string Model, string? KeySource, string? Reason);

/// <summary>Who a call is for (usage log only).</summary>
public sealed record VisionCallContext(Guid FactoryId, Guid SessionId, Guid UserId);

/// <summary>A successful call: the model's JSON answer and what it cost.</summary>
public sealed record VisionCallResult(JsonObject Output, string Model, int Attempts, int? InputTokens, int? OutputTokens, long DurationMs);

/// <summary>A failed call with a stable code: not_configured, unauthorized, quota_exceeded, rate_limited, timeout, unavailable, refused, invalid_output, provider_error.</summary>
public sealed class CameraVisionException : Exception
{
    public string Code { get; }
    public int Attempts { get; }
    public long DurationMs { get; }
    /// <summary>Tokens the failed call still used (a refused or cut-off answer is billed).</summary>
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public CameraVisionException(string code, string message, int attempts = 0, long durationMs = 0, Exception? inner = null) : base(message, inner)
    {
        Code = code; Attempts = attempts; DurationMs = durationMs;
    }

    public bool Retryable => Code is "rate_limited" or "timeout" or "unavailable";
}

/// <summary>Analyses one camera frame. The OpenAI implementation is <see cref="OpenAiVisionClient"/>; tests use fakes.</summary>
public interface ICameraVisionAnalyzer
{
    CameraAiStatus Status();
    Task<VisionCallResult> AnalyzeAsync(byte[] jpeg, VisionCallContext ctx, CancellationToken ct);
}

/// <summary>
/// The OpenAI vision call for camera AI: Chat Completions with the frame as
/// an image input and a strict JSON schema (Structured Outputs), so the answer
/// can only use the contract's labels. Retries only time-outs, rate limits
/// and server errors, up to <c>CAMERA_AI_MAX_ATTEMPTS</c>; every call (with
/// its retries) is written to <c>ai_api_usage</c> as <c>camera.vision</c>
/// with outcome and token counts — never the image, the prompt or the key.
/// Provider error bodies are not logged (they can echo the request).
/// </summary>
public sealed class OpenAiVisionClient : ICameraVisionAnalyzer
{
    public const string HttpClientName = "camera-ai";
    private static readonly string SchemaJson = CameraAiContract.Schema().ToJsonString();

    private readonly CameraAiOptions _options;
    private readonly IAiSettingsProvider _settings;
    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OpenAiVisionClient> _log;

    public OpenAiVisionClient(CameraAiOptions options, IAiSettingsProvider settings, IHttpClientFactory http, IServiceScopeFactory scopes, ILogger<OpenAiVisionClient> log)
    {
        _options = options; _settings = settings; _http = http; _scopes = scopes; _log = log;
    }

    public CameraAiStatus Status()
    {
        if (!_options.Enabled) return new CameraAiStatus(false, _options.Model, null, "Camera AI is switched off (CAMERA_AI_ENABLED=false).");
        if (_options.ApiKey is not null) return new CameraAiStatus(true, _options.Model, "CAMERA_AI_OPENAI_API_KEY", null);
        try
        {
            var llm = _settings.GetCurrent().Llm;
            if (llm.Provider == "openai" && llm.ApiKey is not null) return new CameraAiStatus(true, _options.Model, "Settings → AI (OpenAI)", null);
        }
        catch (AiSettingsUnavailableException) { return new CameraAiStatus(false, _options.Model, null, "The AI settings could not be read."); }
        return new CameraAiStatus(false, _options.Model, null,
            "No OpenAI key: set CAMERA_AI_OPENAI_API_KEY on the API, or choose OpenAI with a key under Settings → AI.");
    }

    private async Task<(string Key, string BaseUrl)> CredentialsAsync(CancellationToken ct)
    {
        if (_options.ApiKey is not null) return (_options.ApiKey, _options.BaseUrl ?? "https://api.openai.com");
        var llm = (await _settings.GetAsync(ct)).Llm;
        if (llm.Provider != "openai" || llm.ApiKey is null)
            throw new CameraVisionException("not_configured", Status().Reason ?? "Camera AI is not configured.");
        return (llm.ApiKey.Reveal(), _options.BaseUrl ?? (_settings.Startup.LlmBaseUrl ?? "https://api.openai.com").TrimEnd('/'));
    }

    public async Task<VisionCallResult> AnalyzeAsync(byte[] jpeg, VisionCallContext ctx, CancellationToken ct)
    {
        var status = Status();
        if (!status.Configured) throw new CameraVisionException("not_configured", status.Reason!);
        var (key, baseUrl) = await CredentialsAsync(ct);
        var sw = Stopwatch.StartNew();
        var attempts = 0;
        int? inTok = null, outTok = null;
        var outcome = "ok";
        try
        {
            while (true)
            {
                attempts++;
                try
                {
                    var (output, i, o) = await SendOnceAsync(baseUrl, key, jpeg, ct);
                    inTok = (inTok ?? 0) + (i ?? 0); outTok = (outTok ?? 0) + (o ?? 0);
                    return new VisionCallResult(output, _options.Model, attempts, inTok, outTok, sw.ElapsedMilliseconds);
                }
                catch (CameraVisionException ex) when (ex.Retryable && attempts < _options.MaxAttempts && !ct.IsCancellationRequested)
                {
                    // Short back-off: a newer frame is waiting, so an old one is not worth a long wait.
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempts), ct);
                }
            }
        }
        catch (CameraVisionException ex)
        {
            outcome = ex.Code;
            if (ex.InputTokens is not null || ex.OutputTokens is not null)
            {
                inTok = (inTok ?? 0) + (ex.InputTokens ?? 0); outTok = (outTok ?? 0) + (ex.OutputTokens ?? 0);
            }
            throw new CameraVisionException(ex.Code, ex.Message, attempts, sw.ElapsedMilliseconds, ex.InnerException)
            { InputTokens = inTok, OutputTokens = outTok };
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        finally
        {
            await LogUsageAsync(new AiApiUsage
            {
                Purpose = CameraAiContract.Purpose, Provider = "openai", Model = _options.Model.Length > 120 ? _options.Model[..120] : _options.Model,
                Outcome = outcome.Length > 40 ? outcome[..40] : outcome, Attempts = Math.Max(1, attempts), DurationMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds),
                InputTokens = inTok, OutputTokens = outTok, InputChars = CameraAiContract.SystemPrompt.Length + CameraAiContract.UserPrompt.Length,
                FactoryId = ctx.FactoryId, EntityType = "camera_ai_session", EntityId = ctx.SessionId.ToString(), UserId = ctx.UserId,
            });
        }
    }

    /// <summary>The request body (public for tests: proves the schema, image input and limits that are sent).</summary>
    public JsonObject BuildBody(byte[] jpeg)
    {
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_completion_tokens"] = _options.MaxOutputTokens,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = CameraAiContract.SchemaName, ["strict"] = true, ["schema"] = JsonNode.Parse(SchemaJson) },
            },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = CameraAiContract.SystemPrompt },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = CameraAiContract.UserPrompt },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg), ["detail"] = _options.ImageDetail },
                        }),
                }),
        };
        if (_options.ReasoningEffort is { } effort) body["reasoning_effort"] = effort;
        return body;
    }

    private async Task<(JsonObject Output, int? In, int? Out)> SendOnceAsync(string baseUrl, string key, byte[] jpeg, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/chat/completions");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        msg.Content = new StringContent(BuildBody(jpeg).ToJsonString(), Encoding.UTF8, "application/json");
        var client = _http.CreateClient(HttpClientName);
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);
        HttpResponseMessage resp;
        try { resp = await client.SendAsync(msg, timeout.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CameraVisionException("timeout", $"OpenAI did not answer within {_options.Timeout.TotalSeconds:F0} seconds.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new CameraVisionException("unavailable", "The OpenAI API could not be reached.", inner: ex);
        }
        using (resp)
        {
            string raw;
            try { raw = await resp.Content.ReadAsStringAsync(timeout.Token); }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new CameraVisionException("timeout", $"OpenAI did not answer within {_options.Timeout.TotalSeconds:F0} seconds.", inner: ex);
            }
            if (!resp.IsSuccessStatusCode) throw Failure(resp.StatusCode, raw);
            var (i, o) = DocumentAiClient.Tokens("openai", raw);
            try { return (ReadAnswer(raw), i, o); }
            catch (CameraVisionException ex)
            {
                throw new CameraVisionException(ex.Code, ex.Message, inner: ex.InnerException) { InputTokens = i, OutputTokens = o };
            }
        }
    }

    private CameraVisionException Failure(HttpStatusCode status, string raw)
    {
        // Only OpenAI's error code / type / param are logged: the message can echo request content.
        string? code = null, type = null, param = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                code = e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                param = e.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            }
        }
        catch (JsonException) { }
        _log.LogWarning("Camera AI call to OpenAI failed: status={Status} code={Code} type={Type} param={Param}.", (int)status, code, type, param);
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new CameraVisionException("unauthorized", "OpenAI rejected the configured API key."),
            HttpStatusCode.TooManyRequests when code == "insufficient_quota" || type == "insufficient_quota" =>
                new CameraVisionException("quota_exceeded", "The OpenAI account has no quota left (billing)."),
            HttpStatusCode.TooManyRequests => new CameraVisionException("rate_limited", "OpenAI is rate-limiting requests."),
            >= HttpStatusCode.InternalServerError => new CameraVisionException("unavailable", $"OpenAI returned HTTP {(int)status}."),
            HttpStatusCode.NotFound when code == "model_not_found" => new CameraVisionException("provider_error", $"OpenAI does not offer the model {_options.Model} to this key (CAMERA_AI_MODEL)."),
            _ => new CameraVisionException("provider_error",
                param is null ? $"OpenAI refused the request (HTTP {(int)status})." : $"OpenAI refused the request parameter '{param}' (HTTP {(int)status}). Check CAMERA_AI_* settings."),
        };
    }

    /// <summary>The structured answer, or a refusal / truncation / malformed answer as a failure.</summary>
    public static JsonObject ReadAnswer(string raw)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(raw); }
        catch (JsonException ex) { throw new CameraVisionException("invalid_output", "OpenAI's response was not JSON.", inner: ex); }
        var choice = root?["choices"]?[0];
        var message = choice?["message"];
        if (message is null) throw new CameraVisionException("invalid_output", "OpenAI's response had no message.");
        if (message["refusal"] is JsonValue r && r.TryGetValue<string>(out var refusal) && !string.IsNullOrWhiteSpace(refusal))
            throw new CameraVisionException("refused", "The model declined to analyse this frame.");
        if (choice?["finish_reason"] is JsonValue f && f.TryGetValue<string>(out var finish) && finish == "length")
            throw new CameraVisionException("invalid_output", "The answer was cut off (CAMERA_AI_MAX_OUTPUT_TOKENS too low for this model).");
        if (message["content"] is not JsonValue cv || !cv.TryGetValue<string>(out var content) || string.IsNullOrWhiteSpace(content))
            throw new CameraVisionException("invalid_output", "The model's answer was empty.");
        try
        {
            if (JsonNode.Parse(content) is JsonObject o) return o;
        }
        catch (JsonException) { }
        throw new CameraVisionException("invalid_output", "The model did not answer with a JSON object.");
    }

    private async Task LogUsageAsync(AiApiUsage row)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
            db.AiApiUsage.Add(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) { _log.LogWarning(ex, "The camera AI usage record could not be written."); }
    }
}
