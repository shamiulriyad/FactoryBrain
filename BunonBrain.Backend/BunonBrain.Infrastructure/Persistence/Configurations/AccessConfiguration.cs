using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

public class FactoryConfiguration : IEntityTypeConfiguration<Factory>
{
    public void Configure(EntityTypeBuilder<Factory> b)
    {
        b.ToTable("factories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(16).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Location).HasMaxLength(200);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.Name).IsUnique();
    }
}

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500).IsRequired();
        b.Property(x => x.Scope).HasMaxLength(16).IsRequired();
        b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> b)
    {
        b.ToTable("permissions");
        b.HasKey(x => x.Code);
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Module).HasMaxLength(64).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500).IsRequired();
    }
}

public class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> b)
    {
        b.ToTable("role_permissions");
        b.HasKey(x => new { x.RoleId, x.PermissionCode });
        b.Property(x => x.RoleId).HasMaxLength(32);
        b.Property(x => x.PermissionCode).HasMaxLength(64);
        b.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionCode).OnDelete(DeleteBehavior.Cascade);
    }
}

public class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    public void Configure(EntityTypeBuilder<UserRole> b)
    {
        b.ToTable("user_roles");
        b.HasKey(x => new { x.UserId, x.RoleId });
        b.Property(x => x.RoleId).HasMaxLength(32);
        b.Property(x => x.AssignedAt).HasColumnType("timestamptz");
        b.HasIndex(x => x.RoleId);
        // A role that is still held can't be removed from the catalog table.
        b.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class UserFactoryAssignmentConfiguration : IEntityTypeConfiguration<UserFactoryAssignment>
{
    public void Configure(EntityTypeBuilder<UserFactoryAssignment> b)
    {
        b.ToTable("user_factory_assignments");
        b.HasKey(x => new { x.UserId, x.FactoryId });
        b.Property(x => x.AssignedAt).HasColumnType("timestamptz");
        b.HasIndex(x => x.FactoryId);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class UserProductionLineAssignmentConfiguration : IEntityTypeConfiguration<UserProductionLineAssignment>
{
    public void Configure(EntityTypeBuilder<UserProductionLineAssignment> b)
    {
        b.ToTable("user_production_line_assignments");
        b.HasKey(x => new { x.UserId, x.LineId });
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.AssignedAt).HasColumnType("timestamptz");
        b.HasIndex(x => x.LineId);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class WorkerProfileConfiguration : IEntityTypeConfiguration<WorkerProfile>
{
    public void Configure(EntityTypeBuilder<WorkerProfile> b)
    {
        b.ToTable("workers");
        b.HasKey(x => x.Id);
        b.Property(x => x.EmployeeCode).HasMaxLength(32).IsRequired();
        b.Property(x => x.FullName).HasMaxLength(120).IsRequired();
        b.Property(x => x.Designation).HasMaxLength(80);
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Workstation).HasMaxLength(32);
        b.Property(x => x.Phone).HasMaxLength(32);
        b.Property(x => x.Grade).HasMaxLength(32);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.EmployeeCode }).IsUnique();
        b.HasIndex(x => x.LineId);
        // One login account ↔ at most one worker profile.
        b.HasIndex(x => x.UserId).IsUnique().HasFilter("\"UserId\" IS NOT NULL");
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
    }
}
