using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryBrain.Application.Settings;
using FactoryBrain.Infrastructure.Settings;

namespace FactoryBrain.Api.Tests;

/// <summary>The precedence rule (runtime override → database → startup → defaults), as a pure function.</summary>
public sealed class AiSettingsPrecedenceTests
{
    private static readonly SecretValue EnvKey = new("env-key-" + Guid.NewGuid().ToString("N"));
    private static readonly SecretValue DbKey  = new("db-key-" + Guid.NewGuid().ToString("N"));

    private static readonly StartupAiConfig Startup = new(
        new LlmGroup("anthropic", "env-model", EnvKey, SecretStatus.Set),
        new EmbeddingGroup("openai", "text-embedding-3-large", 1024, EnvKey, SecretStatus.Set),
        LlmBaseUrl: null, EmbeddingPricePerMillionTokensUsd: null);

    [Fact]
    public void With_nothing_configured_the_defaults_apply_demo_and_local_384()
    {
        var s = AiSettingsResolution.Resolve(null, null, StartupAiConfig.Empty);
        Assert.False(s.Llm.IsLive);
        Assert.Equal(AiSettingsSource.Default, s.Llm.Source);
        Assert.Equal(("local", "hash-md5", 384, AiSettingsSource.Default),
            (s.Embedding.Provider, s.Embedding.Model, s.Embedding.Dimensions, s.Embedding.Source));
    }

    [Fact]
    public void Startup_configuration_applies_only_while_a_group_has_never_been_saved()
    {
        var s = AiSettingsResolution.Resolve(null, PersistedAiSettings.None, Startup);
        Assert.Equal(("anthropic", "env-model", EnvKey, AiSettingsSource.StartupConfig),
            (s.Llm.Provider, s.Llm.Model, s.Llm.ApiKey, s.Llm.Source));
        Assert.True(s.Llm.IsLive);
        Assert.Equal(("openai", "text-embedding-3-large", 1024, AiSettingsSource.StartupConfig),
            (s.Embedding.Provider, s.Embedding.Model, s.Embedding.Dimensions, s.Embedding.Source));
    }

    [Fact]
    public void A_saved_group_wins_over_startup_configuration_as_a_whole()
    {
        var persisted = new PersistedAiSettings(
            new LlmGroup("openai", "db-model", DbKey, SecretStatus.Set),
            Embedding: null, Version: 7, UpdatedAt: DateTimeOffset.UtcNow, UpdatedByEmail: "a@b");
        var s = AiSettingsResolution.Resolve(null, persisted, Startup);

        Assert.Equal(("openai", "db-model", DbKey, AiSettingsSource.Database), (s.Llm.Provider, s.Llm.Model, s.Llm.ApiKey, s.Llm.Source));
        // The embedding group was never saved, so it still comes from startup: groups are independent.
        Assert.Equal(AiSettingsSource.StartupConfig, s.Embedding.Source);
        Assert.Equal(7, s.Version);
    }

    [Fact]
    public void A_key_cleared_in_the_database_is_not_refilled_from_the_environment()
    {
        // Saved group with no key: the environment key must NOT leak back in.
        var cleared = new PersistedAiSettings(new LlmGroup("anthropic", "env-model", null), null, 3, null, null);
        var s = AiSettingsResolution.Resolve(null, cleared, Startup);
        Assert.Null(s.Llm.ApiKey);
        Assert.False(s.Llm.IsLive);
        Assert.Equal(AiSettingsSource.Database, s.Llm.Source);

        // clearAll: every field empty, still authoritative.
        var all = new PersistedAiSettings(LlmGroup.Empty, null, 4, null, null);
        Assert.False(AiSettingsResolution.Resolve(null, all, Startup).Llm.IsLive);
    }

    [Fact]
    public void A_runtime_override_wins_over_the_database_and_startup_for_its_group_only()
    {
        var persisted = new PersistedAiSettings(new LlmGroup("openai", "db-model", DbKey, SecretStatus.Set), null, 1, null, null);
        var s = AiSettingsResolution.Resolve(new AiSettingsOverride(Llm: LlmGroup.Empty), persisted, Startup);
        Assert.False(s.Llm.IsLive);
        Assert.Equal(AiSettingsSource.RuntimeOverride, s.Llm.Source);
        Assert.Equal(AiSettingsSource.StartupConfig, s.Embedding.Source); // not overridden
    }

    [Fact]
    public void An_unreadable_stored_key_is_reported_and_treated_as_missing()
    {
        var persisted = new PersistedAiSettings(new LlmGroup("openai", null, null, SecretStatus.Unreadable), null, 1, null, null);
        var s = AiSettingsResolution.Resolve(null, persisted, Startup);
        Assert.Equal(SecretStatus.Unreadable, s.Llm.KeyStatus);
        Assert.False(s.Llm.IsLive);
    }

    [Theory]
    [InlineData("openai", null, true)]      // OpenAI falls back to the default model
    [InlineData("anthropic", null, false)]  // other providers need an explicit model
    [InlineData("anthropic", "claude-x", true)]
    [InlineData("mistral", "m", false)]     // unsupported provider is never live
    public void Live_requires_a_supported_provider_a_key_and_a_model(string provider, string? model, bool live)
    {
        var s = AiSettingsResolution.Resolve(null,
            new PersistedAiSettings(new LlmGroup(provider, model, DbKey, SecretStatus.Set), null, 1, null, null), StartupAiConfig.Empty);
        Assert.Equal(live, s.Llm.IsLive);
    }

    [Fact]
    public void An_llm_key_in_the_startup_configuration_never_changes_the_embedding_provider()
    {
        var llmOnly = new StartupAiConfig(new LlmGroup("openai", null, EnvKey, SecretStatus.Set), EmbeddingGroup.Empty, null, null);
        var s = AiSettingsResolution.Resolve(null, null, llmOnly);
        Assert.True(s.Llm.IsLive);
        Assert.Equal(("local", 384), (s.Embedding.Provider, s.Embedding.Dimensions));
        Assert.Null(s.Embedding.ApiKey);
    }

    [Fact]
    public void Startup_configuration_is_parsed_from_the_environment_once()
    {
        var env = new Dictionary<string, string?>
        {
            ["LLM_PROVIDER"] = " OpenAI ", ["LLM_MODEL"] = "m", ["LLM_API_KEY"] = "k1",
            ["RAG_EMBEDDING_PROVIDER"] = "openai", ["RAG_EMBEDDING_DIMENSIONS"] = "512", ["RAG_EMBEDDING_API_KEY"] = "k2",
            ["RAG_EMBEDDING_PRICE_PER_MILLION_TOKENS_USD"] = "0.02", ["LLM_BASE_URL"] = "http://127.0.0.1:9",
        };
        var c = StartupAiConfig.FromEnvironment(n => env.GetValueOrDefault(n));
        env["LLM_API_KEY"] = "changed-later";

        Assert.Equal(("openai", "m", "k1"), (c.Llm.Provider, c.Llm.Model, c.Llm.ApiKey!.Reveal()));
        Assert.Equal(("openai", 512, "k2"), (c.Embedding.Provider, c.Embedding.Dimensions, c.Embedding.ApiKey!.Reveal()));
        Assert.Equal(0.02, c.EmbeddingPricePerMillionTokensUsd);
        Assert.Equal("http://127.0.0.1:9", c.LlmBaseUrl);
    }
}

public sealed class SecretValueTests
{
    [Fact]
    public void A_secret_never_appears_in_ToString_records_or_json()
    {
        var raw = "sk-" + Guid.NewGuid().ToString("N");
        var secret = new SecretValue(raw);
        Assert.Equal("[redacted]", secret.ToString());
        Assert.Equal(raw, secret.Reveal());

        var group = new LlmGroup("openai", "m", secret, SecretStatus.Set);
        Assert.DoesNotContain(raw, group.ToString());                       // record-generated ToString
        Assert.DoesNotContain(raw, $"{new LlmSettings("openai", "m", secret, SecretStatus.Set, AiSettingsSource.Database)}");
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(group)); // refuses rather than leaks
    }
}

public sealed class AiSecretProtectorTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Round_trip_uses_fresh_nonces_and_never_stores_plaintext()
    {
        var p = new AiSecretProtector(NewKey());
        const string secret = "sk-roundtrip-value-1234567890";
        var a = p.Protect(secret, AiSecretProtector.LlmApiKeyPurpose);
        var b = p.Protect(secret, AiSecretProtector.LlmApiKeyPurpose);

        Assert.NotEqual(a, b);
        Assert.False(Contains(a, Encoding.UTF8.GetBytes(secret)));
        Assert.True(p.TryUnprotect(a, AiSecretProtector.LlmApiKeyPurpose, out var back));
        Assert.Equal(secret, back);
    }

    [Fact]
    public void Wrong_key_tampering_and_a_swapped_column_all_fail_authentication()
    {
        var key = NewKey();
        var p = new AiSecretProtector(key);
        var blob = p.Protect("sk-x", AiSecretProtector.LlmApiKeyPurpose);

        Assert.False(new AiSecretProtector(NewKey()).TryUnprotect(blob, AiSecretProtector.LlmApiKeyPurpose, out _));
        Assert.False(p.TryUnprotect(blob, AiSecretProtector.EmbeddingApiKeyPurpose, out _)); // AAD binds the column

        var tampered = (byte[])blob.Clone();
        tampered[^1] ^= 0x01;
        Assert.False(p.TryUnprotect(tampered, AiSecretProtector.LlmApiKeyPurpose, out _));
        Assert.False(p.TryUnprotect(blob[..10], AiSecretProtector.LlmApiKeyPurpose, out _));
        Assert.False(new AiSecretProtector(null).TryUnprotect(blob, AiSecretProtector.LlmApiKeyPurpose, out _));
    }

    [Fact]
    public void A_previous_key_decrypts_for_rotation_and_is_detected()
    {
        var oldKey = NewKey();
        var blob = new AiSecretProtector(oldKey).Protect("sk-old", AiSecretProtector.LlmApiKeyPurpose);
        var rotated = new AiSecretProtector(NewKey(), oldKey);

        Assert.True(rotated.TryUnprotect(blob, AiSecretProtector.LlmApiKeyPurpose, out var v));
        Assert.Equal("sk-old", v);
        Assert.True(rotated.UsesPreviousKey(blob));
        Assert.False(rotated.UsesPreviousKey(rotated.Protect("sk-new", AiSecretProtector.LlmApiKeyPurpose)));
    }

    [Fact]
    public void Without_a_key_nothing_can_be_encrypted()
    {
        var p = new AiSecretProtector(null);
        Assert.False(p.CanEncrypt);
        Assert.Throws<AiEncryptionKeyMissingException>(() => p.Protect("sk", AiSecretProtector.LlmApiKeyPurpose));
    }

    [Theory]
    [InlineData("not base64 !!")]
    [InlineData("c2hvcnQ=")] // 5 bytes
    public void A_malformed_key_fails_at_startup_without_echoing_it(string raw)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AiSecretProtector.FromEnvironment(n => n == AiSecretProtector.EnvKey ? raw : null));
        Assert.DoesNotContain(raw, ex.Message);
    }

    [Fact]
    public void The_key_can_come_from_a_secret_file()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Convert.ToBase64String(NewKey()) + "\n");
            var p = AiSecretProtector.FromEnvironment(n => n == AiSecretProtector.EnvKeyFile ? path : null);
            Assert.True(p.CanEncrypt);
        }
        finally { File.Delete(path); }
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }
}

public sealed class EmbeddingCatalogTests
{
    [Theory]
    [InlineData("openai", "text-embedding-3-small", 512, true)]
    [InlineData("openai", "text-embedding-3-small", 1536, true)]
    [InlineData("openai", "text-embedding-3-small", 1537, false)]
    [InlineData("openai", "text-embedding-3-large", 3072, true)]
    [InlineData("openai", "text-embedding-3-large", 0, false)]
    [InlineData("openai", "text-embedding-ada-002", 1536, true)]
    [InlineData("openai", "text-embedding-ada-002", 512, false)]  // ada can't be shortened
    [InlineData("gemini", "text-embedding-004", 768, true)]
    [InlineData("gemini", "text-embedding-004", 384, false)]
    [InlineData("local", "hash-md5", 384, true)]
    [InlineData("local", "hash-md5", 5000, false)]
    public void Dimensions_are_checked_against_what_the_model_can_return(string provider, string model, int dims, bool ok)
    {
        var (_, _, _, errors) = EmbeddingCatalog.Normalize(provider, model, dims);
        Assert.Equal(ok, errors.Count == 0);
    }

    [Fact]
    public void Unknown_providers_and_models_are_rejected_not_guessed()
    {
        Assert.Contains("provider", EmbeddingCatalog.Normalize("voyage", null, null).Errors.Keys);
        Assert.Contains("model", EmbeddingCatalog.Normalize("openai", "text-embedding-4-mega", null).Errors.Keys);
    }

    [Fact]
    public void Omitted_model_and_dimensions_take_the_catalogue_defaults()
    {
        Assert.Equal(("openai", "text-embedding-3-small", 1536), Trim(EmbeddingCatalog.Normalize("openai", null, null)));
        Assert.Equal(("local", "hash-md5", 384), Trim(EmbeddingCatalog.Normalize(null, null, null)));
        Assert.Equal(("gemini", "text-embedding-004", 768), Trim(EmbeddingCatalog.Normalize("GEMINI", null, null)));
    }

    private static (string, string, int) Trim((string P, string M, int D, Dictionary<string, string> E) r) => (r.P, r.M, r.D);
}
