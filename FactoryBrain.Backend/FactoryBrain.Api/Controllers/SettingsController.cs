using FactoryBrain.Application.Dtos.Settings;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly ILlmSettingsService _svc;

    public SettingsController(ILlmSettingsService svc)
    { _svc = svc; }

    // Step 48b — GET/POST /api/settings/llm are served by THIS controller.
    // The Next.js rewrite in `next.config.mjs` forwards every /api/:path*
    // request to http://localhost:5000/api/:path* (the ASP.NET Core host),
    // so this is the actual endpoint the smoke probe (`scripts/smoke.mjs`
    // assertNoStore) hits. The matching legacy route handler at
    // `src/_legacy_api/settings/llm/route.ts` is intentionally unreachable
    // (the `_legacy_api` underscore prefix prevents Next.js from mounting
    // it as an App Router route).
    //
    // Every response — 200 Ok, 400 BadRequest, and any future error path —
    // must set `Cache-Control: no-store` so the secret-bearing LLM status
    // is never stored by browsers, CDNs, or the Next.js data cache. We
    // stamp the header on HttpResponse.Headers up front, BEFORE reading the
    // service state or running validation, so the header is present on
    // every code path that returns from this controller. The middleware
    // `NoStoreMiddleware` already covers request-level failures (e.g. an
    // invalid JSON body that short-circuits before this method runs), but
    // we keep the explicit stamp here so the route is self-documenting and
    // doesn't rely on pipeline ordering. The response body and status
    // codes are unchanged.
    private void StampNoStore()
        => Response.Headers["Cache-Control"] = "no-store";

    [HttpGet("llm")]
    public IActionResult Get()
    {
        StampNoStore();
        return Ok(_svc.ReadStatus());
    }

    [HttpPost("llm")]
    [AdminToken]
    [Authorize(Policy = AuthPolicies.AdminOrLegacyToken)]
    public IActionResult Post([FromBody] LlmSettingsRequest body)
    {
        StampNoStore();
        // Step 51: body validation runs in the global FluentValidationFilter
        // (registered in Program.cs); on failure it short-circuits with a
        // 400 ValidationProblemDetails (RFC 7807 with an errors map) before
        // this method runs. The controller's catch (ArgumentException ex)
        // still emits the canonical {error: "invalid_provider"} envelope for
        // the runtime check below — both shapes coexist on this route.
        try { return Ok(_svc.Update(body)); }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
