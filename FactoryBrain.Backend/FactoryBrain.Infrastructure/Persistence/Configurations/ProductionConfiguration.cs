using FactoryBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FactoryBrain.Infrastructure.Persistence.Configurations;

// Production planning tables. Enums are stored as text (appending a value
// never renumbers rows); xmin is the optimistic concurrency token where
// concurrent edits matter; check constraints keep quantities, times and
// percentages valid at the database level as well as in the services.

public class ProductionLineConfiguration : IEntityTypeConfiguration<ProductionLine>
{
    public void Configure(EntityTypeBuilder<ProductionLine> b)
    {
        b.ToTable("production_lines", t =>
        {
            t.HasCheckConstraint("CK_production_lines_id", "\"Id\" ~ '^[a-z0-9][a-z0-9_-]{0,31}$'");
            t.HasCheckConstraint("CK_production_lines_workstations", "\"Workstations\" BETWEEN 1 AND 300");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.Name).IsUnique();
    }
}

public class ProductionShiftConfiguration : IEntityTypeConfiguration<ProductionShift>
{
    public void Configure(EntityTypeBuilder<ProductionShift> b)
    {
        b.ToTable("production_shifts", t =>
        {
            t.HasCheckConstraint("CK_production_shifts_code", "\"Code\" ~ '^[a-z0-9][a-z0-9_-]{0,15}$'");
            t.HasCheckConstraint("CK_production_shifts_break", "\"BreakMinutes\" >= 0 AND \"BreakMinutes\" <= 240");
            t.HasCheckConstraint("CK_production_shifts_weekdays", "\"Weekdays\" BETWEEN 1 AND 127");
            t.HasCheckConstraint("CK_production_shifts_times", "\"StartLocal\" <> \"EndLocal\"");
        });
        b.HasKey(x => x.Code);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.Name).IsUnique();
    }
}

public class ProductionStyleConfiguration : IEntityTypeConfiguration<ProductionStyle>
{
    public void Configure(EntityTypeBuilder<ProductionStyle> b)
    {
        b.ToTable("production_styles", t =>
            t.HasCheckConstraint("CK_production_styles_smv", "\"ApprovedSmv\" IS NULL OR (\"ApprovedSmv\" > 0 AND \"ApprovedSmv\" <= 600)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(40).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.ProductType).HasMaxLength(64);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.Code).IsUnique();
        b.HasMany(x => x.Operations).WithOne().HasForeignKey(o => o.StyleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class StyleOperationConfiguration : IEntityTypeConfiguration<StyleOperation>
{
    public void Configure(EntityTypeBuilder<StyleOperation> b)
    {
        b.ToTable("production_style_operations", t =>
        {
            t.HasCheckConstraint("CK_style_operations_smv", "\"Smv\" > 0 AND \"Smv\" <= 120");
            t.HasCheckConstraint("CK_style_operations_sequence", "\"Sequence\" BETWEEN 1 AND 9999");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.MachineType).HasMaxLength(32);
        b.Property(x => x.Predecessors).HasColumnType("integer[]");
        b.HasIndex(x => new { x.StyleId, x.Sequence }).IsUnique();
    }
}

public class ProductionOrderConfiguration : IEntityTypeConfiguration<ProductionOrder>
{
    public void Configure(EntityTypeBuilder<ProductionOrder> b)
    {
        b.ToTable("production_orders", t => t.HasCheckConstraint("CK_production_orders_quantity", "\"Quantity\" > 0 AND \"Quantity\" <= 10000000"));
        b.HasKey(x => x.Id);
        b.Property(x => x.OrderNumber).HasMaxLength(64).IsRequired();
        b.Property(x => x.Buyer).HasMaxLength(120);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.OrderNumber).IsUnique();
        b.HasIndex(x => new { x.Status, x.DueDate });
        b.HasOne<ProductionStyle>().WithMany().HasForeignKey(x => x.StyleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionPlanConfiguration : IEntityTypeConfiguration<ProductionPlan>
{
    public void Configure(EntityTypeBuilder<ProductionPlan> b)
    {
        b.ToTable("production_plans", t =>
        {
            t.HasCheckConstraint("CK_production_plans_dates", "\"EndDate\" >= \"StartDate\"");
            t.HasCheckConstraint("CK_production_plans_quantity", "\"PlannedQuantity\" > 0");
            t.HasCheckConstraint("CK_production_plans_operators", "\"Operators\" BETWEEN 1 AND 500");
            t.HasCheckConstraint("CK_production_plans_efficiency", "\"TargetEfficiencyPct\" > 0 AND \"TargetEfficiencyPct\" <= 150");
            t.HasCheckConstraint("CK_production_plans_overtime", "\"OvertimeMinutesPerDay\" BETWEEN 0 AND 480");
            t.HasCheckConstraint("CK_production_plans_shifts", "cardinality(\"ShiftCodes\") > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Reference).HasMaxLength(32).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.ShiftCodes).HasColumnType("text[]");
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ActivationApprovalId).HasMaxLength(128);
        b.Property(x => x.SubmittedByEmail).HasMaxLength(256);
        b.Property(x => x.SubmittedAt).HasColumnType("timestamptz");
        b.Property(x => x.ActivatedAt).HasColumnType("timestamptz");
        b.Property(x => x.ActivatedByEmail).HasMaxLength(256);
        b.Property(x => x.ClosedAt).HasColumnType("timestamptz");
        b.Property(x => x.ClosedReason).HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.Reference).IsUnique();
        b.HasIndex(x => new { x.LineId, x.Status, x.StartDate, x.EndDate });
        b.HasIndex(x => new { x.OrderId, x.Status });
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionPlanChangeConfiguration : IEntityTypeConfiguration<ProductionPlanChange>
{
    public void Configure(EntityTypeBuilder<ProductionPlanChange> b)
    {
        b.ToTable("production_plan_changes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(16).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.PreviousValues).HasColumnType("jsonb");
        b.Property(x => x.ProposedValues).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ApprovalId).HasMaxLength(128);
        b.Property(x => x.RequestedAt).HasColumnType("timestamptz");
        b.Property(x => x.RequestedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.DecidedAt).HasColumnType("timestamptz");
        b.Property(x => x.DecidedByEmail).HasMaxLength(256);
        b.HasIndex(x => new { x.PlanId, x.RequestedAt });
        // At most one pending change per plan.
        b.HasIndex(x => x.PlanId).IsUnique().HasFilter("\"Status\" = 'Pending'").HasDatabaseName("IX_production_plan_changes_one_pending");
        b.HasOne<ProductionPlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ProductionOutputEventConfiguration : IEntityTypeConfiguration<ProductionOutputEvent>
{
    public void Configure(EntityTypeBuilder<ProductionOutputEvent> b)
    {
        b.ToTable("production_output_events", t =>
        {
            t.HasCheckConstraint("CK_output_interval", "\"IntervalEndUtc\" > \"IntervalStartUtc\"");
            t.HasCheckConstraint("CK_output_quantities", "\"Quantity\" >= 0 AND \"ReworkQuantity\" >= 0 AND \"RejectQuantity\" >= 0");
            t.HasCheckConstraint("CK_output_operators", "\"OperatorsPresent\" IS NULL OR \"OperatorsPresent\" BETWEEN 1 AND 500");
            t.HasCheckConstraint("CK_output_downtime", "\"DowntimeMinutes\" IS NULL OR \"DowntimeMinutes\" >= 0");
            t.HasCheckConstraint("CK_output_superseded", "\"IsSuperseded\" = (\"SupersededById\" IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.Stage).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.IntervalStartUtc).HasColumnType("timestamptz");
        b.Property(x => x.IntervalEndUtc).HasColumnType("timestamptz");
        b.Property(x => x.ShiftCode).HasMaxLength(16);
        b.Property(x => x.DowntimeReason).HasMaxLength(200);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.SourceRef).HasMaxLength(128);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.CorrectionReason).HasMaxLength(500);
        b.Property(x => x.RecordedAt).HasColumnType("timestamptz");
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();

        // Event deduplication: a source's own id is accepted once.
        b.HasIndex(x => new { x.Source, x.SourceRef }).IsUnique().HasFilter("\"SourceRef\" IS NOT NULL");
        // One live record per line, order, stage and interval (overlaps are rejected by the service under a line lock).
        b.HasIndex(x => new { x.LineId, x.OrderId, x.Stage, x.IntervalStartUtc, x.IntervalEndUtc })
            .IsUnique().HasFilter("NOT \"IsSuperseded\"").HasDatabaseName("IX_output_events_live_interval");
        b.HasIndex(x => new { x.LineId, x.IntervalStartUtc });
        b.HasIndex(x => new { x.OrderId, x.Stage, x.IntervalStartUtc });
        b.HasIndex(x => new { x.ProductionDate, x.LineId });
        b.HasIndex(x => x.CorrectionOfId);
        b.HasOne<ProductionOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class OperationObservationConfiguration : IEntityTypeConfiguration<OperationObservation>
{
    public void Configure(EntityTypeBuilder<OperationObservation> b)
    {
        b.ToTable("production_operation_observations", t =>
        {
            t.HasCheckConstraint("CK_observations_cycle", "\"CycleSeconds\" > 0 AND \"CycleSeconds\" <= 7200");
            t.HasCheckConstraint("CK_observations_samples", "\"SampleCount\" BETWEEN 1 AND 1000");
            t.HasCheckConstraint("CK_observations_operators", "\"OperatorsAssigned\" BETWEEN 1 AND 100");
            t.HasCheckConstraint("CK_observations_queue", "\"QueueBefore\" IS NULL OR \"QueueBefore\" >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.ObservedAtUtc).HasColumnType("timestamptz");
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.RecordedAt).HasColumnType("timestamptz");
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(x => new { x.LineId, x.StyleId, x.OperationId, x.ObservedAtUtc });
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionStyle>().WithMany().HasForeignKey(x => x.StyleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StyleOperation>().WithMany().HasForeignKey(x => x.OperationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class LineLayoutConfiguration : IEntityTypeConfiguration<LineLayout>
{
    public void Configure(EntityTypeBuilder<LineLayout> b)
    {
        b.ToTable("production_line_layouts");
        b.HasKey(x => x.Id);
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.Origin).HasMaxLength(16).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.SupersededAt).HasColumnType("timestamptz");
        // Exactly one current layout per line and style.
        b.HasIndex(x => new { x.LineId, x.StyleId }).IsUnique().HasFilter("\"IsCurrent\"").HasDatabaseName("IX_line_layouts_one_current");
        b.HasMany(x => x.Stations).WithOne().HasForeignKey(s => s.LayoutId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionStyle>().WithMany().HasForeignKey(x => x.StyleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class LineLayoutStationConfiguration : IEntityTypeConfiguration<LineLayoutStation>
{
    public void Configure(EntityTypeBuilder<LineLayoutStation> b)
    {
        b.ToTable("production_line_layout_stations", t => t.HasCheckConstraint("CK_layout_station_no", "\"StationNo\" BETWEEN 1 AND 300"));
        b.HasKey(x => x.Id);
        b.Property(x => x.OperationIds).HasColumnType("uuid[]");
        b.HasIndex(x => new { x.LayoutId, x.StationNo }).IsUnique();
    }
}

public class LineBalanceProposalConfiguration : IEntityTypeConfiguration<LineBalanceProposal>
{
    public void Configure(EntityTypeBuilder<LineBalanceProposal> b)
    {
        b.ToTable("production_balance_proposals", t =>
            t.HasCheckConstraint("CK_balance_proposals_workstations", "\"Workstations\" BETWEEN 1 AND 300"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Reference).HasMaxLength(32).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.InputHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.ResultJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.ApprovalId).HasMaxLength(128);
        b.Property(x => x.SubmittedByEmail).HasMaxLength(256);
        b.Property(x => x.SubmittedAt).HasColumnType("timestamptz");
        b.Property(x => x.AppliedAt).HasColumnType("timestamptz");
        b.Property(x => x.AppliedByEmail).HasMaxLength(256);
        b.Property(x => x.StatusReason).HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.Reference).IsUnique();
        b.HasIndex(x => new { x.LineId, x.StyleId, x.CreatedAt });
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionStyle>().WithMany().HasForeignKey(x => x.StyleId).OnDelete(DeleteBehavior.Restrict);
    }
}
