// FactoryBrain.Api — Program.cs
// ---------------------------------------------------------------------------
// ASP.NET Core 9 entry point for the FactoryBrain Web API backend.
//
// Frontend bridge:
//   The Next.js dev server (next.config.mjs `rewrites`) proxies /api/* to
//   http://localhost:5000/api/* by default, or to $DOTNET_API_URL when set.
//   That makes the entire frontend (Next.js App Router + Zustand stores +
//   GSAP/Framer Motion components) continue to talk to a familiar origin
//   while data and compute move to a .NET 9 service.
//
// Configuration loading order (later overrides earlier):
//   1. appsettings.json
//   2. appsettings.{Environment}.json
//   3. .env  (DotNetEnv — the single repo-root file shared with Next.js and
//      rag/; found by walking up from the working directory; non-clobbering)
//   4. .env.local (DotNetEnv — overrides .env; non-clobbering)
//   5. OS environment variables (and anything the host sets before launch)
//
// IMPORTANT: both Env.Load calls below use LoadOptions(clobberExistingVars:
// false) so a host-set env var (e.g. FACTORYBRAIN_DB on a Docker / CI box,
// RAG_EMBEDDING_API_KEY on the operator's shell) always wins over what is
// committed in .env.
//
// LLM and embedding settings (see docs/AI_SETTINGS_DESIGN.md):
//   One IAiSettingsProvider decides them, per group (LLM, embedding):
//   runtime override > saved settings (ai_settings table, once that group has
//   been saved) > startup configuration > defaults. The startup configuration
//   is read ONCE below:
//     LLM_PROVIDER / LLM_MODEL / LLM_API_KEY / LLM_BASE_URL
//     RAG_EMBEDDING_PROVIDER = local | openai | gemini   (default: local)
//     RAG_EMBEDDING_MODEL / RAG_EMBEDDING_DIMENSIONS / RAG_EMBEDDING_API_KEY
//   An LLM key never changes the embedding provider or dimensions, and
//   embeddings never use LLM_API_KEY. Saved API keys are AES-GCM encrypted
//   with AI_SETTINGS_ENCRYPTION_KEY (or _FILE), which never goes in the DB.
//   Startup never resizes the vector column and never re-embeds: an Admin
//   reviews a plan and confirms it (POST /api/rag/reindex).
//
// Health check:
//   GET /health → 200 OK when PostgreSQL is reachable, otherwise 503.
// ---------------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using System.Threading.RateLimiting;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using DotNetEnv;
using FactoryBrain.Infrastructure.Persistence;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Infrastructure.Services;
using FactoryBrain.Infrastructure.HostedServices;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Application.Rag;
using FactoryBrain.Infrastructure.Rag;
using FactoryBrain.Infrastructure.Configuration;
using FactoryBrain.Application.Settings;
using FactoryBrain.Infrastructure.Settings;
using FactoryBrain.Api.Realtime;
using FactoryBrain.Api.Hubs;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

// LoadOptions(clobberExistingVars: false) means: any variable the host
// already set (FACTORYBRAIN_DB, ASPNETCORE_*, OS secrets, CI vars) keeps
// its value. Variables ONLY present in the file are loaded. This is what
// makes "FACTORYBRAIN_DB on the command line" actually win over the
// committed `.env` — without it, the file would silently overwrite the
// host's value on every restart.
//
// Because neither load clobbers, the FIRST file to define a key wins — so
// .env.local is loaded before .env to make it the override (host > .env.local
// > .env), as documented in the loading-order list above.
//
// .env itself is NOT read from the working directory only: onlyExactPath:
// false walks up parent folders until it finds one, so `dotnet run` from
// FactoryBrain.Api/ picks up the single .env at the repo root.
//
// The integration tests run with ASPNETCORE_ENVIRONMENT=Testing (no
// database) or IntegrationTesting (a disposable local PostgreSQL) and must
// never pick up the developer's repo-root .env (which can point at a
// remote database and carry real provider keys), so the files are skipped
// in both environments.
var hostEnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
var isTestingHost =
    string.Equals(hostEnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(hostEnvironmentName, "IntegrationTesting", StringComparison.OrdinalIgnoreCase);
if (!isTestingHost)
{
    Env.Load(".env.local",  new LoadOptions(clobberExistingVars: false));
    Env.Load(".env",        new LoadOptions(clobberExistingVars: false, onlyExactPath: false));
}

var builder = WebApplication.CreateBuilder(args);

// ─── Logging ────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.IncludeScopes = false;
    o.TimestampFormat = "HH:mm:ss ";
});

// ─── HTTP / JSON ───────────────────────────────────────────────────────────
builder.Services
    .AddControllers()
    // Coerce every [ApiController] model-binding failure (malformed JSON,
    // empty body, [Required] misses, type mismatches) into the same RFC 7807
    // ValidationProblemDetails envelope the FluentValidationFilter emits.
    // Without this override the framework returns a default
    // ValidationProblemDetails 400 with type names / JSON paths / byte
    // positions in the messages; we redirect every failure through
    // ValidationProblemFactory so the messages collapse to one of three
    // canonical shapes (required / wrong type / unparseable JSON) and the
    // keys become camelCase field names (or "body" for parse failures).
    .ConfigureApiBehaviorOptions(o =>
    {
        o.InvalidModelStateResponseFactory = ctx =>
        {
            // Step 51-fix-3: the model-state funnel runs in four steps.
            //   (1) Resolve the bound action parameter's name AND CLR type
            //       once per request. The type lets the funnel decide
            //       whether a candidate whole-request key (e.g. the
            //       actionParamName itself, or a future binding literally
            //       named "model") is also a real property on the DTO —
            //       if it is, the error is treated as a real field and
            //       must NOT collapse onto "body".
            //   (2) Flatten ModelState into (key, message, exception) and
            //       scrub each one onto one of the three canonical shapes
            //       (required / wrong type / unparseable JSON) via the
            //       factory's ScrubModelStateMessage.
            //   (3) Drop whole-request "required" entries when at least
            //       one more-specific error exists. This collapses
            //       {query:123} -> only errors.query and
            //       notjson       -> only errors.body.
            //   (4) Normalise each remaining key (strip "$." / "$", camel-
            //       case the last segment, fold the synthetic body
            //       spellings, including the bound action parameter's
            //       name when it is not a real DTO property) via
            //       ValidationProblemFactory.NormaliseModelStateKey, then
            //       hand the canonicalised errors to the factory.
            var (actionParamName, actionParamType) = ResolveBodyParam(ctx);

            var scrubbed = ctx.ModelState
                .Where(kvp => kvp.Value is not null && kvp.Value!.Errors.Count > 0)
                .SelectMany(kvp => kvp.Value!.Errors
                    .Select(e => new
                    {
                        Raw   = kvp.Key,
                        Canon = ValidationProblemFactory.ScrubModelStateMessage(
                            new ValidationProblemError(
                                kvp.Key,
                                e.ErrorMessage ?? e.Exception?.Message,
                                e.Exception)),
                    }))
                .Where(x => !string.IsNullOrEmpty(x.Canon))
                .ToList();

            var totalCount = scrubbed.Count;
            var raw = scrubbed
                .Where(x => !(totalCount > 1
                              && ValidationProblemFactory.IsWholeRequestModelStateKey(
                                  x.Raw, actionParamName, actionParamType)
                              && ValidationProblemFactory.IsRequiredCanonicalMessage(x.Canon)))
                .Select(x => new ValidationProblemError(
                    ValidationProblemFactory.NormaliseModelStateKey(
                        x.Raw, actionParamName, actionParamType),
                    x.Canon,
                    null));

            return ValidationProblemFactory.BuildResult(ctx.HttpContext, raw);
        };
    })
    .AddJsonOptions(o =>
    {
        // Hard backstop — when false, the System.Text.Json input formatter
        // records a bare field name in ModelState and drops the raw
        // exception text (the one with .NET type names, JSON paths, line
        // and byte positions). Even with this off, the factory above
        // rewrites any message that did slip through, so this is
        // belt-and-braces.
        o.AllowInputFormatterExceptionMessages = false;

        o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    });

// ─── RFC 7807 ProblemDetails ───────────────────────────────────────────────
// Step 51: register the IExceptionHandler that funnels uncaught throws
// into application/problem+json (see GlobalExceptionHandler.cs). Also add
// the framework's ProblemDetails service so UseStatusCodePages emits
// ProblemDetails-shaped bodies for bare 404 / 405 responses, and so any
// future IResult-returning route can opt into ProblemDetails without
// repeating the formatter wiring.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// ─── PostgreSQL + EF Core (FactoryBrainDbContext) ─────────────────────────
var pgConn =
    Environment.GetEnvironmentVariable("FACTORYBRAIN_DB")
    ?? Environment.GetEnvironmentVariable("DOTNET_CONNECTION_STRING")
    ?? builder.Configuration.GetConnectionString("Postgres")
    ?? "Host=localhost;Port=5432;Database=factorybrain;Username=postgres;Password=postgres;Include Error Detail=true";

// Development conveniences (detailed EF errors, SQL parameter values in
// logs) are only safe against a database on this machine. Against a remote
// or shared database they would write real row data into the logs, so they
// are switched off and the operator is told why.
var dbIsLoopback = IsLoopbackDatabase(pgConn);
// IntegrationTesting migrates and seeds the database it is given, so it
// must only ever run against a disposable database on this machine.
if (builder.Environment.IsEnvironment("IntegrationTesting") && !dbIsLoopback)
{
    throw new InvalidOperationException(
        "ASPNETCORE_ENVIRONMENT=IntegrationTesting only runs against a loopback database " +
        "(it applies migrations and seeds data). Point FACTORYBRAIN_DB at a local test database.");
}
if (builder.Environment.IsDevelopment() && !dbIsLoopback)
{
    Console.WriteLine(
        "[startup] Development environment with a non-local database host: " +
        "EF sensitive-data logging stays OFF. Authentication and role checks are " +
        "enforced exactly as in Production.");
}

builder.Services.AddDbContext<FactoryBrainDbContext>(opts =>
{
    opts.UseNpgsql(pgConn, npg =>
    {
        npg.EnableRetryOnFailure(maxRetryCount: 3);
        npg.MigrationsHistoryTable("__ef_migrations");
        npg.UseVector();
    });
    if (builder.Environment.IsDevelopment() && dbIsLoopback)
    {
        opts.EnableDetailedErrors();
        opts.EnableSensitiveDataLogging();
    }
});

// ─── FluentValidation (assembly scan + global action filter) ──────────────
// Validators live in FactoryBrain.Application (AbstractValidator<TDto> for
// the DTOs that live alongside them), so the scan must run over that
// assembly — not over the Api assembly, which no longer holds any
// validators.
//
// Step 51: every controller's POST body is validated by the global
// FluentValidationFilter below. The filter resolves IValidator<T> for
// each action argument whose concrete type has a registered validator,
// runs the rules, and short-circuits with a 400 ValidationProblemDetails
// (RFC 7807 with an errors map) on failure. Routes that already return
// their own explicit envelope (AdminToken* / 409 degraded reindex /
// controller-emitted BadRequest for LlmSettingsRequest invalid_provider)
// keep that envelope — the filter never replaces a controller's own
// explicit BadRequest / Conflict / Forbid result.
builder.Services.AddValidatorsFromAssemblyContaining<FactoryBrain.Application.Dtos.Ask.AskRequest>();
builder.Services.AddScoped<FluentValidationFilter>();
builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o =>
{
    o.Filters.AddService<FluentValidationFilter>();
});

// ─── JWT signing key resolution ────────────────────────────────────────────
// Bind JwtOptions from configuration first (Issuer/Audience come from
// appsettings.json), then overlay the runtime signing key from the
// JWT_SIGNING_KEY env var (preferred over the Jwt__SigningKey config
// value). Production: the env var is REQUIRED and must produce ≥ 32 UTF-8
// bytes — startup throws otherwise. Development: a missing key falls
// back to a per-process random 32-byte key (NEVER logged) so a fresh
// checkout boots without ceremony.
var jwtSection = builder.Configuration.GetSection("Jwt");
builder.Services.Configure<JwtOptions>(jwtSection);
var jwtOpts = jwtSection.Get<JwtOptions>() ?? new JwtOptions();

var envKey = Environment.GetEnvironmentVariable("JWT_SIGNING_KEY");
var envKeyBytes = string.IsNullOrEmpty(envKey) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(envKey);
if (envKeyBytes.Length >= 32)
{
    jwtOpts.SigningKeyBytes = envKeyBytes;
}
else if (builder.Environment.IsDevelopment())
{
    // Generate a per-process random 32-byte key. Log only the LENGTH so
    // the key itself never leaks via diagnostic output.
    var generated = RandomNumberGenerator.GetBytes(32);
    jwtOpts.SigningKeyBytes = generated;
    Console.WriteLine(
        "[startup] JWT_SIGNING_KEY missing/short in Development — generated random 32-byte key (length=32). Tokens will not survive a restart.");
}
else
{
    throw new InvalidOperationException(
        "JWT_SIGNING_KEY is missing or shorter than 32 UTF-8 bytes. " +
        "Production requires a signing key of at least 32 bytes; " +
        "set JWT_SIGNING_KEY in the environment before starting the service.");
}

// Re-bind the (possibly overridden) options so the registered IOptions<JwtOptions>
// carries the resolved bytes into TokenService at runtime.
builder.Services.PostConfigure<JwtOptions>(o => { o.SigningKeyBytes = jwtOpts.SigningKeyBytes; });

// ─── Authentication (JWT bearer) ───────────────────────────────────────────
// The bearer scheme backs the fallback (authenticated) policy and the
// Operator / Admin role policies below. Issuer / Audience / SigningKey come from
// the resolved JwtOptions above.
var jwtIssuer   = jwtOpts.Issuer   ?? "factorybrain";
var jwtAudience = jwtOpts.Audience ?? "factorybrain";
var jwtKey      = new SymmetricSecurityKey(jwtOpts.SigningKeyBytes);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false; // keep "sub" / "email" claim names verbatim
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = jwtIssuer,
            ValidateAudience         = true,
            ValidAudience            = jwtAudience,
            ValidateLifetime         = true,
            ClockSkew                = TimeSpan.FromSeconds(jwtOpts.ClockSkewSeconds),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = jwtKey,
            NameClaimType            = JwtRegisteredClaimNames.Email,
            RoleClaimType            = System.Security.Claims.ClaimTypes.Role,
        };

        // Browsers can't set Authorization headers on WebSocket upgrade
        // requests, so for /hubs/* we also accept the access token via
        // the `access_token` query string. Restricted to the hub path:
        // every other route keeps header-only auth. The query-string
        // value is consumed by the handler — it never reaches the
        // hub method or the log line.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var path = ctx.Request.Path;
                if (path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
                {
                    if (ctx.Request.Query.TryGetValue("access_token", out var tokenValues)
                        && !string.IsNullOrWhiteSpace(tokenValues.ToString()))
                    {
                        ctx.Token = tokenValues.ToString();
                    }
                }
                return Task.CompletedTask;
            }
        };
    });

// ─── Authorization policies ───────────────────────────────────────────────
// Every endpoint requires an authenticated user unless it explicitly opts
// out with [AllowAnonymous] (login / refresh / logout / health). Role
// checks are evaluated here, on the server, from the validated JWT role
// claim — the frontend only hides buttons, it never decides access.
//
// The former legacy X-Admin-Token path (a shared secret that the Next.js
// proxy attached on behalf of any caller) and its Development-mode bypass
// are gone: admin actions now need a JWT whose role is Admin. Bootstrap the
// first admin with SEED_ADMIN_EMAIL / SEED_ADMIN_PASSWORD.
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    o.AddPolicy(AuthPolicies.Operator, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin, Roles.Operator));
    o.AddPolicy(AuthPolicies.Admin,    p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));
});

// ─── Rate limits ──────────────────────────────────────────────────────────
// "account"     same budget per client IP on register / forgot-password / reset-password.
// "login"       5/min per client IP on POST /api/auth/login (LOGIN_RATE_LIMIT_PER_MINUTE
//               overrides it, 1–1000; the end-to-end harness raises it because every
//               browser login there comes from 127.0.0.1).
// "sensor-sim"  20/min per user on the endpoints that advance the
//               simulation (POST /api/sensors/ingest, /api/line-board/refresh).
// "sensor-live" 120/min per user on POST /api/sensors/live (a floor gateway
//               posting every second or two stays inside it).
// Every other route is unrestricted. UseRateLimiter runs after
// authentication, so the per-user policies partition on the JWT subject;
// UseForwardedHeaders runs earlier so the IP-based login policy sees the
// real client IP behind a trusted proxy.
builder.Services.AddRateLimiter(o =>
{
    // PartitionedRateLimiter.Create<HttpContext, string> is required for
    // per-IP buckets. AddFixedWindowLimiter alone creates a single shared
    // bucket — useless for credential-stuffing protection.
    o.AddPolicy(RateLimitPolicies.Login, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => FixedWindow(RateLimitPolicies.LoginPerMinute())));
    o.AddPolicy(RateLimitPolicies.Account, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => FixedWindow(RateLimitPolicies.LoginPerMinute())));
    o.AddPolicy(RateLimitPolicies.SensorSimulation, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RateLimitPolicies.UserPartition(httpContext),
            factory: _ => FixedWindow(RateLimitPolicies.SensorSimulationPerMinute)));
    o.AddPolicy(RateLimitPolicies.SensorLive, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RateLimitPolicies.UserPartition(httpContext),
            factory: _ => FixedWindow(RateLimitPolicies.SensorLivePerMinute)));
    o.AddPolicy(RateLimitPolicies.QcUpload, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: RateLimitPolicies.UserPartition(httpContext),
            factory: _ => FixedWindow(RateLimitPolicies.QcUploadPerMinute)));

    // 429 response shape: RFC 7807 ProblemDetails + Retry-After. Matches
    // the rest of the API's error envelope.
    o.OnRejected = async (ctx, ct) =>
    {
        var retryAfterSeconds = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
            : 60; // one full window
        var isLogin = ctx.HttpContext.Request.Path.StartsWithSegments("/api/auth/login", StringComparison.OrdinalIgnoreCase);

        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.HttpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        ctx.HttpContext.Response.Headers["Cache-Control"] = "no-store";
        ctx.HttpContext.Response.ContentType = "application/problem+json";
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Type   = "https://datatracker.ietf.org/doc/html/rfc6585#section-4",
            Title  = "Too Many Requests",
            Status = StatusCodes.Status429TooManyRequests,
            Detail = isLogin
                ? $"Too many login attempts from this client. Retry after {retryAfterSeconds} seconds."
                : $"Too many requests to this endpoint. Retry after {retryAfterSeconds} seconds.",
            Instance = ctx.HttpContext.Request.Path.HasValue
                ? ctx.HttpContext.Request.Path.Value! : string.Empty,
        };
        await ctx.HttpContext.Response.WriteAsJsonAsync(problem, ct);
    };

    static FixedWindowRateLimiterOptions FixedWindow(int permitsPerMinute) => new()
    {
        PermitLimit          = permitsPerMinute,
        Window               = TimeSpan.FromMinutes(1),
        QueueLimit           = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment    = true,
    };
});

// ─── AUTH_COOKIE_SAMESITE validation (fail-fast on bad values) ────────────
// AuthController reads the same env var on every cookie stamp/clear, but
// a bad value would only blow up at request time. Catch it here at
// startup so an operator typo is caught immediately.
var cookieSameSiteRaw = Environment.GetEnvironmentVariable("AUTH_COOKIE_SAMESITE");
try
{
    _ = AuthCookieSettings.ParseSameSite(cookieSameSiteRaw);
}
catch (InvalidOperationException ex)
{
    throw new InvalidOperationException(
        $"AUTH_COOKIE_SAMESITE={cookieSameSiteRaw ?? "<unset>"} — {ex.Message}",
        ex);
}

// ─── Auth DI ──────────────────────────────────────────────────────────────
// Stateless token service is a singleton; the auth service + hasher are
// scoped because they ride the request DbContext.
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<IPasswordHasher, PasswordHasherAdapter>();
builder.Services.AddScoped<IAuthService, AuthService>();

// ─── Self-service accounts + email ─────────────────────────────────────────
// Sign-up (always Viewer; ALLOW_SELF_REGISTRATION=false turns it off),
// forgot / reset password with a one-time emailed link, Admin role changes.
// Emails go through a background queue; SMTP_* configures delivery, and
// without SMTP_HOST the message is only logged (outside Production).
var emailOptions = EmailOptions.FromEnvironment(Environment.GetEnvironmentVariable);
builder.Services.AddSingleton(emailOptions);
builder.Services.AddSingleton(AccountOptions.FromEnvironment(Environment.GetEnvironmentVariable));
if (emailOptions.IsConfigured) builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
else builder.Services.AddSingleton<IEmailSender, LogOnlyEmailSender>();
builder.Services.AddSingleton<EmailQueue>();
builder.Services.AddSingleton<IEmailQueue>(sp => sp.GetRequiredService<EmailQueue>());
builder.Services.AddHostedService<EmailDispatcher>();
builder.Services.AddScoped<IAccountService, AccountService>();

// ─── RAG tuning config (Rag: section in appsettings.json, env Rag__*) ──────
builder.Services.Configure<RagConfig>(builder.Configuration.GetSection("Rag"));

// ─── LLM + embedding settings (one provider for Ask, agents, embeddings) ───
// Startup configuration and the encryption key are read here, once. A
// malformed AI_SETTINGS_ENCRYPTION_KEY fails startup; a missing one only
// prevents saving API keys (the settings API says so).
var startupAi = StartupAiConfig.FromEnvironment(Environment.GetEnvironmentVariable);
var secretProtector = AiSecretProtector.FromEnvironment(Environment.GetEnvironmentVariable);
var settingsRefresh = AiSettingsProvider.RefreshIntervalFrom(Environment.GetEnvironmentVariable(AiSettingsProvider.EnvRefreshSeconds));
builder.Services.AddSingleton(startupAi);
builder.Services.AddSingleton(secretProtector);
builder.Services.AddSingleton<IAiSettingsProvider>(sp => new AiSettingsProvider(
    sp.GetRequiredService<IServiceScopeFactory>(), secretProtector, startupAi, settingsRefresh,
    sp.GetRequiredService<ILogger<AiSettingsProvider>>()));
builder.Services.AddScoped<ILlmSettingsService, LlmSettingsService>();

// ─── RAG: pluggable embedder + chunker + scorer ────────────────────────────
// IEmbeddingService resolves the embedder for the settings in effect at each
// call, so a saved change applies without a restart. Hosted embedders are
// wrapped so a failure drops that configuration to the local hash embedder
// with a warning — never a crash, never a key in the log.
builder.Services.AddSingleton<EmbeddingProviderResolver>();
builder.Services.AddSingleton<IEmbeddingService, SettingsBoundEmbeddingService>();
builder.Services.AddScoped<EmbeddingReindexService>();
builder.Services.AddSingleton<TextChunker>();
builder.Services.AddSingleton<HybridScorer>();

// Background health probe of the current embedding settings. It never
// re-embeds; that is an Admin-confirmed operation.
builder.Services.AddSingleton<RagHealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RagHealthMonitor>());

// Scoped helper for the live column dimension and the per-vector dim guard.
builder.Services.AddScoped<EmbeddingColumnAdmin>();

// Python RAG service (rag/main.py): the source of every Ask and agent answer.
// PYRAG_SERVICE_URL / PYRAG_API_KEY / PYRAG_COLLECTION_NAME from the shared .env.
builder.Services.AddSingleton(PyRagOptions.FromEnvironment(Environment.GetEnvironmentVariable));
builder.Services.AddSingleton<IPyRagClient, PyRagClient>();

// Scoped: each request gets a fresh instance with its own DbContext.
builder.Services.AddScoped<IRagService,        RagService>();
builder.Services.AddScoped<IAskService,        AskService>();
builder.Services.AddScoped<IFloorAlertService, FloorAlertService>();
builder.Services.AddScoped<ILineBoardService,  LineBoardService>();
builder.Services.AddScoped<IQcService,         QcService>();
builder.Services.AddScoped<ISensorService,     SensorService>();
builder.Services.AddScoped<IAgentRunService,   AgentRunService>();
builder.Services.AddScoped<IBriefService,      BriefService>();
builder.Services.AddScoped<IApprovalService,   ApprovalService>();

// ─── Quality inspection (QC_* env vars, read once; see QcOptions) ──────────
// Images go to the file store (QC_IMAGE_STORAGE_PATH), metadata and
// predictions to PostgreSQL. The vision provider is chosen by
// QC_VISION_PROVIDER (llm | http); unset means inference is reported as not
// configured — no prediction is ever made up. A background worker claims
// queued inspections from the database (safe across several instances).
var qcOptions = FactoryBrain.Infrastructure.Qc.QcOptions.FromEnvironment(
    Environment.GetEnvironmentVariable, builder.Environment.ContentRootPath);
if (qcOptions.ConfigurationError is not null)
    Console.WriteLine($"[startup] QC vision: {qcOptions.ConfigurationError}");
builder.Services.AddSingleton(qcOptions);
builder.Services.AddSingleton<IQcImageStore, FactoryBrain.Infrastructure.Qc.LocalFileQcImageStore>();
builder.Services.AddSingleton<IDefectDetectionProvider, FactoryBrain.Infrastructure.Qc.ConfiguredDefectDetectionProvider>();
builder.Services.AddSingleton<IQcWorkSignal, FactoryBrain.Infrastructure.Qc.QcWorkSignal>();
builder.Services.AddScoped<IQcInspectionService, FactoryBrain.Infrastructure.Qc.QcInspectionService>();
builder.Services.AddScoped<IQcDefectCategoryService, FactoryBrain.Infrastructure.Qc.QcDefectCategoryService>();
builder.Services.AddScoped<IQcInspectionProcessor, FactoryBrain.Infrastructure.Qc.QcInspectionProcessor>();
// The database-free "Testing" host has nothing to process.
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<FactoryBrain.Infrastructure.Qc.QcInspectionWorker>();

// ─── Production planning & line efficiency (PRODUCTION_* env vars, read once) ─
// Lines, shifts, styles, orders, plans and output live in PostgreSQL; every
// metric is computed from those rows at read time. Plans and line-balance
// proposals go live only through the approval queue plus an explicit
// activate / apply step. See ProductionOptions for the settings.
var productionOptions = FactoryBrain.Application.Production.ProductionOptions.FromEnvironment(Environment.GetEnvironmentVariable);
if (productionOptions.ConfigurationError is not null)
    Console.WriteLine($"[startup] Production: {productionOptions.ConfigurationError}");
builder.Services.AddSingleton(productionOptions);
builder.Services.AddScoped<IProductionSetupService, FactoryBrain.Infrastructure.Production.ProductionSetupService>();
builder.Services.AddScoped<IProductionOrderService, FactoryBrain.Infrastructure.Production.ProductionOrderService>();
builder.Services.AddScoped<IProductionPlanService, FactoryBrain.Infrastructure.Production.ProductionPlanService>();
builder.Services.AddScoped<IProductionOutputService, FactoryBrain.Infrastructure.Production.ProductionOutputService>();
builder.Services.AddScoped<IProductionAnalysisService, FactoryBrain.Infrastructure.Production.ProductionAnalysisService>();

// ─── Realtime (SignalR) ────────────────────────────────────────────────────
// FactoryHub is server-to-client only — clients don't call hub methods.
// IRealtimeNotifier is the Application-layer seam Infrastructure services
// call after a successful REST write, so SignalR stays out of the
// Infrastructure project.
builder.Services.AddSignalR();
builder.Services.AddScoped<IRealtimeNotifier, SignalRRealtimeNotifier>();

// Optional background simulator. Reads SIMULATOR_ENABLED / SIMULATOR_SEED
// at construction; when SIMULATOR_ENABLED is unset or false the service
// short-circuits inside ExecuteAsync and no timer is started.
builder.Services.AddHostedService<SimulatorHostedService>();

// HTTP client used by AskService / AgentRunService + hosted embedders.
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();

// ─── CORS — exact-origin allowlist (CORS_ORIGINS, default http://localhost:5173) ──
// Step 51: the named "AllowListed" policy reads CORS_ORIGINS (comma-separated
// exact origins, trimmed). When unset, the default is http://localhost:5173
// so a fresh checkout just-works with the most common Vite dev port. Any
// header, GET/POST/PUT/PATCH/DELETE, credentials allowed. No wildcards on
// the origin list — wildcard origins + credentials would be rejected by
// the spec at the browser level anyway, so we forbid them here.
const string CorsPolicyName = "AllowListed";
builder.Services.AddCors(o =>
{
    o.AddPolicy(CorsPolicyName, p =>
    {
        var raw = Environment.GetEnvironmentVariable("CORS_ORIGINS");
        var origins = (string.IsNullOrWhiteSpace(raw)
            ? "http://localhost:5173"
            : raw)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        p.WithOrigins(origins)
         .AllowAnyHeader()
         .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
         .AllowCredentials();
    });
});

// ─── Swagger / OpenAPI (Development only) ──────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "FactoryBrain.Api",
        Version = "v9",
        Description = "Backend for the BunonBrain (factoryBrain) platform. " +
                      "Every route mirrors a /api/** path the Next.js frontend expects. " +
                      "Most endpoints need a signed-in user: call POST /api/auth/login, copy accessToken, " +
                      "then use Authorize."
    });
    // "Authorize" button: send the access token as Authorization: Bearer <token>.
    var bearer = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Access token from POST /api/auth/login (valid 15 minutes).",
        Reference = new Microsoft.OpenApi.Models.OpenApiReference
        {
            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
            Id = "Bearer",
        },
    };
    c.AddSecurityDefinition("Bearer", bearer);
    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement { [bearer] = Array.Empty<string>() });
});

// ─── Health checks (PostgreSQL connectivity via NpgSql) ────────────────────
builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionStringFactory: _ => pgConn,
        name: "postgres",
        tags: new[] { "ready" });

var app = builder.Build();

// ─── Automatic database initialization & seeding ───────────────────────────
// Skipped for the database-free integration tests (environment "Testing"):
// they exercise authentication, authorization, validation and rate limits,
// none of which need a database, and they must never migrate or seed one.
// "IntegrationTesting" does run it, against a loopback-only test database
// (enforced above).
if (!app.Environment.IsEnvironment("Testing"))
using (var scope = app.Services.CreateScope())
{
    var db    = scope.ServiceProvider.GetRequiredService<FactoryBrainDbContext>();
    var admin = scope.ServiceProvider.GetRequiredService<EmbeddingColumnAdmin>();
    var log   = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var httpF = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
    var lf    = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    var resolver = scope.ServiceProvider.GetRequiredService<EmbeddingProviderResolver>();

    // Ensure the pgvector extension is present before EF tries to use it.
    db.Database.ExecuteSqlRaw("CREATE EXTENSION IF NOT EXISTS vector;");

    // (a) If the app tables exist (legacy EnsureCreated bootstrap) but the
    //     __ef_migrations history table is missing or empty, create it and
    //     stamp the InitialBaseline migration as already applied so EF
    //     doesn't try to recreate existing schema and lose data. Newer
    //     migrations still run normally via Database.Migrate() below.
    BootstrapMigrationBaseline(db, log);

    // (b) Apply EF Core migrations. They run before anything reads the
    //     ai_settings table (the settings provider, the probe below).
    var pending = db.Database.GetPendingMigrations();
    if (pending.Any())
    {
        log.LogInformation("Applying {N} pending EF migration(s): {Names}",
            pending.Count(), string.Join(", ", pending));
        db.Database.Migrate();
    }

    // Dev-only test switches: ignored (with one warning) when not in
    // Development.
    if (!builder.Environment.IsDevelopment())
    {
        var swRaw = System.Environment.GetEnvironmentVariable(EmbeddingProviderResolver.EnvFakeFail)
                    ?? System.Environment.GetEnvironmentVariable(EmbeddingProviderResolver.EnvFakeFailAfter);
        if (!string.IsNullOrWhiteSpace(swRaw))
        {
            log.LogWarning(
                "RAG_EMBEDDING_FAKE_FAIL* ignored outside Development (ASPNETCORE_ENVIRONMENT={Env}).",
                builder.Environment.EnvironmentName);
        }
    }

    // Embeddings no longer borrow LLM_API_KEY; say so if that is what a
    // deployment was relying on.
    if (EmbeddingCatalog.IsHosted(startupAi.Embedding.Provider ?? "") && startupAi.Embedding.ApiKey is null
        && startupAi.Llm.ApiKey is not null)
    {
        log.LogWarning(
            "RAG_EMBEDDING_PROVIDER={Provider} has no RAG_EMBEDDING_API_KEY. LLM_API_KEY is no longer used for embeddings; " +
            "embeddings stay degraded (keyword-only retrieval) until an embedding key is configured.",
            startupAi.Embedding.Provider);
    }

    // Pre-flight: one probe call against the effective embedding settings
    // when they are hosted (no call for the local embedder). Failure marks
    // that configuration degraded; the RagHealthMonitor probes again later.
    try
    {
        await resolver.ProbeAsync(httpF, lf, CancellationToken.None);
    }
    catch (Exception ex)
    {
        log.LogWarning("RAG embedding probe threw unexpectedly ({ErrorType}).", ex.GetType().Name);
    }

    var r1 = resolver.Resolve();
    if (r1.Degraded)
    {
        log.LogWarning(
            "RAG embedding startup degraded mode: configuredProvider={ConfiguredProvider} activeProvider=local configuredDims={ConfiguredDims} apiKey={ApiKey}.",
            r1.Configured.Provider, r1.Configured.Dims, r1.Configured.KeySet ? "set" : "missing");
    }
    else
    {
        log.LogInformation(
            "RAG embedding startup: configuredProvider={ConfiguredProvider} activeProvider={ActiveProvider} model={Model} dims={Dims}",
            r1.Configured.Provider, r1.Active.Provider, r1.Active.Model, r1.Active.Dims);
    }

    // (c) Never resize or re-embed at startup. A mismatch is reported and
    //     retrieval ranks by keywords until an Admin confirms a reindex.
    var liveDims = admin.TryProbeLiveColumnDim();
    if (liveDims is not null && liveDims != r1.Configured.Dims)
    {
        log.LogWarning(
            "Embedding column is vector({Live}) but the embedding settings ask for {Configured} dimensions. Nothing was resized; " +
            "vector scoring is off until an Admin reviews and confirms a reindex (GET /api/rag/reindex/plan, POST /api/rag/reindex).",
            liveDims, r1.Configured.Dims);
    }

    await DbInitializer.Initialize(scope.ServiceProvider);
}

// ─── Middleware pipeline ───────────────────────────────────────────────────
// Step 51: replace the old GlobalExceptionMiddleware with the framework's
// IExceptionHandler pipeline. UseExceptionHandler() picks up the
// GlobalExceptionHandler registered via AddExceptionHandler<>() above; it
// catches unhandled throws AFTER the rest of the pipeline has run, so
// explicit error envelopes (AdminTokenMissing, AdminTokenNotConfigured,
// 409 EmbeddingProviderUnavailable, missing_id, alert_not_found, etc.) are
// unaffected. UseStatusCodePages emits RFC 7807 ProblemDetails for bare
// 404/405 responses (i.e. routes that don't exist or methods that aren't
// allowed on a route).
app.UseExceptionHandler();
app.UseStatusCodePages();

// Cache-Control: no-store on every /api/* GET and every status-bearing
// POST/PUT/PATCH/DELETE so simulated / live data is never cached by
// intermediaries (browsers, CDNs, Next.js data cache). The contract is
// enforced by frontend/scripts/smoke.mjs's assertNoStore probe. Must run BEFORE
// UseRouting/UseCors so the header is stamped even when the downstream
// middleware short-circuits (e.g. on a 401/403).
app.UseMiddleware<NoStoreMiddleware>();

// UseForwardedHeaders runs BEFORE UseRouting / UseRateLimiter /
// UseAuthentication so the rate limiter partitions on the real client
// IP and any future IP-aware middleware sees the same value. Only
// active when FORWARDED_HEADERS_ENABLED=true (default off) so a direct
// exposure (no proxy in front) cannot be tricked by an inbound
// X-Forwarded-For header.
var fwdEnabled = string.Equals(
    Environment.GetEnvironmentVariable("FORWARDED_HEADERS_ENABLED"),
    "true", StringComparison.OrdinalIgnoreCase);
if (fwdEnabled)
{
    var fwdOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit     = 1,
    };
    // Start clean — we only honour proxies the operator named in
    // TRUSTED_PROXIES, otherwise ASP.NET Core's defaults (the loop-back
    // subnets) leak trusted status beyond what's intended. The
    // collections are read-only properties so we mutate them in place.
    fwdOptions.KnownProxies.Clear();
    fwdOptions.KnownNetworks.Clear();

    var rawProxies = Environment.GetEnvironmentVariable("TRUSTED_PROXIES");
    var proxyEntries = string.IsNullOrWhiteSpace(rawProxies)
        ? Array.Empty<string>()
        : rawProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (proxyEntries.Length == 0)
    {
        Console.WriteLine(
            "[startup] FORWARDED_HEADERS_ENABLED=true with TRUSTED_PROXIES empty: " +
            "X-Forwarded-For is trusted from any sender. Anyone who can reach " +
            "the API directly can spoof their client IP and bypass the login " +
            "rate limit. Only do this when the API is reachable exclusively " +
            "through the proxy; otherwise set TRUSTED_PROXIES.");
    }
    else
    {
        foreach (var entry in proxyEntries)
        {
            if (IPAddress.TryParse(entry, out var ip))
            {
                fwdOptions.KnownProxies.Add(ip);
                continue;
            }
            if (Microsoft.AspNetCore.HttpOverrides.IPNetwork.TryParse(entry, out var net))
            {
                fwdOptions.KnownNetworks.Add(net);
                continue;
            }
            throw new InvalidOperationException(
                $"TRUSTED_PROXIES entry '{entry}' is not a valid IPv4/IPv6 address or CIDR.");
        }
    }

    app.UseForwardedHeaders(fwdOptions);
}

// Swagger UI (/swagger) and the OpenAPI document (/swagger/v1/swagger.json):
// on in Development, otherwise only with SWAGGER_ENABLED=true. They describe
// the API but expose no data; every protected endpoint still needs a token
// (use the Authorize button with an access token from /api/auth/login).
if (SwaggerEnabled(Environment.GetEnvironmentVariable("SWAGGER_ENABLED"), app.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.DocumentTitle = "FactoryBrain API");
}

app.UseRouting();
app.UseCors(CorsPolicyName);                       // CORS for the configured allowlist
app.UseAuthentication();
app.UseAuthorization();                            // fallback: authenticated; role policies per action
app.UseRateLimiter();                              // after auth so per-user policies see the JWT subject

app.MapControllers();                              // Attribute-routed controllers
app.MapHub<FactoryHub>("/hubs/factory");           // SignalR hub — server→client only
app.MapHealthChecks("/health").AllowAnonymous();   // Health probe — intentionally public, returns only Healthy/Unhealthy

app.MapGet("/", () => Results.Ok(new
{
    service  = "factorybrain-api",
    version  = "1.0.0",
    swagger  = "/swagger",
    health   = "/health",
    endpoints = new[]
    {
        "POST /api/ask",
        "POST /api/auth/register",
        "POST /api/auth/forgot-password",
        "POST /api/auth/reset-password",
        "GET  /api/users",
        "PATCH /api/users/{id}/role",
        "POST /api/rag/reindex",
        "GET  /api/rag/status",
        "GET  /api/brief/morning",
        "GET  /api/floor-alerts",
        "PATCH /api/floor-alerts/{id}",
        "GET  /api/line-board",
        "POST /api/line-board/refresh",
        "GET  /api/qc/defects",
        "POST /api/qc/flag",
        "GET  /api/sensors/latest",
        "GET  /api/sensors/snapshot",
        "POST /api/sensors/ingest",
        "POST /api/sensors/live",
        "GET  /api/sensors/ingest",
        "GET  /api/qc/inspections",
        "POST /api/qc/inspections",
        "GET  /api/qc/inspections/{id}",
        "GET  /api/qc/inspections/{id}/image",
        "POST /api/qc/inspections/{id}/review",
        "POST /api/qc/inspections/{id}/retry",
        "GET  /api/qc/summary",
        "GET  /api/qc/vision/status",
        "GET  /api/qc/categories",
        "POST /api/qc/categories",
        "PUT  /api/qc/categories/{code}",
        "GET  /api/settings/llm",
        "POST /api/settings/llm",
        "GET  /api/agents",
        "POST /api/agents/{agentId}/run",
        "GET  /api/approvals",
        "POST /api/approvals/{id}/approve",
        "POST /api/approvals/{id}/reject",
        "GET  /api/audit"
    }
}));

// ─── Listen ────────────────────────────────────────────────────────────────
// In Development, appsettings.Development.json supplies Urls (the
// Next.js dev proxy expects http://localhost:5000). In Production and
// every other environment, ASPNETCORE_URLS / launchSettings / the host's
// settings take effect — no URL is hard-coded here.
app.Run();

// ─── Helpers ────────────────────────────────────────────────────────────────
public partial class Program
{
    /// <summary>
    /// Whether to serve Swagger: <c>SWAGGER_ENABLED=true/false</c> when set,
    /// otherwise only in Development.
    /// </summary>
    internal static bool SwaggerEnabled(string? raw, bool isDevelopment) =>
        bool.TryParse(raw?.Trim(), out var on) ? on : isDevelopment;

    /// <summary>
    /// True when every host in the Npgsql connection string is this machine
    /// (localhost, a loopback IP, or a Unix socket path). Used to keep
    /// Development-only diagnostics away from remote / shared databases.
    /// An unparseable string counts as remote, the safe default.
    /// </summary>
    internal static bool IsLoopbackDatabase(string connectionString)
    {
        try
        {
            var csb = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            var hosts = (csb.Host ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (hosts.Length == 0) return false;
            return hosts.All(h =>
            {
                if (IPAddress.TryParse(h.Trim('[', ']'), out var literal)) return IPAddress.IsLoopback(literal);
                var host = h.Split(':')[0];            // multi-host entries may carry ":port"
                if (host.StartsWith('/')) return true; // Unix domain socket
                if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
                return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
            });
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Return the C# parameter name and CLR type of the action's
    /// <c>[FromBody]</c> bound argument, or <c>(null, null)</c> when the
    /// action binds the body some other way. Used by the model-state
    /// funnel for two purposes:
    /// <list type="number">
    ///   <item><b>Body-parameter recognition:</b> the action's body
    ///   parameter (named <c>body</c> on every route in this API today)
    ///   is treated as a "whole-request" key alongside the synthetic
    ///   <c>""</c> / <c>"$"</c> / <c>"body"</c> / <c>"request"</c>
    ///   spellings.</item>
    ///   <item><b>Property collision carve-out:</b> when the body
    ///   parameter's name is ALSO a real property on the parameter's CLR
    ///   type (e.g. a future route binds <c>[FromBody] Model model</c>),
    ///   the model-state funnel must NOT fold a synthetic
    ///   <c>"$.model"</c> entry onto <c>"body"</c> — it is a real field
    ///   error and the response must be <c>errors.model</c>. Step
    ///   51-fix-3 added the carve-out for <c>LlmSettingsRequest.Model</c>,
    ///   and the type lookup makes the carve-out work without hard-coding
    ///   field names.</item>
    /// </list>
    /// </summary>
    private static (string? Name, Type? Type) ResolveBodyParam(Microsoft.AspNetCore.Mvc.ActionContext ctx)
    {
        var desc = ctx.ActionDescriptor as Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor;
        if (desc is null) return (null, null);

        foreach (var p in desc.Parameters)
        {
            var bi = p.BindingInfo;
            if (bi is null) continue;
            if (bi.BindingSource == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Body)
                return (p.Name, p.ParameterType);
        }
        return (null, null);
    }


    /// <summary>
    /// (a) If the app tables exist (legacy <c>EnsureCreated()</c> bootstrap)
    /// but the <c>__ef_migrations</c> history table is missing or empty,
    /// create the history table if needed and stamp the
    /// <c>InitialBaseline</c> migration as already applied so EF doesn't try
    /// to recreate existing schema and lose data. Newer migrations still
    /// run normally via <c>Database.Migrate()</c> on the next line.
    /// </summary>
    private static void BootstrapMigrationBaseline(
        Microsoft.EntityFrameworkCore.DbContext db,
        ILogger log)
    {
        try
        {
            var conn = db.Database.GetDbConnection();
            conn.Open();
            try
            {
                // 1. History table — create it now if it doesn't exist yet.
                long historyExists;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT COUNT(*) FROM pg_catalog.pg_tables WHERE tablename = '__ef_migrations';";
                    historyExists = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }
                if (historyExists == 0)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
                            CREATE TABLE ""__ef_migrations"" (
                                ""MigrationId""    VARCHAR(150) NOT NULL,
                                ""ProductVersion"" VARCHAR(32)  NOT NULL,
                                PRIMARY KEY (""MigrationId"")
                            );";
                        cmd.ExecuteNonQuery();
                    }
                }

                // 2. Probe app tables: did EnsureCreated already create them?
                long tablesPresent;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT COUNT(*) FROM pg_catalog.pg_tables WHERE tablename IN ('manual_documents', 'document_chunks');";
                    tablesPresent = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }

                // 3. If the app tables are present, check history emptiness.
                long appliedCount;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM \"__ef_migrations\";";
                    appliedCount = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }
                if (tablesPresent > 0 && appliedCount == 0)
                {
                    db.Database.ExecuteSqlRaw(
                        "INSERT INTO \"__ef_migrations\" (\"MigrationId\", \"ProductVersion\") VALUES ({0}, {1})",
                        "20260927222103_InitialBaseline", "9.0.0");
                    log.LogInformation(
                        "Adopted InitialBaseline migration for an EnsureCreated-bootstrapped database; newer migrations will still run.");
                }
            }
            finally
            {
                conn.Close();
            }
        }
        catch (Exception ex)
        {
            // Don't crash startup if the probe fails — the next call to
            // GetPendingMigrations() / Migrate() will surface the real error.
            log.LogWarning(ex, "BootstrapMigrationBaseline probe failed; continuing startup.");
        }
    }
}

