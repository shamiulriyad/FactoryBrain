using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Middleware;

/// <summary>
/// RFC 7807 <c>application/problem+json</c> error funnel for the
/// <see cref="IExceptionHandler"/> pipeline. Replaces the old
/// <see cref="GlobalExceptionMiddleware"/> shape so unhandled exceptions
/// still return a 500, but as <see cref="ProblemDetails"/> with a stable
/// <c>traceId</c> property that an operator can correlate against the
/// server log.
///
/// <para>
/// <b>What it does:</b>
/// <list type="bullet">
///   <item>Returns 500 <c>application/problem+json</c> with
///   <c>ProblemDetails { type, title, status, detail, traceId }</c>.</item>
///   <item>In Development, <c>detail</c> widens to include the exception
///   type name and message so a developer can correlate against the source.
///   In every other environment, <c>detail</c> is the fixed string
///   <c>"An unexpected error occurred."</c> — never the exception type,
///   message, SQL, table name, or host info.</item>
///   <item>Sets <c>Cache-Control: no-store</c> on the response so a secret
///   ever leaks via this path, it never gets cached.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>What it does NOT do:</b>
/// <list type="bullet">
///   <item>It does not translate explicit error envelopes. <c>AdminToken</c>
///   still returns <c>{error:"AdminTokenMissing",...}</c> /
///   <c>{error:"AdminTokenNotConfigured",...}</c>, and the 409 degraded
///   reindex still returns <c>{error:"EmbeddingProviderUnavailable",...}</c>.
///   Per-route <c>BadRequest(new{error="missing_id",...})</c> /
///   <c>NotFound(new{error="alert_not_found",...})</c> envelopes are still
///   emitted directly by the controllers. This handler is only the funnel
///   for uncaught throws (e.g. an Npgsql connection drop in the middle of a
///   request that wasn't caught deeper down).</item>
///   <item>It does not handle <see cref="ArgumentException"/>,
///   <see cref="KeyNotFoundException"/>, or
///   <see cref="UnauthorizedAccessException"/>: those are caught at the
///   controller layer and translated into the canonical envelopes there.
///   Anything that escapes them and reaches this handler is treated as
///   "unexpected" and surfaces as 500.</item>
/// </list>
/// </para>
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IHostEnvironment _env;
    private readonly ILogger<GlobalExceptionHandler> _log;

    public GlobalExceptionHandler(
        IHostEnvironment env,
        ILogger<GlobalExceptionHandler> log)
    {
        _env = env;
        _log = log;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = httpContext.TraceIdentifier;

        // Always log the full exception with the trace id so the prod
        // envelope can be matched back to the server log. We never log
        // API keys, full connection strings, or request bodies here.
        _log.LogError(exception,
            "Unhandled exception traceId={TraceId} path={Path} method={Method}",
            traceId, httpContext.Request.Path, httpContext.Request.Method);

        var problem = new ProblemDetails
        {
            Type    = "https://datatracker.ietf.org/doc/html/rfc7807",
            Title   = "Internal Server Error",
            Status  = StatusCodes.Status500InternalServerError,
            Detail  = _env.IsDevelopment()
                ? $"{exception.GetType().Name}: {exception.Message}"
                : "An unexpected error occurred.",
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = traceId;

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = "application/problem+json";
        if (!httpContext.Response.Headers.ContainsKey("Cache-Control"))
            httpContext.Response.Headers["Cache-Control"] = "no-store";

        await httpContext.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json",
            cancellationToken: cancellationToken);

        return true;
    }
}
