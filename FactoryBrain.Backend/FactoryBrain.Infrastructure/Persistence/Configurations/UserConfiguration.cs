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
