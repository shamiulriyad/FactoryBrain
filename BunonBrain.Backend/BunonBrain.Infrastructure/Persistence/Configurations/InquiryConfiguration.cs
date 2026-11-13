using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

/// <summary>Website form submissions (see <see cref="Inquiry"/>).</summary>
public class InquiryConfiguration : IEntityTypeConfiguration<Inquiry>
{
    public void Configure(EntityTypeBuilder<Inquiry> b)
    {
        b.ToTable("inquiries", t =>
        {
            t.HasCheckConstraint("CK_inquiries_kind",
                "\"Kind\" IN ('demo','consultation','contact','support','problem','feature','feedback','partnership')");
            t.HasCheckConstraint("CK_inquiries_status", "\"Status\" IN ('new','in_progress','closed')");
            t.HasCheckConstraint("CK_inquiries_reply_channel", "\"Email\" IS NOT NULL OR \"Phone\" IS NOT NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(16).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Email).HasMaxLength(254);
        b.Property(x => x.Phone).HasMaxLength(32);
        b.Property(x => x.Company).HasMaxLength(160);
        b.Property(x => x.JobTitle).HasMaxLength(120);
        b.Property(x => x.Subject).HasMaxLength(200);
        b.Property(x => x.Message).HasMaxLength(4000).IsRequired();
        b.Property(x => x.DetailsJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.SourcePath).HasMaxLength(200);
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => x.Kind);
    }
}
