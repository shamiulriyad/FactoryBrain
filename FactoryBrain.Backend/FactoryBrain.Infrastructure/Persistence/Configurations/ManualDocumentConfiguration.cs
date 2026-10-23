using FactoryBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pgvector;

namespace FactoryBrain.Infrastructure.Data.Configurations;

public class ManualDocumentConfiguration : IEntityTypeConfiguration<ManualDocument>
{
    public void Configure(EntityTypeBuilder<ManualDocument> b)
    {
        b.ToTable("manual_documents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Tags).HasColumnType("text[]");
        // Embedding metadata is required after AddEmbeddingMetadata runs.
        // The migration seeds every existing row with the same defaults so
        // NOT NULL is safe to add in one ALTER TABLE statement.
        b.Property(x => x.EmbeddingProvider).IsRequired();
        b.Property(x => x.EmbeddingModel).IsRequired();
        b.Property(x => x.Dims).IsRequired();
        // Added by the AddIngestMetadata migration for the new
        // /api/rag/ingest endpoint + RMG demo corpus.
        b.Property(x => x.Url).HasMaxLength(2048);
        b.Property(x => x.IsDemo).IsRequired();
        b.Property(x => x.CreatedAt).IsRequired();
        b.HasIndex(x => x.Source);
        b.HasMany(x => x.Chunks)
            .WithOne()
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class DocumentChunkConfiguration : IEntityTypeConfiguration<DocumentChunk>
{
    public void Configure(EntityTypeBuilder<DocumentChunk> b)
    {
        b.ToTable("document_chunks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Tags).HasColumnType("text[]");
        // The pgvector column is mapped without a fixed dimension so EF Core
        // never tries to alter it back. The actual dimension is owned by the
        // database at runtime:
        //   * InitialBaseline migration creates it as `vector(384)`.
        //   * Program.cs StartupDetectColumnDim() checks the live column and
        //     resizes it (ALTER TABLE ... ALTER COLUMN ... TYPE vector(N))
        //     when the configured embedding dims change, then reindexes.
        //   * POST /api/rag/reindex runs the same detect-and-resize pipeline.
        b.Property(x => x.Embedding).HasColumnType("vector");
        b.HasIndex(x => x.DocumentId);
        b.HasIndex(x => new { x.Department, x.Category });
    }
}
