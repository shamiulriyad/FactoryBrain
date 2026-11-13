using System.Globalization;
using BunonBrain.Application.Attendance;
using BunonBrain.Application.Dtos.Attendance;
using FluentValidation;

namespace BunonBrain.Application.Validation;

internal static class AttendanceFieldRules
{
    public static bool IsTime(string? v) => TimeOnly.TryParseExact(v?.Trim(), new[] { "HH:mm", "H:mm", "HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

}

public class ShiftBreakDtoValidator : AbstractValidator<ShiftBreakDto>
{
    public ShiftBreakDtoValidator()
    {
        RuleFor(x => x.BreakType).Must(t => BreakTypes.Parse(t) is not null).WithMessage($"Break type must be one of: {string.Join(", ", BreakTypes.All)}.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.DurationMinutes).InclusiveBetween(1, 240);
        RuleFor(x => x.StartLocal).Must(t => string.IsNullOrWhiteSpace(t) || AttendanceFieldRules.IsTime(t)).WithMessage("Break start must be a time like 13:00.");
    }
}

public class CreateShiftRequestValidator : AbstractValidator<CreateShiftRequest>
{
    public CreateShiftRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().Must(AttendanceRules.IsCode).WithMessage("Code: letters, digits, '-' and '_', up to 16 characters.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.StartLocal).Must(AttendanceFieldRules.IsTime).WithMessage("Start must be a time like 08:00.");
        RuleFor(x => x.EndLocal).Must(AttendanceFieldRules.IsTime).WithMessage("End must be a time like 17:00.");
        RuleFor(x => x).Must(x => !AttendanceFieldRules.IsTime(x.StartLocal) || !AttendanceFieldRules.IsTime(x.EndLocal) || x.StartLocal.Trim() != x.EndLocal.Trim())
            .WithName("endLocal").WithMessage("Start and end can't be the same time.");
        RuleFor(x => x.WorkingDays).NotNull().Must(d => d is not null && AttendanceRules.MaskFromNames(d) is not null).WithMessage("Choose at least one working day (Sun … Sat).");
        RuleFor(x => x.LateGraceMinutes).InclusiveBetween(0, 120);
        RuleFor(x => x.EarlyLeaveGraceMinutes).InclusiveBetween(0, 120);
        RuleFor(x => x.Description).MaximumLength(300);
        RuleFor(x => x.ProductionShiftCode).MaximumLength(16);
        RuleFor(x => x.Breaks).Must(b => b is null || b.Count <= 10).WithMessage("At most 10 breaks per shift.");
        RuleForEach(x => x.Breaks).SetValidator(new ShiftBreakDtoValidator());
    }
}

public class UpdateShiftRequestValidator : AbstractValidator<UpdateShiftRequest>
{
    public UpdateShiftRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.StartLocal).Must(AttendanceFieldRules.IsTime).WithMessage("Start must be a time like 08:00.");
        RuleFor(x => x.EndLocal).Must(AttendanceFieldRules.IsTime).WithMessage("End must be a time like 17:00.");
        RuleFor(x => x).Must(x => !AttendanceFieldRules.IsTime(x.StartLocal) || !AttendanceFieldRules.IsTime(x.EndLocal) || x.StartLocal.Trim() != x.EndLocal.Trim())
            .WithName("endLocal").WithMessage("Start and end can't be the same time.");
        RuleFor(x => x.WorkingDays).NotNull().Must(d => d is not null && AttendanceRules.MaskFromNames(d) is not null).WithMessage("Choose at least one working day (Sun … Sat).");
        RuleFor(x => x.LateGraceMinutes).InclusiveBetween(0, 120);
        RuleFor(x => x.EarlyLeaveGraceMinutes).InclusiveBetween(0, 120);
        RuleFor(x => x.Description).MaximumLength(300);
        RuleFor(x => x.ProductionShiftCode).MaximumLength(16);
        RuleFor(x => x.Breaks).Must(b => b is null || b.Count <= 10).WithMessage("At most 10 breaks per shift.");
        RuleForEach(x => x.Breaks).SetValidator(new ShiftBreakDtoValidator());
    }
}

public class AssignShiftRequestValidator : AbstractValidator<AssignShiftRequest>
{
    public AssignShiftRequestValidator()
    {
        RuleFor(x => x.ShiftId).NotEmpty();
        RuleFor(x => x).Must(x => (x.WorkerIds?.Count ?? 0) > 0 || !string.IsNullOrWhiteSpace(x.LineId) || x.DepartmentId is not null)
            .WithName("workerIds").WithMessage("Choose workers, a production line or a department.");
        RuleFor(x => x.WorkerIds).Must(w => w is null || w.Count <= AttendanceRules.MaxBulkWorkers)
            .WithMessage($"At most {AttendanceRules.MaxBulkWorkers} workers at a time.");
        RuleFor(x => x.EffectiveTo).Must((x, to) => to is null || to >= x.EffectiveFrom).WithMessage("The end date can't be before the start date.");
        RuleFor(x => x.Reason).MaximumLength(AttendanceRules.ReasonMax);
    }
}

public class EndShiftAssignmentRequestValidator : AbstractValidator<EndShiftAssignmentRequest>
{
    public EndShiftAssignmentRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(AttendanceRules.ReasonMax);
}

public class PunchRequestValidator : AbstractValidator<PunchRequest>
{
    public PunchRequestValidator()
    {
        RuleFor(x => x.WorkerId).NotEmpty();
        RuleFor(x => x.IdempotencyKey).MaximumLength(128);
        RuleFor(x => x.Note).MaximumLength(AttendanceRules.ReasonMax);
    }
}

public class BreakPunchRequestValidator : AbstractValidator<BreakPunchRequest>
{
    public BreakPunchRequestValidator()
    {
        RuleFor(x => x.WorkerId).NotEmpty();
        RuleFor(x => x.BreakType).Must(t => string.IsNullOrWhiteSpace(t) || BreakTypes.Parse(t) is not null)
            .WithMessage($"Break type must be one of: {string.Join(", ", BreakTypes.All)}.");
        RuleFor(x => x.IdempotencyKey).MaximumLength(128);
        RuleFor(x => x.Note).MaximumLength(AttendanceRules.ReasonMax);
    }
}

public class SimulatorPunchRequestValidator : AbstractValidator<SimulatorPunchRequest>
{
    public SimulatorPunchRequestValidator()
    {
        RuleFor(x => x.WorkerId).NotEmpty();
        RuleFor(x => x.IdempotencyKey).MaximumLength(128);
        RuleFor(x => x.BreakType).Must(t => string.IsNullOrWhiteSpace(t) || BreakTypes.Parse(t) is not null);
    }
}

public class DeviceEventRequestValidator : AbstractValidator<DeviceEventRequest>
{
    public DeviceEventRequestValidator()
    {
        RuleFor(x => x.ReaderId).NotEmpty();
        RuleFor(x => x.CardUid).NotEmpty().MaximumLength(96);
        RuleFor(x => x.EventType).Must(t => AttendanceEventTypes.Parse(t) is not null).WithMessage($"Event type must be one of: {string.Join(", ", AttendanceEventTypes.All)}.");
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(128);
    }
}

public class CreateCorrectionRequestValidator : AbstractValidator<CreateCorrectionRequest>
{
    public CreateCorrectionRequestValidator()
    {
        RuleFor(x => x).Must(x => x.CheckInAt is not null || x.CheckOutAt is not null).WithName("checkInAt").WithMessage("Give the corrected check-in, check-out or both.");
        RuleFor(x => x.CheckOutAt).Must((x, o) => o is null || x.CheckInAt is null || o > x.CheckInAt).WithMessage("Check-out must be after check-in.");
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(AttendanceRules.ReasonMax);
    }
}

public class CreateMissingSessionRequestValidator : AbstractValidator<CreateMissingSessionRequest>
{
    public CreateMissingSessionRequestValidator()
    {
        RuleFor(x => x.WorkerId).NotEmpty();
        RuleFor(x => x.CheckOutAt).Must((x, o) => o > x.CheckInAt).WithMessage("Check-out must be after check-in.");
        RuleFor(x => x).Must(x => x.CheckOutAt - x.CheckInAt <= TimeSpan.FromHours(24)).WithName("checkOutAt").WithMessage("A session can't be longer than 24 hours.");
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(AttendanceRules.ReasonMax);
    }
}

public class CreateOvertimeRequestValidator : AbstractValidator<CreateOvertimeRequest>
{
    public CreateOvertimeRequestValidator()
    {
        RuleFor(x => x.WorkerId).NotEmpty();
        RuleFor(x => x.PlannedEnd).Must((x, e) => e > x.PlannedStart).WithMessage("The end must be after the start.");
        RuleFor(x => x).Must(x => x.PlannedEnd - x.PlannedStart <= TimeSpan.FromHours(8)).WithName("plannedEnd").WithMessage("One overtime request covers at most 8 hours.");
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(AttendanceRules.ReasonMax);
    }
}

public class ApproveOvertimeRequestValidator : AbstractValidator<ApproveOvertimeRequest>
{
    public ApproveOvertimeRequestValidator()
    {
        RuleFor(x => x.ApprovedMinutes!.Value).GreaterThan(0).OverridePropertyName("approvedMinutes").When(x => x.ApprovedMinutes.HasValue);
        RuleFor(x => x.Note).MaximumLength(AttendanceRules.ReasonMax);
    }
}

public class OvertimeReasonRequestValidator : AbstractValidator<OvertimeReasonRequest>
{
    public OvertimeReasonRequestValidator() => RuleFor(x => x.Reason).NotEmpty().MaximumLength(AttendanceRules.ReasonMax);
}

public class ConfirmPayableRequestValidator : AbstractValidator<ConfirmPayableRequest>
{
    public ConfirmPayableRequestValidator()
    {
        RuleFor(x => x.PayableMinutes).InclusiveBetween(0, 8 * 60);
        RuleFor(x => x.Note).MaximumLength(AttendanceRules.ReasonMax);
    }
}

public class UpdatePolicyRequestValidator : AbstractValidator<UpdatePolicyRequest>
{
    public UpdatePolicyRequestValidator()
    {
        RuleFor(x => x.TimeZoneId).NotEmpty().Must(z => z is not null && AttendanceRules.ResolveTimeZone(z) is not null).WithMessage("Unknown time zone (use an IANA id such as Asia/Dhaka).");
        RuleFor(x => x.DuplicateWindowSeconds).InclusiveBetween(0, 3600);
        RuleFor(x => x.MissingCheckOutAfterMinutes).InclusiveBetween(30, 24 * 60);
        RuleFor(x => x.MaxOpenSessionHours).InclusiveBetween(4, 48);
        RuleFor(x => x.MaxBackdateDays).InclusiveBetween(0, 62);
        RuleFor(x => x.EarlyCheckInWindowMinutes).InclusiveBetween(0, 12 * 60);
        RuleFor(x => x.OvertimeRoundingMinutes).InclusiveBetween(1, 60);
        RuleFor(x => x.MinOvertimeMinutes).InclusiveBetween(0, 240);
        RuleFor(x => x.MaxDailyOvertimeMinutes).InclusiveBetween(0, 12 * 60);
        RuleFor(x => x.MaxWeeklyOvertimeMinutes).InclusiveBetween(0, 60 * 60);
    }
}

public class CreateReaderRequestValidator : AbstractValidator<CreateReaderRequest>
{
    public CreateReaderRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().Must(AttendanceRules.IsCode).WithMessage("Code: letters, digits, '-' and '_', up to 16 characters.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Direction).Must(d => ReaderKinds.Directions.Contains(d)).WithMessage("Direction: Entry, Exit or Both.");
        RuleFor(x => x.Kind).Must(k => ReaderKinds.All.Contains(k)).WithMessage("Kind: Simulated, Rfid, Nfc or Gateway.");
        RuleFor(x => x.Location).MaximumLength(120);
    }
}

public class UpdateReaderRequestValidator : AbstractValidator<UpdateReaderRequest>
{
    public UpdateReaderRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Direction).Must(d => ReaderKinds.Directions.Contains(d)).WithMessage("Direction: Entry, Exit or Both.");
        RuleFor(x => x.Location).MaximumLength(120);
    }
}
