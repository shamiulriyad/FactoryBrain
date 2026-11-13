using System.Text.Json;
using BunonBrain.Application.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Options;

namespace BunonBrain.Api.Authorization;

/// <summary>
/// Requires the caller to hold at least one of the listed permissions
/// (from the validated JWT). Evaluated by <see cref="PermissionHandler"/> on
/// the server for every request; the frontend only mirrors it.
/// <code>[RequirePermission(Permissions.UsersCreate)]</code>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public const string PolicyPrefix = "perm:";

    public RequirePermissionAttribute(params string[] anyOf)
    {
        if (anyOf.Length == 0) throw new ArgumentException("At least one permission is required.", nameof(anyOf));
        foreach (var p in anyOf)
            if (AccessCatalog.Permission(p) is null) throw new ArgumentException($"Unknown permission '{p}'.", nameof(anyOf));
        AnyOf = anyOf;
        Policy = PolicyPrefix + string.Join('|', anyOf);
    }

    public IReadOnlyList<string> AnyOf { get; }

    /// <summary>The permissions named by a <c>perm:</c> policy, or null for any other policy.</summary>
    public static string[]? Parse(string? policy) =>
        policy is not null && policy.StartsWith(PolicyPrefix, StringComparison.Ordinal)
            ? policy[PolicyPrefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries)
            : null;
}

/// <summary>Satisfied when the user holds any of <see cref="AnyOf"/> and has no pending password change.</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(IReadOnlyList<string> anyOf) { AnyOf = anyOf; }
    public IReadOnlyList<string> AnyOf { get; }
}

public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true) return Task.CompletedTask;
        // A temporary password must be replaced before anything else is allowed.
        if (user.HasClaim(AccessClaims.MustChangePassword, "1"))
        {
            context.Fail(new AuthorizationFailureReason(this, AccessProblem.PasswordChangeRequired));
            return Task.CompletedTask;
        }
        foreach (var p in requirement.AnyOf)
        {
            if (user.HasClaim(AccessClaims.Permission, p))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
        }
        context.Fail(new AuthorizationFailureReason(this, AccessProblem.PermissionDenied));
        return Task.CompletedTask;
    }
}

/// <summary>Builds <c>perm:a|b</c> policies on demand; every other name goes to the default provider.</summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    private readonly DefaultAuthorizationPolicyProvider _fallback;
    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options) { _fallback = new DefaultAuthorizationPolicyProvider(options); }

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var perms = RequirePermissionAttribute.Parse(policyName);
        if (perms is null) return _fallback.GetPolicyAsync(policyName);
        var policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(perms))
            .Build();
        return Task.FromResult<AuthorizationPolicy?>(policy);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();
    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();
}

/// <summary>
/// Writes 403s as RFC 7807 problem details with a stable <c>code</c>
/// (<c>permission_denied</c> or <c>password_change_required</c>) and the
/// permissions the endpoint needs, so clients can explain the refusal.
/// 401s keep the framework challenge (the client refreshes and retries).
/// </summary>
public sealed class AccessProblemResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (!result.Forbidden || context.User.Identity?.IsAuthenticated != true)
        {
            await _default.HandleAsync(next, context, policy, result);
            return;
        }
        var reasons = result.AuthorizationFailure?.FailureReasons.Select(r => r.Message).ToList() ?? new List<string>();
        var passwordChange = reasons.Contains(AccessProblem.PasswordChangeRequired);
        var required = policy.Requirements.OfType<PermissionRequirement>().SelectMany(r => r.AnyOf).Distinct().ToArray();

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers.CacheControl = "no-store";
        var body = new Dictionary<string, object?>
        {
            ["type"] = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.4",
            ["title"] = passwordChange ? "Password change required" : "Forbidden",
            ["status"] = 403,
            ["detail"] = passwordChange
                ? "Change your temporary password before using the system."
                : required.Length > 0
                    ? $"You don't have permission for this action. Needs: {string.Join(" or ", required)}."
                    : "You don't have permission for this action.",
            ["code"] = passwordChange ? AccessProblem.PasswordChangeRequired : AccessProblem.PermissionDenied,
            ["requiredPermissions"] = required,
            ["traceId"] = System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier,
        };
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}

public static class AccessProblem
{
    public const string PermissionDenied = "permission_denied";
    public const string PasswordChangeRequired = "password_change_required";
}

/// <summary>Controller access to the caller's <see cref="AccessContext"/>.</summary>
public static class AccessContextExtensions
{
    /// <summary>
    /// The caller's access. Only call from actions that require an
    /// authenticated user (every non-anonymous action does).
    /// </summary>
    public static AccessContext Caller(this Microsoft.AspNetCore.Mvc.ControllerBase controller) =>
        AccessContext.FromPrincipal(controller.User)
        ?? throw new InvalidOperationException("No authenticated caller.");

    /// <summary>
    /// For endpoints of modules that are not factory-partitioned yet: a
    /// line-scoped caller (Production Supervisor) may only act on their own
    /// lines. Returns the 403 to send, or null when the line is in scope.
    /// </summary>
    public static Microsoft.AspNetCore.Mvc.IActionResult? LineOutOfScope(this Microsoft.AspNetCore.Mvc.ControllerBase controller, string? lineId)
    {
        var caller = controller.Caller();
        if (!caller.IsLineScoped) return null;
        if (!string.IsNullOrWhiteSpace(lineId) && caller.LineIds.Contains(lineId.Trim())) return null;
        return BunonBrain.Api.Controllers.AccessResults.Problem(controller, StatusCodes.Status403Forbidden, "Forbidden",
            "line_out_of_scope", "You can only record this for one of your assigned production lines.");
    }
}
