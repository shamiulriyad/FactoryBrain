namespace FactoryBrain.Application.Dtos.Merchandising;

// Wire shapes of /api/merchandising. Dates are plant-local yyyy-MM-dd;
// timestamps are UTC. Every figure is read from persisted rows.

// ── Writes ──────────────────────────────────────────────────────────────────

/// <summary>Register (POST) or edit (PUT) a buyer order. On PUT a null field is left unchanged; list keys in <see cref="ClearFields"/> to empty them.</summary>
public sealed record MerchOrderWrite(
    string? Buyer,
    string? BuyerOrderNumber,
    string? StyleRef,
    string? ProductDescription,
    int? Quantity,
    string? Unit,
    string? DeliveryDate,
    string? Currency,
    decimal? UnitPrice,
    string? Destination,
    List<string>? RequiredDocumentTypes,
    Guid? ProductionOrderId,
    bool? UnlinkProductionOrder,
    string? Status,
    string? Notes,
    List<string>? ClearFields,
    List<MerchMilestoneWrite>? Milestones,
    uint? ExpectedVersion);

public sealed record MerchMilestoneWrite(string? Name, string? DueDate, string? Status, string? CompletedOn, string? Notes, uint? ExpectedVersion);

public sealed record MerchFieldDecisionInput(Guid FieldId, string? Decision, string? CorrectedValue, string? Note);

/// <summary>A value the reviewer adds for a field the extractor did not return (<c>accepted</c>, or <c>unresolved</c> without a value).</summary>
public sealed record MerchFieldAdditionInput(string? FieldKey, string? Value, string? Decision, string? Note);

public sealed record MerchConfirmRequest(
    uint? ExpectedVersion,
    List<MerchFieldDecisionInput>? Decisions,
    List<MerchFieldAdditionInput>? Additions,
    /// <summary>Confirmed fields to write to the order record (each must be accepted or corrected).</summary>
    List<string>? ApplyToOrder,
    string? Notes);

public sealed record MerchConflictResolution(uint? ExpectedVersion, string? ChosenValue, string? Note);

// ── Configuration ───────────────────────────────────────────────────────────

public sealed record ExtractionStatusDto(bool Configured, string Mode, string? Provider, string? Model, bool ReportsConfidence, string? Reason);

public sealed record MerchFieldDefDto(string Key, string Label, string Type, string Description, bool RequiredOnPurchaseOrder, bool Compared);

public sealed record MerchDocumentTypeDto(string Type, string Label);

public sealed record MerchConfigDto(
    ExtractionStatusDto Extraction,
    bool OcrConfigured,
    string OcrNote,
    IReadOnlyList<string> SupportedFormats,
    IReadOnlyList<string> AcceptedExtensions,
    long MaxFileBytes,
    int MaxPages,
    double ReviewConfidenceThreshold,
    string ConfidenceNote,
    IReadOnlyList<MerchDocumentTypeDto> DocumentTypes,
    IReadOnlyList<string> DefaultRequiredDocumentTypes,
    IReadOnlyList<MerchFieldDefDto> Fields,
    bool EmailIntegrationConfigured,
    string EmailIntegrationNote);

// ── Orders ──────────────────────────────────────────────────────────────────

public sealed record BreakdownRowDto(string Color, string Size, int Quantity);

public sealed record MerchFieldSourceDto(
    /// <summary><c>manual</c>, <c>document</c> or <c>conflict_resolution</c>.</summary>
    string Kind,
    Guid? DocumentId,
    Guid? VersionId,
    int? VersionNumber,
    string? DocumentTitle,
    Guid? ConflictId,
    string ByEmail,
    DateTime At,
    /// <summary>The source version has since been replaced by a newer one.</summary>
    bool Stale);

public sealed record MerchFieldValueDto(string Key, string Label, string? Value, string Display, MerchFieldSourceDto? Source);

public sealed record MerchOrderDto(
    Guid Id, string Reference, string Buyer, string BuyerOrderNumber, string? StyleRef, string? ProductDescription,
    int? Quantity, string? Unit, string? DeliveryDate, string? Currency, decimal? UnitPrice, string? Destination,
    IReadOnlyList<BreakdownRowDto>? Breakdown, int? BreakdownTotal, IReadOnlyList<string> RequiredDocumentTypes,
    Guid? ProductionOrderId, string Status, string? Notes, DateTime CreatedAt, string CreatedByEmail, DateTime UpdatedAt, uint Version);

public sealed record MerchMilestoneDto(
    Guid Id, Guid OrderId, string Name, string DueDate, string Status, string? CompletedOn, string? Notes,
    bool Overdue, int DaysUntilDue, DateTime UpdatedAt, string? UpdatedByEmail, uint Version);

public sealed record MerchNextMilestoneDto(string Name, string DueDate, int DaysUntilDue);

public sealed record MerchOrderListItem(
    Guid Id, string Reference, string Buyer, string BuyerOrderNumber, string? StyleRef, int? Quantity, string? Unit,
    string? DeliveryDate, int? DaysToDelivery, string Status, int DocumentCount, int AwaitingReview, int FailedDocuments,
    int OpenConflicts, int OverdueMilestones, int MissingDocuments, MerchNextMilestoneDto? NextMilestone, DateTime UpdatedAt, uint Version);

public sealed record RequiredDocumentDto(string Type, string Label, string State, Guid? DocumentId, Guid? VersionId);

/// <summary>One item of the prioritised "missing information and deadlines" list.</summary>
public sealed record AttentionItemDto(
    string Kind, string Priority, string Title, string Detail, string? DueDate,
    string? FieldKey, Guid? DocumentId, Guid? VersionId, Guid? ConflictId, Guid? MilestoneId);

public sealed record MerchSummaryDto(string Headline, IReadOnlyList<string> Lines, int MilestonesDone, int MilestonesTotal, int? DaysToDelivery, string Basis);

public sealed record LinkedProductionOrderDto(
    Guid Id, string OrderNumber, string Status, int Quantity, string DueDate, string StyleCode, string StyleName, int GoodSewingOutput,
    IReadOnlyList<string> Differences);

public sealed record MerchOrderDetail(
    MerchOrderDto Order,
    IReadOnlyList<MerchFieldValueDto> Fields,
    IReadOnlyList<MerchMilestoneDto> Milestones,
    IReadOnlyList<MerchDocumentListItem> Documents,
    IReadOnlyList<RequiredDocumentDto> RequiredDocuments,
    IReadOnlyList<MerchConflictDto> Conflicts,
    IReadOnlyList<AttentionItemDto> Attention,
    MerchSummaryDto Summary,
    LinkedProductionOrderDto? ProductionOrder,
    string Today);

public sealed record MerchDeadlineDto(
    string Kind, Guid OrderId, string OrderReference, string Buyer, string BuyerOrderNumber, string Name, string DueDate,
    int DaysUntilDue, bool Overdue, Guid? MilestoneId);

public sealed record MerchOverviewDto(
    int ActiveOrders, int DocumentsAwaitingReview, int OpenConflicts, int OverdueMilestones, int FailedDocuments, int MissingRequiredDocuments,
    IReadOnlyList<MerchDeadlineDto> Deadlines,
    IReadOnlyList<MerchDocumentListItem> AwaitingReview,
    IReadOnlyList<MerchConflictDto> Conflicts,
    ExtractionStatusDto Extraction,
    string Today,
    int HorizonDays);

// ── Documents ───────────────────────────────────────────────────────────────

public sealed record MerchVersionSummary(
    Guid Id, int VersionNumber, bool IsCurrent, string Status, string Format, string? OriginalFileName, long SizeBytes,
    DateTime CreatedAt, string UploadedByEmail, string? ChangeNote, string? FailureCode, string? ExtractionCode,
    int FieldCount, int BlockingIssues, DateTime? ConfirmedAt, string? ConfirmedByEmail);

public sealed record MerchDocumentListItem(
    Guid Id, Guid OrderId, string OrderReference, string Buyer, string BuyerOrderNumber, string Type, string TypeLabel, string Title,
    int VersionCount, MerchVersionSummary? CurrentVersion, DateTime UpdatedAt);

public sealed record MerchDocumentDetail(
    Guid Id, Guid OrderId, string OrderReference, string Type, string TypeLabel, string Title,
    IReadOnlyList<MerchVersionSummary> Versions, DateTime CreatedAt, string CreatedByEmail);

public sealed record MerchFileDto(string Url, string ContentType, string Format, long SizeBytes, string? OriginalFileName, string Sha256);

public sealed record MerchProcessingDto(
    string Status, int Attempts, DateTime? ProcessingStartedAt, DateTime? ProcessedAt, string? FailureCode, string? FailureMessage,
    int? PageCount, int? TextLength, bool TextTruncated, string? ExtractionCode, string? ExtractionMessage,
    string? Provider, string? Model, string? ModelVersion, int? ExtractionMs, bool Retryable, string ConfidenceNote);

public sealed record MerchPageDto(int PageNumber, string Text);

public sealed record MerchIssueDto(string Code, string Message, bool Blocking);

public sealed record MerchExtractedFieldDto(
    Guid Id, string Key, string Label, string Source, string? RawValue, string? NormalizedValue, string Display,
    double? Confidence, int? PageNumber, string? Snippet, string EvidenceStatus, IReadOnlyList<MerchIssueDto> Issues,
    string Decision, string? CorrectedValue, string? EffectiveValue, string? ReviewerNote, DateTime? ReviewedAt, string? ReviewedByEmail);

public sealed record MerchReviewDto(DateTime ConfirmedAt, string? ConfirmedByEmail, string? Notes);

public sealed record MerchVersionDetail(
    Guid Id, Guid DocumentId, Guid OrderId, string OrderReference, string Buyer, string BuyerOrderNumber, uint OrderVersion,
    string DocumentType, string DocumentTitle, int VersionNumber, bool IsCurrent, string? ChangeNote,
    MerchFileDto File, MerchProcessingDto Processing, IReadOnlyList<MerchPageDto> Pages,
    IReadOnlyList<MerchExtractedFieldDto> Fields,
    /// <summary>Fields a purchase order should state that this version has no value for.</summary>
    IReadOnlyList<string> MissingRequiredFields,
    IReadOnlyList<MerchFieldValueDto> OrderValues,
    IReadOnlyList<MerchConflictDto> Conflicts,
    IReadOnlyList<MerchVersionSummary> OtherVersions,
    MerchReviewDto? Review,
    bool Reviewable,
    DateTime CreatedAt, string UploadedByEmail, uint Version);

// ── Conflicts ───────────────────────────────────────────────────────────────

public sealed record MerchConflictValueDto(
    string Value, string Display, string SourceKind, Guid? DocumentId, string? DocumentTitle, string? DocumentType,
    Guid? VersionId, int? VersionNumber, string? VersionStatus, Guid? FieldId, int? Page, string? Snippet);

public sealed record MerchConflictDto(
    Guid Id, Guid OrderId, string OrderReference, string FieldKey, string Label, string Status, IReadOnlyList<MerchConflictValueDto> Values,
    DateTime DetectedAt, DateTime? ResolvedAt, string? ResolvedByEmail, string? ResolvedValue, string? ResolutionNote, uint Version);
