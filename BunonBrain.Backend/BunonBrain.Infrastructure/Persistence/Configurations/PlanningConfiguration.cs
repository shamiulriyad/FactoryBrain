using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

// Production planning tables. Rules the database itself guarantees:
//   * quantities are positive, date ranges run forwards, names are from the documented sets;
//   * at most one published plan, and at most one plan being prepared, per factory and date;
//   * a target version is never edited into another: one current version per target key,
//     and at most one pending revision;
//   * one open exception per detection key;
//   * every record points at real factories, lines, orders, shifts and devices.
// Status and kind columns are text; xmin is the optimistic-concurrency token.

public class ProductionOrderStatusChangeConfiguration : IEntityTypeConfiguration<ProductionOrderStatusChange>
{
    public void Configure(EntityTypeBuilder<ProductionOrderStatusChange> b)
    {
        b.ToTable("production_order_status_changes");
        b.HasKey(x => x.Id);
        b.Property(x => x.FromStatus).HasMaxLength(16);
        b.Property(x => x.ToStatus).HasMaxLength(16).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ChangedByEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(x => new { x.OrderId, x.ChangedAt });
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionAllocationConfiguration : IEntityTypeConfiguration<ProductionAllocation>
{
    public void Configure(EntityTypeBuilder<ProductionAllocation> b)
    {
        b.ToTable("production_allocations", t =>
        {
            t.HasCheckConstraint("CK_production_allocations_quantity", "\"Quantity\" > 0 AND \"Quantity\" <= 10000000");
            t.HasCheckConstraint("CK_production_allocations_dates", "\"PlannedEnd\" >= \"PlannedStart\"");
            t.HasCheckConstraint("CK_production_allocations_status", "\"Status\" IN ('PLANNED','ACTIVE','COMPLETED','CANCELLED')");
            t.HasCheckConstraint("CK_production_allocations_priority", "\"Priority\" IN ('LOW','NORMAL','HIGH','URGENT')");
            t.HasCheckConstraint("CK_production_allocations_override", "NOT \"OverAllocated\" OR (\"OverrideReason\" IS NOT NULL AND \"OverrideByEmail\" IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.Priority).HasMaxLength(8).IsRequired();
        b.Property(x => x.Status).HasMaxLength(12).IsRequired();
        b.Property(x => x.SupervisorEmail).HasMaxLength(256);
        b.Property(x => x.OverrideReason).HasMaxLength(500);
        b.Property(x => x.OverrideByEmail).HasMaxLength(256);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.OrderId, x.Status });
        b.HasIndex(x => new { x.FactoryId, x.LineId, x.PlannedStart, x.PlannedEnd });
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionAllocationChangeConfiguration : IEntityTypeConfiguration<ProductionAllocationChange>
{
    public void Configure(EntityTypeBuilder<ProductionAllocationChange> b)
    {
        b.ToTable("production_allocation_changes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(16).IsRequired();
        b.Property(x => x.PreviousValues).HasMaxLength(2000);
        b.Property(x => x.NewValues).HasMaxLength(2000).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ChangedByEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(x => new { x.AllocationId, x.ChangedAt });
        b.HasIndex(x => x.OrderId);
        b.HasOne<ProductionAllocation>().WithMany().HasForeignKey(x => x.AllocationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class LineOutputPointConfiguration : IEntityTypeConfiguration<LineOutputPoint>
{
    public const string ActiveIndex = "UX_line_output_points_active";

    public void Configure(EntityTypeBuilder<LineOutputPoint> b)
    {
        b.ToTable("line_output_points");
        b.HasKey(x => x.Id);
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.Note).HasMaxLength(300);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.DeactivatedByEmail).HasMaxLength(256);
        b.HasIndex(x => new { x.LineId, x.DeviceId }).IsUnique().HasFilter("\"IsActive\"").HasDatabaseName(ActiveIndex);
        b.HasIndex(x => x.DeviceId);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class LineCapacitySettingConfiguration : IEntityTypeConfiguration<LineCapacitySetting>
{
    public void Configure(EntityTypeBuilder<LineCapacitySetting> b)
    {
        b.ToTable("line_capacity_settings", t =>
        {
            t.HasCheckConstraint("CK_line_capacity_settings_efficiency", "\"EfficiencyPct\" IS NULL OR (\"EfficiencyPct\" > 0 AND \"EfficiencyPct\" <= 150)");
            t.HasCheckConstraint("CK_line_capacity_settings_operators", "\"PlannedOperators\" IS NULL OR (\"PlannedOperators\" BETWEEN 1 AND 500)");
        });
        b.HasKey(x => x.LineId);
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.UpdatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasOne<ProductionLine>().WithOne().HasForeignKey<LineCapacitySetting>(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionTargetConfiguration : IEntityTypeConfiguration<ProductionTarget>
{
    public const string CurrentIndex = "UX_production_targets_current";
    public const string PendingIndex = "UX_production_targets_pending";

    public void Configure(EntityTypeBuilder<ProductionTarget> b)
    {
        b.ToTable("production_targets", t =>
        {
            t.HasCheckConstraint("CK_production_targets_quantity", "\"Quantity\" > 0 AND \"Quantity\" <= 10000000");
            t.HasCheckConstraint("CK_production_targets_dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
            t.HasCheckConstraint("CK_production_targets_level", "\"Level\" IN ('FACTORY','DEPARTMENT','FLOOR','LINE','SHIFT','WORKSTATION','MACHINE')");
            t.HasCheckConstraint("CK_production_targets_period", "\"Period\" IN ('DAILY','SHIFT','HOURLY')");
            t.HasCheckConstraint("CK_production_targets_approval", "\"ApprovalStatus\" IN ('NOT_REQUIRED','PENDING','APPROVED','REJECTED')");
            t.HasCheckConstraint("CK_production_targets_revision", "\"Revision\" >= 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Level).HasMaxLength(12).IsRequired();
        b.Property(x => x.Period).HasMaxLength(8).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.ApprovalStatus).HasMaxLength(12).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.ChangeReason).HasMaxLength(500);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.DecidedByEmail).HasMaxLength(256);
        b.Property(x => x.DecisionNote).HasMaxLength(500);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => new { x.TargetKey, x.Revision }).IsUnique();
        // One version in force per target; one pending revision per target.
        // Named indexes: two indexes over the same column may only coexist when they are told apart by name.
        b.HasIndex(new[] { nameof(ProductionTarget.TargetKey) }, CurrentIndex).IsUnique().HasFilter("NOT \"IsSuperseded\" AND \"ApprovalStatus\" IN ('NOT_REQUIRED','APPROVED')");
        b.HasIndex(new[] { nameof(ProductionTarget.TargetKey) }, PendingIndex).IsUnique().HasFilter("\"ApprovalStatus\" = 'PENDING'");
        b.HasIndex(x => new { x.FactoryId, x.Level, x.LineId, x.EffectiveFrom });
        b.HasIndex(x => x.OrderId);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFloor>().WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionAllocation>().WithMany().HasForeignKey(x => x.AllocationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionTargetRevisionConfiguration : IEntityTypeConfiguration<ProductionTargetRevision>
{
    public void Configure(EntityTypeBuilder<ProductionTargetRevision> b)
    {
        b.ToTable("production_target_revisions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(12).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ByEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(x => new { x.TargetKey, x.At });
        b.HasIndex(x => x.TargetId);
        b.HasOne<ProductionTarget>().WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class DailyProductionPlanConfiguration : IEntityTypeConfiguration<DailyProductionPlan>
{
    public const string PublishedIndex = "UX_daily_plans_published";
    public const string OpenIndex = "UX_daily_plans_open";

    public void Configure(EntityTypeBuilder<DailyProductionPlan> b)
    {
        b.ToTable("daily_production_plans", t =>
        {
            t.HasCheckConstraint("CK_daily_plans_status", "\"Status\" IN ('DRAFT','UNDER_REVIEW','APPROVED','PUBLISHED','REVISED','CANCELLED')");
            t.HasCheckConstraint("CK_daily_plans_revision", "\"RevisionNo\" >= 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Reference).HasMaxLength(32).IsRequired();
        b.Property(x => x.Status).HasMaxLength(14).IsRequired();
        b.Property(x => x.RevisionReason).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.SubmittedByEmail).HasMaxLength(256);
        b.Property(x => x.ApprovedByEmail).HasMaxLength(256);
        b.Property(x => x.PublishedByEmail).HasMaxLength(256);
        b.Property(x => x.ClosedReason).HasMaxLength(500);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.Reference).IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.PlanDate, x.RevisionNo }).IsUnique();
        b.HasIndex(new[] { nameof(DailyProductionPlan.FactoryId), nameof(DailyProductionPlan.PlanDate) }, PublishedIndex).IsUnique().HasFilter("\"Status\" = 'PUBLISHED'");
        b.HasIndex(new[] { nameof(DailyProductionPlan.FactoryId), nameof(DailyProductionPlan.PlanDate) }, OpenIndex).IsUnique().HasFilter("\"Status\" IN ('DRAFT','UNDER_REVIEW','APPROVED')");
        b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.PlanId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class DailyPlanItemConfiguration : IEntityTypeConfiguration<DailyPlanItem>
{
    public void Configure(EntityTypeBuilder<DailyPlanItem> b)
    {
        b.ToTable("daily_plan_items", t =>
        {
            t.HasCheckConstraint("CK_daily_plan_items_target", "\"TargetQuantity\" > 0 AND \"TargetQuantity\" <= 10000000");
            t.HasCheckConstraint("CK_daily_plan_items_source", "\"TargetSource\" IN ('MANUAL','TARGET')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.TargetSource).HasMaxLength(8).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasIndex(x => new { x.PlanId, x.LineId, x.ShiftId }).IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.PlanDate, x.LineId, x.ShiftId });
        b.HasIndex(x => x.OrderId);
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionAllocation>().WithMany().HasForeignKey(x => x.AllocationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class DailyPlanEventConfiguration : IEntityTypeConfiguration<DailyPlanEvent>
{
    public void Configure(EntityTypeBuilder<DailyPlanEvent> b)
    {
        b.ToTable("daily_plan_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.Details).HasMaxLength(1000);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ByEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(x => new { x.PlanId, x.At });
        b.HasOne<DailyProductionPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionExceptionConfiguration : IEntityTypeConfiguration<ProductionException>
{
    public const string OpenIndex = "UX_production_exceptions_open";

    public void Configure(EntityTypeBuilder<ProductionException> b)
    {
        b.ToTable("production_exceptions", t =>
        {
            t.HasCheckConstraint("CK_production_exceptions_status", "\"Status\" IN ('OPEN','ACKNOWLEDGED','RESOLVED')");
            t.HasCheckConstraint("CK_production_exceptions_severity", "\"Severity\" IN ('LOW','MEDIUM','HIGH')");
            t.HasCheckConstraint("CK_production_exceptions_source", "\"SourceType\" IN ('REAL','SIMULATED')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasMaxLength(32).IsRequired();
        b.Property(x => x.Severity).HasMaxLength(8).IsRequired();
        b.Property(x => x.Status).HasMaxLength(12).IsRequired();
        b.Property(x => x.DedupeKey).HasMaxLength(160).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Title).HasMaxLength(300).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.SupportingData).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.SuggestedInvestigation).HasMaxLength(1000).IsRequired();
        b.Property(x => x.SourceType).HasMaxLength(10).IsRequired();
        b.Property(x => x.AcknowledgedByEmail).HasMaxLength(256);
        b.Property(x => x.ResolvedByEmail).HasMaxLength(256);
        b.Property(x => x.ResolutionNote).HasMaxLength(500);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.DedupeKey).IsUnique().HasFilter("\"Status\" <> 'RESOLVED'").HasDatabaseName(OpenIndex);
        b.HasIndex(x => new { x.FactoryId, x.Status, x.Severity });
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => x.LineId);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
    }
}
