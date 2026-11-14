using BunonBrain.Application.Cameras;
using BunonBrain.Application.Devices;
using BunonBrain.Application.Dtos.Cameras;
using BunonBrain.Application.Dtos.Devices;
using FluentValidation;

namespace BunonBrain.Application.Validation;

// Shape checks only; relationships (factory, floor, line, workstation,
// machine, camera, reader) are checked by the services against the database.

public class CreateCameraRequestValidator : AbstractValidator<CreateCameraRequest>
{
    public CreateCameraRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        RuleFor(x => x.Code!).Must(CameraRules.IsCode).When(x => !string.IsNullOrWhiteSpace(x.Code))
            .WithMessage("Camera ID: letters, digits, '-', '_', '/' and '.', up to 24 characters (e.g. CAM-001).");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(CameraRules.NameMax);
        RuleFor(x => x.CameraType).NotEmpty().Must(t => CameraTypes.Parse(t) is not null)
            .WithMessage($"Camera type: one of {string.Join(", ", CameraTypes.All)}.");
        RuleFor(x => x.FloorId).NotEmpty().WithMessage("Choose the production floor the camera is on.");
        RuleFor(x => x.LineId).MaximumLength(32);
        RuleFor(x => x.Location).MaximumLength(CameraRules.LocationMax);
        RuleFor(x => x.Description).MaximumLength(CameraRules.DescriptionMax);
        RuleFor(x => x.CoverageLineIds).Must(l => l is null || l.Count <= CameraRules.MaxCoverageLines)
            .WithMessage($"At most {CameraRules.MaxCoverageLines} covered lines.");
        RuleFor(x => x.WorkstationIds).Must(l => l is null || l.Count <= CameraRules.MaxCoverageWorkstations)
            .WithMessage($"At most {CameraRules.MaxCoverageWorkstations} covered workstations.");
        RuleFor(x => x.HealthDevice).Must(h => h is null || h is "None" or "Simulated" or "Real")
            .WithMessage("Health device: None, Simulated or Real.");
    }
}

public class UpdateCameraRequestValidator : AbstractValidator<UpdateCameraRequest>
{
    public UpdateCameraRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(CameraRules.NameMax);
        RuleFor(x => x.CameraType).NotEmpty().Must(t => CameraTypes.Parse(t) is not null)
            .WithMessage($"Camera type: one of {string.Join(", ", CameraTypes.All)}.");
        RuleFor(x => x.FloorId).NotEmpty().WithMessage("Choose the production floor the camera is on.");
        RuleFor(x => x.LineId).MaximumLength(32);
        RuleFor(x => x.Location).MaximumLength(CameraRules.LocationMax);
        RuleFor(x => x.Description).MaximumLength(CameraRules.DescriptionMax);
    }
}

public class SetCameraStatusRequestValidator : AbstractValidator<SetCameraStatusRequest>
{
    public SetCameraStatusRequestValidator()
    {
        RuleFor(x => x.Status).NotEmpty().Must(s => CameraStatuses.Parse(s) is not null)
            .WithMessage($"Status: one of {string.Join(", ", CameraStatuses.All)}.");
        RuleFor(x => x.Reason).MaximumLength(CameraRules.ReasonMax);
        RuleFor(x => x.Reason).NotEmpty().When(x => CameraStatuses.Parse(x.Status) is { } s && s != CameraStatuses.Active)
            .WithMessage("Say why the camera is being deactivated or put into maintenance.");
    }
}

public class SetCameraCoverageRequestValidator : AbstractValidator<SetCameraCoverageRequest>
{
    public SetCameraCoverageRequestValidator()
    {
        RuleFor(x => x.LineIds).Must(l => l is null || l.Count <= CameraRules.MaxCoverageLines)
            .WithMessage($"At most {CameraRules.MaxCoverageLines} covered lines.");
        RuleFor(x => x.WorkstationIds).Must(l => l is null || l.Count <= CameraRules.MaxCoverageWorkstations)
            .WithMessage($"At most {CameraRules.MaxCoverageWorkstations} covered workstations.");
    }
}

public class SetCameraSourceRequestValidator : AbstractValidator<SetCameraSourceRequest>
{
    public SetCameraSourceRequestValidator()
    {
        RuleFor(x => x.SourceType).NotEmpty().Must(t => CameraSourceTypes.Parse(t) is not null)
            .WithMessage($"Source type: one of {string.Join(", ", CameraSourceTypes.All)}.");
        RuleFor(x => x.StreamUrl).MaximumLength(CameraRules.UrlMax);
        RuleFor(x => x.StreamUrl).NotEmpty().When(x => CameraSourceTypes.Parse(x.SourceType) is { } t && CameraSourceTypes.HasUrl(t))
            .WithMessage("Enter the stream URL.");
        RuleFor(x => x.UserName).MaximumLength(CameraRules.CredentialMax);
        RuleFor(x => x.Password).MaximumLength(CameraRules.CredentialMax);
        RuleFor(x => x.WebcamLabel).MaximumLength(CameraRules.WebcamLabelMax);
        RuleFor(x => x.Notes).MaximumLength(CameraRules.NotesMax);
    }
}

internal static class ZoneFieldRules
{
    public static readonly string TypeMessage = $"Zone type: one of {string.Join(", ", ZoneTypes.All)}.";
}

public class CreateCameraZoneRequestValidator : AbstractValidator<CreateCameraZoneRequest>
{
    public CreateCameraZoneRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(CameraRules.ZoneNameMax);
        RuleFor(x => x.ZoneType).NotEmpty().Must(t => ZoneTypes.Parse(t) is not null).WithMessage(ZoneFieldRules.TypeMessage);
        RuleFor(x => x.Points).NotNull().WithMessage("Draw the zone's points.");
        RuleFor(x => x.LineId).MaximumLength(32);
        RuleFor(x => x.Color).Must(CameraRules.IsColor).WithMessage("Colour: #RRGGBB.");
        RuleFor(x => x.Notes).MaximumLength(CameraRules.NotesMax);
    }
}

public class UpdateCameraZoneRequestValidator : AbstractValidator<UpdateCameraZoneRequest>
{
    public UpdateCameraZoneRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(CameraRules.ZoneNameMax);
        RuleFor(x => x.ZoneType).NotEmpty().Must(t => ZoneTypes.Parse(t) is not null).WithMessage(ZoneFieldRules.TypeMessage);
        RuleFor(x => x.Points).NotNull().WithMessage("Draw the zone's points.");
        RuleFor(x => x.LineId).MaximumLength(32);
        RuleFor(x => x.Color).Must(CameraRules.IsColor).WithMessage("Colour: #RRGGBB.");
        RuleFor(x => x.Notes).MaximumLength(CameraRules.NotesMax);
        RuleFor(x => x.ExpectedVersion!.Value).GreaterThan(0).OverridePropertyName("expectedVersion").When(x => x.ExpectedVersion.HasValue);
    }
}

// ─── Devices ───────────────────────────────────────────────────────────────

internal static class DeviceFieldRules
{
    public const string CodeMessage = "Device ID: letters, digits, '-', '_', '/', '.' and ':', up to 32 characters.";
    public static readonly string CategoryMessage = $"Category: one of {string.Join(", ", DeviceCategories.All)}.";
    public const string IntervalMessage = "Heartbeat interval: 5 seconds to 24 hours.";
    public const string OfflineMessage = "Offline after: longer than the heartbeat interval, at most 7 days.";

    public static bool MetadataValid(IReadOnlyDictionary<string, string?>? m) => DeviceRules.TryMetadata(m, out _, out _);
}

public class CreateDeviceRequestValidator : AbstractValidator<CreateDeviceRequest>
{
    public CreateDeviceRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty().Must(DeviceRules.IsCode).WithMessage(DeviceFieldRules.CodeMessage);
        RuleFor(x => x.Name).NotEmpty().MaximumLength(DeviceRules.NameMax);
        RuleFor(x => x.Category).NotEmpty().Must(c => DeviceCategories.Parse(c) is not null).WithMessage(DeviceFieldRules.CategoryMessage);
        RuleFor(x => x.LineId).MaximumLength(32);
        RuleFor(x => x.Manufacturer).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.Model).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.SerialNumber).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.FirmwareVersion).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.Metadata).Must(DeviceFieldRules.MetadataValid)
            .WithMessage(x => DeviceRules.TryMetadata(x.Metadata, out _, out var e) ? "" : e!);
        RuleFor(x => x.HeartbeatIntervalSeconds!.Value).InclusiveBetween(DeviceRules.MinHeartbeatSeconds, DeviceRules.MaxHeartbeatSeconds)
            .OverridePropertyName("heartbeatIntervalSeconds").When(x => x.HeartbeatIntervalSeconds.HasValue).WithMessage(DeviceFieldRules.IntervalMessage);
        RuleFor(x => x.OfflineAfterSeconds!.Value).InclusiveBetween(DeviceRules.MinHeartbeatSeconds * 2, 7 * 86_400)
            .OverridePropertyName("offlineAfterSeconds").When(x => x.OfflineAfterSeconds.HasValue).WithMessage(DeviceFieldRules.OfflineMessage);
    }
}

public class UpdateDeviceRequestValidator : AbstractValidator<UpdateDeviceRequest>
{
    public UpdateDeviceRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(DeviceRules.NameMax);
        RuleFor(x => x.Category).NotEmpty().Must(c => DeviceCategories.Parse(c) is not null).WithMessage(DeviceFieldRules.CategoryMessage);
        RuleFor(x => x.LineId).MaximumLength(32);
        RuleFor(x => x.Manufacturer).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.Model).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.SerialNumber).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.FirmwareVersion).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.Metadata).Must(DeviceFieldRules.MetadataValid)
            .WithMessage(x => DeviceRules.TryMetadata(x.Metadata, out _, out var e) ? "" : e!);
        RuleFor(x => x.HeartbeatIntervalSeconds).InclusiveBetween(DeviceRules.MinHeartbeatSeconds, DeviceRules.MaxHeartbeatSeconds)
            .WithMessage(DeviceFieldRules.IntervalMessage);
        RuleFor(x => x.OfflineAfterSeconds).InclusiveBetween(DeviceRules.MinHeartbeatSeconds * 2, 7 * 86_400)
            .GreaterThan(x => x.HeartbeatIntervalSeconds).WithMessage(DeviceFieldRules.OfflineMessage);
    }
}

public class SetDeviceStatusRequestValidator : AbstractValidator<SetDeviceStatusRequest>
{
    public SetDeviceStatusRequestValidator()
    {
        RuleFor(x => x.Status).NotEmpty().Must(s => DeviceStatuses.Parse(s) is not null)
            .WithMessage($"Status: one of {string.Join(", ", DeviceStatuses.All)}.");
        RuleFor(x => x.Reason).MaximumLength(DeviceRules.ReasonMax);
        RuleFor(x => x.Reason).NotEmpty().When(x => DeviceStatuses.Parse(x.Status) is { } s && s != DeviceStatuses.Active)
            .WithMessage("Say why the device is being disabled or put into maintenance.");
    }
}

internal static class MessageFieldRules
{
    public static bool MetricsValid(IReadOnlyDictionary<string, double>? m) => DeviceRules.TryMetrics(m, out _, out _);
}

public class DeviceHeartbeatRequestValidator : AbstractValidator<DeviceHeartbeatRequest>
{
    public DeviceHeartbeatRequestValidator()
    {
        RuleFor(x => x.MessageId).MaximumLength(DeviceRules.MessageIdMax);
        RuleFor(x => x.FirmwareVersion).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.Metrics).Must(MessageFieldRules.MetricsValid)
            .WithMessage(x => DeviceRules.TryMetrics(x.Metrics, out _, out var e) ? "" : e!);
    }
}

public class DeviceReportRequestValidator : AbstractValidator<DeviceReportRequest>
{
    public DeviceReportRequestValidator()
    {
        RuleFor(x => x.Kind).NotEmpty().Must(k => DeviceMessageKinds.Parse(k) is { } p && p != DeviceMessageKinds.Heartbeat)
            .WithMessage("Kind: Online, Offline or Error (heartbeats go to …/heartbeat).");
        RuleFor(x => x.MessageId).MaximumLength(DeviceRules.MessageIdMax);
        RuleFor(x => x.Code).MaximumLength(DeviceRules.ErrorCodeMax);
        RuleFor(x => x.Message).MaximumLength(DeviceRules.MessageMax);
        RuleFor(x => x.Severity).Must(s => s is null || DeviceSeverities.Parse(s) is not null).WithMessage("Severity: Info, Warning or Error.");
        RuleFor(x => x.Message).NotEmpty().When(x => DeviceMessageKinds.Parse(x.Kind) == DeviceMessageKinds.Error && string.IsNullOrWhiteSpace(x.Code))
            .WithMessage("An error needs a code or a message.");
    }
}

public class DeviceIngestRequestValidator : AbstractValidator<DeviceIngestRequest>
{
    public DeviceIngestRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        RuleFor(x => x.DeviceCode).NotEmpty().Must(DeviceRules.IsCode).WithMessage(DeviceFieldRules.CodeMessage);
        RuleFor(x => x.Kind).NotEmpty().Must(k => DeviceMessageKinds.Parse(k) is not null)
            .WithMessage($"Kind: one of {string.Join(", ", DeviceMessageKinds.All)}.");
        RuleFor(x => x.MessageId).MaximumLength(DeviceRules.MessageIdMax);
        RuleFor(x => x.FirmwareVersion).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.Metrics).Must(MessageFieldRules.MetricsValid)
            .WithMessage(x => DeviceRules.TryMetrics(x.Metrics, out _, out var e) ? "" : e!);
        RuleFor(x => x.Code).MaximumLength(DeviceRules.ErrorCodeMax);
        RuleFor(x => x.Message).MaximumLength(DeviceRules.MessageMax);
        RuleFor(x => x.Severity).Must(s => s is null || DeviceSeverities.Parse(s) is not null).WithMessage("Severity: Info, Warning or Error.");
    }
}

public class SimulatorDeviceRequestValidator : AbstractValidator<SimulatorDeviceRequest>
{
    public SimulatorDeviceRequestValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(DeviceRules.MessageMax);
    }
}

public class SimulatorHeartbeatRequestValidator : AbstractValidator<SimulatorHeartbeatRequest>
{
    public SimulatorHeartbeatRequestValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Count!.Value).InclusiveBetween(1, DeviceRules.MaxSimulatedHeartbeats).OverridePropertyName("count").When(x => x.Count.HasValue)
            .WithMessage($"Send between 1 and {DeviceRules.MaxSimulatedHeartbeats} heartbeats at a time.");
        RuleFor(x => x.IntervalSeconds!.Value).InclusiveBetween(1, 3600).OverridePropertyName("intervalSeconds").When(x => x.IntervalSeconds.HasValue);
        RuleFor(x => x.FirmwareVersion).MaximumLength(DeviceRules.TextMax);
        RuleFor(x => x.Metrics).Must(MessageFieldRules.MetricsValid)
            .WithMessage(x => DeviceRules.TryMetrics(x.Metrics, out _, out var e) ? "" : e!);
    }
}

public class SimulatorErrorRequestValidator : AbstractValidator<SimulatorErrorRequest>
{
    public SimulatorErrorRequestValidator()
    {
        RuleFor(x => x.DeviceId).NotEmpty();
        RuleFor(x => x.Code).MaximumLength(DeviceRules.ErrorCodeMax);
        RuleFor(x => x.Message).MaximumLength(DeviceRules.MessageMax);
        RuleFor(x => x.Severity).Must(s => s is null || DeviceSeverities.Parse(s) is not null).WithMessage("Severity: Info, Warning or Error.");
    }
}
