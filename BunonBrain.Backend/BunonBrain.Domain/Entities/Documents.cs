namespace BunonBrain.Domain.Entities;

// Factory documentation automation. One engine for every document type
// (production, HR, store, quality, merchandising, compliance, commercial,
// devices): a document belongs to one factory and one type from the code
// catalog (BunonBrain.Application.Documents.DocumentCatalog), holds its
// structured content in append-only versions, moves through
// DRAFT → SUBMITTED → UNDER_REVIEW → APPROVED / REJECTED (→ ARCHIVED), and
// every step is written to its own append-only event history and to the
// audit log. Uploaded originals live in the private document file store;
// only their keys are stored here.

/// <summary>A factory document (report, form, register or record) of one catalog type.</summary>
public class FactoryDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    /// <summary>Catalog type code, e.g. <c>production.daily</c>.</summary>
    public string TypeCode { get; set; } = default!;
    /// <summary>Catalog category (department), e.g. <c>production</c>. Copied for filtering and access checks.</summary>
    public string Category { get; set; } = default!;
    /// <summary>Human-readable, unique, e.g. <c>DOC-PRD-20261116-1A2B3C</c>.</summary>
    public string Reference { get; set; } = default!;
    public string Title { get; set; } = default!;
    /// <summary>Reporting period (factory-local dates); null for documents without one.</summary>
    public DateOnly? PeriodFrom { get; set; }
    public DateOnly? PeriodTo { get; set; }
    /// <summary>Production line the document is about (line-scoped access), if any.</summary>
    public string? LineId { get; set; }
    public Guid? DepartmentId { get; set; }

    /// <summary>DRAFT, SUBMITTED, UNDER_REVIEW, APPROVED, REJECTED or ARCHIVED.</summary>
    public string Status { get; set; } = "DRAFT";
    /// <summary>LOW, NORMAL or HIGH (high needs <c>documents.approve_high</c>).</summary>
    public string Risk { get; set; } = "NORMAL";
    /// <summary>NONE (filed on submit), SINGLE (one approver) or REVIEW_AND_APPROVE (verified, then approved by someone else). Fixed when submitted.</summary>
    public string ApprovalLevel { get; set; } = "SINGLE";
    /// <summary>FORM, GENERATED, UPLOAD or TEMPLATE: how the first version was made.</summary>
    public string Source { get; set; } = "FORM";
    public int CurrentVersion { get; set; }
    /// <summary>Duplicate guard: one live (not archived) document per factory and key.</summary>
    public string? DedupKey { get; set; }

    // Snapshot of the current version's checks, for lists and the status board.
    public int MissingCount { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }

    /// <summary>Stock was posted from this document when it was approved (store documents); it can no longer be revised.</summary>
    public bool StockPosted { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public DateTimeOffset? SubmittedAt { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public string? SubmittedByEmail { get; set; }
    public DateTimeOffset? ReviewStartedAt { get; set; }
    public string? ReviewStartedByEmail { get; set; }
    /// <summary>Verified by a reviewer (needed before approval at REVIEW_AND_APPROVE).</summary>
    public DateTimeOffset? VerifiedAt { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public string? VerifiedByEmail { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public string? DecidedByEmail { get; set; }
    public string? DecisionNote { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public string? ArchivedByEmail { get; set; }

    /// <summary>Last AI summary / discrepancy explanation (advisory text, never a decision).</summary>
    public string? AiSummary { get; set; }
    public DateTimeOffset? AiSummaryAt { get; set; }
    public string? AiSummaryModel { get; set; }

    /// <summary>PostgreSQL <c>xmin</c>.</summary>
    public uint Version { get; set; }
}

/// <summary>
/// One saved version of a document's content. Append-only: an edit adds a
/// version, nothing is overwritten. <see cref="DataJson"/> holds the field
/// values and table rows; <see cref="SourcesJson"/> says where each value
/// came from (generated from records, typed by a person, suggested by AI and
/// accepted, copied from a template); <see cref="ChecksJson"/> the missing
/// fields, errors and warnings found when it was saved.
/// </summary>
public class FactoryDocumentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public int Number { get; set; }
    public string DataJson { get; set; } = "{}";
    public string SourcesJson { get; set; } = "{}";
    public string ChecksJson { get; set; } = "{}";
    public int MissingCount { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public string ContentSha256 { get; set; } = default!;
    public string? ChangeNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
}

/// <summary>An uploaded original (PDF, DOCX, XLSX, CSV, text or image) attached to a document, with its extraction result.</summary>
public class FactoryDocumentFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; }
    public Guid FactoryId { get; set; }
    public string StorageKey { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    /// <summary>pdf, docx, xlsx, csv, txt or image.</summary>
    public string Format { get; set; } = default!;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = default!;
    public string? OriginalFileName { get; set; }

    /// <summary>NONE, COMPLETED or FAILED.</summary>
    public string ExtractionStatus { get; set; } = "NONE";
    /// <summary>Suggested values (validated against the type's fields), JSON; never applied without a person.</summary>
    public string? ExtractionJson { get; set; }
    /// <summary><c>rules</c> (deterministic) or the AI provider.</summary>
    public string? ExtractionMethod { get; set; }
    public string? ExtractionModel { get; set; }
    public DateTimeOffset? ExtractedAt { get; set; }
    public int ExtractionAttempts { get; set; }
    public string? FailureCode { get; set; }
    public string? FailureMessage { get; set; }
    /// <summary>Document type the classifier suggested for this file, if asked.</summary>
    public string? SuggestedTypeCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedByUserId { get; set; }
    public string CreatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

/// <summary>One step in a document's life (created, saved, submitted, verified, approved, rejected, archived, file added, AI used). Append-only.</summary>
public class FactoryDocumentEvent
{
    public long Id { get; set; }
    public Guid DocumentId { get; set; }
    public Guid FactoryId { get; set; }
    public string Action { get; set; } = default!;
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public int? VersionNumber { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActorUserId { get; set; }
    public string ActorEmail { get; set; } = default!;
}

/// <summary>A factory's settings for one document type: approval level, automatic generation and missing-document tracking.</summary>
public class DocumentTypeSetting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    public string TypeCode { get; set; } = default!;
    /// <summary>NONE, SINGLE or REVIEW_AND_APPROVE.</summary>
    public string ApprovalLevel { get; set; } = "SINGLE";
    /// <summary>Generate a draft automatically each day the source records exist.</summary>
    public bool AutoGenerate { get; set; }
    /// <summary>A document is expected every working day; its absence is reported.</summary>
    public bool ExpectedDaily { get; set; }
    /// <summary>Hours a submitted document may wait for a decision before approvers are reminded.</summary>
    public int DecisionDueHours { get; set; } = 24;
    /// <summary>DAILY (day reports: the previous day), WEEKLY (the previous full week) or MONTHLY (the previous calendar month). Used when <see cref="AutoGenerate"/> is on.</summary>
    public string ScheduleFrequency { get; set; } = "DAILY";
    /// <summary>FACTORY (one document for the factory) or EACH_LINE (one per active line, for types prepared per line).</summary>
    public string ScheduleScope { get; set; } = "FACTORY";
    /// <summary>First day of a WEEKLY period (0 = Sunday … 6 = Saturday; default Saturday, the usual first working day in Bangladesh).</summary>
    public int WeekStartDay { get; set; } = 6;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string UpdatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

/// <summary>
/// One scheduled generation of one document type, period and scope (the factory or one line).
/// The unique key makes a period's document at most once; the row records whether it was
/// created, had no source data yet, already existed, or failed (with the error and the attempts, capped).
/// </summary>
public class DocumentScheduleRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    public string TypeCode { get; set; } = default!;
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    /// <summary><c>*</c> for the whole factory, otherwise the line id.</summary>
    public string LineKey { get; set; } = "*";
    /// <summary>CREATED, EXISTS (made by a person first), NO_DATA (retried while the period is recent), FAILED (retried up to the attempt limit).</summary>
    public string Status { get; set; } = default!;
    public int Attempts { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? DocumentId { get; set; }
    public DateTimeOffset FirstAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public uint Version { get; set; }
}

/// <summary>An in-app notification for one user (pending approval, missing document, draft ready, decision).</summary>
public class UserNotification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? FactoryId { get; set; }
    /// <summary>e.g. <c>document.pending_approval</c>, <c>document.missing</c>, <c>document.rejected</c>.</summary>
    public string Kind { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string Body { get; set; } = default!;
    /// <summary>App path to open, e.g. <c>/app/documents/{id}</c>.</summary>
    public string? Link { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    /// <summary>One notification per user and key (repeated checks don't repeat it).</summary>
    public string? DedupKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>One call (with its retries) to an external AI API. Append-only; never holds prompts, document text or keys.</summary>
public class AiApiUsage
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>document.extract, document.classify, document.summarize, document.draft_text.</summary>
    public string Purpose { get; set; } = default!;
    public string Provider { get; set; } = default!;
    public string Model { get; set; } = default!;
    /// <summary><c>ok</c> or a stable error code (provider_timeout, provider_rate_limited, invalid_output …).</summary>
    public string Outcome { get; set; } = default!;
    public int Attempts { get; set; }
    public int DurationMs { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    /// <summary>Characters of text sent (after field selection and truncation).</summary>
    public int InputChars { get; set; }
    public Guid? FactoryId { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public Guid? UserId { get; set; }
}

/// <summary>A store item of one factory (fabric, accessories, trims, …). Stock is never stored: it is the sum of its movements.</summary>
public class InventoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    public string Code { get; set; } = default!;
    /// <summary>Upper-case trimmed code; unique per factory.</summary>
    public string CodeKey { get; set; } = default!;
    public string Name { get; set; } = default!;
    /// <summary>FABRIC, ACCESSORIES, TRIMS, PACKING, CHEMICALS or OTHER.</summary>
    public string Category { get; set; } = "OTHER";
    public string Unit { get; set; } = default!;
    public decimal? ReorderLevel { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedByEmail { get; set; } = default!;
    public uint Version { get; set; }
}

/// <summary>
/// Stock into (RECEIPT, from an approved GRN) or out of (ISSUE, from an
/// approved material issue note) the store. Append-only; posted once per
/// document line in the approval's transaction.
/// </summary>
public class StockMovement
{
    public long Id { get; set; }
    public Guid FactoryId { get; set; }
    public Guid ItemId { get; set; }
    /// <summary>RECEIPT or ISSUE.</summary>
    public string Kind { get; set; } = default!;
    /// <summary>Positive quantity in the item's unit.</summary>
    public decimal Quantity { get; set; }
    public decimal? UnitCost { get; set; }
    public DateOnly MovementDate { get; set; }
    public Guid DocumentId { get; set; }
    public string DocumentReference { get; set; } = default!;
    /// <summary>Row of the document table the movement came from.</summary>
    public int LineNo { get; set; }
    /// <summary>Supplier (receipt) or receiving department / line (issue).</summary>
    public string? Counterparty { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public string RecordedByEmail { get; set; } = default!;
}

/// <summary>
/// One fabric roll in the store, created when the GRN that received it is approved.
/// Identity: supplier + lot + roll number, unique per factory. Quantity is in the
/// store item's unit. What is left on the roll is never stored: it is the received
/// quantity minus its issues (<see cref="InventoryRollMovement"/>); the status
/// (in stock, partly issued, issued) follows from that.
/// </summary>
public class InventoryRoll
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FactoryId { get; set; }
    public Guid ItemId { get; set; }
    public string RollNumber { get; set; } = default!;
    /// <summary>Upper-case trimmed roll number.</summary>
    public string RollKey { get; set; } = default!;
    public string? LotNumber { get; set; }
    /// <summary>Upper-case trimmed lot number ("" when none).</summary>
    public string LotKey { get; set; } = "";
    public string? Supplier { get; set; }
    /// <summary>Lower-case trimmed supplier ("" when none).</summary>
    public string SupplierKey { get; set; } = "";
    public string? Color { get; set; }
    public string? ShadeGroup { get; set; }
    public decimal? WidthCm { get; set; }
    public string? Warehouse { get; set; }
    public string? Location { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public string Unit { get; set; } = default!;
    public DateOnly ReceivedOn { get; set; }
    public Guid GrnDocumentId { get; set; }
    public string GrnReference { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CreatedByEmail { get; set; } = default!;
}

/// <summary>
/// Roll-level detail of a stock movement: how much of which roll a GRN line received or an
/// issue note line issued. The roll quantities of a movement add up to the movement's quantity.
/// Append-only, posted with the movement in the approval's transaction.
/// </summary>
public class InventoryRollMovement
{
    public long Id { get; set; }
    public Guid FactoryId { get; set; }
    public Guid RollId { get; set; }
    public long StockMovementId { get; set; }
    public Guid DocumentId { get; set; }
    public string DocumentReference { get; set; } = default!;
    /// <summary>Row of the document's roll table.</summary>
    public int RowNo { get; set; }
    /// <summary>RECEIPT or ISSUE.</summary>
    public string Kind { get; set; } = default!;
    public decimal Quantity { get; set; }
    public DateOnly MovementDate { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
    public string RecordedByEmail { get; set; } = default!;
}
