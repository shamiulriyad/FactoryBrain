using FactoryBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FactoryBrain.Infrastructure.Persistence.Configurations;

// Merchandising tables. Enums are stored as text so appended values never
// renumber existing rows; xmin is the optimistic concurrency token of every
// row people edit; document bytes live in the document store and only the
// server-generated key is stored here.

public class MerchOrderConfiguration : IEntityTypeConfiguration<MerchOrder>
{
    public void Configure(EntityTypeBuilder<MerchOrder> b)
    {
        b.ToTable("merch_orders", t =>
        {
            t.HasCheckConstraint("CK_merch_orders_quantity", "\"Quantity\" IS NULL OR (\"Quantity\" >= 1 AND \"Quantity\" <= 10000000)");
            t.HasCheckConstraint("CK_merch_orders_unit_price", "\"UnitPrice\" IS NULL OR \"UnitPrice\" > 0");
            t.HasCheckConstraint("CK_merch_orders_currency", "\"Currency\" IS NULL OR \"Currency\" ~ '^[A-Z]{3}$'");
            t.HasCheckConstraint("CK_merch_orders_unit", "\"Unit\" IS NULL OR \"Unit\" IN ('pcs','dozen','sets','packs','pairs')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Reference).HasMaxLength(32).IsRequired();
        b.Property(x => x.Buyer).HasMaxLength(120).IsRequired();
        b.Property(x => x.BuyerKey).HasMaxLength(120).IsRequired();
        b.Property(x => x.BuyerOrderNumber).HasMaxLength(64).IsRequired();
        b.Property(x => x.StyleRef).HasMaxLength(64);
        b.Property(x => x.ProductDescription).HasMaxLength(200);
        b.Property(x => x.Unit).HasMaxLength(16);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.UnitPrice).HasPrecision(14, 4);
        b.Property(x => x.Destination).HasMaxLength(120);
        b.Property(x => x.BreakdownJson).HasColumnType("jsonb");
        b.Property(x => x.FieldSourcesJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.RequiredDocumentTypes).HasColumnType("text[]");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();

        b.HasIndex(x => x.Reference).IsUnique();
        b.HasIndex(x => new { x.BuyerKey, x.BuyerOrderNumber }).IsUnique();
        b.HasIndex(x => new { x.Status, x.DeliveryDate });
        b.HasIndex(x => x.ProductionOrderId);
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.ProductionOrderId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class MerchMilestoneConfiguration : IEntityTypeConfiguration<MerchMilestone>
{
    public void Configure(EntityTypeBuilder<MerchMilestone> b)
    {
        b.ToTable("merch_milestones", t =>
            t.HasCheckConstraint("CK_merch_milestones_done", "\"Status\" <> 'Done' OR \"CompletedOn\" IS NOT NULL"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.UpdatedByEmail).HasMaxLength(256);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.OrderId, x.DueDate });
        b.HasIndex(x => new { x.Status, x.DueDate });
        b.HasOne<MerchOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class MerchDocumentConfiguration : IEntityTypeConfiguration<MerchDocument>
{
    public void Configure(EntityTypeBuilder<MerchDocument> b)
    {
        b.ToTable("merch_documents", t => t.HasCheckConstraint("CK_merch_documents_versions", "\"VersionCount\" >= 0"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.Title).HasMaxLength(160).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.OrderId, x.Type });
        b.HasOne<MerchOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class MerchDocumentVersionConfiguration : IEntityTypeConfiguration<MerchDocumentVersion>
{
    public void Configure(EntityTypeBuilder<MerchDocumentVersion> b)
    {
        b.ToTable("merch_document_versions", t =>
        {
            t.HasCheckConstraint("CK_merch_versions_size", "\"SizeBytes\" > 0");
            t.HasCheckConstraint("CK_merch_versions_attempts", "\"Attempts\" >= 0");
            t.HasCheckConstraint("CK_merch_versions_number", "\"VersionNumber\" >= 1");
            t.HasCheckConstraint("CK_merch_versions_format", "\"Format\" IN ('pdf','txt','csv','docx','image')");
            // A confirmed version always records who confirmed it and when.
            t.HasCheckConstraint("CK_merch_versions_confirmed",
                "\"Status\" <> 'Confirmed' OR (\"ConfirmedAt\" IS NOT NULL AND \"ConfirmedByEmail\" IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.ChangeNote).HasMaxLength(500);
        b.Property(x => x.StorageKey).HasMaxLength(128).IsRequired();
        b.Property(x => x.Format).HasMaxLength(8).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.OriginalFileName).HasMaxLength(255);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ProcessingStartedAt).HasColumnType("timestamptz");
        b.Property(x => x.ProcessedAt).HasColumnType("timestamptz");
        b.Property(x => x.FailureCode).HasMaxLength(64);
        b.Property(x => x.FailureMessage).HasMaxLength(500);
        b.Property(x => x.ExtractionCode).HasMaxLength(64);
        b.Property(x => x.ExtractionMessage).HasMaxLength(500);
        b.Property(x => x.Provider).HasMaxLength(32);
        b.Property(x => x.Model).HasMaxLength(128);
        b.Property(x => x.ModelVersion).HasMaxLength(128);
        b.Property(x => x.ConfirmedAt).HasColumnType("timestamptz");
        b.Property(x => x.ConfirmedByEmail).HasMaxLength(256);
        b.Property(x => x.ReviewNotes).HasMaxLength(1000);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UploadedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(128);
        b.Property(x => x.Version).IsRowVersion();

        b.HasIndex(x => new { x.DocumentId, x.VersionNumber }).IsUnique();
        // Exactly one current version per document.
        b.HasIndex(x => x.DocumentId).IsUnique().HasFilter("\"IsCurrent\"").HasDatabaseName("IX_merch_document_versions_current");
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => new { x.OrderId, x.IsCurrent });
        b.HasIndex(x => x.Sha256);
        b.HasIndex(x => new { x.UploadedByUserId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
        b.HasOne<MerchDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class MerchDocumentPageConfiguration : IEntityTypeConfiguration<MerchDocumentPage>
{
    public void Configure(EntityTypeBuilder<MerchDocumentPage> b)
    {
        b.ToTable("merch_document_pages", t => t.HasCheckConstraint("CK_merch_pages_number", "\"PageNumber\" >= 1"));
        b.HasKey(x => new { x.VersionId, x.PageNumber });
        b.Property(x => x.Text).IsRequired();
        b.HasOne<MerchDocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class MerchExtractedFieldConfiguration : IEntityTypeConfiguration<MerchExtractedField>
{
    public void Configure(EntityTypeBuilder<MerchExtractedField> b)
    {
        b.ToTable("merch_extracted_fields", t =>
        {
            t.HasCheckConstraint("CK_merch_fields_confidence", "\"Confidence\" IS NULL OR (\"Confidence\" >= 0 AND \"Confidence\" <= 1)");
            t.HasCheckConstraint("CK_merch_fields_evidence", "\"EvidenceStatus\" IN ('verified','not_found','none')");
            // A quote is stored only when it was found in the document.
            t.HasCheckConstraint("CK_merch_fields_snippet", "\"Snippet\" IS NULL OR \"EvidenceStatus\" = 'verified'");
            // Reviewer-entered values carry no model confidence.
            t.HasCheckConstraint("CK_merch_fields_reviewer", "\"Source\" <> 'Reviewer' OR \"Confidence\" IS NULL");
            t.HasCheckConstraint("CK_merch_fields_corrected", "\"Decision\" <> 'Corrected' OR \"CorrectedValue\" IS NOT NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.FieldKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.RawValue).HasMaxLength(4000);
        b.Property(x => x.NormalizedValue).HasMaxLength(200_000);
        b.Property(x => x.Snippet).HasMaxLength(600);
        b.Property(x => x.EvidenceStatus).HasMaxLength(16).IsRequired();
        b.Property(x => x.Issues).HasColumnType("text[]");
        b.Property(x => x.Decision).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.CorrectedValue).HasMaxLength(200_000);
        b.Property(x => x.ReviewerNote).HasMaxLength(500);
        b.Property(x => x.ReviewedAt).HasColumnType("timestamptz");
        b.Property(x => x.ReviewedByEmail).HasMaxLength(256);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        // One row per field of a version: re-processing replaces, never duplicates.
        b.HasIndex(x => new { x.VersionId, x.FieldKey }).IsUnique();
        b.HasOne<MerchDocumentVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class MerchConflictConfiguration : IEntityTypeConfiguration<MerchConflict>
{
    public void Configure(EntityTypeBuilder<MerchConflict> b)
    {
        b.ToTable("merch_conflicts", t =>
            t.HasCheckConstraint("CK_merch_conflicts_resolved",
                "\"Status\" <> 'Resolved' OR (\"ResolvedAt\" IS NOT NULL AND \"ResolvedValue\" IS NOT NULL AND \"ResolvedByEmail\" IS NOT NULL)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.FieldKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ValuesJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        b.Property(x => x.DetectedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.ResolvedAt).HasColumnType("timestamptz");
        b.Property(x => x.ResolvedByEmail).HasMaxLength(256);
        b.Property(x => x.ResolvedValue).HasMaxLength(200_000);
        b.Property(x => x.ResolutionNote).HasMaxLength(1000);
        b.Property(x => x.Version).IsRowVersion();
        // At most one open conflict per order and field.
        b.HasIndex(x => new { x.OrderId, x.FieldKey }).IsUnique().HasFilter("\"Status\" = 'Open'").HasDatabaseName("IX_merch_conflicts_open");
        b.HasIndex(x => new { x.OrderId, x.Status });
        b.HasIndex(x => new { x.Status, x.DetectedAt });
        b.HasOne<MerchOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}
