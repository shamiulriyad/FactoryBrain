using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

// Workstations, machines and assignments. Nothing here cascades a delete
// onto assignment history. Two rules are also enforced by the database, so a
// bug or a concurrent request can't break them:
//   * at most one active primary assignment per worker (partial unique index);
//   * no two primary assignments of one worker overlap in time (exclusion
//     constraint over tstzrange(StartAt, EndAt), added in the migration
//     because EF can't model it: ExclusionConstraintName).

public class WorkstationConfiguration : IEntityTypeConfiguration<Workstation>
{
    public void Configure(EntityTypeBuilder<Workstation> b)
    {
        b.ToTable("workstations", t =>
        {
            t.HasCheckConstraint("CK_workstations_capacity", "\"Capacity\" BETWEEN 1 AND 20");
            t.HasCheckConstraint("CK_workstations_position", "\"Position\" >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(24).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.Capacity).HasDefaultValue(1);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.LineId, x.Position });
        b.HasIndex(x => x.DepartmentId);
        b.HasIndex(x => x.FloorId);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFloor>().WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MachineConfiguration : IEntityTypeConfiguration<Machine>
{
    public void Configure(EntityTypeBuilder<Machine> b)
    {
        b.ToTable("machines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(24).IsRequired();
        b.Property(x => x.MachineType).HasMaxLength(80);
        b.Property(x => x.Brand).HasMaxLength(80);
        b.Property(x => x.SerialNumber).HasMaxLength(80);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.Code }).IsUnique();
        b.HasIndex(x => x.WorkstationId);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class WorkstationAssignmentConfiguration : IEntityTypeConfiguration<WorkstationAssignment>
{
    /// <summary>Exclusion constraint created by the AddWorkstationAssignments migration.</summary>
    public const string ExclusionConstraintName = "EX_workstation_assignments_no_overlap";
    /// <summary>Partial unique index: one active primary assignment per worker.</summary>
    public const string OneActivePrimaryIndex = "IX_workstation_assignments_one_active_primary";

    public void Configure(EntityTypeBuilder<WorkstationAssignment> b)
    {
        b.ToTable("workstation_assignments", t =>
        {
            t.HasCheckConstraint("CK_workstation_assignments_status", "\"Status\" IN ('Active','Ended','Transferred')");
            t.HasCheckConstraint("CK_workstation_assignments_period", "\"EndAt\" IS NULL OR \"EndAt\" > \"StartAt\"");
            // Active ⇔ open-ended.
            t.HasCheckConstraint("CK_workstation_assignments_open", "(\"Status\" = 'Active') = (\"EndAt\" IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.EndReason).HasMaxLength(500);
        b.Property(x => x.AssignedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.EndedByEmail).HasMaxLength(256);
        b.Property(x => x.StartAt).HasColumnType("timestamptz");
        b.Property(x => x.EndAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");

        b.HasIndex(x => x.WorkerId).IsUnique().HasDatabaseName(OneActivePrimaryIndex)
            .HasFilter("\"EndAt\" IS NULL AND \"IsPrimary\"");
        b.HasIndex(x => new { x.WorkerId, x.StartAt });
        b.HasIndex(x => new { x.WorkstationId, x.EndAt });
        b.HasIndex(x => new { x.LineId, x.EndAt });
        b.HasIndex(x => new { x.FactoryId, x.Status });
        b.HasIndex(x => x.PreviousAssignmentId);

        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFloor>().WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkstationAssignment>().WithMany().HasForeignKey(x => x.PreviousAssignmentId).OnDelete(DeleteBehavior.Restrict);
    }
}
