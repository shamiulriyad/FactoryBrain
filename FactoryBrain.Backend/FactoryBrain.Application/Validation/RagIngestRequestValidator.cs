using FactoryBrain.Application.Dtos.Rag;
using FluentValidation;

namespace FactoryBrain.Application.Validation;

/// <summary>
/// POST /api/rag/ingest body validation. Mirrors the camelCase error
/// envelope the GlobalExceptionMiddleware emits — i.e. one
/// <c>ValidationException</c> per failing field with the property name
/// in camelCase so the frontend doesn't have to special-case
/// <see cref="IngestRequest"/>.
/// </summary>
public class RagIngestRequestValidator : AbstractValidator<RagIngestRequest>
{
    public const int MaxContentBytes = 200 * 1024;

    public RagIngestRequestValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty().MaximumLength(280);
        RuleFor(x => x.TitleBn)
            .MaximumLength(280)
            .When(x => !string.IsNullOrEmpty(x.TitleBn));
        RuleFor(x => x.Source)
            .NotEmpty()
            .Must(RagSources.IsValid)
            .WithMessage("source must be one of: manual, sop, compliance, faq");
        RuleFor(x => x.Content)
            .NotEmpty()
            .Must((req, content) =>
                System.Text.Encoding.UTF8.GetByteCount(content ?? string.Empty) <= MaxContentBytes)
            .WithMessage($"content exceeds {MaxContentBytes} bytes (max 200 KB)");
        RuleFor(x => x.Tags)
            .Must(tags => tags is null || tags.Count <= 32)
            .WithMessage("tags: at most 32 entries");
        RuleForEach(x => x.Tags!)
            .MaximumLength(64)
            .When(x => x.Tags is not null);
        RuleFor(x => x.Url)
            .MaximumLength(2048)
            .Must(url => string.IsNullOrEmpty(url) || Uri.TryCreate(url, UriKind.Absolute, out _))
            .WithMessage("url: must be an absolute URL")
            .When(x => !string.IsNullOrEmpty(x.Url));
    }
}