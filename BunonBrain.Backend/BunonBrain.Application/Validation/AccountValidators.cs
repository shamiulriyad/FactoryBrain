using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Access;
using BunonBrain.Application.Dtos.Auth;
using FluentValidation;

namespace BunonBrain.Application.Validation;

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

public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(LoginRequestValidator.MaxFieldLength);
        RuleFor(x => x.FullName).MaximumLength(AccessFieldRules.NameMax);
        RuleFor(x => x.Phone).MaximumLength(AccessFieldRules.PhoneMax).Matches(AccessFieldRules.PhonePattern)
            .When(x => !string.IsNullOrEmpty(x.Phone)).WithMessage("Phone may contain digits, spaces, +, - and parentheses.");
        RuleFor(x => x.TemporaryPassword).NewPassword()
            .Must((req, pw) => !string.Equals(pw?.Trim(), req.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
            .WithMessage("Password must not be the email address.");
        RuleFor(x => x.Roles).Must(AccessFieldRules.KnownRoles).WithMessage("Unknown role.");
        RuleFor(x => x.Roles).Must(r => r is null || r.Count <= AccessCatalog.AllRoles.Count);
        RuleFor(x => x.FactoryIds).Must(f => f is null || f.Count <= AccessFieldRules.MaxFactories)
            .WithMessage($"At most {AccessFieldRules.MaxFactories} factories.");
        RuleFor(x => x.LineIds).Must(l => l is null || l.Count <= AccessFieldRules.MaxLines)
            .WithMessage($"At most {AccessFieldRules.MaxLines} lines.");
    }
}

public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(LoginRequestValidator.MaxFieldLength);
        RuleFor(x => x.FullName).MaximumLength(AccessFieldRules.NameMax);
        RuleFor(x => x.Phone).MaximumLength(AccessFieldRules.PhoneMax).Matches(AccessFieldRules.PhonePattern)
            .When(x => !string.IsNullOrEmpty(x.Phone)).WithMessage("Phone may contain digits, spaces, +, - and parentheses.");
    }
}

public class SetUserStatusRequestValidator : AbstractValidator<SetUserStatusRequest>
{
    public SetUserStatusRequestValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public class AssignRolesRequestValidator : AbstractValidator<AssignRolesRequest>
{
    public AssignRolesRequestValidator()
    {
        RuleFor(x => x.Roles).NotNull().Must(AccessFieldRules.KnownRoles).WithMessage("Unknown role.");
        RuleFor(x => x.Roles).Must(r => r is null || r.Count <= AccessCatalog.AllRoles.Count);
    }
}

public class AssignScopeRequestValidator : AbstractValidator<AssignScopeRequest>
{
    public AssignScopeRequestValidator()
    {
        RuleFor(x => x.FactoryIds).NotNull().Must(f => f is null || f.Count <= AccessFieldRules.MaxFactories)
            .WithMessage($"At most {AccessFieldRules.MaxFactories} factories.");
        RuleFor(x => x.LineIds).Must(l => l is null || l.Count <= AccessFieldRules.MaxLines)
            .WithMessage($"At most {AccessFieldRules.MaxLines} lines.");
    }
}

public class AdminResetPasswordRequestValidator : AbstractValidator<AdminResetPasswordRequest>
{
    public AdminResetPasswordRequestValidator()
    {
        RuleFor(x => x.TemporaryPassword).NewPassword();
    }
}

public class UpdateProfileRequestValidator : AbstractValidator<UpdateProfileRequest>
{
    public UpdateProfileRequestValidator()
    {
        RuleFor(x => x.FullName).MaximumLength(AccessFieldRules.NameMax);
        RuleFor(x => x.Phone).MaximumLength(AccessFieldRules.PhoneMax).Matches(AccessFieldRules.PhonePattern)
            .When(x => !string.IsNullOrEmpty(x.Phone)).WithMessage("Phone may contain digits, spaces, +, - and parentheses.");
    }
}

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(PasswordRules.MaxLength);
        RuleFor(x => x.NewPassword).NewPassword()
            .Must((req, pw) => !string.Equals(pw, req.CurrentPassword, StringComparison.Ordinal))
            .WithMessage("The new password must be different from the current one.");
    }
}

public class CreateFactoryRequestValidator : AbstractValidator<CreateFactoryRequest>
{
    public CreateFactoryRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches("^[A-Za-z0-9][A-Za-z0-9-]*$")
            .WithMessage("Code: letters, digits and hyphens, up to 16 characters.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(AccessFieldRules.NameMax);
        RuleFor(x => x.Location).MaximumLength(200);
    }
}

public class UpdateFactoryRequestValidator : AbstractValidator<UpdateFactoryRequest>
{
    public UpdateFactoryRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(AccessFieldRules.NameMax);
        RuleFor(x => x.Location).MaximumLength(200);
    }
}

/// <summary>Shared limits for access-management requests.</summary>
public static class AccessFieldRules
{
    public const int NameMax = 120;
    public const int PhoneMax = 32;
    public const string PhonePattern = @"^[0-9+()\- ]+$";
    public const int MaxFactories = 50;
    public const int MaxLines = 200;

    public static bool KnownRoles(IReadOnlyList<string>? roles) => roles is null || roles.All(AccessCatalog.IsRole);
}
