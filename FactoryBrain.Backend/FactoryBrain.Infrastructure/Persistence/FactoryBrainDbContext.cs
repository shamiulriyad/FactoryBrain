using FactoryBrain.Domain.Entities;
using FactoryBrain.Infrastructure.Rag;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace FactoryBrain.Infrastructure.Persistence;

/// <summary>
/// Root EF Core DbContext for the factoryBrain backend. Every aggregate
/// matches a table in PostgreSQL; vector columns use the pgvector type.
/// </summary>
public class FactoryBrainDbContext : DbContext
{
    private readonly EmbeddingProviderResolver? _resolver;

    public FactoryBrainDbContext(
        DbContextOptions<FactoryBrainDbContext> opts,
        EmbeddingProviderResolver? resolver = null) : base(opts)
    {
        _resolver = resolver;
    }

    public DbSet<FloorAlert>        FloorAlerts        => Set<FloorAlert>();
    public DbSet<LineBoardMetric>   LineBoardMetrics   => Set<LineBoardMetric>();
    public DbSet<QcDefect>          QcDefects          => Set<QcDefect>();
    public DbSet<SensorReading>     SensorReadings     => Set<SensorReading>();
    public DbSet<ManualDocument>    ManualDocuments    => Set<ManualDocument>();
    public DbSet<DocumentChunk>     DocumentChunks     => Set<DocumentChunk>();
    public DbSet<PolicyDocument>    Policies           => Set<PolicyDocument>();
    public DbSet<AgentDefinition>   Agents             => Set<AgentDefinition>();
    public DbSet<Insight>           Insights           => Set<Insight>();
    public DbSet<VisionResult>      VisionResults      => Set<VisionResult>();
    public DbSet<ApprovalRequest>   Approvals          => Set<ApprovalRequest>();
    public DbSet<OrderRecord>       Orders             => Set<OrderRecord>();
    public DbSet<ProductRecord>     Products           => Set<ProductRecord>();
    public DbSet<CustomerRecord>    Customers          => Set<CustomerRecord>();
    public DbSet<SupplierRecord>    Suppliers          => Set<SupplierRecord>();
    public DbSet<InventoryRecord>   Inventory          => Set<InventoryRecord>();
    public DbSet<ActivityEvent>     ActivityEvents     => Set<ActivityEvent>();
    public DbSet<User>               Users              => Set<User>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);
        mb.HasPostgresExtension("vector");
        mb.ApplyConfigurationsFromAssembly(typeof(FactoryBrainDbContext).Assembly);

        // The pgvector column on document_chunks is intentionally mapped as
        // "vector" with no fixed dimension. The dimension lives in the
        // database and is mutated at runtime by StartupColumnDims / Reindex;
        // EF never tries to alter it back to a baked-in width. See
        // DocumentChunkConfiguration for the full rationale.
        void _SuppressUnusedWarning() => _ = _resolver; // resolver kept for future extensions
        _SuppressUnusedWarning();
    }
}
