using System.Globalization;
using FactoryBrain.Application.Qc;

namespace FactoryBrain.Infrastructure.Qc;

/// <summary>
/// Quality-inspection settings, read once from the environment at startup.
/// <list type="bullet">
///   <item><c>QC_VISION_PROVIDER</c> = <c>llm</c> (the vision-capable model configured in
///   Settings → AI / <c>LLM_*</c>) | <c>http</c> (a self-hosted detection model, see
///   <see cref="HttpDefectDetectionProvider"/>) | unset / <c>none</c> (inference not configured).</item>
///   <item><c>QC_VISION_MODEL</c>: model for <c>llm</c> mode (defaults to the LLM model).</item>
///   <item><c>QC_VISION_HTTP_URL</c>, <c>QC_VISION_HTTP_API_KEY</c>: endpoint and bearer key for <c>http</c> mode.</item>
///   <item><c>QC_VISION_TIMEOUT_SECONDS</c> (default 60), <c>QC_VISION_MIN_CONFIDENCE</c> (default 0.25),
///   <c>QC_MAX_ATTEMPTS</c> (default 3).</item>
///   <item><c>QC_IMAGE_STORAGE_PATH</c> (default <c>&lt;content root&gt;/App_Data/qc-images</c>),
///   <c>QC_IMAGE_MAX_MB</c> (default 10, at most 20).</item>
///   <item><c>QC_WORKER_ENABLED</c> (default true): run the background analysis worker in this instance.</item>
/// </list>
/// </summary>
public sealed record QcOptions(
    string Mode,
    string? VisionModel,
    Uri? HttpUrl,
    string? HttpApiKey,
    TimeSpan Timeout,
    double MinConfidence,
    int MaxAttempts,
    string StoragePath,
    long MaxImageBytes,
    bool WorkerEnabled,
    /// <summary>Why the configured mode can't run (e.g. http mode without a URL); null when fine.</summary>
    string? ConfigurationError)
{
    public const string ModeNone = "none";
    public const string ModeLlm = "llm";
    public const string ModeHttp = "http";

    public static QcOptions FromEnvironment(Func<string, string?> env, string contentRoot)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

        var mode = (Clean(env("QC_VISION_PROVIDER")) ?? ModeNone).ToLowerInvariant();
        string? error = null;
        if (mode is not (ModeNone or ModeLlm or ModeHttp))
        {
            error = $"QC_VISION_PROVIDER='{mode}' is not one of: llm, http, none.";
            mode = ModeNone;
        }

        Uri? url = null;
        var rawUrl = Clean(env("QC_VISION_HTTP_URL"));
        if (mode == ModeHttp)
        {
            if (rawUrl is null) error = "QC_VISION_PROVIDER=http needs QC_VISION_HTTP_URL.";
            else if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out url) || url.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(url.UserInfo))
            {
                url = null;
                error = "QC_VISION_HTTP_URL must be an absolute http(s) URL without credentials.";
            }
        }

        var timeout = int.TryParse(Clean(env("QC_VISION_TIMEOUT_SECONDS")), out var t) && t is >= 5 and <= 600 ? t : 60;
        var minConfidence = double.TryParse(Clean(env("QC_VISION_MIN_CONFIDENCE")), NumberStyles.Float, CultureInfo.InvariantCulture, out var c)
                            && c is >= 0 and <= 1 ? c : 0.25;
        var maxAttempts = int.TryParse(Clean(env("QC_MAX_ATTEMPTS")), out var a) && a is >= 1 and <= 10 ? a : 3;
        var maxMb = double.TryParse(Clean(env("QC_IMAGE_MAX_MB")), NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) && mb > 0 ? mb : 10;
        var maxBytes = Math.Min((long)(maxMb * 1024 * 1024), QcLimits.MaxImageBytesCeiling);
        var storage = Clean(env("QC_IMAGE_STORAGE_PATH")) ?? Path.Combine(contentRoot, "App_Data", "qc-images");
        var worker = !bool.TryParse(Clean(env("QC_WORKER_ENABLED")), out var w) || w;

        return new QcOptions(mode, Clean(env("QC_VISION_MODEL")), url, Clean(env("QC_VISION_HTTP_API_KEY")),
            TimeSpan.FromSeconds(timeout), minConfidence, maxAttempts, Path.GetFullPath(storage), maxBytes, worker, error);
    }
}
