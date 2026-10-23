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
