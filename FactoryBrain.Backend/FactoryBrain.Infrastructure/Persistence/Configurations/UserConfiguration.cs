using FactoryBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FactoryBrain.Infrastructure.Data.Configurations;

/// <summary>
/// EF Core mapping for <see cref="User"/>. Table <c>users</c>; unique
/// index on <c>Email</c>; the email column is stored case-sensitively
/// but the auth service normalises to lowercase before lookup so two
/// rows that differ only in casing can never coexist. Password and
/// refresh-token hashes are wide <c>text</c> columns — the PBKDF2 +
/// marker format is around 100 chars and the SHA-256 hex is exactly 64.
/// </summary>
public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Email)
            .IsRequired()
            .HasMaxLength(256);
        b.Property(x => x.PasswordHash)
            .IsRequired()
            .HasMaxLength(2048);
        b.Property(x => x.Role)
            .IsRequired()
            .HasMaxLength(16);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.RefreshTokenExpiresAt).HasColumnType("timestamptz");
        b.Property(x => x.RefreshTokenHash).HasMaxLength(128);
        b.HasIndex(x => x.Email).IsUnique();
    }
}

/// <summary>EF Core mapping for <see cref="PasswordResetToken"/> (table <c>password_reset_tokens</c>).</summary>
public class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> b)
    {
        b.ToTable("password_reset_tokens");
        b.HasKey(x => x.Id);
        b.Property(x => x.TokenHash).IsRequired().HasMaxLength(128);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.ExpiresAt).HasColumnType("timestamptz");
        b.Property(x => x.UsedAt).HasColumnType("timestamptz");
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.UserId);
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
