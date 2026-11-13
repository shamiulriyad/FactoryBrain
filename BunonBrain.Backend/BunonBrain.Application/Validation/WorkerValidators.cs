using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Application.Workers;
using FluentValidation;

namespace BunonBrain.Application.Validation;

/// <summary>The fields a new worker and an edited worker share, validated once.</summary>
internal static class WorkerFieldRules
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(14)); // the latest "today" anywhere on earth

    public static void FullName<T>(IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().WithMessage("Full name is required.").MaximumLength(WorkerRules.NameMax);

    public static void RequiredPhone<T>(IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Phone number is required.")
            .Must(p => string.IsNullOrWhiteSpace(p) || WorkerRules.IsPhone(p.Trim()))
            .WithMessage("Phone may contain digits, spaces, +, - and parentheses (at least 6 digits).");

    public static void OptionalPhone<T>(IRuleBuilder<T, string?> rule) =>
        rule.Must(p => string.IsNullOrWhiteSpace(p) || WorkerRules.IsPhone(p.Trim()))
            .WithMessage("Phone may contain digits, spaces, +, - and parentheses (at least 6 digits).");

    public static void JoinedOn<T>(IRuleBuilder<T, DateOnly?> rule) =>
        rule.NotNull().WithMessage("Joining date is required.")
            .Must(d => d is null || WorkerRules.JoiningDateProblem(d.Value, Today) is null)
            .WithMessage("Joining date must be a real date, not more than 90 days in the future.");

    public static void Department<T>(IRuleBuilder<T, Guid?> rule) =>
        rule.Must(d => d is not null && d != Guid.Empty).WithMessage("Department is required.");

    public static void Designation<T>(IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Designation is required.").MaximumLength(WorkerRules.DesignationMax);

    public static void EmploymentType<T>(IRuleBuilder<T, string?> rule) =>
        rule.Must(t => string.IsNullOrWhiteSpace(t) || EmploymentTypes.Parse(t) is not null)
            .WithMessage($"Employment type must be one of: {string.Join(", ", EmploymentTypes.All)}.");

    public static void EmployeeCode<T>(IRuleBuilder<T, string?> rule) =>
        rule.Must(c => string.IsNullOrWhiteSpace(c) || WorkerRules.IsEmployeeCode(c.Trim()))
            .WithMessage("Employee number: letters, digits, '-', '_' and '/', up to 32 characters.");

    public static void Gender<T>(IRuleBuilder<T, string?> rule) =>
        rule.Must(g => string.IsNullOrWhiteSpace(g) || Genders.Parse(g) is not null)
            .WithMessage($"Gender must be one of: {string.Join(", ", Genders.All)}, or left empty.");

    public static void Email<T>(IRuleBuilder<T, string?> rule) =>
        rule.Must(e => string.IsNullOrWhiteSpace(e) || WorkerRules.IsEmail(e.Trim())).WithMessage("Email is not a valid address.");

    public static string? DateOfBirthProblem(DateOnly? born, DateOnly? joined) =>
        born is null ? null : WorkerRules.DateOfBirthProblem(born.Value, Today, joined);
}

public class CreateWorkerRequestValidator : AbstractValidator<CreateWorkerRequest>
{
    public CreateWorkerRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty().WithMessage("Factory is required.");
        WorkerFieldRules.FullName(RuleFor(x => x.FullName));
        WorkerFieldRules.RequiredPhone(RuleFor(x => x.Phone));
        WorkerFieldRules.JoinedOn(RuleFor(x => x.JoinedOn));
        WorkerFieldRules.Department(RuleFor(x => x.DepartmentId));
        WorkerFieldRules.Designation(RuleFor(x => x.Designation));
        WorkerFieldRules.EmploymentType(RuleFor(x => x.EmploymentType));
        WorkerFieldRules.EmployeeCode(RuleFor(x => x.EmployeeCode));
        WorkerFieldRules.Gender(RuleFor(x => x.Gender));
        WorkerFieldRules.Email(RuleFor(x => x.Email));
        RuleFor(x => x.DateOfBirth).Custom((born, ctx) =>
        {
            if (WorkerFieldRules.DateOfBirthProblem(born, ctx.InstanceToValidate.JoinedOn) is { } problem) ctx.AddFailure(problem);
        });
        RuleFor(x => x.PresentAddress).MaximumLength(WorkerRules.AddressMax);
        RuleFor(x => x.EmergencyContactName).MaximumLength(WorkerRules.NameMax);
        WorkerFieldRules.OptionalPhone(RuleFor(x => x.EmergencyContactPhone));
        RuleFor(x => x.EmergencyContactRelation).MaximumLength(WorkerRules.RelationMax);
        RuleFor(x => x.LineId).MaximumLength(64);
        RuleFor(x => x.Grade).MaximumLength(WorkerRules.GradeMax);
    }
}

public class UpdateWorkerRequestValidator : AbstractValidator<UpdateWorkerRequest>
{
    public UpdateWorkerRequestValidator()
    {
        WorkerFieldRules.FullName(RuleFor(x => x.FullName));
        WorkerFieldRules.RequiredPhone(RuleFor(x => x.Phone));
        WorkerFieldRules.JoinedOn(RuleFor(x => x.JoinedOn));
        WorkerFieldRules.Department(RuleFor(x => x.DepartmentId));
        WorkerFieldRules.Designation(RuleFor(x => x.Designation));
        WorkerFieldRules.EmploymentType(RuleFor(x => x.EmploymentType));
        WorkerFieldRules.EmployeeCode(RuleFor(x => x.EmployeeCode));
        WorkerFieldRules.Gender(RuleFor(x => x.Gender));
        WorkerFieldRules.Email(RuleFor(x => x.Email));
        RuleFor(x => x.DateOfBirth).Custom((born, ctx) =>
        {
            if (WorkerFieldRules.DateOfBirthProblem(born, ctx.InstanceToValidate.JoinedOn) is { } problem) ctx.AddFailure(problem);
        });
        RuleFor(x => x.PresentAddress).MaximumLength(WorkerRules.AddressMax);
        RuleFor(x => x.EmergencyContactName).MaximumLength(WorkerRules.NameMax);
        WorkerFieldRules.OptionalPhone(RuleFor(x => x.EmergencyContactPhone));
        RuleFor(x => x.EmergencyContactRelation).MaximumLength(WorkerRules.RelationMax);
        RuleFor(x => x.LineId).MaximumLength(64);
        RuleFor(x => x.Grade).MaximumLength(WorkerRules.GradeMax);
    }
}

public class ChangeWorkerStatusRequestValidator : AbstractValidator<ChangeWorkerStatusRequest>
{
    public ChangeWorkerStatusRequestValidator()
    {
        RuleFor(x => x.Status).NotEmpty().Must(s => WorkerStatuses.Parse(s) is not null)
            .WithMessage($"Status must be one of: {string.Join(", ", WorkerStatuses.All)}.");
        RuleFor(x => x.Reason).MaximumLength(WorkerRules.ReasonMax);
    }
}

public class AssignWorkstationRequestValidator : AbstractValidator<AssignWorkstationRequest>
{
    public AssignWorkstationRequestValidator()
    {
        RuleFor(x => x.LineId).MaximumLength(64);
        RuleFor(x => x.Workstation).MaximumLength(WorkerRules.WorkstationMax);
    }
}

public class CreateWorkerAccountRequestValidator : AbstractValidator<CreateWorkerAccountRequest>
{
    public CreateWorkerAccountRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(LoginRequestValidator.MaxFieldLength);
        RuleFor(x => x.TemporaryPassword).NewPassword()
            .Must((req, pw) => !string.Equals(pw?.Trim(), req.Email?.Trim(), StringComparison.OrdinalIgnoreCase))
            .WithMessage("Password must not be the email address.");
    }
}

public class IssueWorkerCardRequestValidator : AbstractValidator<IssueWorkerCardRequest>
{
    public IssueWorkerCardRequestValidator()
    {
        RuleFor(x => x.CardUid).NotEmpty().MaximumLength(WorkerRules.CardUidMax + 32)
            .Must(u => u is null || WorkerRules.IsCardUid(WorkerRules.NormaliseCardUid(u)))
            .WithMessage("Card ID: 4 to 64 letters and digits (spaces, ':' and '-' are ignored).");
        RuleFor(x => x.CardType).Must(t => string.IsNullOrWhiteSpace(t) || CardTypes.Parse(t) is not null)
            .WithMessage($"Card type must be one of: {string.Join(", ", CardTypes.All)}.");
        RuleFor(x => x.Label).MaximumLength(WorkerRules.CardLabelMax);
    }
}

public class DeactivateWorkerCardRequestValidator : AbstractValidator<DeactivateWorkerCardRequest>
{
    public DeactivateWorkerCardRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().Must(r => CardDeactivationReasons.Parse(r) is not null)
            .WithMessage($"Reason must be one of: {string.Join(", ", CardDeactivationReasons.Selectable)}.");
    }
}

public class CreateOrgUnitRequestValidator : AbstractValidator<CreateOrgUnitRequest>
{
    public CreateOrgUnitRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Must(c => c is null || WorkerRules.IsOrgCode(c.Trim()))
            .WithMessage("Code: letters, digits, spaces, '-' and '_', up to 16 characters.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
    }
}

public class UpdateOrgUnitRequestValidator : AbstractValidator<UpdateOrgUnitRequest>
{
    public UpdateOrgUnitRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
    }
}
