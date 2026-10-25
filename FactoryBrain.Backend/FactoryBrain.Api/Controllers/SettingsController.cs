using FactoryBrain.Application.Dtos.Settings;
using FactoryBrain.Api.Middleware;
using FactoryBrain.Application.Abstractions;
using FactoryBrain.Application.Abstractions.Interfaces;
using FactoryBrain.Infrastructure.Rag;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

/// <summary>
/// LLM and embedding settings. Reads are open to any signed-in user and never
/// include a key; writes are Admin-only, persisted in the database (keys
/// encrypted), audited, and take effect from the next request.
/// <list type="bullet">
///   <item><c>GET/POST /api/settings/llm</c></item>
///   <item><c>GET /api/settings/embedding</c>, <c>POST /api/settings/embedding/plan</c>,
///   <c>POST /api/settings/embedding</c> (a provider / model / dimension change
///   needs the <c>planId</c> of a reviewed plan as <c>confirmPlanId</c>).</item>
/// </list>
/// </summary>
[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly ILlmSettingsService _llm;
    private readonly EmbeddingReindexService _embedding;

    public SettingsController(ILlmSettingsService llm, EmbeddingReindexService embedding)
    { _llm = llm; _embedding = embedding; }

    // Every response from this controller is stamped no-store up front, so
    // settings status is never cached by browsers, CDNs or the Next.js data
    // cache (NoStoreMiddleware covers failures that short-circuit earlier).
    private void StampNoStore()
        => Response.Headers["Cache-Control"] = "no-store";

    [HttpGet("llm")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        StampNoStore();
        return Ok(await _llm.ReadStatusAsync(ct));
    }

    [HttpPost("llm")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> Post([FromBody] LlmSettingsRequest body, CancellationToken ct)
    {
        StampNoStore();
        // Body validation runs in the global FluentValidationFilter first
        // (400 ValidationProblemDetails). The service re-checks the provider
        // and answers with the canonical {error: "invalid_provider"} envelope.
        var result = await _llm.UpdateAsync(body, SettingsResults.Actor(User), SettingsResults.TraceId(HttpContext), ct);
        if (result.Outcome == SettingsWriteOutcome.Invalid && result.Errors?.ContainsKey("provider") == true)
            return BadRequest(new { error = "invalid_provider" });
        return SettingsResults.ToResult(this, result);
    }

    [HttpGet("embedding")]
    public async Task<IActionResult> GetEmbedding(CancellationToken ct)
    {
        StampNoStore();
        return Ok(await _embedding.StatusAsync(ct));
    }

    /// <summary>What a change would do — documents, chunks, dimensions, cost. Makes no provider call.</summary>
    [HttpPost("embedding/plan")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> PlanEmbedding([FromBody] EmbeddingPlanRequest? body, CancellationToken ct)
    {
        StampNoStore();
        return SettingsResults.ToResult(this, await _embedding.PlanAsync(body, ct));
    }

    [HttpPost("embedding")]
    [Authorize(Policy = AuthPolicies.Admin)]
    public async Task<IActionResult> PostEmbedding([FromBody] EmbeddingSettingsRequest body, CancellationToken ct)
    {
        StampNoStore();
        return SettingsResults.ToResult(this,
            await _embedding.UpdateSettingsAsync(body, SettingsResults.Actor(User), SettingsResults.TraceId(HttpContext), ct));
    }
}
