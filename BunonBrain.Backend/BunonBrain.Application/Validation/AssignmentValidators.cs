using BunonBrain.Application.Assignments;
using BunonBrain.Application.Dtos.Assignments;
using FluentValidation;

namespace BunonBrain.Application.Validation;

internal static class AssignmentFieldRules
{
    public const string CodeMessage = "Code: letters, digits, '-', '_', '/' and '.', up to 24 characters.";

    public static void Code<T>(IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().Must(AssignmentRules.IsCode).WithMessage(CodeMessage);

    public static void Capacity<T>(IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(AssignmentRules.MinCapacity, AssignmentRules.MaxCapacity)
            .WithMessage($"Capacity must be between {AssignmentRules.MinCapacity} and {AssignmentRules.MaxCapacity} workers.");
}

public class CreateWorkstationRequestValidator : AbstractValidator<CreateWorkstationRequest>
{
    public CreateWorkstationRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        RuleFor(x => x.LineId).NotEmpty().MaximumLength(32);
        RuleFor(x => x.DepartmentId).NotEmpty().WithMessage("Choose the workstation's department.");
        AssignmentFieldRules.Code(RuleFor(x => x.Code));
        RuleFor(x => x.Name).MaximumLength(AssignmentRules.NameMax);
        RuleFor(x => x.Capacity!.Value).InclusiveBetween(AssignmentRules.MinCapacity, AssignmentRules.MaxCapacity)
            .OverridePropertyName("capacity").When(x => x.Capacity.HasValue)
            .WithMessage($"Capacity must be between {AssignmentRules.MinCapacity} and {AssignmentRules.MaxCapacity} workers.");
        RuleFor(x => x.Position!.Value).InclusiveBetween(0, 10_000).OverridePropertyName("position").When(x => x.Position.HasValue);
    }
}

public class UpdateWorkstationRequestValidator : AbstractValidator<UpdateWorkstationRequest>
{
    public UpdateWorkstationRequestValidator()
    {
        RuleFor(x => x.LineId).NotEmpty().MaximumLength(32);
        RuleFor(x => x.DepartmentId).NotEmpty().WithMessage("Choose the workstation's department.");
        RuleFor(x => x.Name).MaximumLength(AssignmentRules.NameMax);
        AssignmentFieldRules.Capacity(RuleFor(x => x.Capacity));
        RuleFor(x => x.Position).InclusiveBetween(0, 10_000);
    }
}

public class BulkCreateWorkstationsRequestValidator : AbstractValidator<BulkCreateWorkstationsRequest>
{
    public BulkCreateWorkstationsRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        RuleFor(x => x.LineId).NotEmpty().MaximumLength(32);
        RuleFor(x => x.DepartmentId).NotEmpty().WithMessage("Choose the workstations' department.");
        RuleFor(x => x.Prefix).NotEmpty().MaximumLength(16).Must(p => p is null || AssignmentRules.IsCode(p + "1"))
            .WithMessage("Prefix: letters, digits, '-', '_', '/' and '.', e.g. T-.");
        RuleFor(x => x.From).InclusiveBetween(0, 99_999);
        RuleFor(x => x.Count).InclusiveBetween(1, AssignmentRules.MaxBulkWorkstations)
            .WithMessage($"Create between 1 and {AssignmentRules.MaxBulkWorkstations} workstations at a time.");
        RuleFor(x => x.Capacity!.Value).InclusiveBetween(AssignmentRules.MinCapacity, AssignmentRules.MaxCapacity)
            .OverridePropertyName("capacity").When(x => x.Capacity.HasValue);
    }
}

public class CreateMachineRequestValidator : AbstractValidator<CreateMachineRequest>
{
    public CreateMachineRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        AssignmentFieldRules.Code(RuleFor(x => x.Code));
        RuleFor(x => x.MachineType).MaximumLength(80);
        RuleFor(x => x.Brand).MaximumLength(80);
        RuleFor(x => x.SerialNumber).MaximumLength(80);
    }
}

public class UpdateMachineRequestValidator : AbstractValidator<UpdateMachineRequest>
{
    public UpdateMachineRequestValidator()
    {
        RuleFor(x => x.MachineType).MaximumLength(80);
        RuleFor(x => x.Brand).MaximumLength(80);
        RuleFor(x => x.SerialNumber).MaximumLength(80);
    }
}

public class CreateAssignmentRequestValidator : AbstractValidator<CreateAssignmentRequest>
{
    public CreateAssignmentRequestValidator()
    {
        RuleFor(x => x.WorkerId).NotEmpty().WithMessage("Choose a worker.");
        RuleFor(x => x.WorkstationId).NotEmpty().WithMessage("Choose a workstation.");
        RuleFor(x => x.Reason).MaximumLength(AssignmentRules.ReasonMax);
        RuleFor(x => x.Notes).MaximumLength(AssignmentRules.NotesMax);
    }
}

public class TransferAssignmentRequestValidator : AbstractValidator<TransferAssignmentRequest>
{
    public TransferAssignmentRequestValidator()
    {
        RuleFor(x => x.WorkstationId).NotEmpty().WithMessage("Choose the new workstation.");
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Give a reason for the transfer.").MaximumLength(AssignmentRules.ReasonMax);
        RuleFor(x => x.Notes).MaximumLength(AssignmentRules.NotesMax);
    }
}

public class EndAssignmentRequestValidator : AbstractValidator<EndAssignmentRequest>
{
    public EndAssignmentRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Give a reason for ending the assignment.").MaximumLength(AssignmentRules.ReasonMax);
    }
}
