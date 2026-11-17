using System.Text.Json.Nodes;
using BunonBrain.Application.Access;
using BunonBrain.Application.Documents;
using BunonBrain.Application.Dtos.Access;

namespace BunonBrain.Application.Abstractions.Interfaces;

// ── Document types and settings ─────────────────────────────────────────────

public sealed record DocFieldDto(string Key, string Label, string Kind, bool Required, bool Computed, IReadOnlyList<string>? Options, IReadOnlyList<DocFieldDto>? Columns, string? Help, string? Unit, string? Lookup);

public sealed record DocTypeDto(
    string Code, string Name, string Category, string CategoryName, string Description, string Risk, string DefaultApproval, string ApprovalLevel,
    string Period, bool Generated, bool LineOption, string? NumberField, bool AutoGenerate, bool ExpectedDaily, int DecisionDueHours,
    IReadOnlyList<DocFieldDto> Fields, IReadOnlyList<DocFieldDto> Parameters, bool CanCreate,
    string ScheduleFrequency = "DAILY", string ScheduleScope = "FACTORY", int WeekStartDay = 6, IReadOnlyList<string>? ScheduleFrequencies = null);

public sealed record DocCapabilities(bool Create, bool Review, bool Approve, bool ApproveHigh, bool Control, bool Monitor, IReadOnlyList<string> Categories, bool LineScoped);

public sealed record DocCatalogDto(Guid FactoryId, string FactoryName, string Today, IReadOnlyList<DocTypeDto> Types, IReadOnlyList<DocCategoryDto> CategoryList, DocCapabilities Can,
    IReadOnlyList<DocLineRef> Lines, DocAiStatus Ai);

public sealed record DocLineRef(string Id, string Name);

public sealed record DocCategoryDto(string Code, string Name);

public sealed record DocTypeScore(string Code, double Score);

public sealed record DocCategoryCount(string Category, int Pending, int Drafts, int Missing);

public sealed record DocPurposeCount(string Purpose, int Calls, int Failures);

public sealed record DocAiStatus(bool Configured, string? Provider, string? Model, string? Reason);

public sealed record DocTypeSettingWrite(string ApprovalLevel, bool AutoGenerate, bool ExpectedDaily, int DecisionDueHours,
    string? ScheduleFrequency = null, string? ScheduleScope = null, int? WeekStartDay = null);

/// <summary>One scheduled generation attempt record (created, already existed, no data yet, failed).</summary>
public sealed record DocScheduleRunDto(Guid Id, string TypeCode, string TypeName, string PeriodFrom, string PeriodTo, string LineKey, string Status, int Attempts,
    string? ErrorCode, string? ErrorMessage, Guid? DocumentId, string? DocumentReference, DateTimeOffset FirstAttemptAt, DateTimeOffset LastAttemptAt);

public sealed record DocScheduleStatus(bool AutomationEnabled, IReadOnlyList<DocScheduleRunDto> Runs);

// ── Documents ───────────────────────────────────────────────────────────────

public sealed record DocListQuery(Guid? FactoryId, string? Q, string? Category, string? Type, string? Status, DateOnly? From, DateOnly? To, string? LineId,
    /// <summary><c>mine</c> (created by me), <c>action</c> (waiting for something I can do), <c>missing</c> (with missing data).</summary>
    string? View, bool IncludeArchived, int Page, int PageSize);

public sealed record DocListItem(
    Guid Id, Guid FactoryId, string Reference, string TypeCode, string TypeName, string Category, string Title, string Status, string Risk, string ApprovalLevel, string Source,
    string? PeriodFrom, string? PeriodTo, string? LineId, int CurrentVersion, int MissingCount, int ErrorCount, int WarningCount,
    DateTimeOffset CreatedAt, string CreatedByEmail, DateTimeOffset UpdatedAt, DateTimeOffset? SubmittedAt, DateTimeOffset? DecidedAt, string? DecidedByEmail);

public sealed record DocPaged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public sealed record DocActions(bool Edit, bool Submit, bool StartReview, bool Verify, bool Approve, bool Reject, bool Revise, bool Archive, bool Upload, bool Regenerate, bool UseAi,
    string? WhyNotSubmit, string? WhyNotApprove);

public sealed record DocVersionDto(int Number, DateTimeOffset CreatedAt, string CreatedByEmail, string? ChangeNote, int MissingCount, int ErrorCount, int WarningCount, string ContentSha256);

public sealed record DocEventDto(long Id, string Action, string? FromStatus, string? ToStatus, int? VersionNumber, string? Note, DateTimeOffset OccurredAt, string ActorEmail);

public sealed record DocFileDto(Guid Id, string FileName, string Format, string ContentType, long SizeBytes, string Sha256, string ExtractionStatus, string? ExtractionMethod,
    string? ExtractionModel, DateTimeOffset? ExtractedAt, int ExtractionAttempts, string? FailureCode, string? FailureMessage, string? SuggestedTypeCode, JsonNode? Suggestions,
    DateTimeOffset CreatedAt, string CreatedByEmail);

public sealed record DocDetail(
    DocListItem Document, DocTypeDto Type, JsonObject Data, JsonObject Sources, DocumentChecksDto Checks, DocActions Actions,
    IReadOnlyList<DocVersionDto> Versions, IReadOnlyList<DocEventDto> History, IReadOnlyList<DocFileDto> Files,
    string? SubmittedByEmail, string? ReviewStartedByEmail, DateTimeOffset? ReviewStartedAt, string? VerifiedByEmail, DateTimeOffset? VerifiedAt,
    string? DecisionNote, DateTimeOffset? ArchivedAt, string? ArchivedByEmail, bool StockPosted,
    string? AiSummary, DateTimeOffset? AiSummaryAt, string? AiSummaryModel, string? LineName);

public sealed record DocumentChecksDto(IReadOnlyList<DocumentIssue> Missing, IReadOnlyList<DocumentIssue> Errors, IReadOnlyList<DocumentIssue> Warnings)
{
    public static DocumentChecksDto From(DocumentChecks c) => new(c.Missing, c.Errors, c.Warnings);
}

/// <summary>Create a document: blank form, generated from records, a copy of the last approved one (template), or prefilled from a record.</summary>
public sealed record DocCreateRequest(Guid? FactoryId, string TypeCode, string? Mode, DateOnly? PeriodFrom, DateOnly? PeriodTo, string? LineId, string? Title,
    JsonObject? Data, JsonObject? Parameters, string? PrefillKey);

public sealed record DocSaveRequest(JsonObject Data, string? Title, string? ChangeNote, int? ExpectedVersion, IReadOnlyList<string>? AcceptedAiFields, Guid? AiFileId);

public sealed record DocActionRequest(string? Note, int? ExpectedVersion);

public sealed record DocLookupResult(string Lookup, string Key, bool Found, JsonObject? Values, string? Message);

public sealed record DocUploadInput(byte[] Content, string? FileName);

public sealed record DocClassification(string? TypeCode, string Method, double? Confidence, IReadOnlyList<DocTypeScore> Candidates, string? Reason);

public sealed record DocMonitorItem(Guid? DocumentId, string? Reference, string TypeCode, string TypeName, string Category, string Kind, string? Status, string? Period, string? LineId,
    DateTimeOffset? Since, string Message);

public sealed record DocMonitorDto(Guid FactoryId, string Today, int Drafts, int Pending, int Overdue, int Rejected, int MissingToday, int ApprovedThisWeek,
    IReadOnlyList<DocMonitorItem> Items, IReadOnlyList<DocCategoryCount> ByCategory);

public sealed record DocUsageDto(int Calls, int Failures, int InputTokens, int OutputTokens, IReadOnlyList<DocPurposeCount> ByPurpose, string? Provider, string? Model);

public interface IDocumentService
{
    Task<AccessResult<DocCatalogDto>> CatalogAsync(AccessContext caller, Guid? factoryId, CancellationToken ct);
    Task<AccessResult<DocTypeDto>> SaveTypeSettingAsync(AccessContext caller, Guid? factoryId, string typeCode, DocTypeSettingWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<DocPaged<DocListItem>>> ListAsync(AccessContext caller, DocListQuery query, CancellationToken ct);
    Task<AccessResult<DocDetail>> GetAsync(AccessContext caller, Guid id, CancellationToken ct);
    Task<AccessResult<DocDetail>> CreateAsync(AccessContext caller, DocCreateRequest body, string? traceId, CancellationToken ct);
    Task<AccessResult<DocDetail>> SaveAsync(AccessContext caller, Guid id, DocSaveRequest body, string? traceId, CancellationToken ct);
    Task<AccessResult<DocDetail>> RegenerateAsync(AccessContext caller, Guid id, DocActionRequest body, string? traceId, CancellationToken ct);
    Task<AccessResult<DocDetail>> ActAsync(AccessContext caller, Guid id, string action, DocActionRequest body, string? traceId, CancellationToken ct);
    Task<AccessResult<JsonObject>> VersionAsync(AccessContext caller, Guid id, int number, CancellationToken ct);
    Task<AccessResult<DocLookupResult>> LookupAsync(AccessContext caller, Guid? factoryId, string lookup, string key, CancellationToken ct);
    Task<AccessResult<DocMonitorDto>> MonitorAsync(AccessContext caller, Guid? factoryId, CancellationToken ct);
    Task<AccessResult<DocUsageDto>> UsageAsync(AccessContext caller, Guid? factoryId, int days, CancellationToken ct);
    Task<AccessResult<DocScheduleStatus>> ScheduleRunsAsync(AccessContext caller, Guid? factoryId, int days, CancellationToken ct);

    // Files and AI
    Task<AccessResult<DocFileDto>> UploadAsync(AccessContext caller, Guid id, DocUploadInput input, string? traceId, CancellationToken ct);
    Task<AccessResult<(Stream Content, string ContentType, string FileName)>> OpenFileAsync(AccessContext caller, Guid id, Guid fileId, CancellationToken ct);
    Task<AccessResult<DocFileDto>> ExtractAsync(AccessContext caller, Guid id, Guid fileId, bool allowAi, string? traceId, CancellationToken ct);
    Task<AccessResult<DocClassification>> ClassifyAsync(AccessContext caller, Guid? factoryId, DocUploadInput input, bool allowAi, CancellationToken ct);
    Task<AccessResult<DocDetail>> SummarizeAsync(AccessContext caller, Guid id, string? traceId, CancellationToken ct);
    Task<AccessResult<string>> DraftTextAsync(AccessContext caller, Guid id, string fieldKey, string? traceId, CancellationToken ct);

    /// <summary>The document rendered as <c>pdf</c>, <c>xlsx</c> or <c>docx</c> (optionally an older version).</summary>
    Task<AccessResult<(byte[] Content, string ContentType, string FileName)>> RenderAsync(AccessContext caller, Guid id, string format, int? version, string? traceId, CancellationToken ct);
}

// ── Inventory ───────────────────────────────────────────────────────────────

public sealed record InventoryItemDto(Guid Id, Guid FactoryId, string Code, string Name, string Category, string Unit, decimal? ReorderLevel, bool IsActive,
    decimal Received, decimal Issued, decimal Balance, bool BelowReorder, DateTimeOffset UpdatedAt, int RollsInStock = 0, decimal OnRolls = 0);

/// <summary>A fabric roll in the store; <see cref="Remaining"/> and <see cref="Status"/> (IN_STOCK, PARTLY_ISSUED, ISSUED) come from its movements.</summary>
public sealed record InventoryRollDto(Guid Id, Guid ItemId, string ItemCode, string ItemName, string RollNumber, string? LotNumber, string? Supplier, string? Color,
    string? ShadeGroup, decimal? WidthCm, string? Warehouse, string? Location, string Unit, decimal Received, decimal Issued, decimal Remaining, string Status,
    string ReceivedOn, Guid GrnDocumentId, string GrnReference);

public sealed record InventoryItemWrite(Guid? FactoryId, string Code, string Name, string Category, string Unit, decimal? ReorderLevel, bool? IsActive);

public sealed record StockMovementDto(long Id, Guid ItemId, string ItemCode, string ItemName, string Kind, decimal Quantity, decimal? UnitCost, string MovementDate,
    Guid DocumentId, string DocumentReference, string? Counterparty, DateTimeOffset RecordedAt, string RecordedByEmail);

public interface IInventoryService
{
    Task<AccessResult<IReadOnlyList<InventoryItemDto>>> ItemsAsync(AccessContext caller, Guid? factoryId, string? q, string? category, bool includeInactive, CancellationToken ct);
    Task<AccessResult<InventoryItemDto>> CreateAsync(AccessContext caller, InventoryItemWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<InventoryItemDto>> UpdateAsync(AccessContext caller, Guid id, InventoryItemWrite body, string? traceId, CancellationToken ct);
    Task<AccessResult<DocPaged<StockMovementDto>>> MovementsAsync(AccessContext caller, Guid? factoryId, Guid? itemId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct);
    Task<AccessResult<DocPaged<InventoryRollDto>>> RollsAsync(AccessContext caller, Guid? factoryId, Guid? itemId, string? status, string? q, int page, int pageSize, CancellationToken ct);
}

// ── Notifications ───────────────────────────────────────────────────────────

public sealed record NotificationDto(Guid Id, string Kind, string Title, string Body, string? Link, string? EntityType, string? EntityId, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record NotificationList(IReadOnlyList<NotificationDto> Items, int Unread);

public interface INotificationService
{
    Task<NotificationList> ListAsync(AccessContext caller, bool unreadOnly, int limit, CancellationToken ct);
    Task<int> MarkReadAsync(AccessContext caller, IReadOnlyList<Guid>? ids, CancellationToken ct);
}
