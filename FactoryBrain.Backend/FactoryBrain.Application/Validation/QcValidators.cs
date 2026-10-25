using System.Text.RegularExpressions;
using FactoryBrain.Application.Dtos.Qc;
using FactoryBrain.Application.Qc;
using FluentValidation;

namespace FactoryBrain.Application.Validation;

public class QcReviewRequestValidator : AbstractValidator<QcReviewRequest>
{
    public QcReviewRequestValidator()
    {
        RuleFor(x => x.ExpectedVersion).NotNull().WithMessage("expectedVersion is required (send the version you reviewed).");
        RuleFor(x => x.Outcome).Must(o => QcWire.TryParseOutcome(o, out _))
            .WithMessage($"outcome must be one of: {string.Join(", ", QcWire.Outcomes)}");
        RuleFor(x => x.Notes).MaximumLength(QcLimits.NotesMax).When(x => x.Notes is not null);
        RuleFor(x => x.FollowUpNote).MaximumLength(QcLimits.NotesMax).When(x => x.FollowUpNote is not null);
        RuleFor(x => x.Decisions!.Count).LessThanOrEqualTo(QcLimits.MaxFindingsPerInspection)
            .OverridePropertyName("decisions").When(x => x.Decisions is not null);
        RuleFor(x => x.Additions!.Count).LessThanOrEqualTo(QcLimits.MaxAdditionsPerReview)
            .OverridePropertyName("additions").When(x => x.Additions is not null);
        RuleFor(x => x.Decisions).Must(d => d is null || d.Select(x => x.FindingId).Distinct().Count() == d.Count)
            .WithMessage("Each finding may be decided only once.");

        RuleForEach(x => x.Decisions).ChildRules(d =>
        {
            d.RuleFor(v => v.FindingId).NotEqual(Guid.Empty);
            d.RuleFor(v => v.Decision).Must(v => QcWire.TryParseDecision(v, out _))
                .WithMessage("decision must be one of: accepted, rejected, corrected");
            d.RuleFor(v => v.CorrectedSeverity).Must(s => s is null || QcWire.TryParseSeverity(s, out _))
                .WithMessage($"correctedSeverity must be one of: {string.Join(", ", QcWire.Severities)}");
            d.RuleFor(v => v).Must(v => v.CorrectedCategoryCode is not null || v.CorrectedSeverity is not null)
                .When(v => string.Equals(v.Decision?.Trim(), "corrected", StringComparison.OrdinalIgnoreCase))
                .OverridePropertyName("correctedCategoryCode")
                .WithMessage("A correction needs a corrected category or severity.");
            d.RuleFor(v => v.Note).MaximumLength(QcLimits.ReviewNoteMax).When(v => v.Note is not null);
        });

        RuleForEach(x => x.Additions).ChildRules(a =>
        {
            a.RuleFor(v => v.CategoryCode).NotEmpty().MaximumLength(40);
            a.RuleFor(v => v.Severity).Must(s => s is null || QcWire.TryParseSeverity(s, out _))
                .WithMessage($"severity must be one of: {string.Join(", ", QcWire.Severities)}");
            a.RuleFor(v => v.Box).Must(b => b is null || QcDetectionNormalizer.ValidBox(
                    new Abstractions.Interfaces.NormalizedBox(b.X, b.Y, b.Width, b.Height)) is not null)
                .WithMessage("box must lie inside the image (x, y, width, height normalised to 0–1).");
            a.RuleFor(v => v.Note).MaximumLength(QcLimits.ReviewNoteMax).When(v => v.Note is not null);
        });
    }
}

public class QcCategoryCreateRequestValidator : AbstractValidator<QcCategoryCreateRequest>
{
    public QcCategoryCreateRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().Matches(QcLimits.CategoryCodePattern)
            .WithMessage("code must be 2–40 lowercase letters, digits or underscores, starting with a letter.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80);
        RuleFor(x => x.NameBn).MaximumLength(80).When(x => x.NameBn is not null);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
        RuleFor(x => x.DefaultSeverity).Must(s => s is null || QcWire.TryParseSeverity(s, out _))
            .WithMessage($"defaultSeverity must be one of: {string.Join(", ", QcWire.Severities)}");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10_000).When(x => x.SortOrder.HasValue);
    }
}

public class QcCategoryUpdateRequestValidator : AbstractValidator<QcCategoryUpdateRequest>
{
    public QcCategoryUpdateRequestValidator()
    {
        RuleFor(x => x.ExpectedVersion).NotNull().WithMessage("expectedVersion is required.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(80).When(x => x.Name is not null);
        RuleFor(x => x.NameBn).MaximumLength(80).When(x => x.NameBn is not null);
        RuleFor(x => x.Description).MaximumLength(500).When(x => x.Description is not null);
        RuleFor(x => x.DefaultSeverity).Must(s => s is null || QcWire.TryParseSeverity(s, out _))
            .WithMessage($"defaultSeverity must be one of: {string.Join(", ", QcWire.Severities)}");
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10_000).When(x => x.SortOrder.HasValue);
    }
}

/// <summary>
/// Metadata checks for a new inspection. The upload arrives as
/// multipart/form-data, so the service runs these itself (alongside the
/// byte-level image checks) and returns the same field-keyed errors.
/// </summary>
public static class QcCreateInputValidation
{
    private static readonly Regex SafeToken = new(QcLimits.SafeTokenPattern, RegexOptions.CultureInvariant);
    private static readonly Regex CategoryCode = new(QcLimits.CategoryCodePattern, RegexOptions.CultureInvariant);
    private static readonly Regex IdempotencyKey = new(@"^[A-Za-z0-9_.:-]{8,128}$", RegexOptions.CultureInvariant);

    public static Dictionary<string, string[]> Validate(QcCreateInspectionInput input)
    {
        var errors = new Dictionary<string, string[]>();
        void Add(string key, string message) => errors[key] = new[] { message };

        var product = input.ProductType?.Trim() ?? "";
        if (product.Length == 0) Add("productType", "Product type is required.");
        else if (product.Length > QcLimits.ProductTypeMax) Add("productType", $"At most {QcLimits.ProductTypeMax} characters.");
        else if (!SafeToken.IsMatch(product)) Add("productType", "Use letters, digits, spaces and - _ . / # only.");

        if (!QcWire.TryParseStage(input.Stage, out _))
            Add("stage", $"stage must be one of: {string.Join(", ", QcWire.Stages)}");

        CheckToken("lineId", input.LineId, QcLimits.LineIdMax);
        CheckToken("productionOrder", input.ProductionOrder, QcLimits.ProductionOrderMax);

        if (input.Notes is { Length: > QcLimits.NotesMax }) Add("notes", $"At most {QcLimits.NotesMax} characters.");

        if (input.CategoryCodes.Count > QcLimits.MaxCategoriesPerRequest)
            Add("categoryCodes", $"At most {QcLimits.MaxCategoriesPerRequest} categories.");
        else if (input.CategoryCodes.Any(c => !CategoryCode.IsMatch(c)))
            Add("categoryCodes", "Unknown category code format.");

        if (input.IdempotencyKey is not null && !IdempotencyKey.IsMatch(input.IdempotencyKey))
            Add("idempotencyKey", "Idempotency-Key must be 8–128 characters: letters, digits and _ . : -");

        return errors;

        void CheckToken(string key, string? value, int max)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var v = value.Trim();
            if (v.Length > max) Add(key, $"At most {max} characters.");
            else if (!SafeToken.IsMatch(v)) Add(key, "Use letters, digits, spaces and - _ . / # only.");
        }
    }

    /// <summary>A display-only file name: path parts and control characters removed, length capped.</summary>
    public static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var leaf = name.Replace('\\', '/');
        leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        var cleaned = new string(leaf.Where(c => !char.IsControl(c) && c is not '<' and not '>' and not '"').ToArray()).Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= QcLimits.FileNameMax ? cleaned : cleaned[..QcLimits.FileNameMax];
    }
}
