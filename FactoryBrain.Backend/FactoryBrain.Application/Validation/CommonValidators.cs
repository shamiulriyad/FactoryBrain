using FactoryBrain.Application.Dtos.FloorAlerts;
using FactoryBrain.Application.Dtos.LineBoard;
using FactoryBrain.Application.Dtos.Qc;
using FactoryBrain.Application.Dtos.Sensors;
using FactoryBrain.Application.Dtos.Settings;
using FluentValidation;

namespace FactoryBrain.Application.Validation;

public class FloorAlertPatchValidator : AbstractValidator<FloorAlertPatchRequest>
{
    public FloorAlertPatchValidator() => RuleFor(x => x.Read).NotNull();
}

/// <summary>
/// Shared bounds for user-controlled sensor / simulation input. The service
/// layer additionally rejects a <c>tick</c> more than
/// <c>SensorService.MaxTicksPerRequest</c> ahead of the current tick.
/// </summary>
public static class SensorInputLimits
{
    /// <summary>Absolute ceiling for a requested simulation tick.</summary>
    public const int MaxTick = 10_000_000;
    /// <summary>Readings accepted in one POST /api/sensors/live batch.</summary>
    public const int MaxReadingsPerBatch = 500;
    /// <summary>Magnitude accepted for one reading value.</summary>
    public const double MaxAbsValue = 1_000_000;
    /// <summary>How far in the past a reading timestamp may be.</summary>
    public static readonly TimeSpan MaxReadingAge = TimeSpan.FromDays(7);
    /// <summary>Clock-skew allowance for timestamps slightly in the future.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);
}

public class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.Tick)
            .InclusiveBetween(0, SensorInputLimits.MaxTick)
            .When(x => x.Tick.HasValue);
    }
}

public class SensorIngestValidator : AbstractValidator<IngestRequest>
{
    public SensorIngestValidator()
    {
        RuleFor(x => x.Tick)
            .InclusiveBetween(0, SensorInputLimits.MaxTick)
            .When(x => x.Tick.HasValue);
    }
}

public class LiveIngestValidator : AbstractValidator<LiveIngestRequest>
{
    private static readonly HashSet<string> Sources = new(StringComparer.OrdinalIgnoreCase)
        { "rfid", "telemetry", "energy" };

    public LiveIngestValidator()
    {
        RuleFor(x => x.Readings).NotNull().NotEmpty();
        RuleFor(x => x.Readings.Count)
            .LessThanOrEqualTo(SensorInputLimits.MaxReadingsPerBatch)
            .OverridePropertyName("readings")
            .WithMessage($"at most {SensorInputLimits.MaxReadingsPerBatch} readings per request")
            .When(x => x.Readings is not null);
        RuleForEach(x => x.Readings).ChildRules(r =>
        {
            r.RuleFor(v => v.Source).NotEmpty().Must(s => Sources.Contains(s)).WithMessage("unknown_source");
            r.RuleFor(v => v.EntityId).NotEmpty().MaximumLength(64)
                .Matches("^[A-Za-z0-9][A-Za-z0-9:_.-]*$").WithMessage("entityId may contain only letters, digits and : _ . -");
            r.RuleFor(v => v.Metric).NotEmpty().MaximumLength(64);
            r.RuleFor(v => v.Unit).MaximumLength(16).When(v => v.Unit is not null);
            r.RuleFor(v => v.Value)
                .Must(v => !double.IsNaN(v) && !double.IsInfinity(v)).WithMessage("value must be a finite number")
                .InclusiveBetween(-SensorInputLimits.MaxAbsValue, SensorInputLimits.MaxAbsValue);
            r.RuleFor(v => v.Ts)
                .Must(BeWithinAcceptedWindow)
                .WithMessage("ts must be within the last 7 days and not more than 5 minutes in the future")
                .When(v => v.Ts.HasValue);
        });
    }

    private static bool BeWithinAcceptedWindow(DateTime? ts)
    {
        if (ts is null) return true;
        // Same normalisation SensorService applies before storing the value.
        var utc = ts.Value.ToUniversalTime();
        var now = DateTime.UtcNow;
        return utc >= now - SensorInputLimits.MaxReadingAge && utc <= now + SensorInputLimits.MaxClockSkew;
    }
}

public class QcFlagRequestValidator : AbstractValidator<QcFlagRequest>
{
    // Mirrors the OPERATIONS whitelist from src/data/qc.defects.ts.
    private static readonly HashSet<string> _operations = new(StringComparer.Ordinal)
    {
        "cutting", "sewing", "buttonhole", "top_stitch", "qc_inspection", "finishing"
    };

    public QcFlagRequestValidator()
    {
        RuleFor(x => x.Operation)
            .NotEmpty().MaximumLength(64)
            .Must(op => _operations.Contains(op))
            .WithMessage("unknown_operation");
        RuleFor(x => x.LineId).NotEmpty().MaximumLength(32)
            .Matches("^line-[0-9]{1,3}$").WithMessage("lineId must look like line-1");
        RuleFor(x => x.DefectRatePct).InclusiveBetween(0, 100).When(x => x.DefectRatePct.HasValue);
        RuleFor(x => x.ReworkRatePct).InclusiveBetween(0, 100).When(x => x.ReworkRatePct.HasValue);
        RuleFor(x => x.Note).MaximumLength(280).When(x => !string.IsNullOrEmpty(x.Note));
    }
}

public class LlmSettingsRequestValidator : AbstractValidator<LlmSettingsRequest>
{
    public LlmSettingsRequestValidator()
    {
        RuleFor(x => x.Provider)
            .Must(p => string.IsNullOrEmpty(p) || p is "openai" or "anthropic" or "gemini")
            .WithMessage("invalid_provider");
        RuleFor(x => x.ApiKey).MaximumLength(2048);
        RuleFor(x => x.Model).MaximumLength(128);
    }
}

public class ApprovalDecisionRequestValidator : AbstractValidator<FactoryBrain.Application.Dtos.Approvals.ApprovalDecisionRequest>
{
    public ApprovalDecisionRequestValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(1000).When(x => x.Reason is not null);
    }
}
