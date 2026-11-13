using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

public class SiteSettingConfiguration : IEntityTypeConfiguration<SiteSetting>
{
    public void Configure(EntityTypeBuilder<SiteSetting> b)
    {
        b.ToTable("site_settings");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasMaxLength(64);
        b.Property(x => x.ValueJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedByEmail).HasMaxLength(254);
    }
}
