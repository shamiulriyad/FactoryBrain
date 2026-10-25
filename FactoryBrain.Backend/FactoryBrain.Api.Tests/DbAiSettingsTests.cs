using System.Net;
using System.Text;
using System.Text.Json;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Settings;
using FactoryBrain.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryBrain.Api.Tests;

/// <summary>Shared helpers for the AI settings tests.</summary>
internal static class AiSettingsTestKit
{
    public static async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> Post(HttpClient c, string path, object body)
    {
        using var res = await c.PostAsync(path, Rag.Json(body));
        var raw = await res.Content.ReadAsStringAsync();
        var json = raw.Length == 0 ? default : JsonDocument.Parse(raw).RootElement.Clone();
        return (res.StatusCode, json, raw);
    }

    public static async Task<(JsonElement Body, string Raw)> Get(HttpClient c, string path)
    {
        using var res = await c.GetAsync(path);
        var raw = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"GET {path} → {(int)res.StatusCode}: {raw}");
        return (JsonDocument.Parse(raw).RootElement.Clone(), raw);
    }

    /// <summary>
    /// Back to "never saved": both groups resolve from the startup
    /// configuration again. This is an out-of-band SQL write, so the host's
    /// cached copy is dropped (as a write through the API would do).
    /// </summary>
    public static async Task ResetSettings(DbApiFactory api)
    {
        await Rag.WithDb(api, db => db.Database.ExecuteSqlRawAsync("DELETE FROM ai_settings"));
        api.Services.GetRequiredService<IAiSettingsProvider>().Invalidate();
    }

    public static int ChatCalls(DbApiFactory api) => api.Http.Handled.Count(c => c.Uri.AbsolutePath == "/v1/chat/completions");
    public static int EmbeddingCalls(DbApiFactory api) => api.Http.Handled.Count(c => c.Uri.AbsolutePath == "/v1/embeddings");

    public static async Task<List<string>> AuditText(DbApiFactory api, string actionPrefix) => await Rag.WithDb(api, db =>
        db.AuditLog.AsNoTracking().Where(a => a.Action.StartsWith(actionPrefix)).OrderBy(a => a.Id)
            .Select(a => a.Action + "|" + a.FromState + "|" + a.ToState + "|" + a.Reason + "|" + a.ActorEmail).ToListAsync());

    public static void AssertNoSecret(string secret, IEnumerable<string> haystacks, string where)
    {
        foreach (var h in haystacks)
            Assert.False(h.Contains(secret, StringComparison.Ordinal), $"secret found in {where}: {h[..Math.Min(h.Length, 200)]}");
    }

    public const string LiveQuestion = "needle breakage on lockstitch machines";
}

/// <summary>
/// LLM settings persisted in PostgreSQL: precedence, restart, multiple
/// instances, concurrency, encryption at rest and secret redaction. Every
/// test starts from "never saved" on its own database and builds the hosts
/// it needs; these tests never re-apply a host's environment between
/// requests, because settings are no longer read from it after startup.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DbAiSettingsTests
{
    private readonly PostgresStack _stack;
    private readonly string _key = TestEnvironment.RandomEncryptionKey();

    public DbAiSettingsTests(PostgresStack stack) { _stack = stack; }

    private static Dictionary<string, string?> BaseEnv(Dictionary<string, string?>? extra = null)
    {
        var env = new Dictionary<string, string?>
        {
            // The only reachable model is the guard's fake (when settings enable one).
            ["LLM_BASE_URL"] = "https://" + DbApiFactory.FakeLlmHost,
            ["AI_SETTINGS_REFRESH_SECONDS"] = "1",
        };
        foreach (var (k, v) in extra ?? new()) env[k] = v;
        return env;
    }

    private DbApiFactory Host(Dictionary<string, string?>? env = null, string? encryptionKey = null)
    {
        var f = DbApiFactory.Custom(_stack.ConnectionStringFor(PostgresStack.SettingsDatabase), BaseEnv(env), encryptionKey ?? _key);
        _ = f.Services; // build now: migrations + seed
        return f;
    }

    private static object LiveBody(string key, string model = "test-chat-model") => new { provider = "openai", apiKey = key, model };

    [DockerFact]
    public async Task Admin_clear_all_returns_demo_and_every_role_can_read_the_status()
    {
        await using var api = Host();
        await AiSettingsTestKit.ResetSettings(api);

        foreach (var role in new[] { Roles.Viewer, Roles.Operator, Roles.Admin })
        {
            using var c = Rag.Client(api, role);
            var (llm, _) = await AiSettingsTestKit.Get(c, "/api/settings/llm");
            Assert.Equal("demo", llm.GetProperty("mode").GetString());
            await AiSettingsTestKit.Get(c, "/api/settings/embedding");
        }

        using var admin = Rag.Client(api, Roles.Admin);
        var (status, body, _) = await AiSettingsTestKit.Post(admin, "/api/settings/llm", new { clearAll = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("demo", body.GetProperty("mode").GetString());
        Assert.Equal("database", body.GetProperty("persistence").GetString());

        using var op = Rag.Client(api, Roles.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await AiSettingsTestKit.Post(op, "/api/settings/llm", new { clearAll = true })).Status);
    }

    [DockerFact]
    public async Task Saved_settings_survive_a_restart_and_beat_a_different_startup_configuration()
    {
        var savedKey = "sk-saved-" + TestEnvironment.RandomSecret(12);
        await using (var first = Host())
        {
            await AiSettingsTestKit.ResetSettings(first);
            using var admin = Rag.Client(first, Roles.Admin);
            var (status, body, _) = await AiSettingsTestKit.Post(admin, "/api/settings/llm", LiveBody(savedKey, "saved-model"));
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("live", body.GetProperty("mode").GetString());
        }

        // Restart with a startup configuration that disagrees on every field.
        var envKey = "sk-env-" + TestEnvironment.RandomSecret(12);
        await using var second = Host(new()
        {
            ["LLM_PROVIDER"] = "anthropic", ["LLM_MODEL"] = "env-model", ["LLM_API_KEY"] = envKey,
        });
        using var viewer = Rag.Client(second, Roles.Viewer);
        var (llm, _) = await AiSettingsTestKit.Get(viewer, "/api/settings/llm");
        Assert.Equal(("openai", "saved-model", "live", "database"),
            (llm.GetProperty("provider").GetString(), llm.GetProperty("model").GetString(),
             llm.GetProperty("mode").GetString(), llm.GetProperty("persistence").GetString()));

        var answer = await Rag.Ask(viewer, AiSettingsTestKit.LiveQuestion);
        Assert.Equal("live", answer.GetProperty("mode").GetString());
        var call = second.Http.Handled.Last(c => c.Uri.AbsolutePath == "/v1/chat/completions");
        Assert.Equal(DbApiFactory.FakeLlmHost, call.Uri.Host);
        Assert.Equal($"Bearer {savedKey}", call.Authorization); // the saved key, not the environment's
        Assert.Contains("\"model\":\"saved-model\"", call.Body);
    }

    [DockerFact]
    public async Task Clearing_disables_live_calls_even_when_the_environment_has_a_key_and_after_a_restart()
    {
        var envKey = "sk-env-" + TestEnvironment.RandomSecret(12);
        var env = new Dictionary<string, string?> { ["LLM_PROVIDER"] = "openai", ["LLM_MODEL"] = "env-model", ["LLM_API_KEY"] = envKey };

        await using (var api = Host(env))
        {
            await AiSettingsTestKit.ResetSettings(api);
            using var viewer = Rag.Client(api, Roles.Viewer);
            var (llm, _) = await AiSettingsTestKit.Get(viewer, "/api/settings/llm");
            Assert.Equal(("live", "startup_config"), (llm.GetProperty("mode").GetString(), llm.GetProperty("persistence").GetString()));
            Assert.Equal("live", (await Rag.Ask(viewer, AiSettingsTestKit.LiveQuestion)).GetProperty("mode").GetString());

            using var admin = Rag.Client(api, Roles.Admin);

            // Clearing only the key (provider and model kept) is enough: the
            // environment's key must not come back for the saved group.
            var cleared = await AiSettingsTestKit.Post(admin, "/api/settings/llm", new { clearKey = true });
            Assert.Equal(HttpStatusCode.OK, cleared.Status);
            Assert.Equal(("openai", "demo"), (cleared.Body.GetProperty("provider").GetString(), cleared.Body.GetProperty("mode").GetString()));
            var chatBefore = AiSettingsTestKit.ChatCalls(api);
            Assert.Equal("demo", (await Rag.Ask(viewer, AiSettingsTestKit.LiveQuestion)).GetProperty("mode").GetString());
            Assert.Equal(chatBefore, AiSettingsTestKit.ChatCalls(api));

            Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(admin, "/api/settings/llm", new { clearAll = true })).Status);

            // The very next request: demo, and the model is not contacted at all.
            Assert.Equal("demo", (await Rag.Ask(viewer, AiSettingsTestKit.LiveQuestion)).GetProperty("mode").GetString());
            Assert.Equal(chatBefore, AiSettingsTestKit.ChatCalls(api));
        }

        // Same environment (key still set) after a restart: still cleared.
        await using var restarted = Host(env);
        using var v2 = Rag.Client(restarted, Roles.Viewer);
        Assert.Equal("demo", (await Rag.Ask(v2, AiSettingsTestKit.LiveQuestion)).GetProperty("mode").GetString());
        Assert.Equal(0, AiSettingsTestKit.ChatCalls(restarted));
        var audit = await AiSettingsTestKit.AuditText(restarted, "settings.llm.");
        Assert.Contains(audit, a => a.StartsWith("settings.llm.updated|live|demo|apiKey: cleared;"));
        Assert.Contains(audit, a => a.StartsWith("settings.llm.cleared|demo|demo|"));
    }

    [DockerFact]
    public async Task A_change_applies_to_the_next_request_here_and_within_the_refresh_interval_on_another_instance()
    {
        await using var a = Host();
        await AiSettingsTestKit.ResetSettings(a);
        await using var b = Host(); // a second API instance on the same database
        using var adminA = Rag.Client(a, Roles.Admin);
        using var viewerA = Rag.Client(a, Roles.Viewer);
        using var viewerB = Rag.Client(b, Roles.Viewer);
        Assert.Equal("demo", (await AiSettingsTestKit.Get(viewerB, "/api/settings/llm")).Body.GetProperty("mode").GetString());

        Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(adminA, "/api/settings/llm", LiveBody("sk-multi-" + TestEnvironment.RandomSecret(8)))).Status);
        Assert.Equal("live", (await Rag.Ask(viewerA, AiSettingsTestKit.LiveQuestion)).GetProperty("mode").GetString()); // same instance: next request

        await WaitForMode(viewerB, "live"); // other instance: within AI_SETTINGS_REFRESH_SECONDS (1 s)
        Assert.Equal("live", (await Rag.Ask(viewerB, AiSettingsTestKit.LiveQuestion)).GetProperty("mode").GetString());

        Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(adminA, "/api/settings/llm", new { clearAll = true })).Status);
        await WaitForMode(viewerB, "demo");
        var chatBefore = AiSettingsTestKit.ChatCalls(b);
        Assert.Equal("demo", (await Rag.Ask(viewerB, AiSettingsTestKit.LiveQuestion)).GetProperty("mode").GetString());
        Assert.Equal(chatBefore, AiSettingsTestKit.ChatCalls(b));
    }

    private static async Task WaitForMode(HttpClient c, string mode)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = (await AiSettingsTestKit.Get(c, "/api/settings/llm")).Body.GetProperty("mode").GetString();
            if (last == mode) return;
            await Task.Delay(100);
        }
        Assert.Fail($"mode stayed '{last}', expected '{mode}' within 5 s (refresh interval is 1 s)");
    }

    [DockerFact]
    public async Task Concurrent_updates_are_serialised_none_is_lost_and_expected_version_detects_conflicts()
    {
        await using var api = Host();
        await AiSettingsTestKit.ResetSettings(api);
        using var admin = Rag.Client(api, Roles.Admin);
        var auditBefore = (await AiSettingsTestKit.AuditText(api, "settings.llm.")).Count;

        const int writers = 20;
        var results = await Task.WhenAll(Enumerable.Range(0, writers).Select(i =>
            AiSettingsTestKit.Post(admin, "/api/settings/llm", new { provider = "openai", model = $"model-{i}" })));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.Status));

        var row = await Rag.WithDb(api, db => db.AiSettings.AsNoTracking().SingleAsync());
        Assert.Equal(writers, row.Version);                          // every write applied, one at a time
        Assert.Contains(row.LlmModel, Enumerable.Range(0, writers).Select(i => $"model-{i}"));
        Assert.Equal(auditBefore + writers, (await AiSettingsTestKit.AuditText(api, "settings.llm.")).Count);
        var versions = results.Select(r => r.Body.GetProperty("version").GetInt64()).ToList();
        Assert.Equal(writers, versions.Distinct().Count());           // each saw its own committed version

        // Optimistic check: N writers that all loaded version V — exactly one wins.
        var v = row.Version;
        var raced = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            AiSettingsTestKit.Post(admin, "/api/settings/llm", new { model = $"raced-{i}", expectedVersion = v })));
        Assert.Equal(1, raced.Count(r => r.Status == HttpStatusCode.OK));
        Assert.Equal(7, raced.Count(r => r.Status == HttpStatusCode.Conflict));
        Assert.All(raced.Where(r => r.Status == HttpStatusCode.Conflict), r => Assert.Contains("version_conflict", r.Raw));
    }

    [DockerFact]
    public async Task Api_keys_are_encrypted_at_rest_and_never_returned_logged_or_audited()
    {
        var secret = "sk-secret-" + TestEnvironment.RandomSecret(16);
        await using var api = Host();
        await AiSettingsTestKit.ResetSettings(api);
        using var admin = Rag.Client(api, Roles.Admin);
        using var viewer = Rag.Client(api, Roles.Viewer);

        var responses = new List<string>();
        var saved = await AiSettingsTestKit.Post(admin, "/api/settings/llm", LiveBody(secret));
        Assert.Equal(HttpStatusCode.OK, saved.Status);
        Assert.True(saved.Body.GetProperty("keySet").GetBoolean());
        Assert.Equal("set", saved.Body.GetProperty("keyStatus").GetString());
        responses.Add(saved.Raw);
        responses.Add((await AiSettingsTestKit.Get(viewer, "/api/settings/llm")).Raw);
        responses.Add((await AiSettingsTestKit.Get(admin, "/api/settings/embedding")).Raw);
        responses.Add((await AiSettingsTestKit.Post(admin, "/api/settings/llm", new { model = "other-model" })).Raw);
        var answer = await Rag.Ask(viewer, AiSettingsTestKit.LiveQuestion);
        responses.Add(answer.GetRawText());

        // The key reached the model (so it was stored and decrypted)…
        Assert.Equal($"Bearer {secret}", api.Http.Handled.Last(c => c.Uri.AbsolutePath == "/v1/chat/completions").Authorization);

        // …but is not in any response, log line, audit row, or the stored bytes.
        AiSettingsTestKit.AssertNoSecret(secret, responses, "an API response");
        AiSettingsTestKit.AssertNoSecret(secret, api.Logs.Lines, "the log");
        var audit = await AiSettingsTestKit.AuditText(api, "settings.");
        AiSettingsTestKit.AssertNoSecret(secret, audit, "the audit log");
        Assert.Contains(audit, a => a.Contains("apiKey: set"));

        var row = await Rag.WithDb(api, db => db.AiSettings.AsNoTracking().SingleAsync());
        Assert.NotNull(row.LlmApiKeyEncrypted);
        Assert.False(Encoding.UTF8.GetString(row.LlmApiKeyEncrypted!).Contains(secret, StringComparison.Ordinal));
        var tableText = await Rag.Strings(api, "SELECT row_to_json(s)::text FROM ai_settings s");
        AiSettingsTestKit.AssertNoSecret(secret, tableText, "ai_settings as text");
        AiSettingsTestKit.AssertNoSecret(_key, tableText, "ai_settings (the encryption key)");
    }

    [DockerFact]
    public async Task An_undecryptable_key_fails_closed_a_missing_encryption_key_refuses_to_store_and_rotation_works()
    {
        var oldKey = _key;
        await using (var first = Host())
        {
            await AiSettingsTestKit.ResetSettings(first);
            using var admin = Rag.Client(first, Roles.Admin);
            Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(admin, "/api/settings/llm", LiveBody("sk-rot-" + TestEnvironment.RandomSecret(8)))).Status);
        }

        // A different encryption key: the stored key can't be read → demo, no model call.
        var newKey = TestEnvironment.RandomEncryptionKey();
        await using (var wrong = Host(encryptionKey: newKey))
        {
            using var viewer = Rag.Client(wrong, Roles.Viewer);
            var (llm, _) = await AiSettingsTestKit.Get(viewer, "/api/settings/llm");
            Assert.Equal(("demo", "unreadable", false), (llm.GetProperty("mode").GetString(), llm.GetProperty("keyStatus").GetString(), llm.GetProperty("keySet").GetBoolean()));
            Assert.Equal("demo", (await Rag.Ask(viewer, AiSettingsTestKit.LiveQuestion)).GetProperty("mode").GetString());
            Assert.Equal(0, AiSettingsTestKit.ChatCalls(wrong));
            Assert.DoesNotContain(wrong.Logs.Lines, l => l.Contains(oldKey) || l.Contains(newKey));
        }

        // No encryption key at all: a key can't be stored; nothing changes.
        await using (var none = Host(encryptionKey: ""))
        {
            using var admin = Rag.Client(none, Roles.Admin);
            var before = await Rag.WithDb(none, db => db.AiSettings.AsNoTracking().Select(r => r.Version).SingleAsync());
            var (status, _, raw) = await AiSettingsTestKit.Post(admin, "/api/settings/llm", LiveBody("sk-nokey"));
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Contains("encryption_key_not_configured", raw);
            Assert.False((await AiSettingsTestKit.Get(admin, "/api/settings/llm")).Body.GetProperty("canStoreKeys").GetBoolean());
            Assert.Equal(before, await Rag.WithDb(none, db => db.AiSettings.AsNoTracking().Select(r => r.Version).SingleAsync()));
        }

        // Rotation: new primary + old as previous decrypts; the next save re-encrypts under the new key…
        await using (var rotating = Host(new() { ["AI_SETTINGS_ENCRYPTION_KEY_PREVIOUS"] = oldKey }, newKey))
        {
            using var admin = Rag.Client(rotating, Roles.Admin);
            Assert.Equal("live", (await AiSettingsTestKit.Get(admin, "/api/settings/llm")).Body.GetProperty("mode").GetString());
            Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(admin, "/api/settings/llm", new { model = "after-rotation" })).Status);
        }
        // …so a host with only the new key can read it.
        await using var rotated = Host(encryptionKey: newKey);
        using var v = Rag.Client(rotated, Roles.Viewer);
        Assert.Equal(("live", "set"), ((await AiSettingsTestKit.Get(v, "/api/settings/llm")).Body.GetProperty("mode").GetString(),
                                        (await AiSettingsTestKit.Get(v, "/api/settings/llm")).Body.GetProperty("keyStatus").GetString()));
    }

    [DockerFact]
    public async Task Switching_provider_without_a_new_key_never_sends_the_old_vendor_key_to_the_new_vendor()
    {
        await using var api = Host();
        await AiSettingsTestKit.ResetSettings(api);
        using var admin = Rag.Client(api, Roles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(admin, "/api/settings/llm", LiveBody("sk-openai-" + TestEnvironment.RandomSecret(8)))).Status);

        var (status, body, _) = await AiSettingsTestKit.Post(admin, "/api/settings/llm", new { provider = "anthropic", model = "claude-test" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("keySet").GetBoolean());
        Assert.Equal("demo", body.GetProperty("mode").GetString());
        Assert.Contains(await AiSettingsTestKit.AuditText(api, "settings.llm."), a => a.Contains("apiKey: cleared (provider changed)"));
    }

    [DockerFact]
    public async Task A_runtime_override_wins_only_inside_its_scope()
    {
        await using var api = Host();
        await AiSettingsTestKit.ResetSettings(api);
        using var admin = Rag.Client(api, Roles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(admin, "/api/settings/llm", LiveBody("sk-ovr-" + TestEnvironment.RandomSecret(8)))).Status);

        var provider = api.Services.GetRequiredService<IAiSettingsProvider>();
        Assert.Equal(AiSettingsSource.Database, (await provider.GetAsync()).Llm.Source);
        using (provider.BeginOverride(new AiSettingsOverride(Llm: LlmGroup.Empty)))
        {
            var inside = await provider.GetAsync();
            Assert.Equal(AiSettingsSource.RuntimeOverride, inside.Llm.Source);
            Assert.False(inside.Llm.IsLive);
            // A different async flow (an HTTP request) is not affected.
            using var viewer = Rag.Client(api, Roles.Viewer);
            Assert.Equal("live", (await Rag.Ask(viewer, AiSettingsTestKit.LiveQuestion)).GetProperty("mode").GetString());
        }
        Assert.True((await provider.GetAsync()).Llm.IsLive);
    }

    [DockerFact]
    public async Task An_llm_key_alone_never_changes_the_embedding_provider_or_dimensions()
    {
        await using var api = Host(new() { ["LLM_PROVIDER"] = "openai", ["LLM_API_KEY"] = "sk-llm-only-" + TestEnvironment.RandomSecret(8) });
        await AiSettingsTestKit.ResetSettings(api);
        using var admin = Rag.Client(api, Roles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(admin, "/api/settings/llm", LiveBody("sk-llm-" + TestEnvironment.RandomSecret(8)))).Status);

        var (emb, _) = await AiSettingsTestKit.Get(admin, "/api/settings/embedding");
        Assert.Equal(("local", "hash-md5", 384, 384), (emb.GetProperty("provider").GetString(), emb.GetProperty("model").GetString(),
            emb.GetProperty("dimensions").GetInt32(), emb.GetProperty("columnDimensions").GetInt32()));
        Assert.False(emb.GetProperty("keySet").GetBoolean());
        Assert.Equal(0, AiSettingsTestKit.EmbeddingCalls(api)); // no hosted embedding call, ever
        Assert.Equal("vector(384)", (await Rag.Strings(api,
            "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'document_chunks'::regclass AND attname = 'Embedding'")).Single());
    }

    [DockerFact]
    public async Task Agent_runs_read_the_same_settings_and_say_the_model_is_not_used()
    {
        await using var api = Host();
        await AiSettingsTestKit.ResetSettings(api);
        using var op = Rag.Client(api, Roles.Operator);
        using var admin = Rag.Client(api, Roles.Admin);

        var demoRun = await AiSettingsTestKit.Post(op, "/api/agents/line-throughput-agent/run", new { });
        Assert.Equal(HttpStatusCode.OK, demoRun.Status);
        Assert.False(demoRun.Body.TryGetProperty("warning", out var w0) && w0.ValueKind == JsonValueKind.String);

        Assert.Equal(HttpStatusCode.OK, (await AiSettingsTestKit.Post(admin, "/api/settings/llm", LiveBody("sk-agent-" + TestEnvironment.RandomSecret(8)))).Status);
        var liveRun = await AiSettingsTestKit.Post(op, "/api/agents/line-throughput-agent/run", new { });
        Assert.Equal(HttpStatusCode.OK, liveRun.Status);
        Assert.Equal(AgentRunService.ModelNotUsedWarning, liveRun.Body.GetProperty("warning").GetString());
        Assert.Equal(0, AiSettingsTestKit.ChatCalls(api));
    }
}
