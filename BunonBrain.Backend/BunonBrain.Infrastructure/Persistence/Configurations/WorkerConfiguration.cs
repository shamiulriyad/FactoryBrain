using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

// Worker management tables. Statuses and types are stored as text with check
// constraints. Nothing here cascades a delete onto a worker: factories,
// departments and floors that still have workers can't be removed, and a
// worker row is never deleted by the application.

public class DepartmentConfiguration : IEntityTypeConfiguration<Department>
{
    public void Configure(EntityTypeBuilder<Department> b)
    {
        b.ToTable("departments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(16).IsRequired();
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.Name }).IsUnique();
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionFloorConfiguration : IEntityTypeConfiguration<ProductionFloor>
{
    public void Configure(EntityTypeBuilder<ProductionFloor> b)
    {
        b.ToTable("production_floors");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(16).IsRequired();
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.Name }).IsUnique();
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class WorkerProfileConfiguration : IEntityTypeConfiguration<WorkerProfile>
{
    /// <summary>Source of <see cref="WorkerProfile.WorkerNumber"/>. A sequence value is never handed out twice.</summary>
    public const string NumberSequence = "worker_number_seq";

    public void Configure(EntityTypeBuilder<WorkerProfile> b)
    {
        b.ToTable("workers", t =>
        {
            t.HasCheckConstraint("CK_workers_status", "\"Status\" IN ('Active','Inactive','Suspended','Resigned','Terminated')");
            t.HasCheckConstraint("CK_workers_employment_type", "\"EmploymentType\" IN ('Permanent','Probation','Contract','Temporary','Apprentice')");
            t.HasCheckConstraint("CK_workers_gender", "\"Gender\" IS NULL OR \"Gender\" IN ('Female','Male','Other')");
            t.HasCheckConstraint("CK_workers_worker_number", "\"WorkerNumber\" > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.WorkerCode).HasMaxLength(24).IsRequired();
        b.Property(x => x.EmployeeCode).HasMaxLength(32);
        b.Property(x => x.FullName).HasMaxLength(120).IsRequired();
        b.Property(x => x.PhotoKey).HasMaxLength(64);
        b.Property(x => x.PhotoContentType).HasMaxLength(32);
        b.Property(x => x.Gender).HasMaxLength(16);
        b.Property(x => x.Phone).HasMaxLength(32);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.PresentAddress).HasMaxLength(500);
        b.Property(x => x.EmergencyContactName).HasMaxLength(120);
        b.Property(x => x.EmergencyContactPhone).HasMaxLength(32);
        b.Property(x => x.EmergencyContactRelation).HasMaxLength(40);
        b.Property(x => x.Status).HasMaxLength(16).IsRequired().HasDefaultValue("Active");
        b.Property(x => x.StatusChangedAt).HasColumnType("timestamptz");
        b.Property(x => x.StatusReason).HasMaxLength(500);
        b.Property(x => x.EmploymentType).HasMaxLength(16).IsRequired().HasDefaultValue("Permanent");
        b.Property(x => x.Designation).HasMaxLength(80);
        b.Property(x => x.Grade).HasMaxLength(32);
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Workstation).HasMaxLength(32);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");

        // The permanent Worker ID: unique, both as a number and as the code people read.
        b.HasIndex(x => x.WorkerNumber).IsUnique();
        b.HasIndex(x => x.WorkerCode).IsUnique();
        // The factory's own employee number is optional, but unique within the factory when present.
        b.HasIndex(x => new { x.FactoryId, x.EmployeeCode }).IsUnique().HasFilter("\"EmployeeCode\" IS NOT NULL");
        b.HasIndex(x => new { x.FactoryId, x.Status });
        b.HasIndex(x => x.DepartmentId);
        b.HasIndex(x => x.FloorId);
        b.HasIndex(x => x.LineId);
        // One login account ↔ at most one worker.
        b.HasIndex(x => x.UserId).IsUnique().HasFilter("\"UserId\" IS NOT NULL");

        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFloor>().WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class WorkerStatusChangeConfiguration : IEntityTypeConfiguration<WorkerStatusChange>
{
    public void Configure(EntityTypeBuilder<WorkerStatusChange> b)
    {
        b.ToTable("worker_status_history");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.FromStatus).HasMaxLength(16);
        b.Property(x => x.ToStatus).HasMaxLength(16).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ChangedAt).HasColumnType("timestamptz");
        b.Property(x => x.ChangedByEmail).HasMaxLength(256).IsRequired();
        b.HasIndex(x => new { x.WorkerId, x.Id });
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class WorkerCardConfiguration : IEntityTypeConfiguration<WorkerCard>
{
    public void Configure(EntityTypeBuilder<WorkerCard> b)
    {
        b.ToTable("worker_cards", t =>
        {
            t.HasCheckConstraint("CK_worker_cards_type", "\"CardType\" IN ('RFID','NFC')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.CardUid).HasMaxLength(64).IsRequired();
        b.Property(x => x.CardType).HasMaxLength(8).IsRequired();
        b.Property(x => x.Label).HasMaxLength(80);
        b.Property(x => x.DeactivationReason).HasMaxLength(32);
        b.Property(x => x.IssuedAt).HasColumnType("timestamptz");
        b.Property(x => x.DeactivatedAt).HasColumnType("timestamptz");
        // A card UID can be held by one worker at a time; returned cards may be issued again.
        b.HasIndex(x => x.CardUid).IsUnique().HasFilter("\"IsActive\"");
        b.HasIndex(x => x.WorkerId);
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
