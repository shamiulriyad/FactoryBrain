using System.Security.Claims;
using BunonBrain.Application.Abstractions;

namespace BunonBrain.Application.Access;

/// <summary>Claim names carried by BunonBrain access tokens.</summary>
public static class AccessClaims
{
    public const string Subject = "sub";
    public const string Email = "email";
    public const string Name = "name";
    /// <summary>One claim per role id. Same type the bearer handler maps to <c>IsInRole</c>.</summary>
    public const string Role = ClaimTypes.Role;
    /// <summary>One claim per effective permission code.</summary>
    public const string Permission = "perm";
    /// <summary>One claim per assigned factory id.</summary>
    public const string Factory = "fid";
    /// <summary>One claim per assigned production-line id.</summary>
    public const string Line = "lid";
    /// <summary>The user's security stamp at issue time. A token whose stamp no longer matches is rejected.</summary>
    public const string SecurityStamp = "stamp";
    /// <summary>"1" while the user must change a temporary password; permission checks then fail.</summary>
    public const string MustChangePassword = "pwc";
}

/// <summary>
/// Everything the server knows about a user's access at token-issue time.
/// Built from the database by the snapshot loader; serialized into the JWT.
/// </summary>
public sealed record AccessSnapshot(
    Guid UserId,
    string Email,
    string? FullName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<FactoryRef> Factories,
    IReadOnlyList<string> LineIds,
    string SecurityStamp,
    bool MustChangePassword)
{
    public RoleScope Scope => AccessCatalog.ScopeFor(Roles);
}

/// <summary>Short reference to a factory.</summary>
public sealed record FactoryRef(Guid Id, string Code, string Name);

/// <summary>
/// The caller's access, read from the validated JWT. Every service that
/// enforces scope takes one of these; it is never built from request input.
/// </summary>
public sealed class AccessContext
{
    public Guid UserId { get; }
    public string Email { get; }
    public IReadOnlyList<string> Roles { get; }
    public IReadOnlySet<string> Permissions { get; }
    public IReadOnlySet<Guid> FactoryIds { get; }
    public IReadOnlySet<string> LineIds { get; }
    public bool MustChangePassword { get; }
    public RoleScope Scope { get; }

    public AccessContext(Guid userId, string email, IEnumerable<string> roles, IEnumerable<string> permissions,
        IEnumerable<Guid> factoryIds, IEnumerable<string> lineIds, bool mustChangePassword = false)
    {
        UserId = userId;
        Email = email;
        Roles = roles.Distinct(StringComparer.Ordinal).ToArray();
        Permissions = permissions.ToHashSet(StringComparer.Ordinal);
        FactoryIds = factoryIds.ToHashSet();
        LineIds = lineIds.ToHashSet(StringComparer.Ordinal);
        MustChangePassword = mustChangePassword;
        Scope = AccessCatalog.ScopeFor(Roles);
    }

    /// <summary>Super Admin: reaches every factory.</summary>
    public bool IsPlatform => Scope == RoleScope.Platform;

    /// <summary>Only the lines assigned to the user (Production Supervisor without a factory-wide role).</summary>
    public bool IsLineScoped => Scope == RoleScope.Line;

    public bool Has(string permission) => Permissions.Contains(permission);

    public bool HasAny(params string[] permissions) => permissions.Any(Permissions.Contains);

    public bool CanAccessFactory(Guid factoryId) => IsPlatform || FactoryIds.Contains(factoryId);

    /// <summary>
    /// Whether a line (that belongs to <paramref name="lineFactoryId"/>, if
    /// known) is inside the caller's scope.
    /// </summary>
    public bool CanAccessLine(string lineId, Guid? lineFactoryId)
    {
        if (IsPlatform) return true;
        if (Scope == RoleScope.Factory) return lineFactoryId is { } f && FactoryIds.Contains(f);
        if (Scope == RoleScope.Line) return LineIds.Contains(lineId) && (lineFactoryId is null || FactoryIds.Contains(lineFactoryId.Value));
        return false;
    }

    public string PrimaryRole => AccessCatalog.PrimaryRole(Roles);

    /// <summary>Identity for audit records.</summary>
    public AuditActor Actor => new(UserId, Email, PrimaryRole);

    /// <summary>Read the caller's access from a validated principal. Null when it has no usable subject.</summary>
    public static AccessContext? FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        var sub = principal.FindFirst(AccessClaims.Subject)?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(sub, out var userId)) return null;
        var email = principal.FindFirst(AccessClaims.Email)?.Value ?? principal.Identity?.Name ?? "unknown";
        return new AccessContext(
            userId,
            email,
            principal.FindAll(AccessClaims.Role).Select(c => c.Value),
            principal.FindAll(AccessClaims.Permission).Select(c => c.Value),
            principal.FindAll(AccessClaims.Factory).Select(c => Guid.TryParse(c.Value, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty),
            principal.FindAll(AccessClaims.Line).Select(c => c.Value),
            principal.FindFirst(AccessClaims.MustChangePassword)?.Value == "1");
    }

    /// <summary>Build the context a token for <paramref name="snapshot"/> would carry.</summary>
    public static AccessContext FromSnapshot(AccessSnapshot snapshot) => new(
        snapshot.UserId, snapshot.Email, snapshot.Roles, snapshot.Permissions,
        snapshot.Factories.Select(f => f.Id), snapshot.LineIds, snapshot.MustChangePassword);
}
