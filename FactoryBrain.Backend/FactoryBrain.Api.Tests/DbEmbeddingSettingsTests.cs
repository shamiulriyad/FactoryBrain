using System.Net;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using Microsoft.EntityFrameworkCore;

namespace FactoryBrain.Api.Tests;

/// <summary>
/// Embedding provider / dimension changes and reindexing as explicit Admin
/// operations, on their own database: validation, plan and cost, required
/// confirmation, verification against the provider's real output, and
/// all-or-nothing application (provider failure, database failure,
/// concurrent apply). Hosted embeddings are answered by the local fake.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbEmbeddingSettingsTests
{
    private const string ColumnTypeSql =
        "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'document_chunks'::regclass AND attname = 'Embedding'";

    private readonly PostgresStack _stack;
    private readonly string _key = TestEnvironment.RandomEncryptionKey();

    public DbEmbeddingSettingsTests(PostgresStack stack) { _stack = stack; }

    private DbApiFactory Host(Dictionary<string, string?>? env = null)
    {
        var all = new Dictionary<string, string?> { ["AI_SETTINGS_REFRESH_SECONDS"] = "1" };
        foreach (var (k, v) in env ?? new()) all[k] = v;
        var f = DbApiFactory.Custom(_stack.ConnectionStringFor(PostgresStack.EmbeddingDatabase), all, _key);
        _ = f.Services;
        return f;
    }

    /// <summary>
    /// Start every test from the defaults: settings never saved (local/384),
    /// and the column and vectors brought back to local/384 through the
    /// product's own confirmed reindex (free: the local embedder).
    /// </summary>
    private static async Task ResetToLocal384(DbApiFactory api)
    {
        await AiSettingsTestKit.ResetSettings(api);
        await Rag.WithDb(api, db => db.Database.ExecuteSqlRawAsync(
            "UPDATE ai_settings SET \"ReindexLeaseId\" = NULL, \"ReindexLeaseExpiresAt\" = NULL"));
        using var admin = Rag.Client(api, Roles.Admin);
        var (plan, _) = await AiSettingsTestKit.Get(admin, "/api/rag/reindex/plan");
        if (plan.GetProperty("requiresReindex").GetBoolean())
        {
            var applied = await AiSettingsTestKit.Post(admin, "/api/rag/reindex", new { confirmPlanId = plan.GetProperty("planId").GetString() });
            Assert.True(applied.Status == HttpStatusCode.OK, applied.Raw);
        }
        Assert.Equal("vector(384)", (await Rag.Strings(api, ColumnTypeSql)).Single());
    }

    private static async Task<(int Vectors, string Column, string Provider)> Snapshot(DbApiFactory api) =>
        (int.Parse((await Rag.Strings(api, "SELECT count(*) FROM document_chunks WHERE \"Embedding\" IS NOT NULL")).Single()),
         (await Rag.Strings(api, ColumnTypeSql)).Single(),
         (await AiSettingsTestKit.Get(Rag.Client(api, Roles.Viewer), "/api/settings/embedding")).Body.GetProperty("provider").GetString()!);

    [DockerFact]
    public async Task Unsupported_providers_models_and_dimensions_are_rejected_before_anything_runs()
    {
        await using var api = Host();
        await ResetToLocal384(api);
        using var admin = Rag.Client(api, Roles.Admin);

        var cases = new (object Body, string Field)[]
        {
            (new { provider = "gemini", dimensions = 384 }, "dimensions"),
            (new { provider = "openai", model = "text-embedding-ada-002", dimensions = 512 }, "dimensions"),
            (new { provider = "openai", model = "text-embedding-3-small", dimensions = 1537 }, "dimensions"),
            (new { provider = "openai", model = "text-embedding-4-mega" }, "model"),
            (new { provider = "voyage" }, "provider"),
            (new { provider = "stub" }, "provider"), // Development only
        };
        foreach (var (body, field) in cases)
        {
            var plan = await AiSettingsTestKit.Post(admin, "/api/settings/embedding/plan", body);
            Assert.Equal(HttpStatusCode.BadRequest, plan.Status);
            Assert.True(plan.Body.GetProperty("errors").TryGetProperty(field, out _), plan.Raw);
            var save = await AiSettingsTestKit.Post(admin, "/api/settings/embedding", body);
            Assert.Equal(HttpStatusCode.BadRequest, save.Status);
        }
        Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(admin, "/api/settings/embedding/plan",
            new { provider = "openai", model = "text-embedding-3-small", dimensions = 512 })).Status);
        Assert.Empty(api.Http.Handled);
        Assert.Equal("local", (await Snapshot(api)).Provider);
    }

    [DockerFact]
    public async Task The_plan_reports_scope_dimensions_and_an_upper_bound_cost_without_calling_the_provider()
    {
        await using var api = Host(new() { ["RAG_EMBEDDING_PRICE_PER_MILLION_TOKENS_USD"] = "0.02" });
        await ResetToLocal384(api);
        using var admin = Rag.Client(api, Roles.Admin);

        // Nothing to do for the current settings.
        var (same, _) = await AiSettingsTestKit.Get(admin, "/api/rag/reindex/plan");
        Assert.False(same.GetProperty("requiresReindex").GetBoolean());
        Assert.Equal(0, same.GetProperty("documents").GetInt32());
        Assert.Equal(0, same.GetProperty("cost").GetProperty("estimateUsd").GetDouble());

        var docs = await Rag.WithDb(api, db => db.ManualDocuments.CountAsync());
        var chunks = await Rag.WithDb(api, db => db.DocumentChunks.CountAsync());
        var (status, plan, _) = await AiSettingsTestKit.Post(admin, "/api/settings/embedding/plan",
            new { provider = "openai", model = "text-embedding-3-small", dimensions = 1536 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((384, 1536), (plan.GetProperty("columnDimensions").GetInt32(), plan.GetProperty("target").GetProperty("dimensions").GetInt32()));
        Assert.True(plan.GetProperty("requiresColumnResize").GetBoolean());
        Assert.True(plan.GetProperty("reembedsAllDocuments").GetBoolean());
        Assert.Equal((docs, chunks), (plan.GetProperty("documents").GetInt32(), plan.GetProperty("chunks").GetInt32()));
        var bytes = plan.GetProperty("bytes").GetInt64();
        Assert.True(bytes > 0);
        Assert.Equal(bytes, plan.GetProperty("estimatedTokensUpperBound").GetInt64());
        var cost = plan.GetProperty("cost");
        Assert.True(cost.GetProperty("isUpperBound").GetBoolean());
        Assert.Equal(Math.Round(bytes / 1_000_000.0 * 0.02, 6), cost.GetProperty("estimateUsd").GetDouble(), 9);
        Assert.Contains(plan.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("stored vectors are replaced"));
        Assert.Empty(api.Http.Handled); // planning never calls the provider
    }

    [DockerFact]
    public async Task A_provider_change_needs_confirmation_is_verified_against_real_output_and_applies_atomically()
    {
        var secret = "sk-emb-" + TestEnvironment.RandomSecret(16);
        await using var api = Host();
        await ResetToLocal384(api);
        using var admin = Rag.Client(api, Roles.Admin);
        var target = new { provider = "openai", model = "text-embedding-3-small", dimensions = 512, apiKey = secret };
        var before = await Snapshot(api);
        var responses = new List<string>();

        // 1. No confirmation → 428 with the plan; nothing called, nothing written.
        var unconfirmed = await AiSettingsTestKit.Post(admin, "/api/settings/embedding", target);
        Assert.Equal((HttpStatusCode)428, unconfirmed.Status);
        var planId = unconfirmed.Body.GetProperty("plan").GetProperty("planId").GetString()!;
        Assert.Empty(api.Http.Handled);
        responses.Add(unconfirmed.Raw);

        // 2. The provider ignores "dimensions" → the verification call catches it (422), nothing changes.
        api.FakeEmbeddingIgnoresDimensions = true;
        var mismatch = await AiSettingsTestKit.Post(admin, "/api/settings/embedding", new { target.provider, target.model, target.dimensions, target.apiKey, confirmPlanId = planId });
        api.FakeEmbeddingIgnoresDimensions = false;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.Status);
        Assert.Equal((512, 1536), (mismatch.Body.GetProperty("result").GetProperty("expected").GetInt32(), mismatch.Body.GetProperty("result").GetProperty("actual").GetInt32()));
        Assert.Equal(1, AiSettingsTestKit.EmbeddingCalls(api)); // only the verification probe
        Assert.Equal(before, await Snapshot(api));
        responses.Add(mismatch.Raw);

        // 3. The provider fails mid-batch → 502, nothing changes (no partial vectors, no resize, no settings).
        api.ResetFakeEmbeddingCalls();
        api.FakeEmbeddingFailAfter = 4; // probe + 3 chunks succeed, then 500s
        var failed = await AiSettingsTestKit.Post(admin, "/api/settings/embedding", new { target.provider, target.model, target.dimensions, target.apiKey, confirmPlanId = planId });
        api.FakeEmbeddingFailAfter = null;
        Assert.Equal(HttpStatusCode.BadGateway, failed.Status);
        Assert.Equal(before, await Snapshot(api));
        responses.Add(failed.Raw);
        var failures = await AiSettingsTestKit.AuditText(api, "rag.reindex.failed");
        Assert.Contains(failures, a => a.Contains("failure: dimension_mismatch") && a.Contains($"plan: {planId}"));
        Assert.Contains(failures, a => a.Contains("failure: provider_error") && a.Contains("nothing was written"));

        // 4. A stale plan id is refused with the fresh plan.
        var stale = await AiSettingsTestKit.Post(admin, "/api/settings/embedding", new { target.provider, target.model, target.dimensions, target.apiKey, confirmPlanId = "ffffffffffffffff" });
        Assert.Equal(HttpStatusCode.Conflict, stale.Status);
        Assert.Equal(planId, stale.Body.GetProperty("plan").GetProperty("planId").GetString());

        // An unrelated LLM save between review and confirm doesn't invalidate the embedding plan.
        Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(admin, "/api/settings/llm", new { provider = "openai", model = "unrelated-change" })).Status);

        // 5. Confirmed: column resized, every vector 512-d, settings saved, audited.
        var ok =await AiSettingsTestKit.Post(admin, "/api/settings/embedding", new { target.provider, target.model, target.dimensions, target.apiKey, confirmPlanId = planId });
        Assert.True(ok.Status == HttpStatusCode.OK, ok.Raw);
        responses.Add(ok.Raw);
        Assert.Equal("vector(512)", (await Rag.Strings(api, ColumnTypeSql)).Single());
        Assert.Equal(new[] { "512" }, await Rag.Strings(api, "SELECT DISTINCT vector_dims(\"Embedding\") FROM document_chunks"));
        Assert.Equal("0", (await Rag.Strings(api, "SELECT count(*) FROM document_chunks WHERE \"Embedding\" IS NULL")).Single());
        var (emb, embRaw) = await AiSettingsTestKit.Get(admin, "/api/settings/embedding");
        responses.Add(embRaw);
        Assert.Equal(("openai", "text-embedding-3-small", 512, 512, "database", true),
            (emb.GetProperty("provider").GetString(), emb.GetProperty("model").GetString(), emb.GetProperty("dimensions").GetInt32(),
             emb.GetProperty("columnDimensions").GetInt32(), emb.GetProperty("persistence").GetString(), emb.GetProperty("keySet").GetBoolean()));
        Assert.Equal(0, emb.GetProperty("pendingDocuments").GetInt32());
        Assert.All(api.Http.Handled.Where(c => c.Uri.AbsolutePath == "/v1/embeddings"), c =>
        {
            Assert.Equal($"Bearer {secret}", c.Authorization);
            Assert.Contains("\"dimensions\":512", c.Body);
        });
        var audit = await AiSettingsTestKit.AuditText(api, "");
        Assert.Contains(audit, a => a.StartsWith("settings.embedding.updated|local/384|openai/512|") && a.Contains("apiKey: set"));
        Assert.Contains(audit, a => a.StartsWith("rag.reindex.completed|384|512|"));

        // 6. Retrieval now uses the new vectors (the query is embedded at 512 dims).
        var hits = await Rag.Retrieve(api, AiSettingsTestKit.LiveQuestion);
        Assert.True(hits[0].DenseScore != 0);

        // 7. A key-only change needs no plan and re-embeds nothing.
        var calls = AiSettingsTestKit.EmbeddingCalls(api);
        var rotatedKey = "sk-emb2-" + TestEnvironment.RandomSecret(16);
        var keyOnly = await AiSettingsTestKit.Post(admin, "/api/settings/embedding", new { target.provider, target.model, target.dimensions, apiKey = rotatedKey });
        Assert.True(keyOnly.Status == HttpStatusCode.OK, keyOnly.Raw);
        Assert.Equal(JsonValueKind.Null, keyOnly.Body.TryGetProperty("reindex", out var r) ? r.ValueKind : JsonValueKind.Null);
        Assert.Equal(calls, AiSettingsTestKit.EmbeddingCalls(api));
        responses.Add(keyOnly.Raw);

        // 8. An LLM settings change leaves the embedding settings alone.
        Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(admin, "/api/settings/llm", new { provider = "openai", apiKey = "sk-llm-" + TestEnvironment.RandomSecret(8) })).Status);
        Assert.Equal(512, (await AiSettingsTestKit.Get(admin, "/api/settings/embedding")).Body.GetProperty("dimensions").GetInt32());

        // Secrets: nowhere in responses, logs or audit.
        foreach (var s in new[] { secret, rotatedKey })
        {
            AiSettingsTestKit.AssertNoSecret(s, responses, "an API response");
            AiSettingsTestKit.AssertNoSecret(s, api.Logs.Lines, "the log");
            AiSettingsTestKit.AssertNoSecret(s, await AiSettingsTestKit.AuditText(api, ""), "the audit log");
        }

        // Back to local/384 through the same confirmed path; no hosted call.
        calls = AiSettingsTestKit.EmbeddingCalls(api);
        var back = await AiSettingsTestKit.Post(admin, "/api/settings/embedding", new { provider = "local", dimensions = 384 });
        Assert.Equal((HttpStatusCode)428, back.Status);
        var restored = await AiSettingsTestKit.Post(admin, "/api/settings/embedding",
            new { provider = "local", dimensions = 384, confirmPlanId = back.Body.GetProperty("plan").GetProperty("planId").GetString() });
        Assert.True(restored.Status == HttpStatusCode.OK, restored.Raw);
        Assert.Equal("vector(384)", (await Rag.Strings(api, ColumnTypeSql)).Single());
        Assert.Equal(calls, AiSettingsTestKit.EmbeddingCalls(api));
    }

    [DockerFact]
    public async Task A_database_failure_during_the_reindex_rolls_back_the_resize_the_vectors_and_the_settings()
    {
        await using var api = Host();
        await ResetToLocal384(api);
        using var admin = Rag.Client(api, Roles.Admin);
        var before = await Snapshot(api);
        // No row yet means version 0 (nothing has been saved).
        var versionBefore = await Rag.WithDb(api, db => db.AiSettings.AsNoTracking().Select(x => (long?)x.Version).SingleOrDefaultAsync()) ?? 0;

        await Rag.WithDb(api, db => db.Database.ExecuteSqlRawAsync(@"
            CREATE OR REPLACE FUNCTION test_fail_chunk_update() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'injected test failure'; END $$;
            CREATE TRIGGER test_fail_chunk_update BEFORE UPDATE ON document_chunks
                FOR EACH ROW EXECUTE FUNCTION test_fail_chunk_update();"));
        try
        {
            var plan = await AiSettingsTestKit.Post(admin, "/api/settings/embedding/plan", new { provider = "local", dimensions = 256 });
            var res = await AiSettingsTestKit.Post(admin, "/api/settings/embedding",
                new { provider = "local", dimensions = 256, confirmPlanId = plan.Body.GetProperty("planId").GetString() });

            Assert.Equal(HttpStatusCode.InternalServerError, res.Status);
            Assert.Contains("\"traceId\":", res.Raw);
            foreach (var leak in new[] { "injected", "Npgsql", "Exception", "trigger", "Host=" })
                Assert.DoesNotContain(leak, res.Raw, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await Rag.WithDb(api, db => db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER IF EXISTS test_fail_chunk_update ON document_chunks; DROP FUNCTION IF EXISTS test_fail_chunk_update();"));
        }

        Assert.Equal(before, await Snapshot(api)); // column still 384, vectors intact, settings unchanged
        Assert.Equal(versionBefore, await Rag.WithDb(api, db => db.AiSettings.AsNoTracking().Select(x => x.Version).SingleAsync()));
        Assert.Null(await Rag.WithDb(api, db => db.AiSettings.AsNoTracking().Select(x => x.EmbeddingUpdatedAt).SingleAsync()));
        Assert.Contains(await AiSettingsTestKit.AuditText(api, "rag.reindex.failed"), a => a.Contains("failure: database_error"));
        // The lease was released: the next confirmed operation runs.
        Assert.Null(await Rag.WithDb(api, db => db.AiSettings.AsNoTracking().Select(x => x.ReindexLeaseId).SingleAsync()));
    }

    [DockerFact]
    public async Task A_reindex_running_on_another_instance_blocks_a_second_one_until_its_lease_ends()
    {
        await using var api = Host();
        await ResetToLocal384(api);
        using var admin = Rag.Client(api, Roles.Admin);
        var plan = await AiSettingsTestKit.Post(admin, "/api/settings/embedding/plan", new { provider = "local", dimensions = 128 });
        var planId = plan.Body.GetProperty("planId").GetString();

        // Another instance holds the lease.
        var held = await Rag.WithDb(api, db => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO ai_settings (\"Id\", \"Version\", \"UpdatedAt\") VALUES (1, 0, now()) ON CONFLICT (\"Id\") DO NOTHING; " +
            "UPDATE ai_settings SET \"ReindexLeaseId\" = gen_random_uuid(), \"ReindexLeaseExpiresAt\" = now() + interval '5 minutes'"));
        Assert.True(held >= 1);
        var blocked = await AiSettingsTestKit.Post(admin, "/api/settings/embedding", new { provider = "local", dimensions = 128, confirmPlanId = planId });
        Assert.Equal(HttpStatusCode.Conflict, blocked.Status);
        Assert.Contains("reindex_in_progress", blocked.Raw);
        Assert.Equal("vector(384)", (await Rag.Strings(api, ColumnTypeSql)).Single());

        // Its lease expired (the holder crashed): the operation proceeds.
        await Rag.WithDb(api, db => db.Database.ExecuteSqlRawAsync(
            "UPDATE ai_settings SET \"ReindexLeaseExpiresAt\" = now() - interval '1 minute'"));
        var ran = await AiSettingsTestKit.Post(admin, "/api/settings/embedding", new { provider = "local", dimensions = 128, confirmPlanId = planId });
        Assert.True(ran.Status == HttpStatusCode.OK, ran.Raw);
        Assert.Equal("vector(128)", (await Rag.Strings(api, ColumnTypeSql)).Single());
        await ResetToLocal384(api);
    }

    [DockerFact]
    public async Task Startup_never_resizes_or_embeds_and_a_saved_embedding_group_beats_the_startup_configuration()
    {
        await using (var api = Host())
            await ResetToLocal384(api);

        // Never saved + startup asks for OpenAI 1536: no resize, no re-embedding, only the probe.
        var hostedEnv = new Dictionary<string, string?>
        {
            ["RAG_EMBEDDING_PROVIDER"] = "openai", ["RAG_EMBEDDING_MODEL"] = "text-embedding-3-small",
            ["RAG_EMBEDDING_API_KEY"] = "sk-startup-" + TestEnvironment.RandomSecret(8),
        };
        await using (var hosted = Host(hostedEnv))
        {
            Assert.Equal("vector(384)", (await Rag.Strings(hosted, ColumnTypeSql)).Single());
            Assert.All(hosted.Http.Handled, c => Assert.Contains("\"input\":\"health-check-probe\"", c.Body));
            Assert.Equal(1, AiSettingsTestKit.EmbeddingCalls(hosted));
            using var viewer = Rag.Client(hosted, Roles.Viewer);
            var (emb, _) = await AiSettingsTestKit.Get(viewer, "/api/settings/embedding");
            Assert.Equal(("openai", 1536, 384, false, "startup_config"),
                (emb.GetProperty("provider").GetString(), emb.GetProperty("dimensions").GetInt32(), emb.GetProperty("columnDimensions").GetInt32(),
                 emb.GetProperty("dimensionsMatchColumn").GetBoolean(), emb.GetProperty("persistence").GetString()));
            // Retrieval still answers (keyword-only) and does not pay to embed the query.
            await Rag.Retrieve(hosted, AiSettingsTestKit.LiveQuestion);
            Assert.Equal(1, AiSettingsTestKit.EmbeddingCalls(hosted));
        }

        // Save the embedding group (local/384, a same-shape save: no plan needed)…
        await using (var api = Host())
        {
            using var admin = Rag.Client(api, Roles.Admin);
            var saved = await AiSettingsTestKit.Post(admin, "/api/settings/embedding", new { provider = "local", dimensions = 384 });
            Assert.True(saved.Status == HttpStatusCode.OK, saved.Raw);
        }
        // …and the same hosted startup configuration no longer applies.
        await using var after = Host(hostedEnv);
        using var v2 = Rag.Client(after, Roles.Viewer);
        var (e2, _) = await AiSettingsTestKit.Get(v2, "/api/settings/embedding");
        Assert.Equal(("local", 384, "database"), (e2.GetProperty("provider").GetString(), e2.GetProperty("dimensions").GetInt32(), e2.GetProperty("persistence").GetString()));
        Assert.Equal(0, AiSettingsTestKit.EmbeddingCalls(after));

        // Clean up on a host with the default startup configuration (on
        // `after`, "never saved" would resolve to its OpenAI startup config).
        await using var cleanup = Host();
        await ResetToLocal384(cleanup);
    }
}
