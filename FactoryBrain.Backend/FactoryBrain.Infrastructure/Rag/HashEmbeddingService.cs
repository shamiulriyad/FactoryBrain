using System.Security.Cryptography;
using System.Text;
using Pgvector;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Deterministic local embedder used when no hosted provider is configured.
/// Hashes tokens into a stable 384-d vector so the RAG index stays
/// reproducible — same input → same vector. This is the byte-for-byte
/// equivalent of the original <c>EmbeddingService</c>, preserved as the
/// offline / demo fallback.
/// </summary>
public sealed class HashEmbeddingService : IEmbeddingService
{
    private readonly int _dims;

    public HashEmbeddingService(int dims = 384)
    {
        _dims = dims > 0 ? dims : 384;
    }

    public string ProviderId => "local";
    public string ModelId => "hash-md5";
    public int Dimensions => _dims;

    public Vector Embed(string text, int dimensions = 0)
    {
        int d = dimensions > 0 ? dimensions : _dims;
        var v = new float[d];
        foreach (var token in Tokenize(text))
        {
            if (string.IsNullOrEmpty(token)) continue;
            int h = Math.Abs(Hash(token)) % d;
            v[h] += 1f;
        }
        // L2 normalise
        double norm = 0;
        for (int i = 0; i < d; i++) norm += v[i] * v[i];
        norm = Math.Sqrt(norm);
        if (norm > 0)
            for (int i = 0; i < d; i++) v[i] = (float)(v[i] / norm);
        return new Vector(v);
    }

    public Task<Vector> EmbedAsync(string text, CancellationToken ct = default)
    {
        return Task.FromResult(Embed(text));
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var t in text.ToLowerInvariant()
                              .Split(new[] { ' ', '\t', '\r', '\n', '\u09BD', ',', '.', '!', '?', ';', ':', '(', ')', '[', ']', '/', '"' },
                                     StringSplitOptions.RemoveEmptyEntries))
            yield return t;
    }

    private static int Hash(string s)
    {
        Span<byte> data = stackalloc byte[Encoding.UTF8.GetByteCount(s)];
        Encoding.UTF8.GetBytes(s, data);
        var digest = MD5.HashData(data);
        return BitConverter.ToInt32(digest[..4]);
    }
}
