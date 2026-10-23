namespace FactoryBrain.Api.Middleware;

/// <summary>
/// Mirrors the legacy Next.js route handlers: every GET under /api/* sets
/// <c>Cache-Control: no-store</c> so simulated / live data is never cached by
/// intermediaries (browsers, CDNs, Next.js data cache). The contract is
/// enforced by <c>scripts/smoke.mjs</c>'s <c>assertNoStore</c> probe.
/// </summary>
public sealed class NoStoreMiddleware
{
    private readonly RequestDelegate _next;

    public NoStoreMiddleware(RequestDelegate next) { _next = next; }

    public Task Invoke(HttpContext ctx)
    {
        // Legacy route handlers set Cache-Control: no-store on every /api/* GET
        // AND on mutating POSTs that return a status payload. Preserve that
        // contract for the .NET rewrite so smoke.mjs + probe.mjs pass.
        if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
            (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsPost(ctx.Request.Method) ||
             HttpMethods.IsPatch(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method) ||
             HttpMethods.IsDelete(ctx.Request.Method)))
        {
            if (!ctx.Response.Headers.ContainsKey("Cache-Control"))
            {
                ctx.Response.Headers["Cache-Control"] = "no-store";
            }
        }
        return _next(ctx);
    }
}
