using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FactoryBrain.Application.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Controllers;

/// <summary>HTTP mapping shared by the settings and reindex endpoints.</summary>
internal static class SettingsResults
{
    /// <summary>The acting user, from the validated JWT (never from the request body).</summary>
    public static AuditActor Actor(ClaimsPrincipal user)
    {
        var sub = user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return new AuditActor(
            Guid.TryParse(sub, out var id) ? id : null,
            user.FindFirst(JwtRegisteredClaimNames.Email)?.Value ?? user.Identity?.Name ?? "unknown",
            user.FindFirst(ClaimTypes.Role)?.Value ?? "unknown");
    }

    public static string TraceId(HttpContext ctx) => Activity.Current?.Id ?? ctx.TraceIdentifier;

    public static IActionResult ToResult<T>(ControllerBase c, SettingsWriteResult<T> r) => r.Outcome switch
    {
        SettingsWriteOutcome.Ok => c.Ok(r.Value),
        SettingsWriteOutcome.Invalid => c.BadRequest(new { error = "invalid_settings", errors = r.Errors }),
        SettingsWriteOutcome.EncryptionKeyMissing => c.BadRequest(new
        {
            error = "encryption_key_not_configured",
            details = "API keys can't be saved: the API server has no AI_SETTINGS_ENCRYPTION_KEY. Nothing was changed.",
        }),
        SettingsWriteOutcome.Conflict => c.Conflict(new
        {
            error = r.Errors?.ContainsKey("reindex") == true ? "reindex_in_progress" : "version_conflict",
            details = r.Errors?.GetValueOrDefault("reindex")
                      ?? "The settings were changed by someone else since you loaded them. Reload and try again.",
        }),
        SettingsWriteOutcome.ConfirmationRequired => c.StatusCode(StatusCodes.Status428PreconditionRequired, new
        {
            error = "confirmation_required",
            details = "This re-embeds stored documents. Review the plan and send its planId as confirmPlanId.",
            plan = r.Detail,
        }),
        SettingsWriteOutcome.PlanChanged => c.Conflict(new
        {
            error = "plan_changed",
            details = "The documents or settings changed since that plan was made. Review the new plan and confirm again.",
            plan = r.Detail,
        }),
        SettingsWriteOutcome.DimensionMismatch => c.UnprocessableEntity(new
        {
            error = "dimension_mismatch",
            details = r.Errors?.GetValueOrDefault("dimensions") ?? "The provider's output size doesn't match the requested dimensions.",
            result = r.Detail,
        }),
        SettingsWriteOutcome.ProviderFailed => c.StatusCode(StatusCodes.Status502BadGateway, new
        {
            error = "embedding_provider_failed",
            details = "The embedding provider call failed, so nothing was changed. Check the key and model, then try again.",
        }),
        _ => c.StatusCode(StatusCodes.Status500InternalServerError),
    };
}
