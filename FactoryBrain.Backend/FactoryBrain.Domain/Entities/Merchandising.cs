namespace FactoryBrain.Domain.Entities;

// Merchandising & document automation. A buyer order (the commercial record a
// merchandiser follows) collects its documents — purchase orders, tech packs,
// measurement sheets, buyer instructions — in versions. Parsed text and the
// values extracted from it are stored next to their source evidence; nothing
// extracted becomes an order value until an authorised person confirms it.
// The order may link to the production order that makes it (Sector 2).

public enum MerchOrderStatus { Active, Shipped, Closed, Cancelled }

/// <summary>
/// One buyer order (a buyer's PO as the merchandiser tracks it). Its
/// commercial fields are the confirmed, authoritative values: each one was
/// typed by a person, confirmed from a document version, or chosen when a
/// conflict was resolved; <see cref="FieldSourcesJson"/> records which.
/// Not a sales ledger and not an ERP record: there is no pricing or invoicing.
/// </summary>
public class MerchOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Human-readable, e.g. <c>MO-20261027-1A2B3C4D</c>.</summary>
    public string Reference { get; set; } = default!;
    public string Buyer { get; set; } = default!;
    /// <summary>Lower-case trimmed buyer, for the per-buyer uniqueness of the PO number.</summary>
    public string BuyerKey { get; set; } = default!;
    /// <summary>The buyer's purchase-order number.</summary>
    public string BuyerOrderNumber { get; set; } = default!;
    public string? StyleRef { get; set; }
    public string? ProductDescription { get; set; }
    public int? Quantity { get; set; }
    /// <summary><c>pcs</c>, <c>dozen</c>, <c>sets</c>, <c>packs</c>.</summary>
    public string? Unit { get; set; }
    /// <summary>Delivery (ex-factory / ship) date agreed with the buyer.</summary>
    public DateOnly? DeliveryDate { get; set; }
    /// <summary>ISO 4217 code, when the PO states a price.</summary>
    public string? Currency { get; set; }
    public decimal? UnitPrice { get; set; }
    public string? Destination { get; set; }
    /// <summary>Confirmed colour × size breakdown, JSON <c>[{color,size,quantity}]</c>; null when none is confirmed.</summary>
    public string? BreakdownJson { get; set; }
    /// <summary>Field key → where the current value came from (JSON object).</summary>
    public string FieldSourcesJson { get; set; } = "{}";
    /// <summary>Document types this order must have (wire names, e.g. <c>purchase_order</c>).</summary>
    public List<string> RequiredDocumentTypes { get; set; } = new();
    /// <summary>Verified link to the Sector 2 production order that makes this order, if any.</summary>
    public Guid? ProductionOrderId { get; set; }
    public MerchOrderStatus Status { get; set; } = MerchOrderStatus.Active;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    /// <summary>PostgreSQL <c>xmin</c>.</summary>
    public uint Version { get; set; }
}

public enum MerchMilestoneStatus { Pending, Done, Waived }

/// <summary>A dated commercial / production milestone of an order (lab dip approval, PP sample, ex-factory…). Overdue is derived: pending past its due date.</summary>
public class MerchMilestone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Name { get; set; } = default!;
    public DateOnly DueDate { get; set; }
    public MerchMilestoneStatus Status { get; set; } = MerchMilestoneStatus.Pending;
    public DateOnly? CompletedOn { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedByEmail { get; set; } = default!;
    public string? UpdatedByEmail { get; set; }
    public uint Version { get; set; }
}

public enum MerchDocumentType { PurchaseOrder, TechPack, MeasurementSheet, BuyerInstruction, Other }

/// <summary>A logical document of an order (e.g. "Purchase order"); each upload is a <see cref="MerchDocumentVersion"/>.</summary>
public class MerchDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public MerchDocumentType Type { get; set; }
    public string Title { get; set; } = default!;
    public Guid? CurrentVersionId { get; set; }
    public int VersionCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string CreatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

/// <summary>
/// <c>Pending</c> (stored, waiting for the worker) → <c>Processing</c> →
/// <c>NeedsReview</c> (text parsed, values extracted or extraction unavailable)
/// or <c>Failed</c> (unreadable, OCR needed, provider error; some retryable) →
/// <c>Confirmed</c> (a person decided every value). Confirmed is final for
/// the version; a change means a new version.
/// </summary>
public enum MerchVersionStatus { Pending, Processing, NeedsReview, Confirmed, Failed }

/// <summary>One uploaded file of a document. The original is kept unchanged in the document store.</summary>
public class MerchDocumentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid OrderId { get; set; }
    public int VersionNumber { get; set; }
    public bool IsCurrent { get; set; } = true;
    public string? ChangeNote { get; set; }

    // File (bytes live in the document store under a server-generated key)
    public string StorageKey { get; set; } = default!;
    /// <summary><c>pdf</c>, <c>txt</c>, <c>csv</c>, <c>docx</c>, <c>image</c>.</summary>
    public string Format { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = default!;
    /// <summary>Client file name, sanitised, display only.</summary>
    public string? OriginalFileName { get; set; }

    // Processing
    public MerchVersionStatus Status { get; set; } = MerchVersionStatus.Pending;
    public int Attempts { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    /// <summary>Why processing failed (<c>malformed_document</c>, <c>ocr_required</c>, <c>provider_timeout</c>…).</summary>
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    public int? PageCount { get; set; }
    public int? TextLength { get; set; }
    public bool TextTruncated { get; set; }
    /// <summary>Set when text was parsed but values were not extracted (e.g. <c>provider_not_configured</c>).</summary>
    public string? ExtractionCode { get; set; }
    public string? ExtractionMessage { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? ModelVersion { get; set; }
    public int? ExtractionMs { get; set; }

    // Review
    public DateTime? ConfirmedAt { get; set; }
    public Guid? ConfirmedByUserId { get; set; }
    public string? ConfirmedByEmail { get; set; }
    public string? ReviewNotes { get; set; }

    // Provenance
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? UploadedByUserId { get; set; }
    public string UploadedByEmail { get; set; } = default!;
    /// <summary>Client <c>Idempotency-Key</c>; unique per uploading user.</summary>
    public string? IdempotencyKey { get; set; }
    public uint Version { get; set; }
}

/// <summary>Parsed text of one page (PDF page; one "page" for text, CSV and DOCX). The evidence extracted values cite.</summary>
public class MerchDocumentPage
{
    public Guid VersionId { get; set; }
    public int PageNumber { get; set; }
    public string Text { get; set; } = default!;
}

/// <summary>Who produced a field value: an AI model, the deterministic label parser, or the reviewer.</summary>
public enum MerchFieldSource { Model, Rules, Reviewer }

/// <summary>The reviewer's decision. <c>Unresolved</c>: the value can't be confirmed from this document (missing information).</summary>
public enum MerchFieldDecision { Unreviewed, Accepted, Corrected, Rejected, Unresolved }

/// <summary>
/// One field of one document version (one row per field key). The extracted
/// value, its evidence and confidence are never modified; the decision and
/// the corrected value are separate columns, so the original is auditable.
/// </summary>
public class MerchExtractedField
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VersionId { get; set; }
    public string FieldKey { get; set; } = default!;
    public MerchFieldSource Source { get; set; }
    /// <summary>The value as the extractor read it, verbatim.</summary>
    public string? RawValue { get; set; }
    /// <summary>Canonical form (ISO date, integer, ISO currency, breakdown JSON); null when it didn't validate.</summary>
    public string? NormalizedValue { get; set; }
    /// <summary>Provider-reported score in [0,1]; null when the extractor reports none.</summary>
    public double? Confidence { get; set; }
    public int? PageNumber { get; set; }
    /// <summary>Quoted source text, only when it was found in the parsed page.</summary>
    public string? Snippet { get; set; }
    /// <summary><c>verified</c> (quote found in the page), <c>not_found</c> (quote not in the document; dropped), <c>none</c>.</summary>
    public string EvidenceStatus { get; set; } = "none";
    /// <summary>Validation problem codes of the extracted value.</summary>
    public string[] Issues { get; set; } = Array.Empty<string>();

    public MerchFieldDecision Decision { get; set; } = MerchFieldDecision.Unreviewed;
    public string? CorrectedValue { get; set; }
    public string? ReviewerNote { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public string? ReviewedByEmail { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary><c>Open</c> → <c>Resolved</c> (a person chose the value) or <c>Cleared</c> (the documents now agree).</summary>
public enum MerchConflictStatus { Open, Resolved, Cleared }

/// <summary>
/// Disagreeing values of one field across an order's current documents,
/// found by explicit comparison rules. At most one open conflict per order
/// and field.
/// </summary>
public class MerchConflict
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string FieldKey { get; set; } = default!;
    public MerchConflictStatus Status { get; set; } = MerchConflictStatus.Open;
    /// <summary>The disagreeing values and where each came from (JSON array).</summary>
    public string ValuesJson { get; set; } = "[]";
    /// <summary>Hash of the compared sources and values; a resolved conflict with the same fingerprint is not reopened.</summary>
    public string Fingerprint { get; set; } = default!;
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public string? ResolvedByEmail { get; set; }
    public string? ResolvedValue { get; set; }
    public string? ResolutionNote { get; set; }
    public uint Version { get; set; }
}
