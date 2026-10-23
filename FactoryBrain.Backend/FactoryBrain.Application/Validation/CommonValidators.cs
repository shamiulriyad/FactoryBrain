using FactoryBrain.Application.Dtos.FloorAlerts;
using FactoryBrain.Application.Dtos.LineBoard;
using FactoryBrain.Application.Dtos.Qc;
using FactoryBrain.Application.Dtos.Sensors;
using FactoryBrain.Application.Dtos.Settings;
using FactoryBrain.Application.Dtos.Vision;
using FluentValidation;

namespace FactoryBrain.Application.Validation;

public class FloorAlertPatchValidator : AbstractValidator<FloorAlertPatchRequest>
{
    public FloorAlertPatchValidator() => RuleFor(x => x.Read).NotNull();
}

public class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.Tick)
            .GreaterThanOrEqualTo(0)
            .When(x => x.Tick.HasValue);
    }
}

public class SensorIngestValidator : AbstractValidator<IngestRequest>
{
    public SensorIngestValidator()
    {
        RuleFor(x => x.Tick)
            .GreaterThanOrEqualTo(0)
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
        RuleForEach(x => x.Readings).ChildRules(r =>
        {
            r.RuleFor(v => v.Source).NotEmpty().Must(s => Sources.Contains(s)).WithMessage("unknown_source");
            r.RuleFor(v => v.EntityId).NotEmpty().MaximumLength(64);
            r.RuleFor(v => v.Metric).NotEmpty().MaximumLength(64);
            r.RuleFor(v => v.Value).Must(v => !double.IsNaN(v) && !double.IsInfinity(v));
        });
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
        RuleFor(x => x.LineId).NotEmpty().MaximumLength(32);
        RuleFor(x => x.DefectRatePct).InclusiveBetween(0, 100).When(x => x.DefectRatePct.HasValue);
        RuleFor(x => x.ReworkRatePct).InclusiveBetween(0, 100).When(x => x.ReworkRatePct.HasValue);
        RuleFor(x => x.Note).MaximumLength(280).When(x => !string.IsNullOrEmpty(x.Note));
    }
}

public class VisionAnalyzeRequestValidator : AbstractValidator<VisionAnalyzeRequest>
{
    // Keep parity with src/app/api/vision/analyze — the four staged files.
    public VisionAnalyzeRequestValidator()
    {
        RuleFor(x => x.SampleFile).Must(s => s is
            "defect-1-stitch-skip.jpg" or
            "defect-2-buttonhole.jpg" or
            "defect-3-seam-pucker.jpg" or
            "defect-4-fabric-stain.jpg")
            .WithMessage("unknown_sample_file");
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
