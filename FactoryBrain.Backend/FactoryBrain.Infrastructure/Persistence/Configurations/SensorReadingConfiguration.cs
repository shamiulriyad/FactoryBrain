using FactoryBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FactoryBrain.Infrastructure.Data.Configurations;

public class SensorReadingConfiguration : IEntityTypeConfiguration<SensorReading>
{
    public void Configure(EntityTypeBuilder<SensorReading> b)
    {
        b.ToTable("sensor_readings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Ts).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.Source, x.EntityId });
        b.HasIndex(x => x.SimTick);
    }
}

public class FloorAlertConfiguration : IEntityTypeConfiguration<FloorAlert>
{
    public void Configure(EntityTypeBuilder<FloorAlert> b)
    {
        b.ToTable("floor_alerts");
        b.HasKey(x => x.Id);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => x.CreatedAt);
    }
}

public class LineBoardMetricConfiguration : IEntityTypeConfiguration<LineBoardMetric>
{
    public void Configure(EntityTypeBuilder<LineBoardMetric> b)
    {
        b.ToTable("line_board_metrics");
        b.HasKey(x => x.Id);
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
    }
}

public class QcDefectConfiguration : IEntityTypeConfiguration<QcDefect>
{
    public void Configure(EntityTypeBuilder<QcDefect> b)
    {
        b.ToTable("qc_defects");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.Operation, x.LineId }).IsUnique();
        b.OwnsMany(x => x.Weeks, wb =>
        {
            wb.ToTable("qc_defect_weeks");
            wb.WithOwner().HasForeignKey("QcDefectId");
            wb.HasKey("WeekStart", "QcDefectId");
        });
    }
}

public class InsightConfiguration : IEntityTypeConfiguration<Insight>
{
    public void Configure(EntityTypeBuilder<Insight> b)
    {
        b.ToTable("insights");
        b.HasKey(x => x.Id);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.OwnsMany(x => x.Factors, fb =>
        {
            fb.ToTable("insight_factors");
            fb.WithOwner().HasForeignKey("InsightId");
        });
        b.OwnsOne(x => x.Recommendation, rb =>
        {
            rb.ToTable("insight_recommendations");
            rb.WithOwner().HasForeignKey("InsightId");
        });
        b.OwnsMany(x => x.Evidence, eb =>
        {
            eb.ToTable("insight_evidence");
            eb.WithOwner().HasForeignKey("InsightId");
            eb.Property(e => e.Filter).HasColumnType("jsonb");
            eb.Property(e => e.PreviewIds).HasColumnType("text[]");
        });
    }
}

public class AgentDefinitionConfiguration : IEntityTypeConfiguration<AgentDefinition>
{
    public void Configure(EntityTypeBuilder<AgentDefinition> b)
    {
        b.ToTable("agents");
        b.HasKey(x => x.Id);
        b.Property(x => x.ContextSlices).HasColumnType("text[]");
    }
}

public class PolicyDocumentConfiguration : IEntityTypeConfiguration<PolicyDocument>
{
    public void Configure(EntityTypeBuilder<PolicyDocument> b)
    {
        b.ToTable("policies");
        b.HasKey(x => x.Id);
        b.Property(x => x.EffectiveDate).HasColumnType("date");
    }
}

public class VisionResultConfiguration : IEntityTypeConfiguration<VisionResult>
{
    public void Configure(EntityTypeBuilder<VisionResult> b)
    {
        b.ToTable("vision_results");
        b.HasKey(x => x.Id);
        b.Property(x => x.RepairStepsEn).HasColumnType("text[]");
        b.Property(x => x.RepairStepsBn).HasColumnType("text[]");
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
    }
}

public class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> b)
    {
        b.ToTable("approvals");
        b.HasKey(x => x.Id);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.DecidedAt).HasColumnType("timestamptz");
        b.Property(x => x.DecidedByEmail).HasMaxLength(256);
        b.Property(x => x.DecisionReason).HasMaxLength(1000);
        b.HasIndex(x => x.Stage);
    }
}

public class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> b)
    {
        b.ToTable("audit_log");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.OccurredAt).HasColumnType("timestamptz");
        b.Property(x => x.ActorEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.ActorRole).HasMaxLength(32).IsRequired();
        b.Property(x => x.Action).HasMaxLength(64).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(128).IsRequired();
        b.Property(x => x.FromState).HasMaxLength(32);
        b.Property(x => x.ToState).HasMaxLength(32);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.TraceId).HasMaxLength(128);
        b.HasIndex(x => new { x.EntityType, x.EntityId });
        b.HasIndex(x => x.OccurredAt);
    }
}

public class AiSettingsRecordConfiguration : IEntityTypeConfiguration<AiSettingsRecord>
{
    public void Configure(EntityTypeBuilder<AiSettingsRecord> b)
    {
        b.ToTable("ai_settings", t => t.HasCheckConstraint("CK_ai_settings_singleton", "\"Id\" = 1"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.LlmProvider).HasMaxLength(32);
        b.Property(x => x.LlmModel).HasMaxLength(128);
        b.Property(x => x.LlmUpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.EmbeddingProvider).HasMaxLength(32);
        b.Property(x => x.EmbeddingModel).HasMaxLength(128);
        b.Property(x => x.EmbeddingUpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedByEmail).HasMaxLength(256);
        b.Property(x => x.ReindexLeaseExpiresAt).HasColumnType("timestamptz");
    }
}

public class OrderRecordConfiguration : IEntityTypeConfiguration<OrderRecord>
{
    public void Configure(EntityTypeBuilder<OrderRecord> b)
    {
        b.ToTable("orders");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Region);
        b.HasIndex(x => x.DaysAgo);
    }
}

public class ProductRecordConfiguration : IEntityTypeConfiguration<ProductRecord>
{
    public void Configure(EntityTypeBuilder<ProductRecord> b)
    {
        b.ToTable("products");
        b.HasKey(x => x.Id);
    }
}

public class CustomerRecordConfiguration : IEntityTypeConfiguration<CustomerRecord>
{
    public void Configure(EntityTypeBuilder<CustomerRecord> b)
    {
        b.ToTable("customers");
        b.HasKey(x => x.Id);
    }
}

public class SupplierRecordConfiguration : IEntityTypeConfiguration<SupplierRecord>
{
    public void Configure(EntityTypeBuilder<SupplierRecord> b)
    {
        b.ToTable("suppliers");
        b.HasKey(x => x.Id);
    }
}

public class InventoryRecordConfiguration : IEntityTypeConfiguration<InventoryRecord>
{
    public void Configure(EntityTypeBuilder<InventoryRecord> b)
    {
        b.ToTable("inventory");
        b.HasKey(x => x.Id);
    }
}

public class ActivityEventConfiguration : IEntityTypeConfiguration<ActivityEvent>
{
    public void Configure(EntityTypeBuilder<ActivityEvent> b)
    {
        b.ToTable("activity_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.IsoDate).HasColumnType("timestamptz");
    }
}
