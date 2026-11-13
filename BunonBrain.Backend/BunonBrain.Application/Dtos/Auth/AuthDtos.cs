using BunonBrain.Application.Access;

namespace BunonBrain.Application.Dtos.Auth;

/// <summary>
/// POST /api/auth/login body. Validated by
/// <see cref="BunonBrain.Application.Validation.LoginRequestValidator"/>
/// through the global FluentValidationFilter; on failure the framework's
/// 51 funnel emits the standard <c>ValidationProblemDetails</c> with
/// camelCased <c>errors.email</c> / <c>errors.password</c> entries.
/// </summary>
public record LoginRequest(string Email, string Password);

/// <summary>Wire shape returned by login + refresh on success.</summary>
public record AuthResponse(string AccessToken, int ExpiresIn, UserInfo User);

/// <summary>
/// Public view of the signed-in user. Returned inside <see cref="AuthResponse"/>
/// and by GET /api/auth/me. Never carries the password hash or the
/// refresh-token hash. <see cref="Role"/> is the highest-ranked role (display
/// only); <see cref="Roles"/> and <see cref="Permissions"/> are what the
/// server enforces. <see cref="Scope"/> is Platform, Factory, Line or Self.
/// </summary>
public record UserInfo(
    Guid Id,
    string Email,
    string? FullName,
    string Role,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    string Scope,
    IReadOnlyList<FactoryRef> Factories,
    IReadOnlyList<string> LineIds,
    bool MustChangePassword)
{
    public static UserInfo From(AccessSnapshot s) => new(
        s.UserId, s.Email, s.FullName, AccessCatalog.PrimaryRole(s.Roles), s.Roles, s.Permissions,
        s.Scope.ToString(), s.Factories, s.LineIds, s.MustChangePassword);
}
