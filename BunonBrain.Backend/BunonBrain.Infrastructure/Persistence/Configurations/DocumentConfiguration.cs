using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

// Factory documentation automation. Rules the database itself guarantees:
//   * statuses, risks, approval levels and sources are from the documented sets;
//   * one live (not archived) document per factory and duplicate key;
//   * version numbers are unique per document, the same file is attached once;
//   * versions, events, AI usage and stock movements are append-only (triggers in the migration);
//   * a stock movement is posted once per document line, with a positive quantity;
//   * every record points at a real factory (and document / item / user).

public class FactoryDocumentConfiguration : IEntityTypeConfiguration<FactoryDocument>
{
    public void Configure(EntityTypeBuilder<FactoryDocument> b)
    {
        b.ToTable("factory_documents", t =>
        {
            t.HasCheckConstraint("CK_factory_documents_status", "\"Status\" IN ('DRAFT','SUBMITTED','UNDER_REVIEW','APPROVED','REJECTED','ARCHIVED')");
            t.HasCheckConstraint("CK_factory_documents_risk", "\"Risk\" IN ('LOW','NORMAL','HIGH')");
            t.HasCheckConstraint("CK_factory_documents_approval", "\"ApprovalLevel\" IN ('NONE','SINGLE','REVIEW_AND_APPROVE')");
            t.HasCheckConstraint("CK_factory_documents_source", "\"Source\" IN ('FORM','GENERATED','UPLOAD','TEMPLATE')");
            t.HasCheckConstraint("CK_factory_documents_period", "\"PeriodFrom\" IS NULL OR \"PeriodTo\" IS NULL OR \"PeriodTo\" >= \"PeriodFrom\"");
            t.HasCheckConstraint("CK_factory_documents_rejected_reason", "\"Status\" <> 'REJECTED' OR \"DecisionNote\" IS NOT NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.TypeCode).HasMaxLength(64).IsRequired();
        b.Property(x => x.Category).HasMaxLength(24).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(40).IsRequired();
        b.Property(x => x.Title).HasMaxLength(300).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.Risk).HasMaxLength(8).IsRequired();
        b.Property(x => x.ApprovalLevel).HasMaxLength(24).IsRequired();
        b.Property(x => x.Source).HasMaxLength(12).IsRequired();
        b.Property(x => x.DedupKey).HasMaxLength(300);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.SubmittedByEmail).HasMaxLength(256);
        b.Property(x => x.ReviewStartedByEmail).HasMaxLength(256);
        b.Property(x => x.VerifiedByEmail).HasMaxLength(256);
        b.Property(x => x.DecidedByEmail).HasMaxLength(256);
        b.Property(x => x.DecisionNote).HasMaxLength(1000);
        b.Property(x => x.ArchivedByEmail).HasMaxLength(256);
        b.Property(x => x.AiSummary).HasMaxLength(4000);
        b.Property(x => x.AiSummaryModel).HasMaxLength(120);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.Reference).IsUnique();
        b.HasIndex(new[] { nameof(FactoryDocument.FactoryId), nameof(FactoryDocument.DedupKey) }, "UX_factory_documents_live_dedup")
            .IsUnique().HasFilter("\"DedupKey\" IS NOT NULL AND \"Status\" <> 'ARCHIVED'");
        b.HasIndex(x => new { x.FactoryId, x.Category, x.Status });
        b.HasIndex(x => new { x.FactoryId, x.TypeCode, x.PeriodFrom });
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class FactoryDocumentVersionConfiguration : IEntityTypeConfiguration<FactoryDocumentVersion>
{
    public void Configure(EntityTypeBuilder<FactoryDocumentVersion> b)
    {
        b.ToTable("factory_document_versions", t => t.HasCheckConstraint("CK_factory_document_versions_number", "\"Number\" >= 1"));
        b.HasKey(x => x.Id);
        b.Property(x => x.DataJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.SourcesJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ChecksJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ContentSha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.ChangeNote).HasMaxLength(500);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(x => new { x.DocumentId, x.Number }).IsUnique();
        b.HasOne<FactoryDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class FactoryDocumentFileConfiguration : IEntityTypeConfiguration<FactoryDocumentFile>
{
    public void Configure(EntityTypeBuilder<FactoryDocumentFile> b)
    {
        b.ToTable("factory_document_files", t =>
        {
            t.HasCheckConstraint("CK_factory_document_files_format", "\"Format\" IN ('pdf','docx','xlsx','csv','txt','image')");
            t.HasCheckConstraint("CK_factory_document_files_extraction", "\"ExtractionStatus\" IN ('NONE','COMPLETED','FAILED')");
            t.HasCheckConstraint("CK_factory_document_files_size", "\"SizeBytes\" > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.StorageKey).HasMaxLength(80).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Format).HasMaxLength(8).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        b.Property(x => x.OriginalFileName).HasMaxLength(255);
        b.Property(x => x.ExtractionStatus).HasMaxLength(12).IsRequired();
        b.Property(x => x.ExtractionJson).HasColumnType("jsonb");
        b.Property(x => x.ExtractionMethod).HasMaxLength(32);
        b.Property(x => x.ExtractionModel).HasMaxLength(120);
        b.Property(x => x.FailureCode).HasMaxLength(40);
        b.Property(x => x.FailureMessage).HasMaxLength(500);
        b.Property(x => x.SuggestedTypeCode).HasMaxLength(64);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.DocumentId, x.Sha256 }).IsUnique();
        b.HasOne<FactoryDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class FactoryDocumentEventConfiguration : IEntityTypeConfiguration<FactoryDocumentEvent>
{
    public void Configure(EntityTypeBuilder<FactoryDocumentEvent> b)
    {
        b.ToTable("factory_document_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Action).HasMaxLength(40).IsRequired();
        b.Property(x => x.FromStatus).HasMaxLength(16);
        b.Property(x => x.ToStatus).HasMaxLength(16);
        b.Property(x => x.Note).HasMaxLength(1000);
        b.Property(x => x.ActorEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(x => new { x.DocumentId, x.OccurredAt });
        b.HasIndex(x => new { x.FactoryId, x.OccurredAt });
        b.HasOne<FactoryDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class DocumentTypeSettingConfiguration : IEntityTypeConfiguration<DocumentTypeSetting>
{
    public void Configure(EntityTypeBuilder<DocumentTypeSetting> b)
    {
        b.ToTable("document_type_settings", t =>
        {
            t.HasCheckConstraint("CK_document_type_settings_approval", "\"ApprovalLevel\" IN ('NONE','SINGLE','REVIEW_AND_APPROVE')");
            t.HasCheckConstraint("CK_document_type_settings_due", "\"DecisionDueHours\" BETWEEN 1 AND 720");
            t.HasCheckConstraint("CK_document_type_settings_frequency", "\"ScheduleFrequency\" IN ('DAILY','WEEKLY','MONTHLY')");
            t.HasCheckConstraint("CK_document_type_settings_scope", "\"ScheduleScope\" IN ('FACTORY','EACH_LINE')");
            t.HasCheckConstraint("CK_document_type_settings_week_start", "\"WeekStartDay\" BETWEEN 0 AND 6");
        });
        b.Property(x => x.ScheduleFrequency).HasMaxLength(8).IsRequired().HasDefaultValue("DAILY");
        b.Property(x => x.ScheduleScope).HasMaxLength(10).IsRequired().HasDefaultValue("FACTORY");
        b.Property(x => x.WeekStartDay).HasDefaultValue(6);
        b.HasKey(x => x.Id);
        b.Property(x => x.TypeCode).HasMaxLength(64).IsRequired();
        b.Property(x => x.ApprovalLevel).HasMaxLength(24).IsRequired();
        b.Property(x => x.UpdatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.FactoryId, x.TypeCode }).IsUnique();
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
    public void Configure(EntityTypeBuilder<UserNotification> b)
    {
        b.ToTable("user_notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(48).IsRequired();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Body).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Link).HasMaxLength(300);
        b.Property(x => x.EntityType).HasMaxLength(48);
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.Property(x => x.DedupKey).HasMaxLength(300);
        b.HasIndex(new[] { nameof(UserNotification.UserId), nameof(UserNotification.DedupKey) }, "UX_user_notifications_dedup")
            .IsUnique().HasFilter("\"DedupKey\" IS NOT NULL");
        b.HasIndex(x => new { x.UserId, x.ReadAt, x.CreatedAt });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AiApiUsageConfiguration : IEntityTypeConfiguration<AiApiUsage>
{
    public void Configure(EntityTypeBuilder<AiApiUsage> b)
    {
        b.ToTable("ai_api_usage", t => t.HasCheckConstraint("CK_ai_api_usage_attempts", "\"Attempts\" >= 1"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Purpose).HasMaxLength(40).IsRequired();
        b.Property(x => x.Provider).HasMaxLength(24).IsRequired();
        b.Property(x => x.Model).HasMaxLength(120).IsRequired();
        b.Property(x => x.Outcome).HasMaxLength(40).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(48);
        b.Property(x => x.EntityId).HasMaxLength(64);
        b.HasIndex(x => x.OccurredAt);
        b.HasIndex(x => new { x.FactoryId, x.OccurredAt });
    }
}

public class InventoryItemConfiguration : IEntityTypeConfiguration<InventoryItem>
{
    public void Configure(EntityTypeBuilder<InventoryItem> b)
    {
        b.ToTable("inventory_items", t =>
        {
            t.HasCheckConstraint("CK_inventory_items_category", "\"Category\" IN ('FABRIC','ACCESSORIES','TRIMS','PACKING','CHEMICALS','OTHER')");
            t.HasCheckConstraint("CK_inventory_items_reorder", "\"ReorderLevel\" IS NULL OR \"ReorderLevel\" >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.CodeKey).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Category).HasMaxLength(16).IsRequired();
        b.Property(x => x.Unit).HasMaxLength(16).IsRequired();
        b.Property(x => x.ReorderLevel).HasPrecision(18, 3);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.FactoryId, x.CodeKey }).IsUnique();
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.ToTable("stock_movements", t =>
        {
            t.HasCheckConstraint("CK_stock_movements_kind", "\"Kind\" IN ('RECEIPT','ISSUE')");
            t.HasCheckConstraint("CK_stock_movements_quantity", "\"Quantity\" > 0");
            t.HasCheckConstraint("CK_stock_movements_cost", "\"UnitCost\" IS NULL OR \"UnitCost\" >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Kind).HasMaxLength(8).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.UnitCost).HasPrecision(18, 4);
        b.Property(x => x.DocumentReference).HasMaxLength(40).IsRequired();
        b.Property(x => x.Counterparty).HasMaxLength(200);
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(x => new { x.DocumentId, x.LineNo }).IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.ItemId, x.MovementDate });
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FactoryDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class InventoryRollConfiguration : IEntityTypeConfiguration<InventoryRoll>
{
    public void Configure(EntityTypeBuilder<InventoryRoll> b)
    {
        b.ToTable("inventory_rolls", t =>
        {
            t.HasCheckConstraint("CK_inventory_rolls_quantity", "\"ReceivedQuantity\" > 0");
            t.HasCheckConstraint("CK_inventory_rolls_width", "\"WidthCm\" IS NULL OR \"WidthCm\" > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.RollNumber).HasMaxLength(60).IsRequired();
        b.Property(x => x.RollKey).HasMaxLength(60).IsRequired();
        b.Property(x => x.LotNumber).HasMaxLength(60);
        b.Property(x => x.LotKey).HasMaxLength(60).IsRequired();
        b.Property(x => x.Supplier).HasMaxLength(200);
        b.Property(x => x.SupplierKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.Color).HasMaxLength(80);
        b.Property(x => x.ShadeGroup).HasMaxLength(20);
        b.Property(x => x.WidthCm).HasPrecision(8, 2);
        b.Property(x => x.Warehouse).HasMaxLength(80);
        b.Property(x => x.Location).HasMaxLength(80);
        b.Property(x => x.ReceivedQuantity).HasPrecision(18, 3);
        b.Property(x => x.Unit).HasMaxLength(16).IsRequired();
        b.Property(x => x.GrnReference).HasMaxLength(40).IsRequired();
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        // One roll per supplier, lot and roll number in a factory: the same roll can't be received twice.
        b.HasIndex(new[] { nameof(InventoryRoll.FactoryId), nameof(InventoryRoll.SupplierKey), nameof(InventoryRoll.LotKey), nameof(InventoryRoll.RollKey) }, "UX_inventory_rolls_identity").IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.ItemId });
        b.HasIndex(x => x.GrnDocumentId);
        b.HasOne<InventoryItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FactoryDocument>().WithMany().HasForeignKey(x => x.GrnDocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class InventoryRollMovementConfiguration : IEntityTypeConfiguration<InventoryRollMovement>
{
    public void Configure(EntityTypeBuilder<InventoryRollMovement> b)
    {
        b.ToTable("inventory_roll_movements", t =>
        {
            t.HasCheckConstraint("CK_inventory_roll_movements_kind", "\"Kind\" IN ('RECEIPT','ISSUE')");
            t.HasCheckConstraint("CK_inventory_roll_movements_quantity", "\"Quantity\" > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Kind).HasMaxLength(8).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.DocumentReference).HasMaxLength(40).IsRequired();
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(new[] { nameof(InventoryRollMovement.DocumentId), nameof(InventoryRollMovement.RowNo) }, "UX_inventory_roll_movements_document_row").IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.RollId });
        b.HasIndex(x => x.StockMovementId);
        b.HasOne<InventoryRoll>().WithMany().HasForeignKey(x => x.RollId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockMovement>().WithMany().HasForeignKey(x => x.StockMovementId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FactoryDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class DocumentScheduleRunConfiguration : IEntityTypeConfiguration<DocumentScheduleRun>
{
    public void Configure(EntityTypeBuilder<DocumentScheduleRun> b)
    {
        b.ToTable("document_schedule_runs", t =>
        {
            t.HasCheckConstraint("CK_document_schedule_runs_status", "\"Status\" IN ('CREATED','EXISTS','NO_DATA','FAILED')");
            t.HasCheckConstraint("CK_document_schedule_runs_period", "\"PeriodTo\" >= \"PeriodFrom\"");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.TypeCode).HasMaxLength(64).IsRequired();
        b.Property(x => x.LineKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.Status).HasMaxLength(10).IsRequired();
        b.Property(x => x.ErrorCode).HasMaxLength(64);
        b.Property(x => x.ErrorMessage).HasMaxLength(500);
        b.Property(x => x.Version).IsRowVersion();
        // A type's period is generated at most once per factory and scope.
        b.HasIndex(new[] { nameof(DocumentScheduleRun.FactoryId), nameof(DocumentScheduleRun.TypeCode), nameof(DocumentScheduleRun.PeriodFrom),
            nameof(DocumentScheduleRun.PeriodTo), nameof(DocumentScheduleRun.LineKey) }, "UX_document_schedule_runs_period").IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.LastAttemptAt });
        b.HasOne<FactoryDocument>().WithMany().HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class FactoryHolidayConfiguration : IEntityTypeConfiguration<FactoryHoliday>
{
    public void Configure(EntityTypeBuilder<FactoryHoliday> b)
    {
        b.ToTable("factory_holidays", t => t.HasCheckConstraint("CK_factory_holidays_kind", "\"Kind\" IN ('PUBLIC','FACTORY')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(8).IsRequired();
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(new[] { nameof(FactoryHoliday.FactoryId), nameof(FactoryHoliday.Date) }, "UX_factory_holidays_day").IsUnique();
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
