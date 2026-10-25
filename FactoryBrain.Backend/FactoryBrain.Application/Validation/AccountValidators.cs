using FactoryBrain.Application.Dtos.Auth;
using FluentValidation;

namespace FactoryBrain.Application.Validation;

/// <summary>Password rules shared by sign-up and reset.</summary>
public static class PasswordRules
{
    public const int MinLength = 10;
    public const int MaxLength = 256;

    public static IRuleBuilderOptions<T, string> NewPassword<T>(this IRuleBuilder<T, string> rule) => rule
        .NotEmpty()
        .MinimumLength(MinLength).WithMessage($"Password must be at least {MinLength} characters.")
        .MaximumLength(MaxLength);
}

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(LoginRequestValidator.MaxFieldLength);
        RuleFor(x => x.Password).NewPassword()
            .Must((req, pw) => !string.Equals(pw?.Trim(), req.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
            .WithMessage("Password must not be your email address.");
    }
}

public class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(LoginRequestValidator.MaxFieldLength);
    }
}

public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        // Tokens are 32 random bytes, base64url (43 chars). Anything else is not ours.
        RuleFor(x => x.Token).NotEmpty().Length(43).Matches("^[A-Za-z0-9_-]+$");
        RuleFor(x => x.Password).NewPassword();
    }
}

public class ChangeRoleRequestValidator : AbstractValidator<ChangeRoleRequest>
{
    public ChangeRoleRequestValidator()
    {
        RuleFor(x => x.Role).NotEmpty().Must(r => r is "Admin" or "Operator" or "Viewer")
            .WithMessage("Role must be Admin, Operator or Viewer.");
    }
}
