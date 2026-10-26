using System.Globalization;

namespace FactoryBrain.Infrastructure.Merchandising;

/// <summary>
/// Merchandising document settings, read once from the environment at startup.
/// <list type="bullet">
///   <item><c>MERCH_EXTRACTION_PROVIDER</c> = <c>rules</c> (default: the deterministic label / table
///   parser, no AI) | <c>llm</c> (the language model configured in Settings → AI / <c>LLM_*</c>) |
///   <c>none</c> (no automatic extraction: text is parsed, values are entered by the reviewer).</item>
///   <item><c>MERCH_EXTRACTION_MODEL</c>: model for <c>llm</c> mode (defaults to the LLM model).</item>
///   <item><c>MERCH_EXTRACTION_TIMEOUT_SECONDS</c> (default 90), <c>MERCH_EXTRACTION_MAX_CHARS</c> (text sent to
///   the model, default 60000), <c>MERCH_REVIEW_CONFIDENCE</c> (values below it are flagged, default 0.6),
///   <c>MERCH_MAX_ATTEMPTS</c> (default 3).</item>
///   <item><c>MERCH_DOCUMENT_STORAGE_PATH</c> (default <c>&lt;content root&gt;/App_Data/merch-documents</c>),
///   <c>MERCH_DOCUMENT_MAX_MB</c> (default 15, at most 25), <c>MERCH_MAX_PAGES</c> (default 200).</item>
///   <item><c>MERCH_WORKER_ENABLED</c> (default true): run the background processing worker in this instance.</item>
/// </list>
/// There is no OCR integration: scanned PDFs and images are stored and can be
/// reviewed by hand, but their text can't be read.
/// </summary>
public sealed record MerchOptions(
    string Mode,
    string? ExtractionModel,
    TimeSpan Timeout,
    int MaxPromptChars,
    double ReviewConfidence,
    int MaxAttempts,
    string StoragePath,
    long MaxFileBytes,
    int MaxPages,
    bool WorkerEnabled,
    string? ConfigurationError)
{
    public const string ModeNone = "none";
    public const string ModeRules = "rules";
    public const string ModeLlm = "llm";

    public const long MaxFileBytesCeiling = 25L * 1024 * 1024;
    /// <summary>Parsed text kept per version (and searched for evidence).</summary>
    public const int MaxStoredTextChars = 1_000_000;

    public static MerchOptions FromEnvironment(Func<string, string?> env, string contentRoot)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        var inv = CultureInfo.InvariantCulture;

        var mode = (Clean(env("MERCH_EXTRACTION_PROVIDER")) ?? ModeRules).ToLowerInvariant();
        string? error = null;
        if (mode is not (ModeNone or ModeRules or ModeLlm))
        {
            error = $"MERCH_EXTRACTION_PROVIDER='{mode}' is not one of: rules, llm, none.";
            mode = ModeNone;
        }
        var timeout = int.TryParse(Clean(env("MERCH_EXTRACTION_TIMEOUT_SECONDS")), out var t) && t is >= 5 and <= 600 ? t : 90;
        var maxChars = int.TryParse(Clean(env("MERCH_EXTRACTION_MAX_CHARS")), out var mc) && mc is >= 1000 and <= 500_000 ? mc : 60_000;
        var conf = double.TryParse(Clean(env("MERCH_REVIEW_CONFIDENCE")), NumberStyles.Float, inv, out var c) && c is >= 0 and <= 1 ? c : 0.6;
        var attempts = int.TryParse(Clean(env("MERCH_MAX_ATTEMPTS")), out var a) && a is >= 1 and <= 10 ? a : 3;
        var maxMb = double.TryParse(Clean(env("MERCH_DOCUMENT_MAX_MB")), NumberStyles.Float, inv, out var mb) && mb > 0 ? mb : 15;
        var maxBytes = Math.Min((long)(maxMb * 1024 * 1024), MaxFileBytesCeiling);
        var maxPages = int.TryParse(Clean(env("MERCH_MAX_PAGES")), out var p) && p is >= 1 and <= 2000 ? p : 200;
        var storage = Clean(env("MERCH_DOCUMENT_STORAGE_PATH")) ?? Path.Combine(contentRoot, "App_Data", "merch-documents");
        var worker = !bool.TryParse(Clean(env("MERCH_WORKER_ENABLED")), out var w) || w;

        return new MerchOptions(mode, Clean(env("MERCH_EXTRACTION_MODEL")), TimeSpan.FromSeconds(timeout), maxChars, conf, attempts,
            Path.GetFullPath(storage), maxBytes, maxPages, worker, error);
    }
}
