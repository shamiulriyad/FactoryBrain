namespace FactoryBrain.Domain.Entities;

/// <summary>
/// Lifecycle of one inspection:
/// <c>Pending</c> (image stored, waiting for the vision provider) →
/// <c>Processing</c> (claimed by a worker) → <c>Completed</c> (predictions
/// stored) or <c>Failed</c> (provider error / not configured; retryable) →
/// <c>Reviewed</c> (an inspector recorded the verified outcome).
/// </summary>
public enum QcInspectionStatus { Pending, Processing, Completed, Failed, Reviewed }

/// <summary>Where in garment production the piece was inspected.</summary>
public enum QcInspectionStage { Fabric, Cutting, Inline, EndOfLine, Final }

public enum QcDefectSeverity { Minor, Major, Critical }

/// <summary>Who produced a finding: the vision provider or the human inspector.</summary>
public enum QcFindingSource { Model, Inspector }

/// <summary>The inspector's decision on a finding. Inspector-added findings are <c>Accepted</c>.</summary>
public enum QcFindingDecision { Unreviewed, Accepted, Rejected, Corrected }

/// <summary>The verified disposition of the inspected piece.</summary>
public enum QcInspectionOutcome { Pass, Rework, Reject }

/// <summary>
/// One garment / product inspection: the submitted image (by storage
/// reference only, never the bytes), its production context, the vision
/// provider's run metadata and the human review.
/// </summary>
public class QcInspection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-readable reference, e.g. <c>QC-20261026-1A2B3C4D</c>. Unique.</summary>
    public string Reference { get; set; } = default!;

    // Production context
    public string ProductType { get; set; } = default!;
    public QcInspectionStage Stage { get; set; }
    public string? LineId { get; set; }
    public string? ProductionOrder { get; set; }
    public string? Notes { get; set; }
    /// <summary>Defect category codes the provider was asked to look for.</summary>
    public List<string> RequestedCategoryCodes { get; set; } = new();

    // Image (stored by the image store; the key is server-generated)
    public string ImageStorageKey { get; set; } = default!;
    public string ImageContentType { get; set; } = default!;
    public long ImageSizeBytes { get; set; }
    public string ImageSha256 { get; set; } = default!;
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    /// <summary>Client file name, sanitised, for display only. Never used as a path.</summary>
    public string? OriginalFileName { get; set; }

    // Processing
    public QcInspectionStatus Status { get; set; } = QcInspectionStatus.Pending;
    public int Attempts { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? AnalyzedAt { get; set; }
    /// <summary>Stable code, e.g. <c>provider_not_configured</c>, <c>provider_timeout</c>, <c>invalid_output</c>.</summary>
    public string? FailureCode { get; set; }
    /// <summary>Operator-safe message (no keys, no provider bodies).</summary>
    public string? FailureMessage { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? ModelVersion { get; set; }
    public bool? LocalizationSupported { get; set; }
    public double? ConfidenceThreshold { get; set; }
    public int? InferenceMs { get; set; }

    // Review
    public QcInspectionOutcome? Outcome { get; set; }
    public string? ReviewNotes { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string? ReviewedByEmail { get; set; }
    /// <summary>Corrective-action approval raised from the review, if any.</summary>
    public string? FollowUpApprovalId { get; set; }

    // Provenance
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    /// <summary>Client-supplied <c>Idempotency-Key</c>; unique per creating user.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>PostgreSQL <c>xmin</c>: optimistic concurrency token for reviews.</summary>
    public uint Version { get; set; }

    public List<QcInspectionFinding> Findings { get; set; } = new();
}

/// <summary>
/// One defect on an inspection. Model findings keep the provider's original
/// label / confidence / localisation untouched; the inspector's decision and
/// corrections live in separate columns, so the original prediction is
/// always auditable. Coordinates are normalised to [0,1] of the image.
/// </summary>
public class QcInspectionFinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InspectionId { get; set; }
    public QcFindingSource Source { get; set; }

    /// <summary>Mapped taxonomy code; null when the provider's label matched no category.</summary>
    public string? CategoryCode { get; set; }
    /// <summary>The provider's own label, verbatim (model findings only).</summary>
    public string? RawLabel { get; set; }
    /// <summary>Provider-reported score in [0,1]; null for inspector findings.</summary>
    public double? Confidence { get; set; }
    public double? BoxX { get; set; }
    public double? BoxY { get; set; }
    public double? BoxWidth { get; set; }
    public double? BoxHeight { get; set; }
    /// <summary>Optional polygon (mask outline) as flat [x1,y1,x2,y2,…], normalised.</summary>
    public double[]? Polygon { get; set; }
    public QcDefectSeverity Severity { get; set; }
    public string? Description { get; set; }

    public QcFindingDecision Decision { get; set; } = QcFindingDecision.Unreviewed;
    public string? CorrectedCategoryCode { get; set; }
    public QcDefectSeverity? CorrectedSeverity { get; set; }
    public string? ReviewerNote { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A configurable defect category. Seeded with common garment defects on a
/// fresh database; Admins add, rename, re-order or deactivate them. Codes
/// are stable identifiers referenced by findings, so categories are never
/// deleted, only deactivated.
/// </summary>
public class QcDefectCategory
{
    /// <summary>Stable slug, e.g. <c>stitch_skip</c> (lowercase letters, digits, underscores).</summary>
    public string Code { get; set; } = default!;
    public string Name { get; set; } = default!;
    public string? NameBn { get; set; }
    public string? Description { get; set; }
    public QcDefectSeverity DefaultSeverity { get; set; } = QcDefectSeverity.Major;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public uint Version { get; set; }
}
