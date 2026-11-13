using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Rag;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace BunonBrain.Infrastructure.Persistence;

/// <summary>
/// Root EF Core DbContext for the bunonBrain backend. Every aggregate
/// matches a table in PostgreSQL; vector columns use the pgvector type.
/// </summary>
public class BunonBrainDbContext : DbContext
{
    private readonly EmbeddingProviderResolver? _resolver;

    public BunonBrainDbContext(
        DbContextOptions<BunonBrainDbContext> opts,
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
    public DbSet<AuditLogEntry>      AuditLog           => Set<AuditLogEntry>();
    public DbSet<AiSettingsRecord>   AiSettings         => Set<AiSettingsRecord>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<Inquiry>            Inquiries          => Set<Inquiry>();
    public DbSet<SiteSetting>        SiteSettings       => Set<SiteSetting>();
    public DbSet<Factory>                      Factories              => Set<Factory>();
    public DbSet<Role>                         Roles                  => Set<Role>();
    public DbSet<Permission>                   Permissions            => Set<Permission>();
    public DbSet<RolePermission>               RolePermissions        => Set<RolePermission>();
    public DbSet<UserRole>                     UserRoles              => Set<UserRole>();
    public DbSet<UserFactoryAssignment>        UserFactories          => Set<UserFactoryAssignment>();
    public DbSet<UserProductionLineAssignment> UserLines              => Set<UserProductionLineAssignment>();
    public DbSet<WorkerProfile>                Workers                => Set<WorkerProfile>();
    public DbSet<WorkerStatusChange>           WorkerStatusHistory    => Set<WorkerStatusChange>();
    public DbSet<WorkerCard>                   WorkerCards            => Set<WorkerCard>();
    public DbSet<Department>                   Departments            => Set<Department>();
    public DbSet<ProductionFloor>              ProductionFloors       => Set<ProductionFloor>();
    public DbSet<Workstation>                  Workstations           => Set<Workstation>();
    public DbSet<Machine>                      Machines               => Set<Machine>();
    public DbSet<WorkstationAssignment>        Assignments            => Set<WorkstationAssignment>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<ShiftBreak> ShiftBreaks => Set<ShiftBreak>();
    public DbSet<WorkerShiftAssignment> ShiftAssignments => Set<WorkerShiftAssignment>();
    public DbSet<AttendanceReader> AttendanceReaders => Set<AttendanceReader>();
    public DbSet<AttendanceEvent> AttendanceEvents => Set<AttendanceEvent>();
    public DbSet<AttendanceSession> AttendanceSessions => Set<AttendanceSession>();
    public DbSet<BreakSession> BreakSessions => Set<BreakSession>();
    public DbSet<OvertimeRequest> OvertimeRequests => Set<OvertimeRequest>();
    public DbSet<OvertimeApproval> OvertimeApprovals => Set<OvertimeApproval>();
    public DbSet<AttendanceCorrection> AttendanceCorrections => Set<AttendanceCorrection>();
    public DbSet<AttendancePolicy> AttendancePolicies => Set<AttendancePolicy>();
    public DbSet<QcInspection>        QcInspections        => Set<QcInspection>();
    public DbSet<QcInspectionFinding> QcInspectionFindings => Set<QcInspectionFinding>();
    public DbSet<QcDefectCategory>    QcDefectCategories   => Set<QcDefectCategory>();
    public DbSet<ProductionLine>         ProductionLines         => Set<ProductionLine>();
    public DbSet<ProductionShift>        ProductionShifts        => Set<ProductionShift>();
    public DbSet<ProductionStyle>        ProductionStyles        => Set<ProductionStyle>();
    public DbSet<StyleOperation>         StyleOperations         => Set<StyleOperation>();
    public DbSet<ProductionOrder>        ProductionOrders        => Set<ProductionOrder>();
    public DbSet<ProductionPlan>         ProductionPlans         => Set<ProductionPlan>();
    public DbSet<ProductionPlanChange>   ProductionPlanChanges   => Set<ProductionPlanChange>();
    public DbSet<ProductionOutputEvent>  ProductionOutputEvents  => Set<ProductionOutputEvent>();
    public DbSet<OperationObservation>   OperationObservations   => Set<OperationObservation>();
    public DbSet<LineLayout>             LineLayouts             => Set<LineLayout>();
    public DbSet<LineLayoutStation>      LineLayoutStations      => Set<LineLayoutStation>();
    public DbSet<LineBalanceProposal>    BalanceProposals        => Set<LineBalanceProposal>();
    public DbSet<MerchOrder>             MerchOrders             => Set<MerchOrder>();
    public DbSet<MerchMilestone>         MerchMilestones         => Set<MerchMilestone>();
    public DbSet<MerchDocument>          MerchDocuments          => Set<MerchDocument>();
    public DbSet<MerchDocumentVersion>   MerchDocumentVersions   => Set<MerchDocumentVersion>();
    public DbSet<MerchDocumentPage>      MerchDocumentPages      => Set<MerchDocumentPage>();
    public DbSet<MerchExtractedField>    MerchExtractedFields    => Set<MerchExtractedField>();
    public DbSet<MerchConflict>          MerchConflicts          => Set<MerchConflict>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);
        mb.HasPostgresExtension("vector");
        mb.ApplyConfigurationsFromAssembly(typeof(BunonBrainDbContext).Assembly);
        // Worker IDs (W-001, …) come from this sequence: a value is never handed out twice.
        mb.HasSequence<long>(Configurations.WorkerProfileConfiguration.NumberSequence).StartsAt(1).IncrementsBy(1);

        // The pgvector column on document_chunks is intentionally mapped as
        // "vector" with no fixed dimension. The dimension lives in the
        // database and is mutated at runtime by StartupColumnDims / Reindex;
        // EF never tries to alter it back to a baked-in width. See
        // DocumentChunkConfiguration for the full rationale.
        void _SuppressUnusedWarning() => _ = _resolver; // resolver kept for future extensions
        _SuppressUnusedWarning();
    }
}
