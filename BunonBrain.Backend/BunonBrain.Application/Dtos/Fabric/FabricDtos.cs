using BunonBrain.Application.Dtos.Production;
using BunonBrain.Application.Fabric;

namespace BunonBrain.Application.Dtos.Fabric;

// Wire contract of /api/fabric and /api/cutting. Lengths along the roll are
// metres (…M), widths and positions across it centimetres (…Cm), areas m²;
// image boxes are fractions of the image. Enum values are snake_case.

// ── Config ──────────────────────────────────────────────────────────────────

public sealed record FabricUnitsDto(string Along, string Across, string Origin, string ImageSpace, IReadOnlyList<string> LengthUnits, IReadOnlyList<string> WidthUnits);

public sealed record FabricCategoryDto(string Code, string Name, string Description, string DefaultSeverity, bool IsOpening, bool DefaultCutOut);

public sealed record FabricRulesDto(double PassMaxPointsPer100SqYd, double RejectAbovePointsPer100SqYd, double WidthToleranceCm, double MaxRejectedPct,
    bool RequireSeparateApprover, string Scoring, string Recommendation);

public sealed record FabricVisionDto(bool Configured, string Mode, string? Provider, string? Model, bool? LocalizationSupported, string? Reason,
    double MinConfidence, long MaxImageBytes, IReadOnlyList<string> AcceptedContentTypes);

public sealed record IntegrationStatusDto(string Name, string Status, string Detail);

public sealed record FabricConfigDto(FabricUnitsDto Units, IReadOnlyList<FabricCategoryDto> Categories, FabricRulesDto Rules, FabricVisionDto Vision,
    IReadOnlyList<string> Orientations, IReadOnlyList<IntegrationStatusDto> Integrations, string CuttingFormulas, string? ConfigurationError);

// ── Lots ────────────────────────────────────────────────────────────────────

public sealed record FabricLotWrite(string? LotNumber, string? Supplier, string? PurchaseReference, Guid? MerchOrderId, bool? ClearMerchOrder,
    string? FabricType, string? Composition, string? Color, string? ColorCode, double? GsmDeclared, string? ReceivedOn, string? Notes, uint? ExpectedVersion);

public sealed record MerchOrderRefDto(Guid Id, string Reference, string Buyer, string BuyerOrderNumber, string? StyleRef);

public sealed record FabricLotDto(Guid Id, string LotNumber, string? Supplier, string? PurchaseReference, MerchOrderRefDto? MerchOrder, string FabricType,
    string? Composition, string Color, string? ColorCode, double? GsmDeclared, string? ReceivedOn, string? Notes, DateTime CreatedAt, string CreatedByEmail,
    DateTime UpdatedAt, uint Version);

public sealed record FabricLotListItem(Guid Id, string LotNumber, string? Supplier, string? PurchaseReference, string FabricType, string Color, string? ReceivedOn,
    int Rolls, int Uninspected, int Passed, int Restricted, int Rejected, double TotalLengthM, double RemainingM, DateTime UpdatedAt);

public sealed record CategoryCount(string Code, string Name, int Count);

/// <summary>Quality of a lot (or of everything) from the completed inspections of its rolls.</summary>
public sealed record QualitySummaryDto(int Rolls, int Uninspected, int Passed, int Restricted, int Rejected, int InspectedRolls, double TotalLengthM,
    double InspectedLengthM, int TotalPoints, double? PointsPer100SqYd, double? RejectedLengthM, double? UsableLengthM, int RollsWithUnknownUsable,
    int Defects, IReadOnlyList<CategoryCount> DefectsByCategory, double RemainingM, string Definitions);

public sealed record FabricLotDetail(FabricLotDto Lot, IReadOnlyList<FabricRollListItem> Rolls, QualitySummaryDto Quality, IReadOnlyList<HistoryEntryDto> History);

// ── Rolls ───────────────────────────────────────────────────────────────────

/// <param name="Length">Ticket length in <paramref name="LengthUnit"/> (m or yd).</param>
/// <param name="Width">Ticket width in <paramref name="WidthUnit"/> (cm or in).</param>
public sealed record RollWrite(string? RollNumber, double? Length, string? LengthUnit, double? Width, string? WidthUnit, double? WeightKg,
    string? ShadeGroup, string? Location, string? Notes, string? Reason, uint? ExpectedVersion);

public sealed record RollBatchWrite(IReadOnlyList<RollWrite>? Rolls);

public sealed record FabricRollListItem(Guid Id, string RollNumber, Guid LotId, string LotNumber, string? Supplier, string FabricType, string Color,
    string? ShadeGroup, double TicketLengthM, double? MeasuredLengthM, double ReferenceLengthM, double TicketWidthCm, double? MeasuredWidthCm,
    double CuttableWidthCm, string Quality, double BookRemainingM, double? UsableRemainingM, bool IsClosed, bool InspectionInProgress,
    int? TotalPoints, double? PointsPer100SqYd, DateTime UpdatedAt);

public sealed record RollDimensionsDto(double TicketLengthM, string TicketLengthUnit, double TicketWidthCm, string TicketWidthUnit, double? MeasuredLengthM,
    double? MeasuredWidthCm, double ReferenceLengthM, double CuttableWidthCm, double FullWidthCm, string ReferenceBasis);

public sealed record RollQualityDto(string Status, DateTime? Since, Guid? InspectionId, string? InspectionReference, int? TotalPoints, double? PointsPer100SqYd,
    double? RejectedLengthM, double? UsableLengthM, bool EligibleWithoutOverride, string Explanation);

public sealed record RollAllocationRef(Guid BatchId, string Reference, string Status, string OrderNumber, double PlannedLengthM, double CutM);

public sealed record FabricRollDetail(FabricRollListItem Summary, FabricLotDto Lot, RollDimensionsDto Dimensions, RollBalance Balance, RollQualityDto Quality,
    IReadOnlyList<InspectionListItem> Inspections, IReadOnlyList<MovementDto> Movements, IReadOnlyList<RollAllocationRef> Allocations, DefectMapDto Map,
    IReadOnlyList<string> Warnings, double? WeightKg, string? Location, string? Notes, IReadOnlyList<HistoryEntryDto> History, uint Version);

public sealed record AdjustmentWrite(double? MeasuredRemainingM, double? DeltaM, string? Reason);

// ── Defect map ──────────────────────────────────────────────────────────────

public sealed record RollLocationDto(double? AlongStartM, double? AlongLengthCm, double? AcrossStartCm, double? AcrossWidthCm, string State);

public sealed record MapDefectDto(Guid Id, Guid InspectionId, string? CategoryCode, string CategoryName, string Severity, string Source, string Decision,
    bool CutOut, bool Counted, int? Points, RollLocationDto Location, string LocationSource);

public sealed record MapFrameDto(Guid ImageId, double AlongStartM, double AlongEndM, double AcrossStartCm, double AcrossEndCm);

/// <summary>
/// Everything a roll diagram needs: the roll's reference length × full width, defects with their roll
/// coordinates (unmapped ones are counted, not drawn), cut-out intervals and calibrated image frames.
/// </summary>
public sealed record DefectMapDto(double LengthM, double WidthCm, string AlongAxis, string AcrossAxis, IReadOnlyList<MapDefectDto> Defects,
    IReadOnlyList<RejectedInterval> RejectedIntervals, int Mapped, int AlongOnly, int Unmapped, IReadOnlyList<MapFrameDto> Frames);

// ── Inspections ─────────────────────────────────────────────────────────────

public sealed record InspectionStart(string? Notes);

public sealed record InspectionUpdate(uint? ExpectedVersion, double? InspectedLengthM, double? MeasuredLengthM, double? MeasuredWidthCm, string? Notes);

public sealed record InspectionComplete(uint? ExpectedVersion, string? Result, string? Note);

public sealed record InspectionReopen(uint? ExpectedVersion, string? Reason);

public sealed record InspectionListItem(Guid Id, string Reference, Guid RollId, string RollNumber, string LotNumber, string Status, string? Result,
    string? Recommendation, int Defects, int Unreviewed, int? TotalPoints, double? PointsPer100SqYd, DateTime StartedAt, string StartedByEmail,
    DateTime? CompletedAt, string? CompletedByEmail, int Round);

public sealed record ImageBoxDto(double X, double Y, double Width, double Height);

public sealed record DefectDto(Guid Id, Guid InspectionId, string Source, string? CategoryCode, string? CategoryName, string? RawLabel, double? Confidence,
    string? Description, Guid? ImageId, ImageBoxDto? ImageBox, double[][]? ImagePolygon, RollLocationDto Location, string LocationSource, string Severity,
    bool CutOut, string Decision, string? CorrectedCategoryCode, string? CorrectedSeverity, string? EffectiveCategoryCode, string EffectiveSeverity,
    bool Counted, int? Points, string? ReviewerNote, DateTime? ReviewedAt, string? ReviewedByEmail, DateTime CreatedAt, string CreatedByEmail, uint Version);

/// <summary>Location input: roll coordinates (metres along, centimetres across). Omitted parts are unknown.</summary>
public sealed record LocationInput(double? AlongStartM, double? AlongLengthCm, double? AcrossStartCm, double? AcrossWidthCm);

/// <summary>An inspector-recorded defect (create) or its new values (edit, full replacement of the location).</summary>
public sealed record DefectWrite(string? CategoryCode, string? Severity, bool? CutOut, LocationInput? Location, Guid? ImageId, ImageBoxDto? ImageBox,
    string? Description, string? Note, uint? ExpectedVersion);

/// <summary>Decision on a model defect. A correction may change category, severity, cut-out and the roll location.</summary>
public sealed record DefectReview(string? Decision, string? CorrectedCategoryCode, string? CorrectedSeverity, bool? CutOut, LocationInput? Location,
    string? Note, uint? ExpectedVersion);

public sealed record CalibrationWrite(uint? ExpectedVersion, string? Orientation, double? FrameAlongStartM, double? FrameAlongEndM,
    double? FrameAcrossStartCm, double? FrameAcrossEndCm, string? Note);

public sealed record CalibrationDto(string Orientation, double FrameAlongStartM, double FrameAlongEndM, double FrameAcrossStartCm, double FrameAcrossEndCm,
    string? Note, DateTime? CalibratedAt, string? CalibratedByEmail, IReadOnlyList<string> Warnings);

public sealed record FabricImageDto(Guid Id, Guid InspectionId, string ContentType, long SizeBytes, int WidthPx, int HeightPx, string? OriginalFileName,
    string Sha256, CalibrationDto? Calibration, string AnalysisStatus, string? FailureCode, string? FailureMessage, string? Provider, string? Model,
    string? ModelVersion, bool? LocalizationSupported, double? ConfidenceThreshold, int? InferenceMs, DateTime? AnalyzedAt, int Predictions,
    DateTime CreatedAt, string CreatedByEmail, uint Version, string ContentUrl);

public sealed record ImageUploadInput(byte[] Image, string? FileName, string? IdempotencyKey, CalibrationWrite? Calibration);

public sealed record AnalyzeResponse(string Outcome, string Message, int NewDefects, FabricImageDto Image);

public sealed record ScoreDto(bool Stored, int TotalPoints, int UncappedPoints, int ScoredDefects, int UnsizedDefects, int DefectsWithoutAlongPosition,
    double InspectedLengthM, double WidthCmUsed, double InspectedAreaSqYd, double? PointsPer100SqYd, double ReferenceLengthM, double? RejectedLengthM,
    double? UsableLengthM, int UnlocatedCutOuts, string Recommendation, IReadOnlyList<string> Reasons, double PassMax, double RejectAbove, string Definitions);

public sealed record HistoryEntryDto(DateTime OccurredAt, string ActorEmail, string Action, string? FromState, string? ToState, string? Reason);

public sealed record InspectionDetail(InspectionListItem Summary, FabricRollListItem Roll, double? InspectedLengthM, double? MeasuredLengthM,
    double? MeasuredWidthCm, string? Notes, IReadOnlyList<DefectDto> Defects, IReadOnlyList<FabricImageDto> Images, ScoreDto Score, DefectMapDto Map,
    string? ResultNote, string? ReopenReason, string? ReopenedByEmail, DateTime? ReopenedAt, IReadOnlyList<HistoryEntryDto> History, FabricVisionDto Vision,
    uint Version);

// ── Cutting ─────────────────────────────────────────────────────────────────

public sealed record SizeQtyDto(string Size, int Quantity);

public sealed record BatchWrite(Guid? ProductionOrderId, string? Color, IReadOnlyList<SizeQtyDto>? Requirement, bool? RequirementFromMerch, string? Notes,
    uint? ExpectedVersion);

/// <param name="Length">Marker length in <paramref name="LengthUnit"/> (m, yd or cm).</param>
/// <param name="Width">Marker width in <paramref name="WidthUnit"/> (cm or in).</param>
public sealed record MarkerWrite(string? Name, double? Length, string? LengthUnit, double? Width, string? WidthUnit, double? EfficiencyPct,
    double? EndAllowanceCm, int? PlannedPlies, IReadOnlyList<SizeQtyDto>? Ratio, string? Notes, uint? ExpectedVersion);

public sealed record AllocationWrite(Guid? RollId, double? PlannedLengthM, uint? ExpectedVersion);

public sealed record OverrideRequest(Guid? RollId, string? Reason);

public sealed record BatchTransition(string? Reason, uint? ExpectedVersion);

public sealed record CutWrite(Guid? RollId, Guid? MarkerId, int? Plies, double? LengthM, double? EndBitM, string? Notes);

public sealed record CutCorrection(int? Plies, double? LengthM, double? EndBitM, string? Reason);

public sealed record RollReturn(Guid RollId, double? RemainingLengthM);

public sealed record BatchComplete(uint? ExpectedVersion, IReadOnlyList<RollReturn>? Returns, string? Note);

public sealed record PlanOptionsRequest(int? MaxPliesPerLay, double? MaxMarkerLengthM, double? CuttableWidthCm, IReadOnlyList<MarkerWrite>? ExtraCandidates);

public sealed record OrderRefDto(Guid Id, string OrderNumber, Guid StyleId, string StyleCode, string StyleName, string Status, int Quantity, string DueDate, string? Buyer);

public sealed record BatchListItem(Guid Id, string Reference, Guid ProductionOrderId, string OrderNumber, string? StyleCode, string Color, string Status,
    int Markers, int Rolls, double PlannedConsumptionM, double ActualConsumptionM, int PlannedGarments, int CutGarments, DateTime UpdatedAt);

public sealed record MarkerDto(Guid Id, string Name, double LengthM, double WidthCm, double? EfficiencyPct, double EndAllowanceCm, int PlannedPlies,
    IReadOnlyList<SizeQtyDto> Ratio, int GarmentsPerPly, double PlannedConsumptionM, string? Notes, uint Version);

public sealed record AllocationDto(Guid Id, FabricRollListItem Roll, double PlannedLengthM, double CutM, double OutstandingM, double? ReturnedLengthM,
    bool Eligible, bool NeedsOverride, string OverrideState, uint Version);

public sealed record OverrideDto(Guid Id, Guid RollId, string RollNumber, string RollStatusAtRequest, string Reason, string Status, string State,
    ApprovalRefDto? Approval, string RequestedByEmail, DateTime RequestedAt, DateTime? ClosedAt, string? ClosedByEmail);

public sealed record ValidationIssueDto(string Code, string Severity, string Message, Guid? RollId, Guid? MarkerId);

public sealed record BatchValidationDto(bool CanSubmit, int Blocking, int Warnings, double PlannedConsumptionM, double AllocatedM,
    IReadOnlyList<SizeQtyDto> PlannedBySize, IReadOnlyList<ValidationIssueDto> Issues);

public sealed record MovementDto(Guid Id, Guid RollId, string RollNumber, string Kind, Guid? BatchId, string? BatchReference, Guid? MarkerId,
    string? MarkerName, int? Plies, double LengthM, double EndBitM, string? Reason, string? Notes, Guid? OverrideId, Guid? CorrectionOfId,
    bool IsSuperseded, DateTime RecordedAt, string RecordedByEmail);

public sealed record RollReconciliationDto(Guid RollId, string RollNumber, double PlannedLengthM, double CutM, double EndBitsM, double UsedM,
    double VarianceVsPlannedM, double? ReturnedLengthM, double ReconciliationAdjustmentM, double BookRemainingM);

public sealed record ReconciliationDto(double PlannedConsumptionM, double AllocatedM, double ActualConsumptionM, double VarianceM, double ReconciliationAdjustmentsM,
    int RollsReturned, int RollsNotMeasured, IReadOnlyList<RollReconciliationDto> Rolls, string Definitions);

public sealed record BatchDetail(BatchListItem Summary, OrderRefDto Order, MerchOrderRefDto? MerchOrder, IReadOnlyList<SizeQtyDto> Requirement,
    string RequirementSource, IReadOnlyList<MarkerDto> Markers, IReadOnlyList<AllocationDto> Allocations, IReadOnlyList<OverrideDto> Overrides,
    BatchValidationDto Validation, CuttingMetricsResult Metrics, string MetricDefinitions, ReconciliationDto Reconciliation, IReadOnlyList<MovementDto> Cuts,
    ApprovalRefDto? Approval, IReadOnlyList<HistoryEntryDto> History, string? Notes, string? SubmittedByEmail, DateTime? SubmittedAt,
    string? ReleasedByEmail, DateTime? ReleasedAt, string? ClosedByEmail, DateTime? ClosedAt, string? ClosedReason, string CreatedByEmail,
    DateTime CreatedAt, uint Version);

public sealed record BatchActionResponse(string Outcome, string Message, BatchDetail? Batch);

// ── Traceability & summary ──────────────────────────────────────────────────

public sealed record OutputRefDto(Guid Id, string LineId, string ProductionDate, string Stage, int Quantity, string Source);

public sealed record PlanRefDto(Guid Id, string Reference, string LineId, string Status, string StartDate, string EndDate, int PlannedQuantity);

public sealed record TraceBatchDto(Guid Id, string Reference, string Status, string Color, double PlannedLengthM, double CutM, int Plies, int Garments,
    OrderRefDto Order, MerchOrderRefDto? MerchOrder, IReadOnlyList<PlanRefDto> Plans, IReadOnlyList<OutputRefDto> CuttingOutput, int CuttingOutputTotal);

public sealed record RollTraceDto(FabricRollListItem Roll, FabricLotDto Lot, IReadOnlyList<InspectionListItem> Inspections, IReadOnlyList<TraceBatchDto> Batches,
    IReadOnlyList<MovementDto> Movements, RollBalance Balance, IReadOnlyList<string> Notes);

public sealed record FabricSummaryDto(int Lots, QualitySummaryDto Quality, int InspectionsInProgress, int BatchesDraft, int BatchesPendingApproval,
    int BatchesReleased, int BatchesCompleted, IReadOnlyList<InspectionListItem> RecentInspections);
