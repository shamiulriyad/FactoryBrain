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
// committed in .env. EnvReloader may still overwrite RAG_* values at runtime
// in Development; see EnvReloader.cs for the prefix guard.
//
// RAG embedding providers (resolved at startup):
//   RAG_EMBEDDING_PROVIDER = local | openai | gemini   (default: local)
//   RAG_EMBEDDING_MODEL    = e.g. text-embedding-3-small
//   RAG_EMBEDDING_API_KEY  = falls back to LLM_API_KEY
//   RAG_EMBEDDING_DIMENSIONS = falls back to Rag:EmbeddingDimensions, then 384
//   If LLM_PROVIDER is set and a key is present, that provider is mirrored
//   (gemini → Gemini embeddings, openai → text-embedding-3-small).
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
using FactoryBrain.Api.Services;
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
Env.Load(".env.local",  new LoadOptions(clobberExistingVars: false));
Env.Load(".env",        new LoadOptions(clobberExistingVars: false, onlyExactPath: false));

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

builder.Services.AddDbContext<FactoryBrainDbContext>(opts =>
{
    opts.UseNpgsql(pgConn, npg =>
    {
        npg.EnableRetryOnFailure(maxRetryCount: 3);
        npg.MigrationsHistoryTable("__ef_migrations");
        npg.UseVector();
    });
    if (builder.Environment.IsDevelopment())
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

// ─── Admin-token gate (bound to the three write actions via [AdminToken]) ──
// Replaces the old path-matching middleware: the filter is invoked only on
// actions that carry [AdminToken], so URL/path spellings (trailing slash,
// mixed case, double slash, percent-encoded slash) either match the route
// or they don't — the filter never gets a chance to mis-handle them.
builder.Services.AddScoped<AdminTokenAuthorizationFilter>();

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
// The bearer scheme is what /api/auth/me and the AdminOrLegacyToken
// policy's role check rely on. Issuer / Audience / SigningKey come from
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
// AdminOrLegacyToken succeeds when:
//   (1) the caller has role Admin on a valid JWT (the modern path), OR
//   (2) the legacy X-Admin-Token check passes via AdminTokenVerifier, OR
//   (3) the request is anonymous AND the process is in Development
//       (so the [AdminToken] filter can short-circuit with its
//       IsDevelopment bypass).
//
// For an anonymous request in non-Development the policy *fails open*.
// This is intentional: we want the [AdminToken] filter (an
// IAsyncAuthorizationFilter) to get a chance to emit its canonical
// {error: "AdminTokenMissing"} envelope in Production — if the policy
// returned false instead, the framework's JwtBearer challenge would
// win the race and return its default 401 problem details instead.
//
// TODO(54c): remove the legacy X-Admin-Token path. The JWT role branch
// is now the canonical way to authorise admin actions; the legacy
// ADMIN_API_TOKEN is kept only as a fallback so a fresh deploy with no
// seeded users still boots. Batch 54c deletes AdminTokenVerifier and the
// AdminTokenAuthorizationFilter envelope and switches every [AdminToken]
// action to [Authorize(Policy = AdminOrLegacyToken)] alone.
builder.Services.AddSingleton<AdminTokenVerifier>();
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(AuthPolicies.AdminOrLegacyToken, p =>
        p.RequireAssertion(ctx =>
        {
            // Path (1): role Admin on a valid JWT.
            if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.IsInRole("Admin"))
                return true;

            // Path (2): legacy X-Admin-Token matches the env var.
            if (ctx.Resource is HttpContext http)
            {
                var verifier = http.RequestServices.GetService(typeof(AdminTokenVerifier)) as AdminTokenVerifier;
                if (verifier is not null)
                {
                    var outcome = verifier.Verify(http);
                    if (outcome == AdminTokenVerifier.Outcome.Pass
                        || outcome == AdminTokenVerifier.Outcome.PassDevelopment)
                        return true;
                }
            }

            // Path (3): anonymous request — let the [AdminToken] filter
            // decide. In Development it short-circuits; in non-Development
            // it returns the canonical {error: "AdminTokenMissing"}
            // envelope. A *present-but-not-Admin* JWT skips this branch
            // (IsAuthenticated == true) and falls through to "false" →
            // framework 403 problem details, which is what the spec wants.
            //
            // 54c note: this anonymous fallthrough exists ONLY because
            // [AdminToken] still does the legacy token check. In batch
            // 54c the fallthrough and every [AdminToken] attribute must
            // be removed TOGETHER — never one without the other. If you
            // drop the fallthrough first, anonymous callers start
            // getting framework 401 problem details instead of the
            // AdminTokenMissing envelope; if you drop [AdminToken]
            // first, anonymous callers get 403 problem details instead
            // of AdminTokenMissing. Removing them in the same commit
            // keeps the response shape stable.
            if (ctx.User.Identity?.IsAuthenticated != true)
                return true;

            return false;
        }));
});

// ─── Login rate limit (fixed window, 5/min/IP, login route only) ──────────
// Named "login" — AuthController.Login decorates the action with
// [EnableRateLimiting("login")] so every other route is unrestricted.
// UseForwardedHeaders below resolves the real client IP behind a reverse
// proxy before the limiter partitions on it.
builder.Services.AddRateLimiter(o =>
{
    // PartitionedRateLimiter.Create<HttpContext, string> is required for
    // per-IP buckets. AddFixedWindowLimiter alone creates a single shared
    // bucket — useless for credential-stuffing protection. The partition
    // key reads RemoteIpAddress AFTER UseForwardedHeaders rewrites it,
    // so when FORWARDED_HEADERS_ENABLED=true and the request comes from a
    // trusted proxy, each forwarded client gets its own 5/min budget.
    o.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit       = 5,
                Window            = TimeSpan.FromMinutes(1),
                QueueLimit        = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true,
            }));

    // 429 response shape: RFC 7807 ProblemDetails + Retry-After. Matches
    // the rest of the API's error envelope.
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var retryAfterSeconds = 60; // one full window
        ctx.HttpContext.Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
        ctx.HttpContext.Response.Headers["Cache-Control"] = "no-store";
        ctx.HttpContext.Response.ContentType = "application/problem+json";
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Type   = "https://datatracker.ietf.org/doc/html/rfc6585#section-4",
            Title  = "Too Many Requests",
            Status = StatusCodes.Status429TooManyRequests,
            Detail = "Too many login attempts from this client. Retry after 60 seconds.",
            Instance = ctx.HttpContext.Request.Path.HasValue
                ? ctx.HttpContext.Request.Path.Value! : string.Empty,
        };
        await ctx.HttpContext.Response.WriteAsJsonAsync(problem, ct);
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

// ─── RAG tuning config (Rag: section in appsettings.json, env Rag__*) ──────
builder.Services.Configure<RagConfig>(builder.Configuration.GetSection("Rag"));

// ─── RAG: pluggable embedder + chunker + scorer ────────────────────────────
// The resolver is a singleton; the actual IEmbeddingService is built once at
// startup (also a singleton) and is decorated with WithFallback so any
// hosted-provider failure drops permanently to the local hash embedder with
// a warning log — never a crash, never a key in the log.
builder.Services.AddSingleton<EmbeddingProviderResolver>();
builder.Services.AddSingleton<IEmbeddingService>(sp =>
{
    var resolver = sp.GetRequiredService<EmbeddingProviderResolver>();
    var http     = sp.GetRequiredService<IHttpClientFactory>();
    var lf       = sp.GetRequiredService<ILoggerFactory>();
    // BuildService wraps the hosted (or stub) primary with the local hash
    // fallback so transient hosted failures don't crash the request — they
    // flip Degraded via the wrapper and degrade gracefully.
    return resolver.BuildService(http, lf);
});
builder.Services.AddSingleton<TextChunker>();
builder.Services.AddSingleton<HybridScorer>();

// Background health probe + automatic reindex on recovery. The monitor
// re-reads env vars on every cycle so newly-set RAG_EMBEDDING_* values
// take effect without a restart.
builder.Services.AddSingleton<RagHealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RagHealthMonitor>());

// Scoped helpers for column-dim probing + metadata-aware reindex.
// EmbeddingColumnAdmin needs IHttpClientFactory + ILoggerFactory so it can
// build a fresh primary hosted embedder directly (no fallback wrapper)
// during the reindex path — that's how we surface hosted-call failures
// as exceptions that trigger a transactional rollback + Degraded flag.
builder.Services.AddScoped<EmbeddingColumnAdmin>();

// Scoped: each request gets a fresh instance with its own DbContext.
builder.Services.AddScoped<IRagService,        RagService>();
builder.Services.AddScoped<IAskService,        AskService>();
builder.Services.AddScoped<IFloorAlertService, FloorAlertService>();
builder.Services.AddScoped<ILineBoardService,  LineBoardService>();
builder.Services.AddScoped<IQcService,         QcService>();
builder.Services.AddScoped<ISensorService,     SensorService>();
builder.Services.AddScoped<IVisionService,     VisionService>();
builder.Services.AddScoped<IAgentRunService,   AgentRunService>();
builder.Services.AddScoped<ILlmSettingsService, LlmSettingsService>();
builder.Services.AddScoped<IBriefService,      BriefService>();

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
                      "Every route mirrors a /api/** path the Next.js frontend expects."
    });
});

// ─── Health checks (PostgreSQL connectivity via NpgSql) ────────────────────
builder.Services.AddHealthChecks()
    .AddNpgSql(
        connectionStringFactory: _ => pgConn,
        name: "postgres",
        tags: new[] { "ready" });

var app = builder.Build();

// ─── Automatic database initialization & seeding ───────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db    = scope.ServiceProvider.GetRequiredService<FactoryBrainDbContext>();
    var rag   = scope.ServiceProvider.GetRequiredService<IRagService>();
    var admin = scope.ServiceProvider.GetRequiredService<EmbeddingColumnAdmin>();
    var log   = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var embed = scope.ServiceProvider.GetRequiredService<IEmbeddingService>();
    var httpF = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
    var lf    = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();

    // Ensure the pgvector extension is present before EF tries to use it.
    db.Database.ExecuteSqlRaw("CREATE EXTENSION IF NOT EXISTS vector;");

    // Surface the configured-vs-active shape so operators see degraded
    // mode (and which provider was requested) without hitting /api/rag/status.
    var resolver = scope.ServiceProvider.GetRequiredService<EmbeddingProviderResolver>();

    // Dev-only test switches: ignored (with one warning) when not in
    // Development. Always read after the warning so an env flip right
    // before a probe can still take effect.
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

    // Pre-flight: make one probe embedding call against the active hosted
    // provider (when configured != local). Any failure — HTTP error, auth,
    // timeout, wrong-dim response, simulated failure — flips the resolver
    // into Degraded and makes Active = local. The RagHealthMonitor will
    // probe again on its own interval and recover when the provider is
    // available.
    try
    {
        await resolver.ProbeAsync(httpF, lf, CancellationToken.None);
    }
    catch (Exception ex)
    {
        log.LogWarning(ex, "RAG embedding probe threw unexpectedly.");
    }

    var r1 = resolver.Resolve();
    if (r1.Degraded || resolver.IsDegraded)
    {
        // Spec: degraded startup warning prints ONLY configuredProvider,
        // activeProvider=local, configuredDims, apiKey=set|missing.
        log.LogWarning(
            "RAG embedding startup degraded mode: configuredProvider={ConfiguredProvider} activeProvider=local configuredDims={ConfiguredDims} apiKey={ApiKey}.",
            r1.Configured.Provider,
            r1.Configured.Dims,
            r1.Configured.KeySet ? "set" : "missing");
    }
    else
    {
        log.LogInformation(
            "RAG embedding startup: configuredProvider={ConfiguredProvider} activeProvider={ActiveProvider} model={Model} dims={Dims}",
            r1.Configured.Provider, r1.Active.Provider, r1.Active.Model, r1.Active.Dims);
    }

    // (a) If the app tables exist (legacy EnsureCreated bootstrap) but the
    //     __ef_migrations history table is missing or empty, create it and
    //     stamp the InitialBaseline migration as already applied so EF
    //     doesn't try to recreate existing schema and lose data. Newer
    //     migrations still run normally via Database.Migrate() below.
    BootstrapMigrationBaseline(db, log);

    // (b) Apply EF Core migrations. The two migrations are:
    //       20260927222103_InitialBaseline    — pre-45 schema + vector extension
    //       20260927222258_AddEmbeddingMetadata — adds EmbeddingProvider / Model / Dims
    var pending = db.Database.GetPendingMigrations();
    if (pending.Any())
    {
        log.LogInformation("Applying {N} pending EF migration(s): {Names}",
            pending.Count(), string.Join(", ", pending));
        db.Database.Migrate();
    }

    // (c) + (d) Atomic resize + reindex. Embeddings are pre-computed
    //          outside the DB transaction; the DB write (ALTER + per-row
    //          update + commit) is then done in one transaction inside
    //          the EF execution strategy. A mid-batch embedding failure
    //          returns 409; a mid-transaction DB error logs + surfaces
    //          as 500 and rolls the whole thing back.
    if (!resolver.IsDegraded)
    {
        try
        {
            var outcome = await admin.ResizeAndReindexAsync(CancellationToken.None);
            if (outcome.Documents > 0)
            {
                log.LogInformation(
                    "Startup reindex: updated {Docs} docs / {Chunks} chunks to provider={Provider} model={Model} dims={Dims}.",
                    outcome.Documents, outcome.Chunks, outcome.Configured.Provider, outcome.Configured.Model, outcome.Configured.Dims);
            }
        }
        catch (EmbeddingProviderUnavailableException ex)
        {
            log.LogError(ex,
                "Startup reindex rolled back: embedding provider became unavailable mid-batch. Configure a working API key or set RAG_EMBEDDING_PROVIDER=local to recover.");
        }
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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseCors(CorsPolicyName);                       // CORS for the configured allowlist
app.UseRateLimiter();                              // 5/min/IP on POST /api/auth/login
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();                              // Attribute-routed controllers
app.MapHub<FactoryHub>("/hubs/factory");           // SignalR hub — server→client only
app.MapHealthChecks("/health");                    // Health probe

app.MapGet("/", () => Results.Ok(new
{
    service  = "factorybrain-api",
    version  = "1.0.0",
    swagger  = "/swagger",
    health   = "/health",
    endpoints = new[]
    {
        "POST /api/ask",
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
        "POST /api/sensors/ingest",
        "POST /api/sensors/live",
        "GET  /api/sensors/ingest",
        "POST /api/vision/analyze",
        "GET  /api/vision/analyze",
        "GET  /api/settings/llm",
        "POST /api/settings/llm",
        "GET  /api/agents",
        "POST /api/agents/{agentId}/run"
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

