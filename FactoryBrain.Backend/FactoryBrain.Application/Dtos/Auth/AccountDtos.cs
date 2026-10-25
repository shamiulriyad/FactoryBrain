using FactoryBrain.Application.Abstractions.Interfaces;

namespace FactoryBrain.Application.Dtos.Auth;

/// <summary>POST /api/auth/register. The new account is always a Viewer.</summary>
public record RegisterRequest(string Email, string Password);

/// <summary>POST /api/auth/forgot-password. The response never says whether the email has an account.</summary>
public record ForgotPasswordRequest(string Email);

/// <summary>POST /api/auth/reset-password, with the token from the emailed link.</summary>
public record ResetPasswordRequest(string Token, string Password);

/// <summary>GET /api/auth/options: what the sign-in page may offer.</summary>
public record AuthOptionsResponse(bool SelfRegistration, bool PasswordReset, int PasswordMinLength);

/// <summary>One row of GET /api/users (Admin).</summary>
public record UserListItem(Guid Id, string Email, string Role, DateTimeOffset CreatedAt);

/// <summary>PATCH /api/users/{id}/role body.</summary>
public record ChangeRoleRequest(string Role);

/// <summary>A message for <see cref="FactoryBrain.Application.Abstractions.Interfaces.IEmailSender"/>.</summary>
public record EmailMessage(string To, string Subject, string TextBody);

public enum RegisterOutcome { Created, EmailTaken, Disabled }
public record RegisterResult(RegisterOutcome Outcome, LoginResult? Login);

public enum ResetOutcome { Reset, InvalidOrExpired }

public enum RoleChangeOutcome { Changed, NotFound, InvalidRole, OwnRole, LastAdmin }
public record RoleChangeResult(RoleChangeOutcome Outcome, UserListItem? User);
