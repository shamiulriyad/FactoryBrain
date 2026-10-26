using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FactoryBrain.Infrastructure.HostedServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace FactoryBrain.Api.Tests;

/// <summary>Whether a Docker engine is reachable. Checked once per test run.</summary>
public static class DockerProbe
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "info --format {{.ServerVersion}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            return p.WaitForExit(15_000) && p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    });

    public static bool IsAvailable => Available.Value;

    public const string SkipReason =
        "Docker engine not reachable: start Docker Desktop to run the PostgreSQL + pgvector integration tests.";
}

/// <summary>[Fact] that is skipped (not failed) when Docker is unavailable.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute() { if (!DockerProbe.IsAvailable) Skip = DockerProbe.SkipReason; }
}

/// <summary>
/// Disposable local PostgreSQL 16 + pgvector for one test run. Credentials
/// are random and test-only; the container is removed when the run ends.
/// Nothing here ever reads the repo's .env.
/// </summary>
public sealed class PostgresStack : IAsyncLifetime
{
    public const string Image = "pgvector/pgvector:pg16";
    public const string LocalDatabase    = "factorybrain_test";
    public const string MockedDatabase   = "factorybrain_test_mocked";
    /// <summary>Used only by the AI settings tests, which save settings and restart hosts on it.</summary>
    public const string SettingsDatabase = "factorybrain_test_settings";
    /// <summary>Used only by the embedding settings / reindex tests (they resize the vector column).</summary>
    public const string EmbeddingDatabase = "factorybrain_test_embedding";
    /// <summary>Used only by the QC inspection tests (deterministic test detector).</summary>
    public const string QcDatabase = "factorybrain_test_qc";
    /// <summary>Used only by the production-planning tests (plant time zone Asia/Dhaka).</summary>
    public const string ProductionDatabase = "factorybrain_test_production";

    private PostgreSqlContainer? _container;
    private readonly Lazy<DbApiFactory> _local;
    private readonly Lazy<DbApiFactory> _mocked;
    private readonly Lazy<DbApiFactory> _qc;
    private readonly Lazy<DbApiFactory> _production;

    public PostgresStack()
    {
        _local  = new(() => Build(DbApiFactory.LocalEmbeddings(ConnectionStringFor(LocalDatabase))));
        _mocked = new(() => Build(DbApiFactory.MockedHostedProviders(ConnectionStringFor(MockedDatabase))));
        _qc     = new(() => Build(DbApiFactory.QcDetector(ConnectionStringFor(QcDatabase))));
        _production = new(() => Build(DbApiFactory.Custom(ConnectionStringFor(ProductionDatabase),
            new() { ["RAG_EMBEDDING_PROVIDER"] = "local", ["PRODUCTION_TIME_ZONE"] = "Asia/Dhaka" })));
    }

    /// <summary>API host whose vision provider is the deterministic TEST detector (an http provider answered by the guard).</summary>
    public DbApiFactory Qc => _qc.Value.Activate();

    /// <summary>API host for the production-planning tests: its own database, plant time zone Asia/Dhaka (UTC+6).</summary>
    public DbApiFactory Production => _production.Value.Activate();

    /// <summary>API host on its own database, local hash embeddings, no outbound HTTP at all.</summary>
    public DbApiFactory Local => _local.Value.Activate();

    /// <summary>API host on a second database, with OpenAI embeddings + chat answered by local fakes.</summary>
    public DbApiFactory Mocked => _mocked.Value.Activate();

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable) return;
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase(LocalDatabase)
            .WithUsername("fb_test")
            .WithPassword(TestEnvironment.RandomSecret(24))
            .WithLabel("factorybrain.purpose", "integration-tests")
            .Build();
        await _container.StartAsync();

        // Pgvector.EntityFrameworkCore 0.2.x registers the vector type on
        // Npgsql's GlobalTypeMapper, which only affects connection pools
        // created after that call. A plain connection opened here with the
        // API's exact connection string would create the pool first and the
        // API would then fail to read vector columns, so this admin
        // connection uses its own (unpooled, differently named) pool.
        var adminCs = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = "postgres", Pooling = false, ApplicationName = "factorybrain-tests-admin",
        };
        await using var admin = new NpgsqlConnection(adminCs.ConnectionString);
        await admin.OpenAsync();
        foreach (var name in new[] { MockedDatabase, SettingsDatabase, EmbeddingDatabase, QcDatabase, ProductionDatabase })
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
    }

    public string ConnectionStringFor(string database)
    {
        if (_container is null) throw new InvalidOperationException(DockerProbe.SkipReason);
        var csb = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = database };
        if (!Program.IsLoopbackDatabase(csb.ConnectionString))
            throw new InvalidOperationException("Test container is not on a loopback address; refusing to continue.");
        return csb.ConnectionString;
    }

    private static DbApiFactory Build(DbApiFactory factory)
    {
        _ = factory.Services; // build the host now: migrations + seed run here
        return factory;
    }

    public async Task DisposeAsync()
    {
        if (_local.IsValueCreated)  await _local.Value.DisposeAsync();
        if (_mocked.IsValueCreated) await _mocked.Value.DisposeAsync();
        if (_qc.IsValueCreated)     await _qc.Value.DisposeAsync();
        if (_production.IsValueCreated) await _production.Value.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresStack>
{
    public const string Name = "postgres";
}

/// <summary>
/// API host in the <c>IntegrationTesting</c> environment: skips .env, runs
/// the real startup (pgvector extension, migrations, reindex, seed) against
/// the given loopback database, and refuses any non-loopback database.
/// </summary>
public sealed class DbApiFactory : WebApplicationFactory<Program>
{
    public string AdminEmail { get; } = "admin@factorybrain.test";
    public string AdminPassword { get; } = TestEnvironment.RandomSecret(18);
    public string ViewerEmail { get; } = "viewer@factorybrain.test";
    public string ViewerPassword { get; } = TestEnvironment.RandomSecret(18);
    public string OperatorEmail { get; } = "operator@factorybrain.test";
    public string OperatorPassword { get; } = TestEnvironment.RandomSecret(18);
    public string ProviderKey { get; } = "test-fake-key-" + TestEnvironment.RandomSecret(12);

    /// <summary>This host's AI settings encryption key (null: the host has none).</summary>
    public string? EncryptionKey { get; private set; } = TestEnvironment.RandomEncryptionKey();

    /// <summary>Every log message (formatted, with exception text) this host wrote.</summary>
    public LogCapture Logs { get; } = new();

    /// <summary>Every email this host sent (none leave the process).</summary>
    public CapturingEmailSender Mail { get; } = new();

    /// <summary>When true, the fake OpenAI embeddings endpoint ignores the requested <c>dimensions</c>.</summary>
    public bool FakeEmbeddingIgnoresDimensions { get; set; }

    /// <summary>When set, the fake embeddings endpoint returns HTTP 500 once this many calls have succeeded.</summary>
    public int? FakeEmbeddingFailAfter { get; set; }

    private int _fakeEmbeddingCalls;

    /// <summary>Restart the fake embeddings failure counter.</summary>
    public void ResetFakeEmbeddingCalls() => Interlocked.Exchange(ref _fakeEmbeddingCalls, 0);

    /// <summary>Every outbound HTTP call this host made.</summary>
    public OutboundHttpGuard Http { get; } = new();

    private readonly string _connectionString;
    private readonly Dictionary<string, string?> _overrides;

    private DbApiFactory(string connectionString, Dictionary<string, string?> overrides, Action<DbApiFactory>? fakes)
    {
        _connectionString = connectionString;
        _overrides = overrides;
        InstallRagFake();
        fakes?.Invoke(this);
    }

    // ── Fake Python RAG service (rag/main.py) ───────────────────────────────

    /// <summary>Host of the fake RAG service (PYRAG_SERVICE_URL). Never resolvable.</summary>
    public const string FakeRagHost = "rag.fake-pyrag.invalid";
    public const string FakeRagCollection = "test_kb";
    public const string FakeRagAnswer = "MOCK-RAG-ANSWER: replace the bearing above 4.5 mm/s";
    public const string FakeRagNoSourceAnswer = "The documents do not cover this.";
    public const string FakeRagSourceFile = "maintenance-manual.pdf";
    public const double FakeRagScore = 0.81;
    /// <summary>A question containing this gets an answer that cites nothing.</summary>
    public const string RagNoSourceMarker = "zzqx";
    /// <summary>A question containing this makes the fake RAG service return HTTP 500.</summary>
    public const string ForceRagFailure = "FORCE-RAG-FAILURE";
    public static readonly string FakeRagKey = "rag-key-" + TestEnvironment.RandomSecret(12);

    /// <summary>Questions the fake RAG service received, with the X-Rag-Api-Key each carried.</summary>
    public ConcurrentQueue<(string Question, string? ApiKey)> RagQuestions { get; } = new();

    private void InstallRagFake() => Http
        .Fake(FakeRagHost, "/health", (_, _) => OutboundHttpGuard.Json("""{"status":"ok","ready":true,"qdrant":"healthy"}"""))
        .Fake(FakeRagHost, $"/api/kb/{FakeRagCollection}/query", (req, body) =>
        {
            using var doc = JsonDocument.Parse(body);
            var question = doc.RootElement.GetProperty("question").GetString() ?? "";
            RagQuestions.Enqueue((question, req.Headers.TryGetValues("X-Rag-Api-Key", out var k) ? k.FirstOrDefault() : null));
            if (question.Contains(ForceRagFailure))
                return OutboundHttpGuard.Json("""{"detail":"Gemini quota exceeded"}""", HttpStatusCode.InternalServerError);
            if (question.Contains(RagNoSourceMarker))
                return OutboundHttpGuard.Json(JsonSerializer.Serialize(new { answer = FakeRagNoSourceAnswer, sources = Array.Empty<object>() }));
            return OutboundHttpGuard.Json(JsonSerializer.Serialize(new
            {
                answer = FakeRagAnswer,
                sources = new[] { new { document_id = "doc-test", chunk_id = "0", page = 2, source = FakeRagSourceFile, score = FakeRagScore, excerpt = "Replace the bearing when vibration exceeds 4.5 mm/s." } }
            }));
        });

    public static DbApiFactory LocalEmbeddings(string connectionString) =>
        new(connectionString, new() { ["RAG_EMBEDDING_PROVIDER"] = "local" }, fakes: null);

    // ── Deterministic TEST detector for the QC inspection workflow ──────────
    // QC_VISION_PROVIDER=http pointed at a host the outbound guard answers:
    // the real HttpDefectDetectionProvider code path, a scripted test model.
    // Never a production provider; its model name says so.

    public const string FakeDetectorHost = "qc.fake-detector.invalid";
    public const string FakeDetectorModel = "TEST-deterministic-detector";
    public static readonly string FakeDetectorKey = "qc-key-" + TestEnvironment.RandomSecret(12);
    /// <summary>A product type containing this makes the detector return HTTP 500.</summary>
    public const string ForceDetectorFailure = "FORCE-QC-FAILURE";
    /// <summary>A product type containing this makes the detector return a body that is not the contract.</summary>
    public const string ForceDetectorInvalid = "FORCE-QC-INVALID";
    /// <summary>A product type containing this makes the detector find nothing.</summary>
    public const string ForceDetectorClean = "FORCE-QC-CLEAN";

    /// <summary>Bodies the test detector received, with the bearer header each carried.</summary>
    public ConcurrentQueue<(string Body, string? Authorization)> DetectorRequests { get; } = new();

    /// <summary>When true the detector fails every call (to test retry), until set back.</summary>
    public bool DetectorDown { get; set; }

    public static DbApiFactory QcDetector(string connectionString) =>
        new(connectionString, new()
        {
            ["RAG_EMBEDDING_PROVIDER"] = "local",
            ["QC_VISION_PROVIDER"]     = "http",
            ["QC_VISION_HTTP_URL"]     = $"https://{FakeDetectorHost}/v1/detect",
            ["QC_VISION_HTTP_API_KEY"] = FakeDetectorKey,
        }, f => f.InstallDetectorFake());

    private void InstallDetectorFake() => Http.Fake(FakeDetectorHost, "/v1/detect", (req, body) =>
    {
        DetectorRequests.Enqueue((body, req.Headers.Authorization?.ToString()));
        if (DetectorDown || body.Contains(ForceDetectorFailure))
            return OutboundHttpGuard.Json("""{"error":"model server overloaded"}""", HttpStatusCode.ServiceUnavailable);
        if (body.Contains(ForceDetectorInvalid))
            return OutboundHttpGuard.Json("""{"predictions":"not the contract"}""");
        if (body.Contains(ForceDetectorClean))
            return OutboundHttpGuard.Json(JsonSerializer.Serialize(new { model = FakeDetectorModel, modelVersion = "t1", localization = true, defects = Array.Empty<object>() }));
        return OutboundHttpGuard.Json(JsonSerializer.Serialize(new
        {
            model = FakeDetectorModel,
            modelVersion = "t1",
            localization = true,
            defects = new object[]
            {
                // Mapped, localised, with a mask outline.
                new { label = "skipped_stitch", confidence = 0.91, box = new { x = 0.10, y = 0.20, width = 0.30, height = 0.10 },
                      polygon = new[] { new[] { 0.10, 0.20 }, new[] { 0.40, 0.20 }, new[] { 0.40, 0.30 } }, severity = "major", description = "gap in seam" },
                // Unmapped label, no box.
                new { label = "weird_mark", confidence = 0.60 },
                // Below the 0.25 default threshold: must not be stored.
                new { label = "stain", confidence = 0.10, box = new { x = 0.5, y = 0.5, width = 0.1, height = 0.1 } },
                // Box outside the image: kept, without localisation.
                new { label = "hole", confidence = 0.80, box = new { x = 0.90, y = 0.10, width = 0.50, height = 0.10 } },
            },
        }));
    });

    /// <summary>Per-host image store under the temp folder, removed with the host.</summary>
    public string QcStoragePath { get; } = Path.Combine(Path.GetTempPath(), "factorybrain-qc-tests", Guid.NewGuid().ToString("N"));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (Directory.Exists(QcStoragePath)) Directory.Delete(QcStoragePath, recursive: true); }
        catch (IOException) { /* best effort: temp folder */ }
    }

    /// <summary>
    /// For the safety-guard test only: no loopback check here, so the API's
    /// own IntegrationTesting guard is what must refuse the connection string.
    /// </summary>
    public static DbApiFactory UncheckedForGuardTest(string connectionString) =>
        new(connectionString, new() { ["RAG_EMBEDDING_PROVIDER"] = "local" }, fakes: null);

    /// <summary>
    /// OpenAI embeddings (text-embedding-3-small, 1536 dims) and OpenAI chat
    /// configured with a fake key; both endpoints are answered by the guard.
    /// </summary>
    public static DbApiFactory MockedHostedProviders(string connectionString)
    {
        var overrides = new Dictionary<string, string?>
        {
            ["RAG_EMBEDDING_PROVIDER"] = "openai",
            ["RAG_EMBEDDING_MODEL"]    = "text-embedding-3-small",
            ["LLM_PROVIDER"]           = "openai",
            ["LLM_MODEL"]              = "test-chat-model",
            // Chat goes to an OpenAI-compatible endpoint other than
            // api.openai.com, so the Ask tests also prove LLM_BASE_URL is used.
            ["LLM_BASE_URL"]           = "https://" + FakeLlmHost,
        };
        return new DbApiFactory(connectionString, overrides, f =>
        {
            f._overrides["RAG_EMBEDDING_API_KEY"] = f.ProviderKey;
            f._overrides["LLM_API_KEY"]           = f.ProviderKey;
            f.InstallOpenAiFakes();
        });
    }

    /// <summary>
    /// A host with exactly the given startup variables (plus the test
    /// defaults) and the fake OpenAI endpoints. Pass
    /// <paramref name="encryptionKey"/> to share a key between hosts, or
    /// <c>""</c> for a host without one.
    /// </summary>
    public static DbApiFactory Custom(string connectionString, Dictionary<string, string?> overrides, string? encryptionKey = null)
        => new(connectionString, new Dictionary<string, string?>(overrides), f =>
        {
            if (encryptionKey is not null) f.EncryptionKey = encryptionKey.Length == 0 ? null : encryptionKey;
            f.InstallOpenAiFakes();
        });

    private void InstallOpenAiFakes() => Http
        .Fake("api.openai.com", "/v1/embeddings", (_, body) =>
        {
            if (FakeEmbeddingFailAfter is { } limit && Interlocked.Increment(ref _fakeEmbeddingCalls) > limit)
                return OutboundHttpGuard.Json("""{"error":{"type":"server_error","code":"test"}}""", HttpStatusCode.InternalServerError);
            return OutboundHttpGuard.Json(FakeEmbeddingResponse(body, 1536, FakeEmbeddingIgnoresDimensions));
        })
        .Fake(FakeLlmHost, "/v1/chat/completions", (_, body) => body.Contains(ForceLlmFailure)
            ? OutboundHttpGuard.Json("""{"error":{"type":"server_error","code":"test"}}""", HttpStatusCode.InternalServerError)
            : OutboundHttpGuard.Json(FakeChatResponse));

    public const string FakeLlmFinding = "MOCK-LLM-ANSWER: grounded in the retrieved manuals";

    /// <summary>Host of the fake OpenAI-compatible chat endpoint (LLM_BASE_URL). Never resolvable.</summary>
    public const string FakeLlmHost = "llm.fake-openai.invalid";

    /// <summary>A question containing this makes the fake chat endpoint return HTTP 500.</summary>
    public const string ForceLlmFailure = "FORCE-LLM-FAILURE";

    private static readonly string FakeChatResponse = JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content = JsonSerializer.Serialize(new { finding = FakeLlmFinding }) } } }
    });

    /// <summary>
    /// Deterministic unit vector derived from the input text. Like the real
    /// API, it honours a <c>dimensions</c> request field (unless told not to).
    /// </summary>
    private static string FakeEmbeddingResponse(string requestBody, int nativeDims, bool ignoreDimensions = false)
    {
        using var doc = JsonDocument.Parse(requestBody);
        var input = doc.RootElement.GetProperty("input").GetString() ?? string.Empty;
        var dims = !ignoreDimensions && doc.RootElement.TryGetProperty("dimensions", out var d) ? d.GetInt32() : nativeDims;
        var seed = BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(input)), 0);
        var rng = new Random(seed);
        var v = new double[dims];
        double norm = 0;
        for (var i = 0; i < dims; i++) { v[i] = rng.NextDouble() - 0.5; norm += v[i] * v[i]; }
        norm = Math.Sqrt(norm);
        for (var i = 0; i < dims; i++) v[i] /= norm;
        return JsonSerializer.Serialize(new { @object = "list", data = new[] { new { @object = "embedding", index = 0, embedding = v } } });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var env = new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"]      = "IntegrationTesting",
            ["DOTNET_ENVIRONMENT"]          = "IntegrationTesting",
            ["FACTORYBRAIN_DB"]             = _connectionString,
            ["JWT_SIGNING_KEY"]             = TestEnvironment.RandomSecret(48),
            ["SEED_ADMIN_EMAIL"]            = AdminEmail,
            ["SEED_ADMIN_PASSWORD"]         = AdminPassword,
            ["SEED_VIEWER_EMAIL"]           = ViewerEmail,
            ["SEED_VIEWER_PASSWORD"]        = ViewerPassword,
            ["SEED_OPERATOR_EMAIL"]         = OperatorEmail,
            ["SEED_OPERATOR_PASSWORD"]      = OperatorPassword,
            ["SIMULATOR_ENABLED"]           = "false",
            ["RAG_HEALTH_INTERVAL_SECONDS"] = "3600",
            ["AI_SETTINGS_ENCRYPTION_KEY"]  = EncryptionKey,
            ["PUBLIC_APP_URL"]              = "https://app.factorybrain.test",
            ["PYRAG_SERVICE_URL"]           = "http://" + FakeRagHost,
            ["PYRAG_API_KEY"]               = FakeRagKey,
            ["PYRAG_COLLECTION_NAME"]       = FakeRagCollection,
            ["QC_IMAGE_STORAGE_PATH"]       = QcStoragePath,
        };
        foreach (var (k, v) in _overrides) env[k] = v;
        _environment = env;
        TestEnvironment.Apply(env);
        return base.CreateHost(builder);
    }

    private Dictionary<string, string?>? _environment;

    /// <summary>
    /// Re-apply this host's process environment (see ApiFactory.Activate): the
    /// API reads LLM / embedding settings from the process environment per
    /// request, and several hosts share this test process. Builds the host
    /// first if needed. Returns this factory for chaining.
    /// </summary>
    public DbApiFactory Activate()
    {
        _ = Services;
        TestEnvironment.Apply(_environment!);
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("IntegrationTesting");
        builder.ConfigureTestServices(services =>
        {
            OutboundHttpGuard.Install(services, Http);
            services.AddSingleton<ILoggerProvider>(Logs);
            services.AddSingleton<FactoryBrain.Application.Abstractions.Interfaces.IEmailSender>(Mail);
            services.AddSingleton<IStartupFilter, TestClientIpStartupFilter>();
            TestEnvironment.RemoveBackgroundServices(services);
        });
    }

    /// <summary>Client that does not store cookies; tests pass the refresh cookie explicitly.</summary>
    public HttpClient RawClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
}

/// <summary>
/// Lets a test choose the client IP the API sees (header
/// <see cref="Header"/>), so per-IP login rate limits don't bleed between
/// tests. Test-only; never registered by the API itself.
/// </summary>
public sealed class TestClientIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Client-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, nextMiddleware) =>
        {
            if (ctx.Request.Headers.TryGetValue(Header, out var raw) && IPAddress.TryParse(raw.ToString(), out var ip))
                ctx.Connection.RemoteIpAddress = ip;
            await nextMiddleware();
        });
        next(app);
    };

    private static int _next = 1;

    /// <summary>A fresh address in TEST-NET-2 (198.51.100.0/24) / TEST-NET-3 for each caller.</summary>
    public static string NewAddress()
    {
        var n = Interlocked.Increment(ref _next);
        return $"198.51.{100 + n / 250 % 2}.{n % 250 + 1}";
    }
}

/// <summary>Collects every log message a test host writes, so tests can check that no secret was logged.</summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);
    public void Dispose() { }

    private sealed class Capturing : ILogger
    {
        private readonly LogCapture _owner;
        private readonly string _category;
        public Capturing(LogCapture owner, string category) { _owner = owner; _category = category; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => _owner._lines.Enqueue($"{logLevel} {_category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>Test email sender: records messages instead of sending them.</summary>
public sealed class CapturingEmailSender : FactoryBrain.Application.Abstractions.Interfaces.IEmailSender
{
    private readonly ConcurrentQueue<FactoryBrain.Application.Dtos.Auth.EmailMessage> _sent = new();
    public IReadOnlyCollection<FactoryBrain.Application.Dtos.Auth.EmailMessage> Sent => _sent.ToArray();

    public Task SendAsync(FactoryBrain.Application.Dtos.Auth.EmailMessage message, CancellationToken ct)
    {
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>Wait (up to 5 s) until <paramref name="count"/> messages to <paramref name="to"/> have been sent.</summary>
    public async Task<List<FactoryBrain.Application.Dtos.Auth.EmailMessage>> WaitFor(string to, int count = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var mine = _sent.Where(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mine.Count >= count || DateTime.UtcNow > deadline) return mine;
            await Task.Delay(50);
        }
    }
}
