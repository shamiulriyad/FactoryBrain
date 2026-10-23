using FactoryBrain.Application.Dtos.Ask;
using FluentValidation;

namespace FactoryBrain.Application.Validation;

public class AskRequestValidator : AbstractValidator<AskRequest>
{
    public AskRequestValidator()
    {
        RuleFor(x => x.Query).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.AgentId)
            .MaximumLength(64)
            .When(x => !string.IsNullOrEmpty(x.AgentId));
        When(x => x.Filter is not null, () =>
        {
            RuleFor(x => x.Filter!.Department)
                .IsInEnum().When(x => x.Filter!.Department.HasValue);
            RuleFor(x => x.Filter!.Category)
                .IsInEnum().When(x => x.Filter!.Category.HasValue);
        });
    }
}
