using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

/// <summary><c>ask_conversations</c>: saved Ask BunonBrain chats, one owner each; removed with the user.</summary>
public class AskConversationConfiguration : IEntityTypeConfiguration<AskConversation>
{
    public const int TitleMax = 120;

    public void Configure(EntityTypeBuilder<AskConversation> b)
    {
        b.ToTable("ask_conversations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(TitleMax).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.UserId, x.UpdatedAt });
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Messages).WithOne().HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary><c>ask_messages</c>: one question and its answer; cited passages kept as JSON.</summary>
public class AskMessageConfiguration : IEntityTypeConfiguration<AskMessage>
{
    public void Configure(EntityTypeBuilder<AskMessage> b)
    {
        b.ToTable("ask_messages", t =>
            t.HasCheckConstraint("CK_ask_messages_citations", "jsonb_typeof(\"CitationsJson\") = 'array'"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Question).HasMaxLength(2000).IsRequired();
        b.Property(x => x.Answer).HasMaxLength(20000).IsRequired();
        b.Property(x => x.Mode).HasMaxLength(16).IsRequired();
        b.Property(x => x.CitationsJson).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.CachedQuestion).HasMaxLength(2000);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.ConversationId, x.CreatedAt });
    }
}
