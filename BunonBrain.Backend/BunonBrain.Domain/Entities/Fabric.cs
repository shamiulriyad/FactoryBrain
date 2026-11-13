namespace BunonBrain.Domain.Entities;

// Fabric inspection & cutting. A fabric lot (one delivery of one fabric /
// colour from a supplier) holds rolls; each roll is inspected, its defects
// are recorded by hand or returned by the configured vision provider and
// reviewed, and the inspector records the roll's quality result. Cutting
// batches for a production order (Sector 2) allocate rolls, describe their
// markers and record what was actually cut; every metre taken off a roll is
// a movement row, so roll stock and batch consumption always reconcile.
//
// Units and coordinates (canonical, everywhere in the database and API):
//  - Along the roll (length): metres. Across the roll (width): centimetres.
//  - Roll origin: the head end (where unrolling / inspection starts) is
//    0 m along; the left selvedge, looking in the unrolling direction with
//    the face side up, is 0 cm across.
//  - Image coordinates are fractions [0,1] of the image width / height,
//    origin top-left. They are stored in separate columns and become roll
//    coordinates only through an explicit, user-entered frame calibration.

/// <summary>
/// A roll's quality state, set only by completing an inspection
/// (<c>Uninspected</c> until the first one, and again while a completed
/// inspection is reopened for correction).
/// </summary>
public enum FabricQualityStatus { Uninspected, Passed, Restricted, Rejected }

/// <summary>One delivery of one fabric quality and colour, as received from the supplier.</summary>
public class FabricLot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The supplier's / mill's lot (batch, dye lot) number.</summary>
    public string LotNumber { get; set; } = default!;
    public string? Supplier { get; set; }
    /// <summary>Lower-case trimmed supplier ("" when none): the lot number is unique per supplier.</summary>
    public string SupplierKey { get; set; } = "";
    /// <summary>Lower-case trimmed lot number, for the per-supplier uniqueness.</summary>
    public string LotNumberKey { get; set; } = default!;
    /// <summary>Purchase order / goods-received reference, as written on the delivery papers.</summary>
    public string? PurchaseReference { get; set; }
    /// <summary>Verified link to the Sector 3 buyer order the fabric was bought for, if any.</summary>
    public Guid? MerchOrderId { get; set; }
    /// <summary>Fabric quality as the plant names it, e.g. "Single jersey 160 GSM".</summary>
    public string FabricType { get; set; } = default!;
    public string? Composition { get; set; }
    public string Color { get; set; } = default!;
    public string? ColorCode { get; set; }
    /// <summary>Declared weight, grams per square metre.</summary>
    public double? GsmDeclared { get; set; }
    public DateOnly? ReceivedOn { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    /// <summary>PostgreSQL <c>xmin</c>.</summary>
    public uint Version { get; set; }
}

/// <summary>
/// One roll. Ticket dimensions are the supplier's; measured ones come from
/// inspection and, when present, are the reference for every calculation.
/// Remaining length is never stored: it is the reference length minus the
/// live movements (<see cref="FabricRollMovement"/>).
/// </summary>
public class FabricRoll
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LotId { get; set; }
    /// <summary>Roll number as on the roll ticket; unique within the lot.</summary>
    public string RollNumber { get; set; } = default!;
    public string RollNumberKey { get; set; } = default!;
    /// <summary>Ticket length, metres (converted from <see cref="TicketLengthUnit"/> on entry).</summary>
    public double TicketLengthM { get; set; }
    /// <summary>Unit the ticket length was entered in (<c>m</c> or <c>yd</c>), for display.</summary>
    public string TicketLengthUnit { get; set; } = "m";
    /// <summary>Ticket (declared) width, centimetres.</summary>
    public double TicketWidthCm { get; set; }
    /// <summary>Unit the ticket width was entered in (<c>cm</c> or <c>in</c>).</summary>
    public string TicketWidthUnit { get; set; } = "cm";
    /// <summary>Length measured at inspection, metres.</summary>
    public double? MeasuredLengthM { get; set; }
    /// <summary>Usable (cuttable) width measured at inspection, inside the selvedges, centimetres.</summary>
    public double? MeasuredWidthCm { get; set; }
    public double? WeightKg { get; set; }
    /// <summary>Shade band from shade sorting; rolls of different bands should not be mixed in one lay.</summary>
    public string? ShadeGroup { get; set; }
    public string? Location { get; set; }
    public FabricQualityStatus QualityStatus { get; set; } = FabricQualityStatus.Uninspected;
    public DateTime? QualityStatusAt { get; set; }
    /// <summary>The completed inspection whose result is <see cref="QualityStatus"/>.</summary>
    public Guid? QualityInspectionId { get; set; }
    /// <summary>Set when a measured return found nothing left (or the remnant was written off).</summary>
    public bool IsClosed { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

/// <summary><c>InProgress</c> (defects editable) → <c>Completed</c> (result recorded); reopening a completed one is a correction.</summary>
public enum FabricInspectionStatus { InProgress, Completed }

/// <summary>
/// One inspection of one roll (4-point system). The score and lengths are
/// calculated when it is completed and stored with the rule parameters used,
/// so a later change of plant thresholds never rewrites history.
/// </summary>
public class FabricInspection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-readable, e.g. <c>FI-20261028-1A2B3C4D</c>.</summary>
    public string Reference { get; set; } = default!;
    public Guid RollId { get; set; }
    public FabricInspectionStatus Status { get; set; } = FabricInspectionStatus.InProgress;
    /// <summary>Length actually inspected (may be less than the roll), metres.</summary>
    public double? InspectedLengthM { get; set; }
    public double? MeasuredLengthM { get; set; }
    public double? MeasuredWidthCm { get; set; }
    public string? Notes { get; set; }

    // Result (snapshot at completion)
    public FabricQualityStatus? Result { get; set; }
    /// <summary>What the rules recommended (<c>passed</c> / <c>restricted</c> / <c>rejected</c>).</summary>
    public FabricQualityStatus? Recommendation { get; set; }
    public string? ResultNote { get; set; }
    public int? TotalPoints { get; set; }
    public double? PointsPer100SqYd { get; set; }
    public double? RejectedLengthM { get; set; }
    public double? UsableLengthM { get; set; }
    /// <summary>The rule parameters and per-rule findings used for the result (JSON).</summary>
    public string? ScoreJson { get; set; }
    /// <summary>Times the inspection has been completed (corrections increase it).</summary>
    public int Round { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public Guid? StartedByUserId { get; set; }
    public string StartedByEmail { get; set; } = default!;
    public DateTime? CompletedAt { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public string? CompletedByEmail { get; set; }
    public DateTime? ReopenedAt { get; set; }
    public string? ReopenedByEmail { get; set; }
    public string? ReopenReason { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public uint Version { get; set; }
}

public enum FabricDefectSource { Inspector, Model }

public enum FabricDefectSeverity { Minor, Major, Critical }

/// <summary>The inspector's decision on a defect. Inspector-entered defects are <c>Accepted</c>.</summary>
public enum FabricDefectDecision { Unreviewed, Accepted, Rejected, Corrected }

/// <summary>
/// One defect on a roll. A model defect keeps the provider's label,
/// confidence and image localisation untouched; the decision and
/// corrections are separate columns. Physical position (roll coordinates)
/// is null unless typed by a person or derived from a calibrated image.
/// </summary>
public class FabricDefect
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InspectionId { get; set; }
    public Guid RollId { get; set; }
    public FabricDefectSource Source { get; set; }
    /// <summary>Fabric taxonomy code; null when the provider's label matched none.</summary>
    public string? CategoryCode { get; set; }
    public string? RawLabel { get; set; }
    /// <summary>Provider score in [0,1]; null for inspector defects.</summary>
    public double? Confidence { get; set; }
    public string? Description { get; set; }

    // Image space (fractions of the image, origin top-left)
    public Guid? ImageId { get; set; }
    public double? ImageBoxX { get; set; }
    public double? ImageBoxY { get; set; }
    public double? ImageBoxWidth { get; set; }
    public double? ImageBoxHeight { get; set; }
    public double[]? ImagePolygon { get; set; }

    // Roll space (metres along from the head end, centimetres across from the left selvedge)
    public double? AlongStartM { get; set; }
    public double? AlongLengthCm { get; set; }
    public double? AcrossStartCm { get; set; }
    public double? AcrossWidthCm { get; set; }
    /// <summary><c>manual</c> (typed by a person), <c>image_calibration</c> (derived from the image box), <c>none</c>.</summary>
    public string LocationSource { get; set; } = "none";

    public FabricDefectSeverity Severity { get; set; } = FabricDefectSeverity.Major;
    /// <summary>The region is cut out of the usable fabric (counts as rejected length).</summary>
    public bool CutOut { get; set; }

    public FabricDefectDecision Decision { get; set; } = FabricDefectDecision.Unreviewed;
    public string? CorrectedCategoryCode { get; set; }
    public FabricDefectSeverity? CorrectedSeverity { get; set; }
    public string? ReviewerNote { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedByEmail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedByEmail { get; set; } = default!;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public uint Version { get; set; }
}

public enum FabricImageAnalysisStatus { NotRequested, Processing, Completed, Failed }

/// <summary>
/// A photo / scan of part of a roll. The bytes live in the image store under
/// a server-generated key. The optional frame calibration says which roll
/// region the image shows; without it no defect from this image gets a
/// physical position.
/// </summary>
public class FabricImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InspectionId { get; set; }
    public Guid RollId { get; set; }
    public string StorageKey { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = default!;
    public int WidthPx { get; set; }
    public int HeightPx { get; set; }
    public string? OriginalFileName { get; set; }

    // Calibration: an explicit, user-entered mapping from the image frame to the roll.
    /// <summary><c>length_down</c> (roll length runs down the image) or <c>length_right</c>; null = uncalibrated.</summary>
    public string? Orientation { get; set; }
    public double? FrameAlongStartM { get; set; }
    public double? FrameAlongEndM { get; set; }
    public double? FrameAcrossStartCm { get; set; }
    public double? FrameAcrossEndCm { get; set; }
    public string? CalibrationNote { get; set; }
    public DateTime? CalibratedAt { get; set; }
    public string? CalibratedByEmail { get; set; }

    // Analysis
    public FabricImageAnalysisStatus AnalysisStatus { get; set; } = FabricImageAnalysisStatus.NotRequested;
    public int Attempts { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? AnalyzedAt { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? ModelVersion { get; set; }
    public bool? LocalizationSupported { get; set; }
    public double? ConfidenceThreshold { get; set; }
    public int? InferenceMs { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    /// <summary>Client <c>Idempotency-Key</c>; unique per uploading user.</summary>
    public string? IdempotencyKey { get; set; }
    public uint Version { get; set; }
}

public enum CuttingBatchStatus { Draft, PendingApproval, Released, Completed, Cancelled }

/// <summary>
/// A cutting batch (cut order) for one production order and colour.
/// <c>Draft</c> (markers and rolls edited) → <c>PendingApproval</c> (in the
/// approval queue) → <c>Released</c> (approved, then released by a person;
/// cuts are recorded) → <c>Completed</c> (rolls returned and reconciled).
/// <c>Cancelled</c> from any state before cuts are recorded.
/// </summary>
public class CuttingBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-readable, e.g. <c>CB-20261028-1A2B3C4D</c>.</summary>
    public string Reference { get; set; } = default!;
    public Guid ProductionOrderId { get; set; }
    /// <summary>Garment / fabric colour this batch cuts.</summary>
    public string Color { get; set; } = default!;
    /// <summary>Size requirement (parallel arrays: size, garments).</summary>
    public string[] RequirementSizes { get; set; } = Array.Empty<string>();
    public int[] RequirementQuantities { get; set; } = Array.Empty<int>();
    /// <summary><c>manual</c> or <c>merch_breakdown</c> (copied from the linked buyer order's confirmed breakdown).</summary>
    public string RequirementSource { get; set; } = "manual";
    public Guid? MerchOrderId { get; set; }
    public CuttingBatchStatus Status { get; set; } = CuttingBatchStatus.Draft;
    public string? ApprovalId { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedByEmail { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public string? ReleasedByEmail { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? ClosedByEmail { get; set; }
    public string? ClosedReason { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

/// <summary>
/// A marker (lay plan) of a batch as it comes from the marker maker / CAD:
/// its dimensions, the size ratio it holds and, if reported, its efficiency.
/// BunonBrain does not make markers or nest patterns.
/// </summary>
public class CuttingMarker
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public string Name { get; set; } = default!;
    /// <summary>Marker length, metres.</summary>
    public double LengthM { get; set; }
    /// <summary>Marker (usable) width, centimetres; must fit the cuttable width of the rolls.</summary>
    public double WidthCm { get; set; }
    /// <summary>Marker efficiency reported by the CAD system: pattern area ÷ marker area × 100. Optional.</summary>
    public double? EfficiencyPct { get; set; }
    /// <summary>Planned extra length per ply for both lay ends, centimetres.</summary>
    public double EndAllowanceCm { get; set; }
    public int PlannedPlies { get; set; }
    /// <summary>Size ratio: garments of each size per ply (parallel arrays).</summary>
    public string[] RatioSizes { get; set; } = Array.Empty<string>();
    public int[] RatioQuantities { get; set; } = Array.Empty<int>();
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public uint Version { get; set; }
}

/// <summary>A roll allocated to a batch, with the length planned from it and, at completion, the measured remainder.</summary>
public class CuttingBatchRoll
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public Guid RollId { get; set; }
    public double PlannedLengthM { get; set; }
    /// <summary>Length measured on the roll when the batch was completed (null: not measured).</summary>
    public double? ReturnedLengthM { get; set; }
    public DateTime AllocatedAt { get; set; } = DateTime.UtcNow;
    public string AllocatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

/// <summary><c>Open</c> while it may take effect (its approval decides); <c>Withdrawn</c> / <c>Superseded</c> close it.</summary>
public enum FabricOverrideStatus { Open, Withdrawn, Superseded }

/// <summary>
/// A request to cut a roll that is not passed (uninspected, restricted or
/// rejected) in one batch. It takes effect only when its approval in the
/// existing queue is approved (by someone other than the requester when
/// separation of duties is on) and the roll's quality state is still the
/// one it was requested for.
/// </summary>
public class FabricRollOverride
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RollId { get; set; }
    public Guid BatchId { get; set; }
    public FabricQualityStatus RollStatusAtRequest { get; set; }
    public Guid? InspectionIdAtRequest { get; set; }
    public string Reason { get; set; } = default!;
    public string ApprovalId { get; set; } = default!;
    public FabricOverrideStatus Status { get; set; } = FabricOverrideStatus.Open;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public Guid? RequestedByUserId { get; set; }
    public string RequestedByEmail { get; set; } = default!;
    public DateTime? ClosedAt { get; set; }
    public string? ClosedByEmail { get; set; }
}

public enum FabricMovementKind { Cut, Adjustment }

/// <summary>
/// Material taken off (or put back on the books of) a roll. A cut records
/// the plies spread from one marker and the length that left the roll for
/// them (ply lengths plus end losses) plus any unusable end bit; an
/// adjustment is a signed correction of the book length (e.g. a measured
/// return). Rows are never edited: a correction supersedes the row and
/// inserts a replacement.
/// </summary>
public class FabricRollMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RollId { get; set; }
    public FabricMovementKind Kind { get; set; }
    public Guid? BatchId { get; set; }
    public Guid? MarkerId { get; set; }
    public int? Plies { get; set; }
    /// <summary>Cut: metres that left the roll for the plies (&gt; 0). Adjustment: signed change of the book length.</summary>
    public double LengthM { get; set; }
    /// <summary>Cut only: unusable end piece removed from the roll, metres.</summary>
    public double EndBitM { get; set; }
    public string? Reason { get; set; }
    public string? Notes { get; set; }
    /// <summary>The approved override that allowed cutting a non-passed roll, if one was needed.</summary>
    public Guid? OverrideId { get; set; }
    public Guid? CorrectionOfId { get; set; }
    public bool IsSuperseded { get; set; }
    public Guid? SupersededById { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public Guid? RecordedByUserId { get; set; }
    public string RecordedByEmail { get; set; } = default!;
}
