using System.Net;
using System.Text;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Dtos.Qc;
using FactoryBrain.Application.Qc;
using FactoryBrain.Application.Validation;
using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Qc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FactoryBrain.Api.Tests;

/// <summary>Image validation: the bytes decide the format, never the file name or Content-Type.</summary>
public sealed class QcImageInspectorTests
{
    private const long Max = 10 * 1024 * 1024;

    [Fact]
    public void Real_png_is_accepted_with_its_dimensions()
    {
        Assert.True(QcImageInspector.TryInspect(TestImages.Png(64, 48), Max, out var info, out var error), error);
        Assert.Equal(("png", "image/png", "png", 64, 48), (info!.Format, info.ContentType, info.Extension, info.Width, info.Height));
    }

    [Fact]
    public void Jpeg_dimensions_come_from_the_frame_header()
    {
        Assert.True(QcImageInspector.TryInspect(TestImages.JpegHeader(640, 480), Max, out var info, out _));
        Assert.Equal(("image/jpeg", "jpg", 640, 480), (info!.ContentType, info.Extension, info.Width, info.Height));
    }

    [Fact]
    public void Webp_extended_header_is_read()
    {
        Assert.True(QcImageInspector.TryInspect(TestImages.WebpHeader(1920, 1080), Max, out var info, out _));
        Assert.Equal(("image/webp", 1920, 1080), (info!.ContentType, info.Width, info.Height));
    }

    [Theory]
    [InlineData("GIF89a\x01\x00\x01\x00")]
    [InlineData("<html><script>alert(1)</script></html>")]
    [InlineData("%PDF-1.7 not an image")]
    [InlineData("MZ\x90\x00 executable")]
    public void Other_file_types_are_rejected_as_unsupported(string content)
    {
        Assert.False(QcImageInspector.TryInspect(Encoding.Latin1.GetBytes(content), Max, out _, out var error));
        Assert.Equal(QcImageInspector.ErrUnsupported, error);
    }

    [Fact]
    public void Empty_oversized_truncated_and_out_of_range_images_are_rejected()
    {
        Assert.Equal(QcImageInspector.ErrEmpty, Error(Array.Empty<byte>()));
        Assert.Equal(QcImageInspector.ErrTooLarge, Error(TestImages.Png(4, 4), max: 10));
        Assert.Equal(QcImageInspector.ErrCorrupt, Error(TestImages.PngHeader(10, 10)[..12]));     // signature only
        Assert.Equal(QcImageInspector.ErrDimensions, Error(TestImages.PngHeader(0, 10)));
        Assert.Equal(QcImageInspector.ErrDimensions, Error(TestImages.PngHeader(20_000, 10)));    // decompression-bomb guard
        Assert.Equal(QcImageInspector.ErrDimensions, Error(TestImages.PngHeader(uint.MaxValue, 10)));
        // A JPEG that reaches the scan data before any frame header is not an image we can trust.
        Assert.Equal(QcImageInspector.ErrCorrupt, Error(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x08, 0, 0, 0, 0 }));
    }

    private static string? Error(byte[] data, long max = Max)
    {
        QcImageInspector.TryInspect(data, max, out _, out var error);
        return error;
    }
}

public sealed class QcLifecycleTests
{
    [Theory]
    [InlineData(QcInspectionStatus.Pending, QcInspectionStatus.Processing, true)]
    [InlineData(QcInspectionStatus.Processing, QcInspectionStatus.Completed, true)]
    [InlineData(QcInspectionStatus.Processing, QcInspectionStatus.Failed, true)]
    [InlineData(QcInspectionStatus.Failed, QcInspectionStatus.Pending, true)]
    [InlineData(QcInspectionStatus.Completed, QcInspectionStatus.Reviewed, true)]
    [InlineData(QcInspectionStatus.Failed, QcInspectionStatus.Reviewed, true)]
    [InlineData(QcInspectionStatus.Reviewed, QcInspectionStatus.Reviewed, true)]
    [InlineData(QcInspectionStatus.Pending, QcInspectionStatus.Reviewed, false)]
    [InlineData(QcInspectionStatus.Processing, QcInspectionStatus.Reviewed, false)]
    [InlineData(QcInspectionStatus.Reviewed, QcInspectionStatus.Pending, false)]
    [InlineData(QcInspectionStatus.Completed, QcInspectionStatus.Pending, false)]
    [InlineData(QcInspectionStatus.Reviewed, QcInspectionStatus.Completed, false)]
    [InlineData(QcInspectionStatus.Completed, QcInspectionStatus.Processing, false)]
    public void Transitions_follow_the_lifecycle(QcInspectionStatus from, QcInspectionStatus to, bool allowed)
        => Assert.Equal(allowed, QcLifecycle.CanTransition(from, to));

    [Fact]
    public void Only_failed_inspections_can_be_retried()
    {
        foreach (var s in Enum.GetValues<QcInspectionStatus>())
            Assert.Equal(s == QcInspectionStatus.Failed, QcLifecycle.CanRetry(s));
    }

    [Fact]
    public void Wire_names_round_trip_and_unknown_values_are_rejected()
    {
        foreach (var s in Enum.GetValues<QcInspectionStage>())
            Assert.True(QcWire.TryParseStage(QcWire.Stage(s), out var back) && back == s);
        Assert.Equal("end_of_line", QcWire.Stage(QcInspectionStage.EndOfLine));
        Assert.False(QcWire.TryParseStage("EndOfLine", out _));
        Assert.False(QcWire.TryParseDecision("unreviewed", out _)); // an inspector can't submit "unreviewed"
        Assert.True(QcWire.TryParseDecision(" Corrected ", out var d) && d == QcFindingDecision.Corrected);
    }
}

public sealed class QcDetectionNormalizerTests
{
    private static readonly DetectionCategory[] Categories =
    {
        new("skipped_stitch", "Skipped stitch", null),
        new("stain", "Stain / spot", null),
        new("hole", "Hole", null),
    };

    [Fact]
    public void Vision_model_json_is_parsed_and_box_2d_is_normalised()
    {
        var parsed = QcDetectionNormalizer.ParseVisionModelJson("""
            ```json
            {"defects":[{"label":"stain","confidence":0.82,"box_2d":[100,200,300,600],"severity":"major","description":"oil"}]}
            ```
            """);
        var d = Assert.Single(parsed);
        Assert.Equal("stain", d.Label);
        Assert.Equal(0.82, d.Confidence);
        Assert.Equal(new NormalizedBox(0.2, 0.1, 0.4, 0.2), d.Box);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I see a stain near the hem.")]
    [InlineData("""{"result":"ok"}""")]
    [InlineData("""{"defects":"none"}""")]
    [InlineData("[]")]
    public void Output_that_is_not_the_contract_is_invalid_output_never_a_guess(string content)
    {
        var ex = Assert.Throws<DefectDetectionException>(() => QcDetectionNormalizer.ParseVisionModelJson(content));
        Assert.Equal(DefectDetectionException.InvalidOutput, ex.Code);
    }

    [Fact]
    public void Mapping_matches_codes_and_names_filters_by_threshold_and_drops_bad_values()
    {
        var mapped = QcDetectionNormalizer.Map(new DetectedDefect[]
        {
            new("SKIPPED_STITCH", 0.9, new NormalizedBox(0.1, 0.1, 0.2, 0.2), null, "MAJOR", null),
            new("Stain / spot", 0.5, null, null, "urgent", null),       // name match; unknown severity dropped
            new("chalk_mark", 0.7, null, null, null, null),             // unmapped, kept for correction
            new("hole", 0.1, null, null, null, null),                   // below threshold
            new("hole", 1.5, null, null, null, null),                   // invalid confidence
            new("hole", 0.8, new NormalizedBox(0.9, 0.1, 0.5, 0.1), null, null, null), // box outside the image
            new("  ", 0.9, null, null, null, null),                     // no label
        }, Categories, threshold: 0.25);

        Assert.Equal(4, mapped.Count);
        Assert.Equal(("skipped_stitch", "major"), (mapped[0].CategoryCode, mapped[0].Severity));
        Assert.Equal(("stain", (string?)null), (mapped[1].CategoryCode, mapped[1].Severity));
        Assert.Null(mapped[2].CategoryCode);
        Assert.Equal("chalk_mark", mapped[2].RawLabel);
        Assert.Equal("hole", mapped[3].CategoryCode);
        Assert.Null(mapped[3].Box);
    }

    [Fact]
    public void Boxes_and_polygons_must_lie_inside_the_image()
    {
        Assert.NotNull(QcDetectionNormalizer.ValidBox(new NormalizedBox(0, 0, 1, 1)));
        Assert.NotNull(QcDetectionNormalizer.ValidBox(new NormalizedBox(0.5, 0.5, 0.505, 0.5))); // rounding overshoot
        Assert.Null(QcDetectionNormalizer.ValidBox(new NormalizedBox(-0.2, 0, 0.1, 0.1)));
        Assert.Null(QcDetectionNormalizer.ValidBox(new NormalizedBox(0.1, 0.1, 0, 0.1)));
        Assert.Null(QcDetectionNormalizer.ValidBox(new NormalizedBox(double.NaN, 0.1, 0.1, 0.1)));
        Assert.NotNull(QcDetectionNormalizer.ValidPolygon(new[] { 0.1, 0.1, 0.2, 0.1, 0.2, 0.2 }));
        Assert.Null(QcDetectionNormalizer.ValidPolygon(new[] { 0.1, 0.1, 0.2, 0.1 }));        // fewer than 3 points
        Assert.Null(QcDetectionNormalizer.ValidPolygon(new[] { 0.1, 0.1, 0.2, 0.1, 0.2 }));   // odd length
        Assert.Null(QcDetectionNormalizer.ValidPolygon(new[] { 0.1, 0.1, 2.0, 0.1, 0.2, 0.2 }));
    }

    [Fact]
    public void At_most_the_per_inspection_limit_is_kept()
    {
        var many = Enumerable.Range(0, 250).Select(_ => new DetectedDefect("stain", 0.9, null, null, null, null)).ToList();
        Assert.Equal(QcLimits.MaxFindingsPerInspection, QcDetectionNormalizer.Map(many, Categories, 0.25).Count);
    }
}

public sealed class QcProviderTests
{
    private static QcOptions Options(string mode, Uri? url = null, TimeSpan? timeout = null, string? key = null) =>
        new(mode, null, url, key, timeout ?? TimeSpan.FromSeconds(5), 0.25, 3, Path.GetTempPath(), 10 * 1024 * 1024, false, null);

    private static DefectDetectionRequest Request() =>
        new(TestImages.Png(4, 4), "image/png", 4, 4, new[] { new DetectionCategory("stain", "Stain", "oil marks") }, "t-shirt", "final");

    [Fact]
    public void Http_contract_is_parsed_into_the_internal_shape()
    {
        var r = HttpDefectDetectionProvider.Parse("""
            {"model":"garment-yolo","modelVersion":"2026.10","localization":true,
             "defects":[{"label":"stain","confidence":0.9,"box":{"x":0.1,"y":0.2,"width":0.3,"height":0.1},
                         "polygon":[[0.1,0.2],[0.4,0.2],[0.4,0.3]]},{"label":""}]}
            """);
        Assert.Equal(("http", "garment-yolo", "2026.10", true), (r.Provider, r.Model, r.ModelVersion, r.LocalizationSupported));
        var d = Assert.Single(r.Defects);
        Assert.Equal(new[] { 0.1, 0.2, 0.4, 0.2, 0.4, 0.3 }, d.Polygon);
    }

    [Theory]
    [InlineData("""{"defects":[]}""")]                 // no model
    [InlineData("""{"model":"m"}""")]                  // no defects
    [InlineData("not json")]
    public void Http_responses_outside_the_contract_are_invalid_output(string raw)
    {
        var ex = Assert.Throws<DefectDetectionException>(() => HttpDefectDetectionProvider.Parse(raw));
        Assert.Equal(DefectDetectionException.InvalidOutput, ex.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, DefectDetectionException.ProviderError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DefectDetectionException.ProviderError)]
    [InlineData(HttpStatusCode.Unauthorized, DefectDetectionException.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, DefectDetectionException.Unauthorized)]
    public async Task Http_provider_errors_map_to_stable_codes(HttpStatusCode status, string code)
    {
        var provider = new HttpDefectDetectionProvider(new StubHttp((_, _) => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent("""{"detail":"sk-secret-echo"}""") })),
            Options(QcOptions.ModeHttp, new Uri("https://detector.test/detect")), NullLogger<HttpDefectDetectionProvider>.Instance);
        var ex = await Assert.ThrowsAsync<DefectDetectionException>(() => provider.DetectAsync(Request(), CancellationToken.None));
        Assert.Equal(code, ex.Code);
        Assert.DoesNotContain("sk-secret-echo", ex.Message); // provider bodies never reach the stored message
    }

    [Fact]
    public async Task Http_provider_times_out_and_sends_the_bearer_key()
    {
        string? auth = null;
        var provider = new HttpDefectDetectionProvider(new StubHttp(async (req, ct) =>
        {
            auth = req.Headers.Authorization?.ToString();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), Options(QcOptions.ModeHttp, new Uri("https://detector.test/detect"), TimeSpan.FromMilliseconds(200), key: "detector-key"),
            NullLogger<HttpDefectDetectionProvider>.Instance);
        var ex = await Assert.ThrowsAsync<DefectDetectionException>(() => provider.DetectAsync(Request(), CancellationToken.None));
        Assert.Equal(DefectDetectionException.Timeout, ex.Code);
        Assert.Equal("Bearer detector-key", auth);
    }

    [Fact]
    public async Task No_provider_configured_never_returns_a_result()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var provider = new ConfiguredDefectDetectionProvider(Options(QcOptions.ModeNone), services);
        var status = provider.Describe();
        Assert.False(status.Configured);
        Assert.Contains("QC_VISION_PROVIDER", status.Reason);
        var ex = await Assert.ThrowsAsync<DefectDetectionException>(() => provider.DetectAsync(Request(), CancellationToken.None));
        Assert.Equal(DefectDetectionException.NotConfigured, ex.Code);
    }

    [Theory]
    [InlineData("openai", """{"model":"gpt-x-2026","choices":[{"message":{"content":"{\"defects\":[]}"}}]}""", "gpt-x-2026")]
    [InlineData("anthropic", """{"model":"claude-x","content":[{"type":"text","text":"{\"defects\":[]}"}]}""", "claude-x")]
    [InlineData("gemini", """{"modelVersion":"gemini-x-001","candidates":[{"content":{"parts":[{"text":"{\"defects\":[]}"}]}}]}""", "gemini-x-001")]
    public void Vision_model_envelopes_yield_content_and_model_version(string provider, string raw, string version)
    {
        var (content, v) = LlmVisionDefectProvider.ReadContent(provider, raw);
        Assert.Equal("""{"defects":[]}""", content);
        Assert.Equal(version, v);
    }

    [Fact]
    public void Malformed_vision_model_envelope_is_invalid_output()
    {
        var ex = Assert.Throws<DefectDetectionException>(() => LlmVisionDefectProvider.ReadContent("openai", """{"choices":[]}"""));
        Assert.Equal(DefectDetectionException.InvalidOutput, ex.Code);
    }

    [Fact]
    public void Vision_prompt_lists_the_requested_taxonomy()
    {
        var prompt = LlmVisionDefectProvider.BuildPrompt(Request());
        Assert.Contains("- stain: Stain — oil marks", prompt);
        Assert.Contains("box_2d", prompt);
        Assert.Contains("t-shirt", prompt);
    }

    private sealed class StubHttp : IHttpClientFactory
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public StubHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) { _respond = respond; }
        public HttpClient CreateClient(string name) => new(new Handler(_respond));

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
            public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) { _respond = respond; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _respond(request, ct);
        }
    }
}

public sealed class QcOptionsAndStorageTests
{
    private static QcOptions Parse(Dictionary<string, string?> env) =>
        QcOptions.FromEnvironment(k => env.TryGetValue(k, out var v) ? v : null, Path.GetTempPath());

    [Fact]
    public void Defaults_report_no_provider()
    {
        var o = Parse(new());
        Assert.Equal((QcOptions.ModeNone, 0.25, 3, 10L * 1024 * 1024, true, (string?)null),
            (o.Mode, o.MinConfidence, o.MaxAttempts, o.MaxImageBytes, o.WorkerEnabled, o.ConfigurationError));
    }

    [Fact]
    public void Invalid_configuration_is_reported_not_guessed()
    {
        Assert.Contains("not one of", Parse(new() { ["QC_VISION_PROVIDER"] = "magic" }).ConfigurationError);
        var noUrl = Parse(new() { ["QC_VISION_PROVIDER"] = "http" });
        Assert.Null(noUrl.HttpUrl);
        Assert.Contains("QC_VISION_HTTP_URL", noUrl.ConfigurationError);
        Assert.Null(Parse(new() { ["QC_VISION_PROVIDER"] = "http", ["QC_VISION_HTTP_URL"] = "https://u:p@host/x" }).HttpUrl);
        Assert.Null(Parse(new() { ["QC_VISION_PROVIDER"] = "http", ["QC_VISION_HTTP_URL"] = "file:///etc/passwd" }).HttpUrl);
        Assert.Equal(QcLimits.MaxImageBytesCeiling, Parse(new() { ["QC_IMAGE_MAX_MB"] = "500" }).MaxImageBytes);
        Assert.Equal(0.25, Parse(new() { ["QC_VISION_MIN_CONFIDENCE"] = "7" }).MinConfidence);
    }

    [Fact]
    public async Task Image_store_round_trips_and_refuses_keys_outside_its_shape()
    {
        var root = Path.Combine(Path.GetTempPath(), "fb-qc-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Parse(new() { ["QC_IMAGE_STORAGE_PATH"] = root });
            var store = new LocalFileQcImageStore(options, NullLogger<LocalFileQcImageStore>.Instance);
            var bytes = TestImages.Png(8, 8);
            var key = await store.SaveAsync(bytes, "png", CancellationToken.None);
            Assert.Matches(@"^\d{4}/\d{2}/[0-9a-f]{32}\.png$", key);
            await using (var s = await store.OpenReadAsync(key, CancellationToken.None))
            {
                using var ms = new MemoryStream();
                await s!.CopyToAsync(ms);
                Assert.Equal(bytes, ms.ToArray());
            }

            // A secret next to the store root must be unreachable through any key.
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(root)!, "fb-secret.txt"), "secret");
            foreach (var bad in new[] { "../fb-secret.txt", "2026/10/../../../fb-secret.txt", "/etc/passwd", "C:\\Windows\\win.ini",
                                         key.ToUpperInvariant(), key + "/../x.png", "2026/10/" + new string('a', 32) + ".exe" })
            {
                Assert.False(LocalFileQcImageStore.IsValidKey(bad), bad);
                Assert.Null(await store.OpenReadAsync(bad, CancellationToken.None));
            }
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(bytes, "html", CancellationToken.None));

            await store.DeleteAsync(key, CancellationToken.None);
            Assert.Null(await store.OpenReadAsync(key, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

public sealed class QcValidationTests
{
    private static QcCreateInspectionInput Input(string product = "Polo shirt", string stage = "end_of_line", string? line = "line-3",
        string? order = "PO-2026/114", string? key = null, params string[] categories) =>
        new(product, stage, line, order, null, categories, Array.Empty<byte>(), "shot.png", key);

    [Fact]
    public void Valid_metadata_passes()
        => Assert.Empty(QcCreateInputValidation.Validate(Input(key: "4f6c1a7e-2b4d-4c11-9b55-0123456789ab", categories: "stain")));

    [Fact]
    public void Bad_metadata_is_reported_per_field()
    {
        var e = QcCreateInputValidation.Validate(Input(product: "", stage: "packing", line: "line-1; DROP TABLE", order: new string('9', 80),
            key: "short", categories: "Not A Code"));
        Assert.Equal(new[] { "categoryCodes", "idempotencyKey", "lineId", "productType", "productionOrder", "stage" }, e.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\me\\Pictures\\a<b>.png", "ab.png")]
    [InlineData("   ", null)]
    [InlineData("seam\u0000.jpg", "seam.jpg")]
    public void Client_file_names_are_display_only_and_sanitised(string raw, string? expected)
        => Assert.Equal(expected, QcCreateInputValidation.SanitizeFileName(raw));

    [Fact]
    public void Review_request_shape_is_validated()
    {
        var v = new QcReviewRequestValidator();
        var id = Guid.NewGuid();
        Assert.True(v.Validate(new QcReviewRequest(5, "rework", null,
            new[] { new QcFindingDecisionInput(id, "corrected", "hole", null, null) },
            new[] { new QcFindingAdditionInput("stain", "minor", new QcBoxDto(0.1, 0.1, 0.2, 0.2), "near hem") })).IsValid);

        var bad = v.Validate(new QcReviewRequest(null, "maybe", null,
            new[] { new QcFindingDecisionInput(id, "corrected", null, null, null), new QcFindingDecisionInput(id, "accepted", null, null, null) },
            new[] { new QcFindingAdditionInput("stain", "fatal", new QcBoxDto(0.9, 0.1, 0.5, 0.1), null) }));
        var props = bad.Errors.Select(e => e.PropertyName).ToHashSet();
        Assert.Contains("ExpectedVersion", props);
        Assert.Contains("Outcome", props);
        Assert.Contains("Decisions", props);                         // duplicate finding id
        Assert.Contains("Decisions[0].correctedCategoryCode", props); // correction without a value
        Assert.Contains("Additions[0].Severity", props);
        Assert.Contains("Additions[0].Box", props);
    }
}

public sealed class QcMetricsCalculatorTests
{
    private static readonly DateTime Day1 = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Dictionary<string, string> Names = new() { ["hole"] = "Hole", ["stain"] = "Stain / spot" };

    [Fact]
    public void Verified_metrics_come_only_from_reviewed_inspections()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid(), e = Guid.NewGuid(), f = Guid.NewGuid();
        var inspections = new[]
        {
            new QcMetricInspection(a, Day1, "line-1", QcInspectionStatus.Reviewed, QcInspectionOutcome.Pass),
            new QcMetricInspection(b, Day1, "line-1", QcInspectionStatus.Reviewed, QcInspectionOutcome.Rework),
            new QcMetricInspection(c, Day1.AddDays(1), "line-2", QcInspectionStatus.Reviewed, QcInspectionOutcome.Reject),
            new QcMetricInspection(d, Day1.AddDays(1), "line-2", QcInspectionStatus.Completed, null),
            new QcMetricInspection(e, Day1.AddDays(2), null, QcInspectionStatus.Failed, null),
            new QcMetricInspection(f, Day1.AddDays(2), null, QcInspectionStatus.Pending, null),
        };
        var findings = new[]
        {
            new QcMetricFinding(a, QcFindingSource.Model, QcFindingDecision.Rejected, "stain", QcDefectSeverity.Major),
            new QcMetricFinding(b, QcFindingSource.Model, QcFindingDecision.Accepted, "stain", QcDefectSeverity.Major),
            new QcMetricFinding(b, QcFindingSource.Inspector, QcFindingDecision.Accepted, "hole", QcDefectSeverity.Critical),
            new QcMetricFinding(c, QcFindingSource.Model, QcFindingDecision.Corrected, "hole", QcDefectSeverity.Critical),
            new QcMetricFinding(d, QcFindingSource.Model, QcFindingDecision.Unreviewed, "stain", QcDefectSeverity.Major), // not a defect yet
        };

        var s = QcMetricsCalculator.Calculate(Day1.Date, Day1.Date.AddDays(2), inspections, findings, Names, Day1);

        Assert.Equal((6, 1, 1, 1, 3), (s.Totals.Inspections, s.Totals.InProgress, s.Totals.Failed, s.Totals.AwaitingReview, s.Totals.Reviewed));
        Assert.Equal((1, 1, 1, 3), (s.Totals.Passed, s.Totals.Rework, s.Totals.Rejected, s.Totals.VerifiedDefects));
        Assert.Equal(66.67, s.Totals.DefectiveRatePct);   // b and c of 3 reviewed
        Assert.Equal(100.0, s.Totals.Dhu);                // 3 defects / 3 reviewed × 100
        Assert.Equal(66.67, s.Totals.RejectOrReworkRatePct);
        Assert.Equal((3, 1, 1, 1, 33.33, 1, 1), (s.Model.PredictionsReviewed, s.Model.Accepted, s.Model.Corrected, s.Model.Rejected,
            s.Model.AcceptedPct, s.Model.InspectorAdded, s.Model.AwaitingReview));
        Assert.Equal(new[] { ("hole", "Hole", 2), ("stain", "Stain / spot", 1) },
            s.ByCategory.Select(x => (x.Code!, x.Name, x.Count)));
        Assert.Equal(new QcSeverityCounts(0, 1, 2), s.BySeverity);
        Assert.Equal(new[] { ("line-2", 1, 1, (double?)100.0), ("line-1", 2, 1, 50.0) },
            s.ByLine.Select(l => (l.LineId, l.Reviewed, l.Defective, l.DefectiveRatePct)));
        Assert.Equal(new[] { "2026-10-20", "2026-10-21", "2026-10-22" }, s.Daily.Select(p => p.Date));
        Assert.Equal((2, 2, 1, 2), (s.Daily[0].Inspections, s.Daily[0].Reviewed, s.Daily[0].Defective, s.Daily[0].VerifiedDefects));
        Assert.Equal((2, 0, (double?)null), (s.Daily[2].Inspections, s.Daily[2].Reviewed, s.Daily[2].DefectiveRatePct));
        Assert.Empty(s.Recurring);
    }

    [Fact]
    public void Rates_are_null_not_zero_without_reviews_and_days_are_filled()
    {
        var s = QcMetricsCalculator.Calculate(Day1.Date, Day1.Date.AddDays(6), Array.Empty<QcMetricInspection>(),
            Array.Empty<QcMetricFinding>(), Names, Day1);
        Assert.Null(s.Totals.DefectiveRatePct);
        Assert.Null(s.Totals.Dhu);
        Assert.Null(s.Model.AcceptedPct);
        Assert.Equal(7, s.Daily.Count);
        Assert.All(s.Daily, p => Assert.Equal(0, p.Inspections));
    }

    [Fact]
    public void Recurring_issues_need_the_threshold_on_one_line_and_category()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        var inspections = ids.Select((id, i) => new QcMetricInspection(id, Day1, i < 3 ? "line-4" : "line-5",
            QcInspectionStatus.Reviewed, QcInspectionOutcome.Rework)).ToArray();
        var findings = ids.Select(id => new QcMetricFinding(id, QcFindingSource.Inspector, QcFindingDecision.Accepted, "stain", QcDefectSeverity.Minor))
            .Append(new QcMetricFinding(ids[0], QcFindingSource.Inspector, QcFindingDecision.Rejected, "stain", QcDefectSeverity.Minor))
            .ToArray();
        var s = QcMetricsCalculator.Calculate(Day1.Date, Day1.Date, inspections, findings, Names, Day1);
        var r = Assert.Single(s.Recurring);
        Assert.Equal(("line-4", "stain", 3, 3), (r.LineId, r.CategoryCode!, r.Defects, r.Inspections));
    }

    [Theory]
    [InlineData(QcFindingSource.Model, QcFindingDecision.Unreviewed, false)]
    [InlineData(QcFindingSource.Model, QcFindingDecision.Accepted, true)]
    [InlineData(QcFindingSource.Model, QcFindingDecision.Corrected, true)]
    [InlineData(QcFindingSource.Model, QcFindingDecision.Rejected, false)]
    [InlineData(QcFindingSource.Inspector, QcFindingDecision.Accepted, true)]
    [InlineData(QcFindingSource.Inspector, QcFindingDecision.Rejected, false)]
    public void A_defect_is_verified_only_by_an_inspector(QcFindingSource source, QcFindingDecision decision, bool verified)
        => Assert.Equal(verified, QcMetricsCalculator.IsVerified(source, decision));
}

/// <summary>Request handling that is decided before the database (no-database Testing host).</summary>
[Collection(ApiCollection.Name)]
public sealed class QcApiGuardTests
{
    private readonly ApiFactory _factory;
    public QcApiGuardTests(ApiFactory factory) { _factory = factory; factory.Activate(); }

    [Fact]
    public async Task Upload_without_an_image_is_rejected_with_a_field_error()
    {
        using var client = _factory.ClientAs(Roles.Operator);
        using var form = new MultipartFormDataContent { { new StringContent("Polo"), "productType" }, { new StringContent("final"), "stage" } };
        using var res = await client.PostAsync("/api/qc/inspections", form);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("\"image\"", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Vision_status_says_inference_is_not_configured_when_no_provider_is_set()
    {
        using var client = _factory.ClientAs(Roles.Viewer);
        var body = await client.GetStringAsync("/api/qc/vision/status");
        Assert.Contains("\"configured\":false", body);
        Assert.Contains("\"mode\":\"none\"", body);
        Assert.Contains("QC_VISION_PROVIDER", body);
    }

    [Fact]
    public async Task Retired_vision_demo_answers_410()
    {
        using var client = _factory.ClientAs(Roles.Viewer);
        using var res = await client.GetAsync("/api/vision/analyze");
        Assert.Equal(HttpStatusCode.Gone, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/qc/inspections?status=done")]
    [InlineData("/api/qc/inspections?pageSize=500")]
    [InlineData("/api/qc/inspections?page=0")]
    [InlineData("/api/qc/inspections?sort=random")]
    [InlineData("/api/qc/summary?days=0")]
    [InlineData("/api/qc/summary?stage=packing")]
    [InlineData("/api/qc/summary?from=2026-01-01&to=2027-06-01")]
    public async Task Invalid_filters_are_400_before_any_query(string url)
    {
        using var client = _factory.ClientAs(Roles.Viewer);
        using var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Malformed_ids_are_404()
    {
        using var client = _factory.ClientAs(Roles.Viewer);
        using var res = await client.GetAsync("/api/qc/inspections/not-a-guid");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
