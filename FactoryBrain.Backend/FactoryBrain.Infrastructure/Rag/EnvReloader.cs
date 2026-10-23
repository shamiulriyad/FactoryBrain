namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Development-only helper that re-reads <c>.env.local</c> on every probe
/// so newly-set or newly-removed values (notably
/// <c>RAG_EMBEDDING_FAKE_FAIL</c> / <c>RAG_EMBEDDING_FAKE_FAIL_AFTER</c>
/// / <c>RAG_EMBEDDING_API_KEY</c>) take effect without a restart.
/// <para>
/// Rules:
/// <list type="bullet">
///   <item>
///     Only acts when <c>ASPNETCORE_ENVIRONMENT</c> is <c>Development</c>.
///     In every other environment we skip the file and rely solely on
///     process environment variables — there is no Development-only code
///     path that would let an operator change RAG configuration at
///     runtime in production.
///   </item>
///   <item>
///     Only variables whose names start with <c>RAG_</c> are written into
///     <c>Environment.SetEnvironmentVariable</c>. We never touch
///     <c>FACTORYBRAIN_DB</c>, <c>DOTNET_CONNECTION_STRING</c>,
///     <c>ConnectionStrings__*</c>, <c>ASPNETCORE_*</c>, or any other
///     key. This rule is enforced both at parse time (we skip non-RAG_
///     lines) and at any future direct set site — see the
///     <see cref="IsWriteableKey"/> helper.
///   </item>
///   <item>
///     File-read errors (missing file, permission, IO) are swallowed and
///     logged at warning level. We never log any value, never throw.
///   </item>
/// </list>
/// </para>
/// </summary>
public static class EnvReloader
{
    public const string LocalEnvFileName = ".env.local";

    /// <summary>
    /// Prefix allowed for keys this reloader may write. Anything outside
    /// this prefix is read but ignored so a stray line in <c>.env.local</c>
    /// cannot (for example) re-point <c>FACTORYBRAIN_DB</c> at a different
    /// database, or flip <c>ASPNETCORE_ENVIRONMENT</c> to Production.
    /// </summary>
    public const string AllowedKeyPrefix = "RAG_";

    /// <summary>
    /// True when the running environment is Development. Read once per
    /// call so a runtime change to <c>ASPNETCORE_ENVIRONMENT</c> can take
    /// effect on the next reload attempt.
    /// </summary>
    public static bool IsDevelopment()
    {
        var name = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        return string.Equals(name, "Development", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Single source of truth for the "may this key be written?" rule.
    /// Public so any future code path (settings persistence, diagnostic
    /// CLI) can ask before assigning into <c>Environment</c>.
    /// </summary>
    public static bool IsWriteableKey(string key)
        => !string.IsNullOrEmpty(key)
        && key.StartsWith(AllowedKeyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Re-read <c>.env.local</c> and apply only the RAG_* keys it defines.
    /// Non-RAG_* lines are silently skipped. No-op outside Development.
    /// Always swallows file errors and logs them as warnings (never the
    /// contents). Never throws.
    /// </summary>
    public static void Reload(ILogger? logger = null)
    {
        if (!IsDevelopment()) return;
        try
        {
            if (!File.Exists(LocalEnvFileName)) return;

            // We deliberately do NOT call Env.Load() here. DotNetEnv's
            // LoadOptions apply per-file and would let any non-RAG_ key
            // slip into Environment.SetEnvironmentVariable, which would
            // silently re-point FACTORYBRAIN_DB / ConnectionStrings / etc.
            // Instead we parse the file ourselves and write ONLY RAG_*
            // keys. This is the developer-experience surface: a developer
            // can flip RAG_EMBEDDING_FAKE_FAIL in .env.local and see it
            // take effect on the next probe, but they cannot accidentally
            // mutate the connection string or the ASP.NET env.
            int wrote = 0, skipped = 0, malformed = 0;
            foreach (var (key, value) in EnumerateKeyValuePairs(LocalEnvFileName))
            {
                if (!IsWriteableKey(key)) { skipped++; continue; }
                Environment.SetEnvironmentVariable(key, value);
                wrote++;
            }
            // Verbose log only at debug level so we don't spam prod logs
            // (we ARE in Development, but the reload runs every probe tick).
            logger?.LogDebug(
                "EnvReloader: applied {Wrote} RAG_* var(s) from {File} ({Skipped} non-RAG_ skipped, {Malformed} malformed).",
                wrote, LocalEnvFileName, skipped, malformed);
        }
        catch (Exception ex)
        {
            // Never log values — only the failure.
            logger?.LogWarning(ex, "EnvReloader: failed to re-read {File}; continuing with current process env.", LocalEnvFileName);
        }
    }

    /// <summary>
    /// Minimal .env parser. Yields (key, value) pairs for every line in
    /// the file that looks like KEY=VALUE. Lines that are blank, comment
    /// (#), or that start with `export ` are handled. Quote handling
    /// matches DotNetEnv's defaults (single and double quotes are stripped,
    /// no inline escape processing). Malformed lines are skipped.
    /// </summary>
    private static IEnumerable<(string Key, string Value)> EnumerateKeyValuePairs(string path)
    {
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#') continue;
            // Optional 'export ' prefix that shell .env files sometimes carry.
            const string exportPrefix = "export ";
            if (line.StartsWith(exportPrefix, StringComparison.Ordinal))
                line = line.Substring(exportPrefix.Length).TrimStart();

            int eq = line.IndexOf('=');
            if (eq <= 0) yield break; // malformed -> stop yielding
            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1);
            if (key.Length == 0) yield break;

            // Trim a single layer of surrounding " or ' if present, like DotNetEnv does.
            if (val.Length >= 2 &&
                ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                val = val.Substring(1, val.Length - 2);

            yield return (key, val);
        }
    }
}
