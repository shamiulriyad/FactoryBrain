using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FactoryBrain.Application.Abstractions.Interfaces;

namespace FactoryBrain.Infrastructure.Qc;

/// <summary>
/// Defect detection by a self-hosted model (e.g. a YOLO / segmentation model
/// trained on the factory's own defect images) behind an HTTP endpoint.
/// <para><b>Contract</b> (<c>POST QC_VISION_HTTP_URL</c>, JSON, optional
/// <c>Authorization: Bearer QC_VISION_HTTP_API_KEY</c>):</para>
/// <code>
/// request:  { "image": { "contentType": "image/jpeg", "base64": "…", "width": 1024, "height": 768 },
///             "productType": "t-shirt", "stage": "end_of_line",
///             "categories": [ { "code": "stitch_skip", "name": "Skipped stitch", "description": "…" } ] }
/// response: { "model": "garment-defects-yolov8", "modelVersion": "2026.10.1", "localization": true,
///             "defects": [ { "label": "stitch_skip", "confidence": 0.91,
///                            "box": { "x": 0.12, "y": 0.30, "width": 0.08, "height": 0.05 },
///                            "polygon": [[0.12,0.30],[0.20,0.30],[0.20,0.35]],
///                            "severity": "major", "description": "…" } ] }
/// </code>
/// Coordinates are fractions of the image width / height, origin top-left.
/// <c>box</c>, <c>polygon</c>, <c>severity</c> and <c>description</c> are
/// optional; <c>model</c> and <c>defects</c> are required. Any other shape
/// is rejected as <c>invalid_output</c>.
/// </summary>
public sealed class HttpDefectDetectionProvider : IDefectDetectionProvider
{
    public const string ProviderId = "http";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _http;
    private readonly QcOptions _options;
    private readonly ILogger<HttpDefectDetectionProvider> _log;

    public HttpDefectDetectionProvider(IHttpClientFactory http, QcOptions options, ILogger<HttpDefectDetectionProvider> log)
    {
        _http = http; _options = options; _log = log;
    }

    public DefectProviderStatus Describe() => _options.HttpUrl is null
        ? new DefectProviderStatus(false, QcOptions.ModeHttp, ProviderId, null, null,
            _options.ConfigurationError ?? "QC_VISION_HTTP_URL is not set.")
        // The model name comes back with each result; the endpoint host is not echoed (it can be internal).
        : new DefectProviderStatus(true, QcOptions.ModeHttp, ProviderId, null, null, null);

    public async Task<DefectDetectionResult> DetectAsync(DefectDetectionRequest request, CancellationToken ct)
    {
        if (_options.HttpUrl is null)
            throw new DefectDetectionException(DefectDetectionException.NotConfigured, Describe().Reason!);

        var payload = JsonSerializer.Serialize(new
        {
            image = new { contentType = request.ContentType, base64 = Convert.ToBase64String(request.Image), width = request.Width, height = request.Height },
            productType = request.ProductType,
            stage = request.Stage,
            categories = request.Categories.Select(c => new { code = c.Code, name = c.Name, description = c.Description }),
        });
        using var msg = new HttpRequestMessage(HttpMethod.Post, _options.HttpUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (_options.HttpApiKey is { } key) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var client = _http.CreateClient(nameof(HttpDefectDetectionProvider));
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.Timeout);

        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(msg, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DefectDetectionException(DefectDetectionException.Timeout,
                $"The detection service did not answer within {_options.Timeout.TotalSeconds:F0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("QC detection service unreachable ({ErrorType}).", ex.GetType().Name);
            throw new DefectDetectionException(DefectDetectionException.ProviderError, "The detection service could not be reached.", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("QC detection service failed: status={Status}.", (int)resp.StatusCode);
                throw resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? new DefectDetectionException(DefectDetectionException.Unauthorized, $"The detection service rejected the request ({(int)resp.StatusCode}). Check QC_VISION_HTTP_API_KEY.")
                    : new DefectDetectionException(DefectDetectionException.ProviderError, $"The detection service returned HTTP {(int)resp.StatusCode}.");
            }
            return Parse(await resp.Content.ReadAsStringAsync(ct));
        }
    }

    public static DefectDetectionResult Parse(string raw)
    {
        Response? body;
        try { body = JsonSerializer.Deserialize<Response>(raw, Json); }
        catch (JsonException ex)
        {
            throw new DefectDetectionException(DefectDetectionException.InvalidOutput, "The detection service's response was not valid JSON.", ex);
        }
        if (body is null || string.IsNullOrWhiteSpace(body.Model) || body.Defects is null)
            throw new DefectDetectionException(DefectDetectionException.InvalidOutput,
                "The detection service's response is missing \"model\" or \"defects\".");

        var defects = body.Defects
            .Where(d => !string.IsNullOrWhiteSpace(d.Label))
            .Select(d => new DetectedDefect(
                d.Label!,
                d.Confidence,
                d.Box is { } b ? new NormalizedBox(b.X, b.Y, b.Width, b.Height) : null,
                d.Polygon is { Count: > 0 } p && p.All(pt => pt is { Length: 2 }) ? p.SelectMany(pt => pt).ToArray() : null,
                d.Severity,
                d.Description))
            .ToList();
        return new DefectDetectionResult(ProviderId, Trim(body.Model, 128)!, Trim(body.ModelVersion, 128),
            body.Localization ?? defects.Any(d => d.Box is not null), defects);
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];

    private sealed class Response
    {
        public string? Model { get; set; }
        public string? ModelVersion { get; set; }
        public bool? Localization { get; set; }
        public List<DefectOut>? Defects { get; set; }
    }

    private sealed class DefectOut
    {
        public string? Label { get; set; }
        public double? Confidence { get; set; }
        public BoxOut? Box { get; set; }
        public List<double[]>? Polygon { get; set; }
        public string? Severity { get; set; }
        public string? Description { get; set; }
    }

    private sealed class BoxOut
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }
}

/// <summary>
/// The provider selected by <c>QC_VISION_PROVIDER</c>. With no provider (or
/// an invalid configuration) every call fails with
/// <c>provider_not_configured</c>; it never returns a result.
/// </summary>
public sealed class ConfiguredDefectDetectionProvider : IDefectDetectionProvider
{
    private readonly QcOptions _options;
    private readonly IDefectDetectionProvider? _inner;

    public ConfiguredDefectDetectionProvider(QcOptions options, IServiceProvider sp)
    {
        _options = options;
        _inner = options.Mode switch
        {
            QcOptions.ModeLlm => ActivatorUtilities.CreateInstance<LlmVisionDefectProvider>(sp),
            QcOptions.ModeHttp => ActivatorUtilities.CreateInstance<HttpDefectDetectionProvider>(sp),
            _ => null,
        };
    }

    public DefectProviderStatus Describe() => _inner?.Describe()
        ?? new DefectProviderStatus(false, QcOptions.ModeNone, null, null, null,
            _options.ConfigurationError ?? "No computer-vision provider is configured. Set QC_VISION_PROVIDER to llm or http on the API server.");

    public Task<DefectDetectionResult> DetectAsync(DefectDetectionRequest request, CancellationToken ct) => _inner is null
        ? throw new DefectDetectionException(DefectDetectionException.NotConfigured, Describe().Reason!)
        : _inner.DetectAsync(request, ct);
}
