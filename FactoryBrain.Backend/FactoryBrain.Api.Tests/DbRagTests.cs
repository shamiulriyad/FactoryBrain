using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Infrastructure.Rag;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector.EntityFrameworkCore;

namespace FactoryBrain.Api.Tests;

/// <summary>Shared database helpers for the RAG tests.</summary>
internal static class Rag
{
    public static async Task<T> WithDb<T>(DbApiFactory api, Func<FactoryBrainDbContext, Task<T>> work)
    {
        using var scope = api.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<FactoryBrainDbContext>());
    }

    /// <summary>Scalar query against the test database (catalog / pgvector functions EF can't express).</summary>
    public static Task<List<string>> Strings(DbApiFactory api, string sql) => WithDb(api, async db =>
    {
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var list = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(r.IsDBNull(0) ? "null" : r.GetValue(0).ToString()!);
            return list;
        }
        finally { await conn.CloseAsync(); }
    });

    public static HttpClient Client(DbApiFactory api, string role)
    {
        var tokens = api.Services.GetRequiredService<FactoryBrain.Application.Abstractions.Interfaces.ITokenService>();
        var token = tokens.IssueAccessToken(new FactoryBrain.Domain.Entities.User
        {
            Id = Guid.NewGuid(), Email = $"{role.ToLowerInvariant()}@rag.test", Role = role,
        }, out _);
        var client = api.RawClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    public static async Task<JsonElement> Ask(HttpClient client, string query)
    {
        using var res = await client.PostAsync("/api/ask", Json(new { query }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}

/// <summary>
/// RAG ingestion and retrieval on real PostgreSQL + pgvector with the local
/// hash embedder (384 dims). The host's outbound-HTTP guard proves no
/// external provider was contacted.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbRagTests
{
    private readonly PostgresStack _stack;
    public DbRagTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Local;

    private void AssertNoOutboundHttp()
    {
        Assert.Empty(Api.Http.Blocked);
        Assert.Empty(Api.Http.Handled);
    }

    [DockerFact]
    public async Task Startup_migrated_the_database_and_seeded_a_384_dim_local_corpus()
    {
        var (applied, pending, all) = await Rag.WithDb(Api, async db =>
            ((await db.Database.GetAppliedMigrationsAsync()).ToList(),
             (await db.Database.GetPendingMigrationsAsync()).ToList(),
             db.Database.GetMigrations().ToList()));
        Assert.Equal(all, applied);
        Assert.Empty(pending);
        Assert.Contains("20261024183943_AddApprovalDecisionsAndAuditLog", applied);

        // Startup's CREATE EXTENSION IF NOT EXISTS installed pgvector in this database.
        var vectorVersion = Assert.Single(await Rag.Strings(Api, "SELECT extversion FROM pg_extension WHERE extname = 'vector'"));
        Assert.Matches(@"^\d+\.\d+\.\d+$", vectorVersion);
        Assert.Equal("vector(384)", (await Rag.Strings(Api,
            "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'document_chunks'::regclass AND attname = 'Embedding'")).Single());

        var docs = await Rag.WithDb(Api, db => db.ManualDocuments.AsNoTracking()
            .Select(d => new { d.EmbeddingProvider, d.EmbeddingModel, d.Dims }).ToListAsync());
        Assert.True(docs.Count >= 8, $"expected the seeded corpus, found {docs.Count} documents");
        Assert.All(docs, d => Assert.Equal(("local", "hash-md5", 384), (d.EmbeddingProvider, d.EmbeddingModel, d.Dims)));

        Assert.Equal(new[] { "384" }, await Rag.Strings(Api, "SELECT DISTINCT vector_dims(\"Embedding\") FROM document_chunks"));
        Assert.Equal("0", (await Rag.Strings(Api, "SELECT count(*) FROM document_chunks WHERE \"Embedding\" IS NULL")).Single());
        AssertNoOutboundHttp();
    }

    [DockerFact]
    public async Task Rag_status_reports_local_provider_with_matching_dimensions()
    {
        using var client = Rag.Client(Api, Roles.Viewer);
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/rag/status"));
        var s = doc.RootElement;
        Assert.Equal("local", s.GetProperty("configuredProvider").GetString());
        Assert.Equal("local", s.GetProperty("activeProvider").GetString());
        Assert.Equal(384, s.GetProperty("dims").GetInt32());
        Assert.Equal(384, s.GetProperty("storedDims").GetInt32());
        Assert.False(s.GetProperty("degraded").GetBoolean());
        Assert.Equal(0, s.GetProperty("pendingCount").GetInt32());
    }

    [DockerFact]
    public async Task Admin_ingest_is_stored_in_pgvector_and_retrieved_by_ask()
    {
        var marker = "vorpalstitch" + Guid.NewGuid().ToString("N")[..6];
        var content =
            $"# {marker} looper calibration SOP\n\n" +
            $"When the {marker} looper skips on heavy denim, lower the looper height by 0.2 mm, " +
            "re-time the needle guard and run twenty test stitches before releasing the head.";

        using var admin = Rag.Client(Api, Roles.Admin);
        using var ingest = await admin.PostAsync("/api/rag/ingest",
            Rag.Json(new { title = $"{marker} looper calibration", source = "sop", tags = new[] { "test" }, content }));
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
        using var ingested = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync());
        var docId = ingested.RootElement.GetProperty("documentId").GetString()!;
        Assert.True(ingested.RootElement.GetProperty("embedded").GetBoolean());
        Assert.Equal("local", ingested.RootElement.GetProperty("provider").GetString());
        Assert.Equal(384, ingested.RootElement.GetProperty("dims").GetInt32());
        var chunkCount = ingested.RootElement.GetProperty("chunks").GetInt32();
        Assert.True(chunkCount >= 1);

        // Persisted rows: one vector(384) per chunk.
        var dims = await Rag.Strings(Api,
            $"SELECT vector_dims(\"Embedding\") FROM document_chunks WHERE \"DocumentId\" = '{docId}'");
        Assert.Equal(chunkCount, dims.Count);
        Assert.All(dims, d => Assert.Equal("384", d));

        // Retrieval through the API (no LLM configured → deterministic answer, real retrieval).
        using var viewer = Rag.Client(Api, Roles.Viewer);
        var answer = await Rag.Ask(viewer, $"{marker} looper skips");
        var hits = answer.GetProperty("ragHits").EnumerateArray().ToList();
        Assert.NotEmpty(hits);
        Assert.Equal(docId, hits[0].GetProperty("sourceId").GetString());
        Assert.True(hits[0].GetProperty("denseScore").GetDouble() > 0, "stored pgvector embedding contributed a dense score");
        Assert.True(hits[0].GetProperty("bm25Score").GetDouble() > 0);
        var citations = answer.GetProperty("citations").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Contains(hits[0].GetProperty("id").GetString(), citations);
        AssertNoOutboundHttp();
    }

    [DockerFact]
    public async Task Pgvector_cosine_search_in_the_database_ranks_the_ingested_document_first()
    {
        var marker = "quillotine" + Guid.NewGuid().ToString("N")[..6];
        using var admin = Rag.Client(Api, Roles.Admin);
        using var ingest = await admin.PostAsync("/api/rag/ingest", Rag.Json(new
        {
            title = $"{marker} trimmer blade",
            source = "manual",
            content = $"The {marker} trimmer blade must be honed every 400 hours and replaced after 2000 hours.",
        }));
        Assert.Equal(HttpStatusCode.Created, ingest.StatusCode);
        var docId = JsonDocument.Parse(await ingest.Content.ReadAsStringAsync()).RootElement.GetProperty("documentId").GetString();

        // Embed the query with the API's own embedder, then let PostgreSQL
        // order by pgvector's <=> operator (translated from CosineDistance).
        var embedder = Api.Services.GetRequiredService<IEmbeddingService>();
        var query = embedder.Embed($"{marker} trimmer blade honing interval");
        Assert.Equal(384, query.ToArray().Length);

        var nearest = await Rag.WithDb(Api, db => db.DocumentChunks.AsNoTracking()
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(query))
            .Select(c => c.DocumentId)
            .FirstAsync());
        Assert.Equal(docId, nearest);
        AssertNoOutboundHttp();
    }

    [DockerFact]
    public async Task Nonsense_query_returns_no_sources_and_no_citations()
    {
        using var viewer = Rag.Client(Api, Roles.Viewer);
        var answer = await Rag.Ask(viewer, "zzqx purple volcano tax");
        Assert.Empty(answer.GetProperty("ragHits").EnumerateArray());
        Assert.Empty(answer.GetProperty("citations").EnumerateArray());
        Assert.Equal("No confident source was found for your query.", answer.GetProperty("finding").GetString());
    }

    [DockerFact]
    public async Task Ingest_validation_and_admin_role_are_enforced_on_the_real_stack()
    {
        var before = await Rag.WithDb(Api, db => db.ManualDocuments.CountAsync());

        using (var op = Rag.Client(Api, Roles.Operator))
        using (var res = await op.PostAsync("/api/rag/ingest", Rag.Json(new { title = "t", source = "sop", content = "c" })))
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        using (var admin = Rag.Client(Api, Roles.Admin))
        using (var res = await admin.PostAsync("/api/rag/ingest", Rag.Json(new { title = "t", source = "not-a-source", content = "c" })))
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        Assert.Equal(before, await Rag.WithDb(Api, db => db.ManualDocuments.CountAsync()));
    }
}

/// <summary>
/// Hosted providers (OpenAI embeddings at 1536 dims and OpenAI chat), with a
/// fake key and every call answered by the local outbound-HTTP guard. Runs on
/// its own test database.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbMockedProviderTests
{
    private readonly PostgresStack _stack;
    public DbMockedProviderTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Api => _stack.Mocked;

    private void AssertOnlyMockedOpenAiCalls()
    {
        Assert.Empty(Api.Http.Blocked);
        Assert.All(Api.Http.Handled, c =>
        {
            // Embeddings use the OpenAI default; chat uses LLM_BASE_URL.
            var expectedHost = c.Uri.AbsolutePath == "/v1/chat/completions" ? DbApiFactory.FakeLlmHost : "api.openai.com";
            Assert.Equal(expectedHost, c.Uri.Host);
            Assert.Equal($"Bearer {Api.ProviderKey}", c.Authorization);
        });
    }

    [DockerFact]
    public async Task Hosted_embeddings_are_never_paid_for_at_startup_and_an_admin_confirmed_reindex_fills_1536_dim_vectors()
    {
        // ── Startup with RAG_EMBEDDING_PROVIDER=openai on a fresh database ──
        // The column keeps its migrated size and the seed is stored pending:
        // the only embedding call startup made is the one-line health probe.
        // (Ask calls before the reindex don't embed the query either: the
        // column can't hold 1536-d vectors yet, so retrieval is keyword-only.)
        var before = Api.Http.Handled.Where(c => c.Uri.AbsolutePath == "/v1/embeddings").ToList();
        Assert.All(before, c => Assert.Contains("\"input\":\"health-check-probe\"", c.Body));
        var column = (await Rag.Strings(Api,
            "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'document_chunks'::regclass AND attname = 'Embedding'")).Single();
        if (column == "vector(384)")
        {
            Assert.Equal("0", (await Rag.Strings(Api, "SELECT count(*) FROM document_chunks WHERE \"Embedding\" IS NOT NULL")).Single());
            Assert.All(await Rag.WithDb(Api, db => db.ManualDocuments.AsNoTracking().Select(d => d.EmbeddingProvider).ToListAsync()),
                p => Assert.Equal("pending", p));
        }

        using var admin = Rag.Client(Api, Roles.Admin);

        // ── Plan: no provider call, reports the resize, counts and cost ──
        var callsBeforePlan = Api.Http.Handled.Count;
        using var planDoc = JsonDocument.Parse(await admin.GetStringAsync("/api/rag/reindex/plan"));
        var plan = planDoc.RootElement;
        Assert.Equal(callsBeforePlan, Api.Http.Handled.Count);
        var docCount = await Rag.WithDb(Api, db => db.ManualDocuments.CountAsync());
        var chunkCount = await Rag.WithDb(Api, db => db.DocumentChunks.CountAsync());
        Assert.Equal(("openai", "text-embedding-3-small", 1536),
            (plan.GetProperty("target").GetProperty("provider").GetString(), plan.GetProperty("target").GetProperty("model").GetString(),
             plan.GetProperty("target").GetProperty("dimensions").GetInt32()));
        if (column == "vector(384)")
        {
            Assert.Equal(384, plan.GetProperty("columnDimensions").GetInt32());
            Assert.True(plan.GetProperty("requiresColumnResize").GetBoolean());
            Assert.Equal(docCount, plan.GetProperty("documents").GetInt32());
            Assert.Equal(chunkCount, plan.GetProperty("chunks").GetInt32());
        }
        Assert.True(plan.GetProperty("estimatedTokensUpperBound").GetInt64() >= plan.GetProperty("bytes").GetInt64());
        // No price configured on this host: the cost is explicitly not estimated.
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("cost").GetProperty("estimateUsd").ValueKind);
        Assert.Contains("can't be estimated reliably", plan.GetProperty("cost").GetProperty("note").GetString());

        // ── Confirmation is required, and a stale/forged plan id is refused ──
        using (var res = await admin.PostAsync("/api/rag/reindex", Rag.Json(new { })))
        {
            Assert.Equal((HttpStatusCode)428, res.StatusCode);
            Assert.Contains("confirmation_required", await res.Content.ReadAsStringAsync());
        }
        using (var res = await admin.PostAsync("/api/rag/reindex", Rag.Json(new { confirmPlanId = "0000000000000000" })))
        {
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Contains("plan_changed", await res.Content.ReadAsStringAsync());
        }
        Assert.Equal(callsBeforePlan, Api.Http.Handled.Count); // still nothing paid

        // ── Confirmed: resize + embed in one transaction ──
        using (var res = await admin.PostAsync("/api/rag/reindex", Rag.Json(new { confirmPlanId = plan.GetProperty("planId").GetString() })))
        {
            var body = await res.Content.ReadAsStringAsync();
            Assert.True(res.StatusCode == HttpStatusCode.OK, body);
            using var outcome = JsonDocument.Parse(body);
            Assert.Equal(1536, outcome.RootElement.GetProperty("columnDimensionsAfter").GetInt32());
        }

        Assert.Equal("vector(1536)", (await Rag.Strings(Api,
            "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'document_chunks'::regclass AND attname = 'Embedding'")).Single());
        Assert.Equal(new[] { "1536" }, await Rag.Strings(Api, "SELECT DISTINCT vector_dims(\"Embedding\") FROM document_chunks"));
        Assert.Equal("0", (await Rag.Strings(Api, "SELECT count(*) FROM document_chunks WHERE \"Embedding\" IS NULL")).Single());

        var docs = await Rag.WithDb(Api, db => db.ManualDocuments.AsNoTracking()
            .Select(d => new { d.EmbeddingProvider, d.EmbeddingModel, d.Dims }).ToListAsync());
        Assert.NotEmpty(docs);
        Assert.All(docs, d => Assert.Equal(("openai", "text-embedding-3-small", 1536), (d.EmbeddingProvider, d.EmbeddingModel, d.Dims)));

        var embeddingCalls = Api.Http.Handled.Where(c => c.Uri.AbsolutePath == "/v1/embeddings").ToList();
        Assert.All(embeddingCalls, c => Assert.Contains("\"model\":\"text-embedding-3-small\"", c.Body));
        Assert.All(embeddingCalls.Skip(before.Count), c => Assert.Contains("\"dimensions\":1536", c.Body));

        Assert.True(await Rag.WithDb(Api, db => db.AuditLog.AnyAsync(a => a.Action == "rag.reindex.completed" && a.ToState == "1536")));

        using var client = Rag.Client(Api, Roles.Viewer);
        using var status = JsonDocument.Parse(await client.GetStringAsync("/api/rag/status"));
        Assert.Equal("openai", status.RootElement.GetProperty("activeProvider").GetString());
        Assert.Equal(1536, status.RootElement.GetProperty("storedDims").GetInt32());
        Assert.False(status.RootElement.GetProperty("degraded").GetBoolean());
        Assert.Equal(0, status.RootElement.GetProperty("pendingCount").GetInt32());
        AssertOnlyMockedOpenAiCalls();
    }

    [DockerFact]
    public async Task Ask_with_a_mocked_llm_sends_retrieved_context_and_returns_the_model_answer()
    {
        var chatCallsBefore = Api.Http.Handled.Count(c => c.Uri.AbsolutePath == "/v1/chat/completions");

        using var viewer = Rag.Client(Api, Roles.Viewer);
        var answer = await Rag.Ask(viewer, "needle breakage on lockstitch machines");

        Assert.Equal(DbApiFactory.FakeLlmFinding, answer.GetProperty("finding").GetString());
        Assert.StartsWith("live-", answer.GetProperty("taskId").GetString());
        var hits = answer.GetProperty("ragHits").EnumerateArray().ToList();
        Assert.NotEmpty(hits);

        var chatCalls = Api.Http.Handled.Where(c => c.Uri.AbsolutePath == "/v1/chat/completions").ToList();
        Assert.Equal(chatCallsBefore + 1, chatCalls.Count);
        var prompt = chatCalls.Last().Body;
        Assert.Contains("\"model\":\"test-chat-model\"", prompt);
        Assert.Contains(hits[0].GetProperty("sourceId").GetString()!, prompt); // retrieved context went to the model
        AssertOnlyMockedOpenAiCalls();
    }
}
