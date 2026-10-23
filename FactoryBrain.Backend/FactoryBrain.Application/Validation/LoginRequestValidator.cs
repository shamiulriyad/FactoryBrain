using FactoryBrain.Application.Dtos.Auth;
using FluentValidation;

namespace FactoryBrain.Application.Validation;

/// <summary>
/// POST /api/auth/login body validation. Email must be present, parse as
/// an address, and stay under 256 chars; password must be present and
/// under 256 chars. The same 51 FluentValidation funnel handles the
/// <c>errors.email</c> / <c>errors.password</c> envelope on failure —
/// no controller-level error mapping is needed.
/// </summary>
public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public const int MaxFieldLength = 256;

    public LoginRequestValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(MaxFieldLength);
        RuleFor(x => x.Password)
            .NotEmpty()
            .MaximumLength(MaxFieldLength);
    }
}
