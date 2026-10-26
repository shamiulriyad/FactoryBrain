using FactoryBrain.Application.Dtos.Merchandising;
using FactoryBrain.Application.Dtos.Production;

namespace FactoryBrain.Application.Abstractions.Interfaces;

public enum MerchOutcome { Ok, Created, NotFound, Invalid, Conflict }

/// <summary>
/// Result of a merchandising read or write. <see cref="Code"/> is a stable
/// machine code for conflicts (<c>version_conflict</c>, <c>duplicate_order</c>,
/// <c>not_reviewable</c>…); <see cref="Message"/> is safe to show.
/// </summary>
public sealed record MerchResult<T>(
    MerchOutcome Outcome,
    T? Value = default,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Code = null,
    string? Message = null,
    /// <summary>The request repeated an earlier one (same Idempotency-Key and content); the stored result is returned.</summary>
    bool Replayed = false)
{
    public static MerchResult<T> Ok(T value) => new(MerchOutcome.Ok, value);
    public static MerchResult<T> Created(T value) => new(MerchOutcome.Created, value);
    public static MerchResult<T> NotFound(string code) => new(MerchOutcome.NotFound, Code: code);
    public static MerchResult<T> Invalid(IReadOnlyDictionary<string, string[]> errors) => new(MerchOutcome.Invalid, Errors: errors);
    public static MerchResult<T> Invalid(string field, string message) =>
        new(MerchOutcome.Invalid, Errors: new Dictionary<string, string[]> { [field] = new[] { message } });
    public static MerchResult<T> Conflict(string code, string message) => new(MerchOutcome.Conflict, Code: code, Message: message);
}

public sealed record MerchOrderQuery(
    string? Q, string? Buyer, string? Status, DateOnly? DeliveryFrom, DateOnly? DeliveryTo,
    /// <summary><c>needs_review</c> | <c>conflicts</c> | <c>overdue</c> | <c>missing_documents</c> | <c>failed</c>.</summary>
    string? Attention, int Page, int PageSize, string? Sort);

public sealed record MerchDocumentQuery(string? Q, string? Status, string? Type, Guid? OrderId, int Page, int PageSize);

/// <summary>Uploaded file plus its form fields, as the controller received them.</summary>
public sealed record MerchUploadInput(
    byte[] Content, string? FileName, string? ClaimedContentType, string? Type, string? Title, string? ChangeNote, string? IdempotencyKey);

public sealed record MerchFileContent(Stream Content, string ContentType, string FileName, long Length, string Sha256, string Format);

/// <summary>Buyer orders, their milestones and the order workspace read model.</summary>
public interface IMerchOrderService
{
    Task<MerchOverviewDto> OverviewAsync(CancellationToken ct);
    Task<MerchResult<Paged<MerchOrderListItem>>> ListAsync(MerchOrderQuery query, CancellationToken ct);
    Task<MerchOrderDetail?> GetAsync(Guid id, CancellationToken ct);
    Task<MerchResult<MerchOrderDetail>> CreateAsync(MerchOrderWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<MerchResult<MerchOrderDetail>> UpdateAsync(Guid id, MerchOrderWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<MerchResult<MerchMilestoneDto>> AddMilestoneAsync(Guid orderId, MerchMilestoneWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<MerchResult<MerchMilestoneDto>> UpdateMilestoneAsync(Guid id, MerchMilestoneWrite body, AuditActor actor, string? traceId, CancellationToken ct);
    Task<MerchResult<MerchConflictDto>> ResolveConflictAsync(Guid id, MerchConflictResolution body, AuditActor actor, string? traceId, CancellationToken ct);
}

/// <summary>Document upload, versions, extraction status, field review and confirmation.</summary>
public interface IMerchDocumentService
{
    MerchConfigDto Config();
    Task<MerchResult<Paged<MerchDocumentListItem>>> ListAsync(MerchDocumentQuery query, CancellationToken ct);
    Task<MerchDocumentDetail?> GetDocumentAsync(Guid id, CancellationToken ct);
    Task<MerchVersionDetail?> GetVersionAsync(Guid id, CancellationToken ct);
    Task<MerchFileContent?> OpenFileAsync(Guid versionId, CancellationToken ct);
    Task<MerchResult<MerchVersionDetail>> UploadAsync(Guid orderId, MerchUploadInput input, AuditActor actor, string? traceId, CancellationToken ct);
    Task<MerchResult<MerchVersionDetail>> UploadVersionAsync(Guid documentId, MerchUploadInput input, AuditActor actor, string? traceId, CancellationToken ct);
    Task<MerchResult<MerchVersionDetail>> RetryAsync(Guid versionId, AuditActor actor, string? traceId, CancellationToken ct);
    Task<MerchResult<MerchVersionDetail>> ConfirmAsync(Guid versionId, MerchConfirmRequest body, AuditActor actor, string? traceId, CancellationToken ct);
}

/// <summary>Parses and extracts queued document versions. Called by the background worker (and directly by tests).</summary>
public interface IMerchDocumentProcessor
{
    /// <summary>Process at most one queued version. Returns false when nothing was waiting.</summary>
    Task<bool> ProcessNextAsync(CancellationToken ct);
}

/// <summary>Wakes the background worker when a version is queued (a poll is the fallback).</summary>
public interface IMerchWorkSignal
{
    void Notify();
    Task WaitAsync(TimeSpan timeout, CancellationToken ct);
}

/// <summary>Stores original document files outside the database under server-generated keys.</summary>
public interface IMerchDocumentStore
{
    Task<string> SaveAsync(byte[] content, string extension, CancellationToken ct);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);
    Task DeleteAsync(string key, CancellationToken ct);
}

// ── Extraction provider contract ───────────────────────────────────────────

/// <summary>
/// Reads structured order fields from a document's parsed text. Must never
/// invent results: on any failure it throws <see cref="DocumentExtractionException"/>.
/// Values, evidence quotes and confidences are passed on exactly as produced;
/// validation and evidence checks happen afterwards.
/// </summary>
public interface IDocumentExtractionProvider
{
    /// <summary>Current configuration, without making a call. Never includes secrets.</summary>
    ExtractionProviderStatus Describe();
    Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken ct);
}

public sealed record ExtractionProviderStatus(
    bool Configured,
    /// <summary><c>llm</c>, <c>rules</c> or <c>none</c>.</summary>
    string Mode,
    string? Provider,
    string? Model,
    /// <summary>Whether the provider reports its own confidence per value.</summary>
    bool ReportsConfidence,
    string? Reason);

public sealed record PageText(int PageNumber, string Text);

public sealed record ExtractionRequest(string DocumentType, IReadOnlyList<PageText> Pages);

/// <summary>One value as the extractor produced it. <see cref="Value"/> is a string, or breakdown JSON for the breakdown field.</summary>
public sealed record ExtractedCandidate(string FieldKey, string? Value, double? Confidence, int? Page, string? Evidence);

public sealed record ExtractionResult(
    string Provider,
    string Model,
    string? ModelVersion,
    IReadOnlyList<ExtractedCandidate> Fields,
    /// <summary>Text sent to the provider was cut to the configured limit.</summary>
    bool InputTruncated = false);

/// <summary>
/// Extraction could not produce a result. <see cref="Code"/> is stable
/// (<c>provider_not_configured</c>, <c>provider_timeout</c>, <c>provider_unauthorized</c>,
/// <c>provider_rate_limited</c>, <c>provider_error</c>, <c>invalid_output</c>);
/// the message is safe to show and store (no keys, no document text, no response bodies).
/// </summary>
public sealed class DocumentExtractionException : Exception
{
    public const string NotConfigured = "provider_not_configured";
    public const string Timeout       = "provider_timeout";
    public const string Unauthorized  = "provider_unauthorized";
    public const string RateLimited   = "provider_rate_limited";
    public const string ProviderError = "provider_error";
    public const string InvalidOutput = "invalid_output";

    public string Code { get; }

    public DocumentExtractionException(string code, string message, Exception? inner = null) : base(message, inner)
    {
        Code = code;
    }
}
