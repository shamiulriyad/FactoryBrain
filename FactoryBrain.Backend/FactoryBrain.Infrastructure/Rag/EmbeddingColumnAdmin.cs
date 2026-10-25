using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Rag;

/// <summary>
/// Thrown when an embedding call fails while vectors are being computed. No
/// database write has happened at that point.
/// </summary>
public sealed class EmbeddingProviderUnavailableException : Exception
{
    public EmbeddingProviderUnavailableException(string message) : base(message) { }
    public EmbeddingProviderUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Read-only view of the pgvector column on <c>document_chunks.Embedding</c>
/// plus the dimension guard used by every path that writes a vector (seeder,
/// ingest).
/// <para>
/// This class never resizes the column or re-embeds anything. Those are
/// explicit, Admin-confirmed operations in <see cref="EmbeddingReindexService"/>;
/// startup and the health monitor only report a mismatch.
/// </para>
/// </summary>
public sealed class EmbeddingColumnAdmin
{
    private readonly FactoryBrainDbContext _db;
    private readonly EmbeddingProviderResolver _resolver;
    private readonly ILogger<EmbeddingColumnAdmin> _log;

    public EmbeddingColumnAdmin(
        FactoryBrainDbContext db,
        EmbeddingProviderResolver resolver,
        ILogger<EmbeddingColumnAdmin> log)
    {
        _db = db; _resolver = resolver; _log = log;
    }

    /// <summary>Read the configured dims from the resolver.</summary>
    public int ConfiguredDims => _resolver.Resolve().Configured.Dims;

    /// <summary>
    /// Dimension guard shared by every path that writes to
    /// <c>document_chunks.Embedding</c>. When the freshly-computed vector does
    /// NOT match the live column dim, the chunk is stored with
    /// <c>Embedding = null</c> and the document flipped to <c>"pending"</c>;
    /// a warning names both dimensions. A confirmed reindex fills the gap.
    /// </summary>
    /// <returns><c>null</c> on a dim mismatch; the input vector otherwise.</returns>
    public Pgvector.Vector? GuardVectorDims(
        Pgvector.Vector? vector,
        ManualDocument? applyToRow = null)
    {
        if (vector is null) return null;
        int live;
        try
        {
            live = ProbeLiveColumnDim() ?? _resolver.Resolve().Configured.Dims;
        }
        catch
        {
            live = _resolver.Resolve().Configured.Dims;
        }
        var actual = ((float[])vector.ToArray()).Length;
        if (actual == live) return vector;
        var configured = _resolver.Resolve().Configured;
        _log.LogWarning(
            "Embedding dimension mismatch: vector length={Actual} but live column dim={Live} (configuredProvider={Provider} configuredDims={Configured}). Storing chunk with Embedding=null and provider=pending; an Admin-confirmed reindex will fill it in.",
            actual, live, configured.Provider, configured.Dims);
        if (applyToRow is not null)
        {
            applyToRow.EmbeddingProvider = "pending";
            applyToRow.EmbeddingModel    = "pending";
            applyToRow.Dims              = 0;
        }
        return null;
    }

    /// <summary>
    /// Read the live vector dimension of <c>document_chunks.Embedding</c>
    /// directly from <c>pg_attribute</c>. Returns <c>null</c> when the
    /// column is unsized. Throws when the table or column truly cannot be
    /// inspected (e.g. the relation doesn't exist at all).
    /// Don't call inside a transaction: it opens and closes the connection.
    /// </summary>
    public int? ProbeLiveColumnDim()
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = ColumnDimsSql;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    "Embedding column not found on document_chunks. Run the latest EF migration before starting the API.");
            }
            return ParseDims(reader.GetString(0));
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
        {
            throw new InvalidOperationException(
                "document_chunks table not found. Run the latest EF migration before starting the API.", ex);
        }
        finally
        {
            if (conn.State == System.Data.ConnectionState.Open) conn.Close();
        }
    }

    /// <summary>Non-throwing variant for the query path.</summary>
    public int? TryProbeLiveColumnDim()
    {
        try { return ProbeLiveColumnDim(); }
        catch (InvalidOperationException) { return null; }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01") { return null; }
    }

    internal const string ColumnDimsSql = @"
        SELECT format_type(atttypid, atttypmod)
        FROM pg_attribute
        WHERE attrelid = 'document_chunks'::regclass
          AND attname  = 'Embedding';";

    /// <summary><c>vector(384)</c> → 384; an unsized <c>vector</c> → null.</summary>
    internal static int? ParseDims(string formatted)
    {
        var digits = new string(formatted.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var d) && d > 0 ? d : null;
    }
}
