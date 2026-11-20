using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Data.Configurations;

/// <summary>EF Core mapping for <see cref="DemoEnvironment"/> (table <c>demo_environment</c>, one row).</summary>
public class DemoEnvironmentConfiguration : IEntityTypeConfiguration<DemoEnvironment>
{
    public void Configure(EntityTypeBuilder<DemoEnvironment> b)
    {
        b.ToTable("demo_environment", t => t.HasCheckConstraint("CK_demo_environment_single_row", "\"Id\" = 1"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Status).IsRequired().HasMaxLength(16);
        b.Property(x => x.ClaimedAt).HasColumnType("timestamptz");
        b.Property(x => x.LastResetAt).HasColumnType("timestamptz");
        b.Property(x => x.NextResetAt).HasColumnType("timestamptz");
    }
}

/// <summary>EF Core mapping for <see cref="DemoSession"/> (table <c>demo_sessions</c>).</summary>
public class DemoSessionConfiguration : IEntityTypeConfiguration<DemoSession>
{
    public void Configure(EntityTypeBuilder<DemoSession> b)
    {
        b.ToTable("demo_sessions");
        b.HasKey(x => x.Id);
        b.Property(x => x.RefreshTokenHash).IsRequired().HasMaxLength(128);
        b.Property(x => x.RevokedReason).HasMaxLength(200);
        b.Property(x => x.ClientIp).HasMaxLength(64);
        b.Property(x => x.UserAgent).HasMaxLength(256);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.LastSeenAt).HasColumnType("timestamptz");
        b.Property(x => x.ExpiresAt).HasColumnType("timestamptz");
        b.Property(x => x.RevokedAt).HasColumnType("timestamptz");
        b.HasIndex(x => x.RefreshTokenHash).IsUnique();
        b.HasIndex(x => x.ExpiresAt);
        // A demo identity row can disappear only through the daily reset, which empties this table too.
        b.HasOne<User>().WithMany().HasForeignKey(x => x.CurrentUserId).OnDelete(DeleteBehavior.SetNull);
    }
}
