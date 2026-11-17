using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Qc;
using BunonBrain.Application.Settings;

namespace BunonBrain.Infrastructure.Qc;

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

        // One deadline (QC_VISION_TIMEOUT_SECONDS) for the whole analysis; the
        // whole-photo pass and the zoomed tile passes run in parallel under it.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);
        var sw = Stopwatch.StartNew();

        var tiles = CropTiles(request);
        var wholeTask = CallAsync(provider, model, key, request.Image, request.ContentType, BuildPrompt(request), ct, timeout.Token);
        var tileTasks = tiles
            .Select(t => CallTileAsync(provider, model, key, request, t.Box, t.Name, t.Jpeg, ct, timeout.Token))
            .ToList();

        IReadOnlyList<DetectedDefect> whole;
        string? version;
        try
        {
            (whole, version) = await wholeTask;
        }
        catch
        {
            // The whole-photo pass decides success; stop the tiles before failing.
            await timeout.CancelAsync();
            await Task.WhenAll(tileTasks);
            throw;
        }
        var tileDefects = await Task.WhenAll(tileTasks);
        var merged = QcTiling.Merge(whole, tileDefects.SelectMany(d => d));

        _log.LogInformation(
            "QC vision call to {Provider} model={Model}: whole photo {Whole} defect(s), {Tiles} tile(s) {TileCount} defect(s), {Merged} after merging, {Ms} ms.",
            provider, model, whole.Count, tiles.Count, tileDefects.Sum(d => d.Count), merged.Count, sw.ElapsedMilliseconds);
        return new DefectDetectionResult(provider, model, version, LocalizationSupported: true, merged);
    }

    /// <summary>Smallest photo side (pixels) worth tiling; below it the crops add nothing.</summary>
    private const int MinTileSourcePixels = 320;

    /// <summary>
    /// The overlapping N×N crops of the photo (QC_VISION_TILE_GRID), as JPEG,
    /// with their exact position in whole-image coordinates. Empty when tiling
    /// is off, the photo is small or it can't be decoded (the whole-photo pass
    /// still runs).
    /// </summary>
    private IReadOnlyList<(NormalizedBox Box, string Name, byte[] Jpeg)> CropTiles(DefectDetectionRequest r)
    {
        var grid = _options.TileGrid;
        if (grid <= 1) return Array.Empty<(NormalizedBox, string, byte[])>();
        try
        {
            using var bitmap = SKBitmap.Decode(r.Image);
            if (bitmap is null || Math.Min(bitmap.Width, bitmap.Height) < MinTileSourcePixels)
                return Array.Empty<(NormalizedBox, string, byte[])>();

            var (w, h) = (bitmap.Width, bitmap.Height);
            var result = new List<(NormalizedBox, string, byte[])>();
            var tiles = QcTiling.Tiles(grid);
            for (var i = 0; i < tiles.Count; i++)
            {
                var t = tiles[i];
                var left = (int)Math.Floor(t.X * w);
                var top = (int)Math.Floor(t.Y * h);
                var right = Math.Min(w, (int)Math.Ceiling((t.X + t.Width) * w));
                var bottom = Math.Min(h, (int)Math.Ceiling((t.Y + t.Height) * h));
                if (right - left < 16 || bottom - top < 16) continue;

                using var crop = new SKBitmap();
                if (!bitmap.ExtractSubset(crop, new SKRectI(left, top, right, bottom))) continue;
                using var image = SKImage.FromBitmap(crop);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                if (data is null) continue;
                // The exact pixel crop, so boxes map back without rounding drift.
                var box = new NormalizedBox((double)left / w, (double)top / h, (double)(right - left) / w, (double)(bottom - top) / h);
                result.Add((box, QcTiling.Describe(i, grid), data.ToArray()));
            }
            return result;
        }
        catch (Exception ex)
        {
            _log.LogWarning("QC tiling skipped: the photo could not be cropped ({ErrorType}).", ex.GetType().Name);
            return Array.Empty<(NormalizedBox, string, byte[])>();
        }
    }

    /// <summary>One zoomed crop. Never throws: a failed tile only loses that tile's extra findings.</summary>
    private async Task<IReadOnlyList<DetectedDefect>> CallTileAsync(
        string provider, string model, string key, DefectDetectionRequest request,
        NormalizedBox tile, string name, byte[] jpeg, CancellationToken ct, CancellationToken deadline)
    {
        try
        {
            var (defects, _) = await CallAsync(provider, model, key, jpeg, "image/jpeg", BuildPrompt(request, name), ct, deadline);
            return defects.Select(d => QcTiling.ToWholeImage(d, tile)).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning("QC vision tile '{Tile}' skipped ({ErrorType}).", name, ex.GetType().Name);
            return Array.Empty<DetectedDefect>();
        }
    }

    /// <summary>One model call: send the image and prompt, return the parsed defects and the model version.</summary>
    private async Task<(IReadOnlyList<DetectedDefect> Defects, string? Version)> CallAsync(
        string provider, string model, string key, byte[] image, string contentType, string prompt,
        CancellationToken ct, CancellationToken deadline)
    {
        using var http = BuildRequest(provider, model, key, image, contentType, prompt);
        var client = _http.CreateClient(nameof(LlmVisionDefectProvider));
        client.Timeout = Timeout.InfiniteTimeSpan; // the deadline token enforces QC_VISION_TIMEOUT_SECONDS

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(http, deadline);
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
            return (QcDetectionNormalizer.ParseVisionModelJson(content), version);
        }
    }

    /// <param name="region">For a zoomed crop: which part of the photo it is (e.g. "top-left"); null for the whole photo.</param>
    public static string BuildPrompt(DefectDetectionRequest r, string? region = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Product type: {r.ProductType}. Inspection stage: {r.Stage}.");
        if (region is null)
        {
            sb.AppendLine("Inspect the WHOLE photo systematically, region by region (top-left to bottom-right), not only the most obvious spot. " +
                          "A piece often has several defects: report EVERY separate defect as its own entry, including several of the same type. " +
                          "Never merge defects that are apart into one entry, and do not stop after the first one.");
        }
        else
        {
            sb.AppendLine($"This image is a zoomed crop of the {region} part of a larger photo of the piece. " +
                          "Inspect all of it closely and report EVERY separate defect visible in this crop as its own entry; " +
                          "box_2d is relative to this crop. For a defect cut off by the crop edge, box the visible part.");
        }
        sb.AppendLine("Look for these defect categories (use the code as the label):");
        foreach (var c in r.Categories)
            sb.AppendLine($"- {c.Code}: {c.Name}{(string.IsNullOrWhiteSpace(c.Description) ? "" : " — " + c.Description)}");
        sb.AppendLine("If you see a defect that fits none of them, use a short snake_case label of your own.");
        sb.AppendLine();
        sb.AppendLine("Respond with JSON only, exactly this shape:");
        sb.AppendLine("{\"defects\":[{\"label\":\"<code>\",\"confidence\":<0..1>,\"box_2d\":[ymin,xmin,ymax,xmax],\"severity\":\"minor|major|critical\"," +
                      "\"defect_type\":\"<specific defect name, e.g. Broken yarn, Slub, Oil stain, Skipped stitch; max 5 words>\"," +
                      "\"description\":\"<what you see in this image and where, max 30 words>\"," +
                      "\"meaning\":\"<what this defect is and how it affects the garment or buyer acceptance, max 40 words>\"," +
                      "\"probable_causes\":\"<the usual causes of this defect in garment/textile production, most likely first, max 40 words>\"," +
                      "\"recommended_action\":\"<what the line should do now: repair, reject, check machine/material, max 35 words>\"}]}");
        sb.AppendLine("box_2d uses integers 0-1000 relative to the image height (y) and width (x). " +
                      "The box must enclose the WHOLE visible defect area (every affected yarn, thread, stain or hole), not a single point inside it; " +
                      "if unsure where the defect ends, make the box slightly larger. Omit box_2d if you cannot locate the defect.");
        sb.AppendLine("Write the text fields in plain English that a line supervisor understands. " +
                      "probable_causes are typical causes for this defect type, not a verified root cause.");
        sb.AppendLine("confidence is your own estimate that the defect is really there.");
        sb.AppendLine("Report only defects you can actually see. If the piece has no visible defect, return {\"defects\":[]}.");
        return sb.ToString();
    }

    private const string SystemPrompt =
        "You are a garment quality-control inspector examining a photo from a production line. " +
        "You report visible manufacturing defects precisely and never invent defects that are not visible.";

    private HttpRequestMessage BuildRequest(string provider, string model, string key, byte[] image, string contentType, string prompt)
    {
        var b64 = Convert.ToBase64String(image);
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
                                new { type = "image_url", image_url = new { url = $"data:{contentType};base64,{b64}", detail = "high" } },
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
                    max_tokens = 4096, // each defect now carries a short explanation
                    system = SystemPrompt,
                    messages = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = new object[]
                            {
                                new { type = "image", source = new { type = "base64", media_type = contentType, data = b64 } },
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
                                new { inline_data = new { mime_type = contentType, data = b64 } },
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
