using FactoryBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FactoryBrain.Infrastructure.Persistence.Configurations;

/// <summary>
/// <c>qc_inspections</c>: one row per submitted image. Enums are stored as
/// text so appended values never renumber existing rows; <c>xmin</c> is the
/// optimistic concurrency token for reviews; images live in the image store
/// and only their server-generated key is stored here.
/// </summary>
public class QcInspectionConfiguration : IEntityTypeConfiguration<QcInspection>
{
    public void Configure(EntityTypeBuilder<QcInspection> b)
    {
        b.ToTable("qc_inspections", t =>
        {
            t.HasCheckConstraint("CK_qc_inspections_attempts", "\"Attempts\" >= 0");
            t.HasCheckConstraint("CK_qc_inspections_image_size", "\"ImageSizeBytes\" > 0");
            t.HasCheckConstraint("CK_qc_inspections_image_dims", "\"ImageWidth\" > 0 AND \"ImageHeight\" > 0");
            t.HasCheckConstraint("CK_qc_inspections_threshold",
                "\"ConfidenceThreshold\" IS NULL OR (\"ConfidenceThreshold\" >= 0 AND \"ConfidenceThreshold\" <= 1)");
            // A reviewed inspection always carries its outcome and reviewer time.
            t.HasCheckConstraint("CK_qc_inspections_review",
                "\"Status\" <> 'Reviewed' OR (\"Outcome\" IS NOT NULL AND \"ReviewedAt\" IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Reference).HasMaxLength(32).IsRequired();
        b.Property(x => x.ProductType).HasMaxLength(64).IsRequired();
        b.Property(x => x.Stage).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.ProductionOrder).HasMaxLength(64);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.RequestedCategoryCodes).HasColumnType("text[]");

        b.Property(x => x.ImageStorageKey).HasMaxLength(128).IsRequired();
        b.Property(x => x.ImageContentType).HasMaxLength(32).IsRequired();
        b.Property(x => x.ImageSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.OriginalFileName).HasMaxLength(255);

        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ProcessingStartedAt).HasColumnType("timestamptz");
        b.Property(x => x.AnalyzedAt).HasColumnType("timestamptz");
        b.Property(x => x.FailureCode).HasMaxLength(64);
        b.Property(x => x.FailureMessage).HasMaxLength(500);
        b.Property(x => x.Provider).HasMaxLength(32);
        b.Property(x => x.Model).HasMaxLength(128);
        b.Property(x => x.ModelVersion).HasMaxLength(128);

        b.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ReviewNotes).HasMaxLength(1000);
        b.Property(x => x.ReviewedAt).HasColumnType("timestamptz");
        b.Property(x => x.ReviewedByEmail).HasMaxLength(256);
        b.Property(x => x.FollowUpApprovalId).HasMaxLength(128);

        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(128);
        b.Property(x => x.Version).IsRowVersion();

        b.HasIndex(x => x.Reference).IsUnique();
        b.HasIndex(x => x.CreatedAt);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => new { x.LineId, x.CreatedAt });
        b.HasIndex(x => new { x.CreatedByUserId, x.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL");

        b.HasMany(x => x.Findings).WithOne().HasForeignKey(f => f.InspectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class QcInspectionFindingConfiguration : IEntityTypeConfiguration<QcInspectionFinding>
{
    public void Configure(EntityTypeBuilder<QcInspectionFinding> b)
    {
        b.ToTable("qc_inspection_findings", t =>
        {
            t.HasCheckConstraint("CK_qc_findings_confidence", "\"Confidence\" IS NULL OR (\"Confidence\" >= 0 AND \"Confidence\" <= 1)");
            t.HasCheckConstraint("CK_qc_findings_box",
                "(\"BoxX\" IS NULL AND \"BoxY\" IS NULL AND \"BoxWidth\" IS NULL AND \"BoxHeight\" IS NULL) OR " +
                "(\"BoxX\" >= 0 AND \"BoxY\" >= 0 AND \"BoxWidth\" > 0 AND \"BoxHeight\" > 0 " +
                "AND \"BoxX\" + \"BoxWidth\" <= 1.0001 AND \"BoxY\" + \"BoxHeight\" <= 1.0001)");
            // Inspector findings have no model score; model findings keep the provider's label.
            t.HasCheckConstraint("CK_qc_findings_source",
                "(\"Source\" = 'Inspector' AND \"Confidence\" IS NULL) OR (\"Source\" = 'Model' AND \"RawLabel\" IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.CategoryCode).HasMaxLength(40);
        b.Property(x => x.RawLabel).HasMaxLength(64);
        b.Property(x => x.Polygon).HasColumnType("double precision[]");
        b.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Decision).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.CorrectedCategoryCode).HasMaxLength(40);
        b.Property(x => x.CorrectedSeverity).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.ReviewerNote).HasMaxLength(500);
        b.Property(x => x.ReviewedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");

        b.HasIndex(x => x.InspectionId);
        b.HasIndex(x => x.CategoryCode);
        b.HasOne<QcDefectCategory>().WithMany().HasForeignKey(x => x.CategoryCode).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<QcDefectCategory>().WithMany().HasForeignKey(x => x.CorrectedCategoryCode).OnDelete(DeleteBehavior.Restrict);
    }
}

public class QcDefectCategoryConfiguration : IEntityTypeConfiguration<QcDefectCategory>
{
    public void Configure(EntityTypeBuilder<QcDefectCategory> b)
    {
        b.ToTable("qc_defect_categories", t =>
            t.HasCheckConstraint("CK_qc_defect_categories_code", "\"Code\" ~ '^[a-z][a-z0-9_]{1,39}$'"));
        b.HasKey(x => x.Code);
        b.Property(x => x.Code).HasMaxLength(40);
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.NameBn).HasMaxLength(80);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.DefaultSeverity).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.Name).IsUnique();
    }
}
