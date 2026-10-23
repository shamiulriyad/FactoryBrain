namespace FactoryBrain.Application.Dtos.Auth;

/// <summary>
/// POST /api/auth/login body. Validated by
/// <see cref="FactoryBrain.Application.Validation.LoginRequestValidator"/>
/// through the global FluentValidationFilter; on failure the framework's
/// 51 funnel emits the standard <c>ValidationProblemDetails</c> with
/// camelCased <c>errors.email</c> / <c>errors.password</c> entries.
/// </summary>
public record LoginRequest(string Email, string Password);

/// <summary>Wire shape returned by login + refresh on success.</summary>
public record AuthResponse(string AccessToken, int ExpiresIn, UserInfo User);

/// <summary>
/// Public view of a user. Returned inside <see cref="AuthResponse"/>
/// and by GET /api/auth/me. Never carries the password hash or the
/// refresh-token hash.
/// </summary>
public record UserInfo(Guid Id, string Email, string Role);
