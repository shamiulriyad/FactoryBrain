using System.Security.Cryptography;
using System.Text;
using Pgvector;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Dev-only deterministic stub embedder. Behaves like a hosted provider
/// (different <see cref="ProviderId"/> from the local hash, different
/// default dims) but never makes a network call. Honours
/// <c>RAG_EMBEDDING_FAKE_FAIL</c> and <c>RAG_EMBEDDING_FAKE_FAIL_AFTER</c>
/// so the failure path of the reindex / probe code can be exercised
/// without a real API key.
///
/// <para>
/// The call counter is process-wide (static <see cref="Interlocked"/>
/// counter) so even if the service is registered multiple times the
/// AFTER=N semantics remain stable across the process lifetime.
/// <see cref="ResetCounter"/> is invoked by the reindex pipeline at the
/// start of every batch, so AFTER=1 means the very first call of the
/// current reindex throws.
/// </para>
///
/// <para>
/// The dev-only switches are read on every call (not cached) so a probe
/// cycle can pick up a newly-set env var, including one set via
/// <c>.env.local</c> at runtime in Development.
/// </para>
/// </summary>
public sealed class StubEmbeddingService : IEmbeddingService
{
    public const string Provider = "stub";

    // Process-wide counter — singleton or not, every StubEmbeddingService
    // instance shares these so the dev switches behave predictably.
    private static long _processCallCount;
    private static long _resetTick;

    private readonly int _dims;
    private readonly ILogger<StubEmbeddingService>? _log;

    public StubEmbeddingService(int dims, ILogger<StubEmbeddingService>? log = null)
    {
        _dims = dims > 0 ? dims : 768;
        _log  = log;
    }

    public string ProviderId => Provider;
    public string ModelId    => "stub-deterministic";
    public int Dimensions     => _dims;

    /// <summary>
    /// Reset the process-wide call counter so the next call is call #1.
    /// Called by <see cref="EmbeddingColumnAdmin"/> at the start of every
    /// reindex / resize so <c>RAG_EMBEDDING_FAKE_FAIL_AFTER=N</c> applies
    /// to the current batch, not to all calls since process start.
    /// </summary>
    internal static void ResetCounter()
    {
        Interlocked.Exchange(ref _processCallCount, 0L);
        Interlocked.Increment(ref _resetTick);
    }

    public Vector Embed(string text, int dimensions = 0)
    {
        MaybeFail();
        int d = dimensions > 0 ? dimensions : _dims;
        return BuildVector(text, d);
    }

    public Task<Vector> EmbedAsync(string text, CancellationToken ct = default)
    {
        MaybeFail();
        return Task.FromResult(BuildVector(text, _dims));
    }

    private void MaybeFail()
    {
        var n = Interlocked.Increment(ref _processCallCount);
        var envFakeFail       = Environment.GetEnvironmentVariable(EmbeddingProviderResolver.EnvFakeFail);
        var envFakeFailAfter  = Environment.GetEnvironmentVariable(EmbeddingProviderResolver.EnvFakeFailAfter);
        bool forceFail = envFakeFail is { } v1
            && (v1.Equals("true", StringComparison.OrdinalIgnoreCase) || v1 == "1" || v1.Equals("yes", StringComparison.OrdinalIgnoreCase));
        int? failAfter = null;
        if (envFakeFailAfter is { } v2 && int.TryParse(v2, out var f) && f >= 0)
            failAfter = f;

        bool shouldFail = forceFail || (failAfter.HasValue && n > failAfter.Value);
        if (shouldFail)
        {
            throw new InvalidOperationException(
                $"StubEmbeddingService simulated failure (call #{n}, forceFail={forceFail}, failAfter={failAfter}).");
        }
    }

    private static Vector BuildVector(string text, int dims)
    {
        // Stable, deterministic: bucket tokens by SHA-1 hash % dims and
        // L2-normalize. Different from HashEmbeddingService on purpose so
        // the resolver's metadata comparisons catch it as a different
        // provider.
        var v = new float[dims];
        foreach (var token in Tokenize(text))
        {
            if (string.IsNullOrEmpty(token)) continue;
            int bucket = Math.Abs(StableHash(token)) % dims;
            v[bucket] += 1f;
        }
        double norm = 0;
        for (int i = 0; i < dims; i++) norm += v[i] * v[i];
        norm = Math.Sqrt(norm);
        if (norm > 0)
            for (int i = 0; i < dims; i++) v[i] = (float)(v[i] / norm);
        return new Vector(v);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var t in text.ToLowerInvariant()
                              .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':', '(', ')', '[', ']', '/', '"' },
                                     StringSplitOptions.RemoveEmptyEntries))
            yield return t;
    }

    private static int StableHash(string s)
    {
        Span<byte> data = stackalloc byte[Encoding.UTF8.GetByteCount(s)];
        Encoding.UTF8.GetBytes(s, data);
        var digest = SHA1.HashData(data);
        return BitConverter.ToInt32(digest[..4]);
    }
}