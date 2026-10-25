namespace FactoryBrain.Application.Dtos.Qc;

// Wire contracts for the inspection workflow (/api/qc/inspections,
// /api/qc/summary, /api/qc/categories, /api/qc/vision/status). Enum-like
// fields are snake_case strings (see QcWire); coordinates are normalised
// to [0,1] of the image width / height, origin top-left.

public sealed record QcInspectionListItem(
    Guid Id,
    string Reference,
    string Status,
    string ProductType,
    string Stage,
    string? LineId,
    string? ProductionOrder,
    DateTime CreatedAt,
    string CreatedByEmail,
    /// <summary>Findings the vision provider returned.</summary>
    int PredictionCount,
    /// <summary>Findings an inspector accepted, corrected or added (0 until reviewed).</summary>
    int VerifiedDefectCount,
    double? MaxConfidence,
    string? Outcome,
    string? Provider,
    string? Model,
    string? FailureCode);

public sealed record QcInspectionPage(
    IReadOnlyList<QcInspectionListItem> Items,
    int Page,
    int PageSize,
    int Total,
    int TotalPages);

public sealed record QcInspectionQuery(
    string? Q = null,
    string? Status = null,
    string? Stage = null,
    string? LineId = null,
    string? ProductType = null,
    string? Outcome = null,
    DateTime? From = null,
    DateTime? To = null,
    int Page = 1,
    int PageSize = 20,
    string? Sort = null);

public sealed record QcBoxDto(double X, double Y, double Width, double Height);

public sealed record QcFindingDto(
    Guid Id,
    string Source,
    string? CategoryCode,
    string? CategoryName,
    string? RawLabel,
    double? Confidence,
    QcBoxDto? Box,
    /// <summary>Mask outline as [x, y] points, when the provider returned one.</summary>
    IReadOnlyList<double[]>? Polygon,
    string Severity,
    string? Description,
    string Decision,
    string? CorrectedCategoryCode,
    string? CorrectedSeverity,
    string? ReviewerNote,
    /// <summary>Corrected category if any, else the original.</summary>
    string? EffectiveCategoryCode,
    string? EffectiveCategoryName,
    string EffectiveSeverity,
    /// <summary>True for inspector-added findings and accepted / corrected predictions.</summary>
    bool Verified,
    DateTime CreatedAt,
    DateTime? ReviewedAt);

public sealed record QcImageDto(
    string Url,
    string ContentType,
    int Width,
    int Height,
    long SizeBytes,
    string? OriginalFileName,
    string Sha256);

public sealed record QcAnalysisDto(
    string? Provider,
    string? Model,
    string? ModelVersion,
    bool? LocalizationSupported,
    double? ConfidenceThreshold,
    int? InferenceMs,
    int Attempts,
    DateTime? ProcessingStartedAt,
    DateTime? AnalyzedAt,
    string? FailureCode,
    string? FailureMessage,
    bool Retryable);

public sealed record QcReviewDto(string Outcome, string? Notes, DateTime ReviewedAt, string? ReviewedByEmail);

public sealed record QcFollowUpDto(string ApprovalId, string Status, string RiskTier);

public sealed record QcInspectionDetail(
    Guid Id,
    string Reference,
    string Status,
    string ProductType,
    string Stage,
    string? LineId,
    string? ProductionOrder,
    string? Notes,
    IReadOnlyList<string> RequestedCategoryCodes,
    QcImageDto Image,
    QcAnalysisDto Analysis,
    IReadOnlyList<QcFindingDto> Findings,
    QcReviewDto? Review,
    QcFollowUpDto? FollowUp,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string CreatedByEmail,
    /// <summary>Send back as <c>expectedVersion</c> when reviewing.</summary>
    uint Version);

/// <summary>Validated input for creating an inspection (the HTTP form is mapped onto this).</summary>
public sealed record QcCreateInspectionInput(
    string ProductType,
    string Stage,
    string? LineId,
    string? ProductionOrder,
    string? Notes,
    IReadOnlyList<string> CategoryCodes,
    byte[] Image,
    string? OriginalFileName,
    string? IdempotencyKey);

public sealed record QcReviewRequest(
    uint? ExpectedVersion,
    string? Outcome,
    string? Notes,
    IReadOnlyList<QcFindingDecisionInput>? Decisions,
    IReadOnlyList<QcFindingAdditionInput>? Additions,
    bool RequestFollowUp = false,
    string? FollowUpNote = null);

public sealed record QcFindingDecisionInput(
    Guid FindingId,
    string? Decision,
    string? CorrectedCategoryCode,
    string? CorrectedSeverity,
    string? Note);

public sealed record QcFindingAdditionInput(
    string? CategoryCode,
    string? Severity,
    QcBoxDto? Box,
    string? Note);

public sealed record QcVisionStatus(
    bool Configured,
    /// <summary><c>llm</c> | <c>http</c> | <c>none</c>.</summary>
    string Mode,
    string? Provider,
    string? Model,
    bool? LocalizationSupported,
    /// <summary>Why inference is unavailable, when it is.</summary>
    string? Reason,
    double ConfidenceThreshold,
    long MaxImageBytes,
    IReadOnlyList<string> AcceptedContentTypes,
    string ConfidenceNote);

public sealed record QcCategoryDto(
    string Code,
    string Name,
    string? NameBn,
    string? Description,
    string DefaultSeverity,
    bool IsActive,
    int SortOrder,
    uint Version,
    DateTime UpdatedAt);

public sealed record QcCategoryCreateRequest(
    string? Code,
    string? Name,
    string? NameBn,
    string? Description,
    string? DefaultSeverity,
    int? SortOrder);

public sealed record QcCategoryUpdateRequest(
    uint? ExpectedVersion,
    string? Name,
    string? NameBn,
    string? Description,
    string? DefaultSeverity,
    bool? IsActive,
    int? SortOrder);

// ── Quality summary ─────────────────────────────────────────────────────────

public sealed record QcSummaryQuery(
    DateTime? From = null,
    DateTime? To = null,
    int? Days = null,
    string? LineId = null,
    string? ProductType = null,
    string? Stage = null);

public sealed record QcSummaryTotals(
    int Inspections,
    /// <summary>Pending or processing.</summary>
    int InProgress,
    int Failed,
    /// <summary>Completed by the provider, not yet reviewed.</summary>
    int AwaitingReview,
    int Reviewed,
    int Passed,
    int Rework,
    int Rejected,
    int VerifiedDefects,
    /// <summary>Reviewed inspections with ≥1 verified defect ÷ reviewed inspections × 100. Null with no reviews.</summary>
    double? DefectiveRatePct,
    /// <summary>Verified defects ÷ reviewed inspections × 100 (defects per hundred units). Null with no reviews.</summary>
    double? Dhu,
    /// <summary>Rework + reject outcomes ÷ reviewed inspections × 100. Null with no reviews.</summary>
    double? RejectOrReworkRatePct);

public sealed record QcModelAgreement(
    int PredictionsReviewed,
    int Accepted,
    int Corrected,
    int Rejected,
    /// <summary>Accepted ÷ predictions reviewed × 100. Null when none were reviewed.</summary>
    double? AcceptedPct,
    int InspectorAdded,
    int AwaitingReview);

public sealed record QcCategoryCount(string? Code, string Name, int Count, double SharePct);

public sealed record QcSeverityCounts(int Minor, int Major, int Critical);

public sealed record QcLineStats(string LineId, int Reviewed, int Defective, double? DefectiveRatePct, int VerifiedDefects);

public sealed record QcDailyPoint(string Date, int Inspections, int Reviewed, int Defective, int VerifiedDefects, double? DefectiveRatePct);

public sealed record QcRecurringIssue(string LineId, string? CategoryCode, string CategoryName, int Defects, int Inspections);

public sealed record QcSummaryResponse(
    DateTime From,
    DateTime To,
    QcSummaryTotals Totals,
    QcModelAgreement Model,
    IReadOnlyList<QcCategoryCount> ByCategory,
    QcSeverityCounts BySeverity,
    IReadOnlyList<QcLineStats> ByLine,
    IReadOnlyList<QcDailyPoint> Daily,
    IReadOnlyList<QcRecurringIssue> Recurring,
    int RecurringThreshold,
    IReadOnlyDictionary<string, string> Definitions,
    DateTime GeneratedAt);
