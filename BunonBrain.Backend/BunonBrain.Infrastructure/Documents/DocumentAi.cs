using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Documents;
using BunonBrain.Application.Settings;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Persistence;
using BunonBrain.Infrastructure.Qc;
using Microsoft.Extensions.DependencyInjection;

namespace BunonBrain.Infrastructure.Documents;

/// <summary>Who a call is for (usage log only).</summary>
public sealed record AiCallContext(Guid? FactoryId, string? EntityType, string? EntityId, Guid? UserId);

public sealed record AiImage(byte[] Content, string ContentType);

public sealed record AiJsonResult(JsonObject Json, string Provider, string Model);

/// <summary>
/// Structured-JSON calls to the external language model configured in
/// Settings → AI (provider and key from <see cref="IAiSettingsProvider"/>,
/// the same source Ask, QC and merchandising use; model from
/// <c>DOCUMENT_AI_MODEL</c> or the LLM model). No model is trained or hosted
/// here. Calls are retried only on time-outs, rate limits and server errors
/// (up to <c>DOCUMENT_AI_MAX_ATTEMPTS</c>, with back-off), every call is
/// written to <c>ai_api_usage</c> (purpose, outcome, attempts, tokens — never
/// the prompt, document text, response or key), and the answer must be a
/// JSON object or the call fails with <c>invalid_output</c>.
/// </summary>
public sealed class DocumentAiClient
{
    private const string AnthropicVersion = "2023-06-01";
    private readonly IAiSettingsProvider _settings;
    private readonly IHttpClientFactory _http;
    private readonly DocumentOptions _options;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<DocumentAiClient> _log;

    public DocumentAiClient(IAiSettingsProvider settings, IHttpClientFactory http, DocumentOptions options, IServiceScopeFactory scopes, ILogger<DocumentAiClient> log)
    {
        _settings = settings; _http = http; _options = options; _scopes = scopes; _log = log;
    }

    public DocAiStatus Status()
    {
        LlmSettings llm;
        try { llm = _settings.GetCurrent().Llm; }
        catch (AiSettingsUnavailableException) { return new DocAiStatus(false, null, null, "The AI settings could not be read."); }
        var model = _options.AiModel ?? llm.EffectiveModel;
        if (llm.Provider is null || !LlmSettings.SupportedProviders.Contains(llm.Provider))
            return new DocAiStatus(false, llm.Provider, model, "No AI provider is configured (Settings → AI). Deterministic extraction still works.");
        if (llm.ApiKey is null) return new DocAiStatus(false, llm.Provider, model, "The AI provider's API key is not set (Settings → AI).");
        if (string.IsNullOrWhiteSpace(model)) return new DocAiStatus(false, llm.Provider, null, "Set DOCUMENT_AI_MODEL or the LLM model.");
        return new DocAiStatus(true, llm.Provider, model, null);
    }

    public async Task<AiJsonResult> CallAsync(string purpose, string system, string user, IReadOnlyList<AiImage>? images, int maxTokens, AiCallContext ctx, CancellationToken ct)
    {
        var status = Status();
        if (!status.Configured) throw new DocumentExtractionException(DocumentExtractionException.NotConfigured, status.Reason!);
        var key = (await _settings.GetAsync(ct)).Llm.ApiKey!.Reveal();
        var provider = status.Provider!;
        var model = status.Model!;
        var sw = Stopwatch.StartNew();
        var attempts = 0;
        int? inTok = null, outTok = null;
        string outcome = "ok";
        try
        {
            while (true)
            {
                attempts++;
                try
                {
                    var (content, i, o) = await SendOnceAsync(provider, model, key, system, user, images, maxTokens, ct);
                    inTok = (inTok ?? 0) + (i ?? 0); outTok = (outTok ?? 0) + (o ?? 0);
                    return new AiJsonResult(ParseObject(content, provider), provider, model);
                }
                catch (DocumentExtractionException ex) when (Retryable(ex.Code) && attempts < _options.AiMaxAttempts && !ct.IsCancellationRequested)
                {
                    var wait = TimeSpan.FromSeconds(Math.Min(10, Math.Pow(2, attempts - 1)));
                    _log.LogInformation("Document AI call ({Purpose}) to {Provider} failed with {Code}; retry {Attempt} in {Wait}s.", purpose, provider, ex.Code, attempts + 1, wait.TotalSeconds);
                    await Task.Delay(wait, ct);
                }
            }
        }
        catch (DocumentExtractionException ex) { outcome = ex.Code; throw; }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        finally
        {
            await LogUsageAsync(new AiApiUsage
            {
                Purpose = purpose, Provider = provider, Model = model.Length > 120 ? model[..120] : model, Outcome = outcome, Attempts = attempts,
                DurationMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds), InputTokens = inTok, OutputTokens = outTok,
                InputChars = system.Length + user.Length, FactoryId = ctx.FactoryId, EntityType = ctx.EntityType, EntityId = ctx.EntityId, UserId = ctx.UserId,
            });
        }
    }

    private static bool Retryable(string code) => code is DocumentExtractionException.Timeout or DocumentExtractionException.RateLimited or "provider_unavailable";

    private async Task LogUsageAsync(AiApiUsage row)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
            db.AiApiUsage.Add(row);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) { _log.LogWarning(ex, "The AI usage record could not be written."); }
    }

    private async Task<(string? Content, int? InTokens, int? OutTokens)> SendOnceAsync(string provider, string model, string key, string system, string user,
        IReadOnlyList<AiImage>? images, int maxTokens, CancellationToken ct)
    {
        using var msg = Build(provider, model, key, system, user, images, maxTokens);
        var client = _http.CreateClient(nameof(DocumentAiClient));
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.AiTimeout);
        HttpResponseMessage resp;
        try { resp = await client.SendAsync(msg, timeout.Token); }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DocumentExtractionException(DocumentExtractionException.Timeout, $"The {provider} model did not answer within {_options.AiTimeout.TotalSeconds:F0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DocumentExtractionException("provider_unavailable", $"The {provider} API could not be reached.", ex);
        }
        using (resp)
        {
            var raw = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Status only: provider bodies can echo the request.
                _log.LogWarning("Document AI call to {Provider} failed: status={Status}.", provider, (int)resp.StatusCode);
                throw resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new DocumentExtractionException(DocumentExtractionException.Unauthorized, $"The {provider} API rejected the configured key."),
                    HttpStatusCode.TooManyRequests => new DocumentExtractionException(DocumentExtractionException.RateLimited, $"The {provider} API is rate-limiting requests. Try again later."),
                    >= HttpStatusCode.InternalServerError => new DocumentExtractionException("provider_unavailable", $"The {provider} API returned HTTP {(int)resp.StatusCode}."),
                    _ => new DocumentExtractionException(DocumentExtractionException.ProviderError, $"The {provider} API returned HTTP {(int)resp.StatusCode}."),
                };
            }
            string? content;
            try { (content, _) = LlmVisionDefectProvider.ReadContent(provider, raw); }
            catch (DefectDetectionException ex)
            {
                throw new DocumentExtractionException(DocumentExtractionException.InvalidOutput, $"The {provider} response did not have the expected structure.", ex);
            }
            var (i, o) = Tokens(provider, raw);
            return (content, i, o);
        }
    }

    public static (int? In, int? Out) Tokens(string provider, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            int? Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
            return provider switch
            {
                "openai" when root.TryGetProperty("usage", out var u) => (Get(u, "prompt_tokens"), Get(u, "completion_tokens")),
                "anthropic" when root.TryGetProperty("usage", out var u) => (Get(u, "input_tokens"), Get(u, "output_tokens")),
                "gemini" when root.TryGetProperty("usageMetadata", out var u) => (Get(u, "promptTokenCount"), Get(u, "candidatesTokenCount")),
                _ => (null, null),
            };
        }
        catch (JsonException) { return (null, null); }
    }

    /// <summary>The model's answer as a JSON object (a ```json fence is tolerated); anything else is invalid output.</summary>
    public static JsonObject ParseObject(string? content, string provider)
    {
        var text = content?.Trim() ?? "";
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = text.IndexOf('\n');
            text = nl < 0 ? "" : text[(nl + 1)..];
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) text = text[..end];
        }
        try
        {
            if (JsonNode.Parse(text) is JsonObject o) return o;
        }
        catch (JsonException) { }
        throw new DocumentExtractionException(DocumentExtractionException.InvalidOutput, $"The {provider} model did not answer with a JSON object.");
    }

    private HttpRequestMessage Build(string provider, string model, string key, string system, string user, IReadOnlyList<AiImage>? images, int maxTokens)
    {
        HttpRequestMessage msg;
        object body;
        var imgs = images ?? Array.Empty<AiImage>();
        switch (provider)
        {
            case "openai":
            {
                var baseUrl = (_settings.Startup.LlmBaseUrl ?? "https://api.openai.com").TrimEnd('/');
                msg = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions");
                msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
                var parts = new List<object> { new { type = "text", text = user } };
                parts.AddRange(imgs.Select(i => (object)new { type = "image_url", image_url = new { url = $"data:{i.ContentType};base64,{Convert.ToBase64String(i.Content)}", detail = "high" } }));
                body = new
                {
                    model, temperature = 0, max_tokens = maxTokens, response_format = new { type = "json_object" },
                    messages = new object[] { new { role = "system", content = system }, new { role = "user", content = imgs.Count == 0 ? (object)user : parts } },
                };
                break;
            }
            case "anthropic":
            {
                msg = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                msg.Headers.Add("x-api-key", key);
                msg.Headers.Add("anthropic-version", AnthropicVersion);
                var parts = imgs.Select(i => (object)new { type = "image", source = new { type = "base64", media_type = i.ContentType, data = Convert.ToBase64String(i.Content) } }).ToList();
                parts.Add(new { type = "text", text = user });
                body = new { model, max_tokens = maxTokens, temperature = 0, system, messages = new object[] { new { role = "user", content = parts } } };
                break;
            }
            case "gemini":
            {
                msg = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
                msg.Headers.Add("x-goog-api-key", key);
                var parts = new List<object> { new { text = user } };
                parts.AddRange(imgs.Select(i => (object)new { inline_data = new { mime_type = i.ContentType, data = Convert.ToBase64String(i.Content) } }));
                body = new
                {
                    systemInstruction = new { parts = new[] { new { text = system } } },
                    contents = new object[] { new { role = "user", parts } },
                    generationConfig = new { responseMimeType = "application/json", temperature = 0, maxOutputTokens = maxTokens },
                };
                break;
            }
            default:
                throw new DocumentExtractionException(DocumentExtractionException.NotConfigured, $"Provider '{provider}' is not supported.");
        }
        msg.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return msg;
    }
}

/// <summary>Values read from a file: field values / table rows, per-field method and confidence, and what was dropped.</summary>
public sealed record ExtractionOutcome(JsonObject Values, Dictionary<string, string> Methods, Dictionary<string, double> Confidence, List<string> Rejected, string Method, string? Model);

/// <summary>
/// Reads document fields from a file's text, deterministic first: labelled
/// values ("Invoice No: 123") and tables whose header row names the type's
/// columns (CSV / XLSX / DOCX tables). Only when required values are still
/// missing — and AI is allowed and configured — are those fields (and only
/// those) asked from the language model, with the text cut to
/// <c>DOCUMENT_AI_MAX_CHARS</c>. AI values whose text can't be found in the
/// document are rejected; images go to the model directly (OCR) and are
/// marked for review. Nothing here is applied to a document: the result is a
/// suggestion a person accepts.
/// </summary>
public sealed class DocumentExtractor
{
    public const string SystemPrompt =
        "You read factory documents (garment industry) and return ONLY a JSON object. Use only values that are written in the document. " +
        "Never guess, calculate or invent a value: leave out any field you cannot find. Dates as yyyy-MM-dd, numbers without units or thousands separators. " +
        "Answer: {\"fields\":{\"<key>\":<value>},\"tables\":{\"<key>\":[{\"<column>\":<value>}]},\"confidence\":{\"<key>\":0.0-1.0}}.";

    private readonly DocumentAiClient _ai;
    private readonly DocumentOptions _options;

    public DocumentExtractor(DocumentAiClient ai, DocumentOptions options) { _ai = ai; _options = options; }

    public static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "");

    /// <summary>Deterministic pass over the text.</summary>
    public static JsonObject Rules(DocumentTypeDef type, string text, Dictionary<string, string> methods)
    {
        var result = new JsonObject();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var f in type.Fields.Where(f => !f.Computed && f.Kind != FieldKind.Table))
        {
            var names = new[] { f.Label, f.Key, Regex.Replace(f.Key, "([a-z])([A-Z])", "$1 $2") }.Select(Norm).Where(n => n.Length >= 2).Distinct().ToArray();
            foreach (var line in lines)
            {
                var m = Regex.Match(line, @"^\s*([^:\t=]{2,60}?)\s*(?::|\t|=|\s-\s)\s*(.+?)\s*$");
                if (!m.Success) continue;
                if (!names.Contains(Norm(m.Groups[1].Value))) continue;
                var value = m.Groups[2].Value.Split('\t').First().Trim();
                if (value.Length == 0) continue;
                result[f.Key] = value;
                methods[f.Key] = "rules";
                break;
            }
        }
        foreach (var t in type.Fields.Where(f => f.Kind == FieldKind.Table && !f.Computed))
        {
            var cols = t.Columns!.Where(c => !c.Computed).ToList();
            for (var i = 0; i < lines.Length; i++)
            {
                var cells = Split(lines[i]);
                if (cells.Length < 2) continue;
                var map = new Dictionary<int, FieldDef>();
                for (var c = 0; c < cells.Length; c++)
                {
                    var n = Norm(cells[c]);
                    var col = cols.FirstOrDefault(x => n.Length > 0 && (Norm(x.Label) == n || Norm(x.Key) == n));
                    if (col is not null && !map.ContainsValue(col)) map[c] = col;
                }
                if (map.Count < Math.Min(2, cols.Count)) continue;
                var rows = new JsonArray();
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var rc = Split(lines[j]);
                    if (rc.All(string.IsNullOrWhiteSpace)) break;
                    var row = new JsonObject();
                    foreach (var (c, col) in map) if (c < rc.Length && !string.IsNullOrWhiteSpace(rc[c])) row[col.Key] = rc[c].Trim();
                    if (row.Count > 0) rows.Add(row);
                    if (rows.Count >= 1000) break;
                }
                if (rows.Count > 0) { result[t.Key] = rows; methods[t.Key] = "rules"; break; }
            }
        }
        return result;
    }

    private static string[] Split(string line) => line.Contains('\t') ? line.Split('\t') : line.Contains(';') && !line.Contains(',') ? line.Split(';') : line.Split(',');

    /// <summary>Required (or all, when nothing was found) fields still empty after the deterministic pass: the only ones sent to the model.</summary>
    public static List<FieldDef> Wanted(DocumentTypeDef type, JsonObject found)
    {
        var editable = type.Fields.Where(f => !f.Computed).ToList();
        var missingRequired = editable.Where(f => f.Required && DocJson.Empty(found[f.Key])).ToList();
        if (missingRequired.Count > 0) return editable.Where(f => DocJson.Empty(found[f.Key])).ToList();
        return new List<FieldDef>();
    }

    public static string Schema(IEnumerable<FieldDef> fields)
    {
        var sb = new StringBuilder();
        foreach (var f in fields)
        {
            if (f.Kind == FieldKind.Table)
                sb.Append($"- table \"{f.Key}\" ({f.Label}), rows with columns: ")
                  .AppendJoin(", ", f.Columns!.Where(c => !c.Computed).Select(c => $"\"{c.Key}\" ({c.Label}, {Kind(c)})")).Append('\n');
            else sb.Append($"- field \"{f.Key}\" ({f.Label}, {Kind(f)})\n");
        }
        return sb.ToString();
    }

    private static string Kind(FieldDef f) => f.Kind switch
    {
        FieldKind.Select => "one of " + string.Join("|", f.Options ?? Array.Empty<string>()),
        FieldKind.Date => "date yyyy-MM-dd",
        FieldKind.Number => "number",
        FieldKind.Integer => "whole number",
        FieldKind.Boolean => "true/false",
        _ => "text",
    };

    public async Task<ExtractionOutcome> ExtractAsync(DocumentTypeDef type, string? text, AiImage? image, bool allowAi, AiCallContext ctx, CancellationToken ct)
    {
        var methods = new Dictionary<string, string>();
        var confidence = new Dictionary<string, double>();
        var rejected = new List<string>();
        var values = text is null ? new JsonObject() : Rules(type, text, methods);
        var method = "rules";
        string? model = null;

        var wanted = image is not null ? type.Fields.Where(f => !f.Computed).ToList() : Wanted(type, values);
        if (wanted.Count > 0 && allowAi)
        {
            if (image is null && text is null) return new ExtractionOutcome(values, methods, confidence, rejected, method, model);
            var body = new StringBuilder($"Document type: {type.Name}.\nReturn these fields if present:\n{Schema(wanted)}");
            var sent = "";
            if (text is not null)
            {
                sent = text.Length > _options.AiMaxChars ? text[.._options.AiMaxChars] : text;
                body.Append("\nDocument text:\n<<<\n").Append(sent).Append("\n>>>");
            }
            else body.Append("\nThe document is the attached image: read its text (OCR) and extract the fields.");
            var result = await _ai.CallAsync(image is null ? "document.extract" : "document.extract_image", SystemPrompt, body.ToString(),
                image is null ? null : new[] { image }, 4096, ctx, ct);
            method = image is null ? "rules+ai" : "ai_ocr";
            model = $"{result.Provider}/{result.Model}";
            var aiValues = new JsonObject();
            if (result.Json["fields"] is JsonObject fo) foreach (var kv in fo) if (kv.Value is not null) aiValues[kv.Key] = kv.Value.DeepClone();
            if (result.Json["tables"] is JsonObject to) foreach (var kv in to) if (kv.Value is not null) aiValues[kv.Key] = kv.Value.DeepClone();
            if (result.Json["confidence"] is JsonObject co)
                foreach (var kv in co)
                    if (DocJson.Dec(kv.Value) is { } c && c is >= 0 and <= 1) confidence[kv.Key] = (double)c;
            var wantedKeys = wanted.Select(w => w.Key).ToHashSet();
            var normText = text is null ? null : Norm(text);
            foreach (var kv in aiValues.ToList())
            {
                if (!wantedKeys.Contains(kv.Key)) { rejected.Add($"{kv.Key}: not requested"); continue; }
                if (normText is not null && !Grounded(kv.Value, normText)) { rejected.Add($"{kv.Key}: not found in the document text"); continue; }
                values[kv.Key] = kv.Value!.DeepClone();
                methods[kv.Key] = image is null ? "ai" : "ai_ocr";
            }
        }
        var dropped = new List<string>();
        var clean = DocumentRules.Sanitize(type, values, keepComputed: false, dropped);
        rejected.AddRange(dropped.Select(d => $"{d}: not a field of {type.Name}"));
        return new ExtractionOutcome(clean, methods, confidence, rejected, method, model);
    }

    /// <summary>A value is grounded when its text (letters and digits) occurs in the document; table rows need most of their cells grounded.</summary>
    public static bool Grounded(JsonNode? value, string normText)
    {
        switch (value)
        {
            case JsonArray rows:
                return rows.OfType<JsonObject>().All(r =>
                {
                    var cells = r.Select(kv => kv.Value).Where(v => v is JsonValue).ToList();
                    return cells.Count == 0 || cells.Count(v => Grounded(v, normText)) * 2 >= cells.Count;
                });
            case JsonValue:
                var s = DocJson.Str(value);
                if (s is null) return true;
                if (s is "true" or "false") return true;
                var n = Norm(s);
                if (n.Length == 0) return true;
                if (normText.Contains(n, StringComparison.Ordinal)) return true;
                // Numbers may be written with separators or trailing zeros ("1,200.00" vs 1200).
                if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                {
                    var plain = Norm(d.ToString("0.############", CultureInfo.InvariantCulture));
                    return normText.Contains(plain, StringComparison.Ordinal);
                }
                // Dates may be written another way (16/11/2026, 16 Nov 2026): accept when day, month-year digits occur.
                if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return normText.Contains(date.Year.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                return false;
            default:
                return true;
        }
    }

    /// <summary>Deterministic type suggestion from the catalog keywords; scores are hits weighted by keyword length.</summary>
    public static IReadOnlyList<DocTypeScore> ScoreTypes(IEnumerable<DocumentTypeDef> types, string text)
    {
        var lower = " " + Regex.Replace(text.ToLowerInvariant(), @"\s+", " ") + " ";
        var scores = new List<DocTypeScore>();
        foreach (var t in types)
        {
            double score = 0;
            foreach (var k in (t.Keywords ?? Array.Empty<string>()).Append(t.Name.ToLowerInvariant()))
            {
                var hits = Regex.Matches(lower, @"\b" + Regex.Escape(k.ToLowerInvariant()) + @"\b").Count;
                if (hits > 0) score += Math.Min(3, hits) * k.Length;
            }
            if (score > 0) scores.Add(new DocTypeScore(t.Code, score));
        }
        var total = scores.Sum(s => s.Score);
        return scores.Select(s => s with { Score = Math.Round(s.Score / total, 3) }).OrderByDescending(s => s.Score).Take(5).ToList();
    }
}
