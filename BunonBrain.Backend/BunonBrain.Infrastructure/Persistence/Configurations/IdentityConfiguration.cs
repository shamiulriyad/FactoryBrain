using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

// Identity association. Rules the database itself guarantees:
//   * at most one active mapping per reader (partial unique index);
//   * one read per factory and idempotency key;
//   * one VERIFIED association per camera track, and one per worker and
//     camera (partial unique indexes): two requests at the same moment can
//     never both verify conflicting identities;
//   * a VERIFIED association always names a worker;
//   * evidence and history are append-only (triggers in the migration).

public class RfidReaderMappingConfiguration : IEntityTypeConfiguration<RfidReaderMapping>
{
    public const string OneActiveIndex = "IX_rfid_reader_mappings_one_active";

    public void Configure(EntityTypeBuilder<RfidReaderMapping> b)
    {
        b.ToTable("rfid_reader_mappings", t =>
            t.HasCheckConstraint("CK_rfid_reader_mappings_precision", "\"Precision\" IN ('WORKSTATION','ZONE','LINE','FLOOR','AREA')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Precision).HasMaxLength(16).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Location).HasMaxLength(200);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.EndedByEmail).HasMaxLength(256);
        b.Property(x => x.EffectiveFrom).HasColumnType("timestamptz");
        b.Property(x => x.EffectiveTo).HasColumnType("timestamptz");
        b.HasIndex(x => x.DeviceId).IsUnique().HasFilter("\"EffectiveTo\" IS NULL").HasDatabaseName(OneActiveIndex);
        b.HasIndex(x => new { x.DeviceId, x.EffectiveFrom });
        b.HasIndex(x => x.WorkstationId);
        b.HasIndex(x => x.CameraZoneId);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFloor>().WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CameraZone>().WithMany().HasForeignKey(x => x.CameraZoneId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class RfidReadEventConfiguration : IEntityTypeConfiguration<RfidReadEvent>
{
    public const string IdempotencyIndex = "IX_rfid_read_events_FactoryId_IdempotencyKey";

    public void Configure(EntityTypeBuilder<RfidReadEvent> b)
    {
        b.ToTable("rfid_read_events", t =>
        {
            t.HasCheckConstraint("CK_rfid_read_events_outcome", "\"Outcome\" IN ('ACCEPTED','REJECTED')");
            t.HasCheckConstraint("CK_rfid_read_events_source", "\"Source\" IN ('Device','Api','Simulator')");
            t.HasCheckConstraint("CK_rfid_read_events_simulator", "(\"Source\" = 'Simulator') = \"IsSimulated\"");
            t.HasCheckConstraint("CK_rfid_read_events_evaluation",
                "\"Evaluation\" IS NULL OR \"Evaluation\" IN ('PENDING','VERIFIED','AMBIGUOUS','UNVERIFIED','MANUAL_REVIEW')");
            // A rejected read never names a worker, and is never evaluated.
            t.HasCheckConstraint("CK_rfid_read_events_rejected", "\"Outcome\" = 'ACCEPTED' OR (\"WorkerId\" IS NULL AND \"Evaluation\" IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityByDefaultColumn();
        b.Property(x => x.CardHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.CardSuffix).HasMaxLength(4).IsRequired();
        b.Property(x => x.Source).HasMaxLength(12).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
        b.Property(x => x.Outcome).HasMaxLength(10).IsRequired();
        b.Property(x => x.OutcomeCode).HasMaxLength(48);
        b.Property(x => x.Evaluation).HasMaxLength(16);
        b.Property(x => x.EvaluationCode).HasMaxLength(48);
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.OccurredAt).HasColumnType("timestamptz");
        b.Property(x => x.ReceivedAt).HasColumnType("timestamptz");
        b.Property(x => x.EvaluatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.IdempotencyKey }).IsUnique().HasDatabaseName(IdempotencyIndex);
        b.HasIndex(x => new { x.DeviceId, x.OccurredAt });
        b.HasIndex(x => new { x.MappingId, x.OccurredAt });
        b.HasIndex(x => new { x.WorkerId, x.OccurredAt });
        b.HasIndex(x => new { x.FactoryId, x.Evaluation });
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RfidReaderMapping>().WithMany().HasForeignKey(x => x.MappingId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkerCard>().WithMany().HasForeignKey(x => x.CardId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class WorkerTrackAssociationConfiguration : IEntityTypeConfiguration<WorkerTrackAssociation>
{
    public const string NumberSequence = "worker_track_association_number_seq";
    public const string OneVerifiedPerTrack = "IX_worker_track_associations_one_verified_track";
    public const string OneVerifiedPerWorkerCamera = "IX_worker_track_associations_one_verified_worker_camera";

    public void Configure(EntityTypeBuilder<WorkerTrackAssociation> b)
    {
        b.ToTable("worker_track_associations", t =>
        {
            t.HasCheckConstraint("CK_worker_track_associations_status",
                "\"Status\" IN ('PENDING','VERIFIED','AMBIGUOUS','UNVERIFIED','EXPIRED','REVOKED','MANUAL_REVIEW')");
            t.HasCheckConstraint("CK_worker_track_associations_verified_worker", "\"Status\" <> 'VERIFIED' OR \"WorkerId\" IS NOT NULL");
            t.HasCheckConstraint("CK_worker_track_associations_method",
                "\"VerificationMethod\" IS NULL OR \"VerificationMethod\" IN ('RFID_WORKSTATION','MANUAL_REVIEW')");
            t.HasCheckConstraint("CK_worker_track_associations_expiry", "\"ExpiresAt\" > \"StartAt\"");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Number).HasDefaultValueSql($"nextval('{NumberSequence}')").ValueGeneratedOnAdd();
        b.Property(x => x.TrackId).HasMaxLength(64).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.VerificationMethod).HasMaxLength(20);
        b.Property(x => x.ReasonCode).HasMaxLength(48).IsRequired();
        b.Property(x => x.Summary).HasMaxLength(400).IsRequired();
        b.Property(x => x.CandidateWorkers).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.CandidateTracks).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ReviewedByEmail).HasMaxLength(256);
        foreach (var p in new[] { nameof(WorkerTrackAssociation.StartAt), nameof(WorkerTrackAssociation.EndAt), nameof(WorkerTrackAssociation.ExpiresAt),
                                  nameof(WorkerTrackAssociation.CreatedAt), nameof(WorkerTrackAssociation.UpdatedAt), nameof(WorkerTrackAssociation.ReviewedAt) })
            b.Property(p).HasColumnType("timestamptz");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.CameraId, x.TrackId }).IsUnique().HasFilter("\"Status\" = 'VERIFIED'").HasDatabaseName(OneVerifiedPerTrack);
        b.HasIndex(x => new { x.WorkerId, x.CameraId }).IsUnique().HasFilter("\"Status\" = 'VERIFIED'").HasDatabaseName(OneVerifiedPerWorkerCamera);
        b.HasIndex(x => new { x.FactoryId, x.Status, x.UpdatedAt });
        b.HasIndex(x => new { x.CameraId, x.TrackId, x.CreatedAt });
        b.HasIndex(x => new { x.WorkerId, x.CreatedAt });
        b.HasIndex(x => x.RfidReadEventId);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Camera>().WithMany().HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CameraZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RfidReadEvent>().WithMany().HasForeignKey(x => x.RfidReadEventId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkstationAssignment>().WithMany().HasForeignKey(x => x.AssignmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class IdentityAssociationEvidenceConfiguration : IEntityTypeConfiguration<IdentityAssociationEvidence>
{
    public void Configure(EntityTypeBuilder<IdentityAssociationEvidence> b)
    {
        b.ToTable("identity_association_evidence", t =>
            t.HasCheckConstraint("CK_identity_association_evidence_effect", "\"Effect\" IN ('SUPPORTS','CONTRADICTS','CONTEXT')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityByDefaultColumn();
        b.Property(x => x.Kind).HasMaxLength(24).IsRequired();
        b.Property(x => x.Effect).HasMaxLength(12).IsRequired();
        b.Property(x => x.RefType).HasMaxLength(32);
        b.Property(x => x.RefId).HasMaxLength(64);
        b.Property(x => x.Detail).HasMaxLength(500).IsRequired();
        b.Property(x => x.ObservedAt).HasColumnType("timestamptz");
        b.Property(x => x.RecordedAt).HasColumnType("timestamptz");
        b.HasIndex(x => x.AssociationId);
        b.HasOne<WorkerTrackAssociation>().WithMany().HasForeignKey(x => x.AssociationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class IdentityAssociationHistoryConfiguration : IEntityTypeConfiguration<IdentityAssociationHistory>
{
    public void Configure(EntityTypeBuilder<IdentityAssociationHistory> b)
    {
        b.ToTable("identity_association_history");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityByDefaultColumn();
        b.Property(x => x.Action).HasMaxLength(24).IsRequired();
        b.Property(x => x.FromStatus).HasMaxLength(16);
        b.Property(x => x.ToStatus).HasMaxLength(16).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.ActorEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.ActorRole).HasMaxLength(64);
        b.Property(x => x.At).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.AssociationId, x.At });
        b.HasIndex(x => new { x.FactoryId, x.At });
        b.HasOne<WorkerTrackAssociation>().WithMany().HasForeignKey(x => x.AssociationId).OnDelete(DeleteBehavior.Restrict);
    }
}
