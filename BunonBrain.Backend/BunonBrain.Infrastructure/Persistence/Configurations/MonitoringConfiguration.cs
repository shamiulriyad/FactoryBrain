using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

// Central monitoring. Rules the database itself guarantees:
//   * one event per factory and idempotency key;
//   * a simulator event is SIMULATED and an API event is REAL (check constraint);
//   * a worker id only with a verified identity method and never on an
//     uncertain detection (check constraints);
//   * at most one active alert per factory and dedup key (partial unique index);
//   * at most one running simulation per factory (partial unique index);
//   * events, event reviews and alert actions are append-only (triggers, added
//     in the AddCentralMonitoring migration).

public class MonitoringEventConfiguration : IEntityTypeConfiguration<MonitoringEvent>
{
    public const string IdempotencyIndex = "IX_monitoring_events_FactoryId_IdempotencyKey";

    public void Configure(EntityTypeBuilder<MonitoringEvent> b)
    {
        b.ToTable("monitoring_events", t =>
        {
            t.HasCheckConstraint("CK_monitoring_events_type",
                "\"EventType\" IN ('PERSON_DETECTED','TRACK_UPDATED','PERSON_LOST','ZONE_ENTERED','ZONE_EXITED','ACTIVITY_CLASSIFIED','ACTIVITY_STATE_CHANGED','ACTIVITY_UNCERTAIN','GARMENT_ACTIVITY_CLASSIFIED','GARMENT_ACTIVITY_CHANGED','GARMENT_ACTIVITY_UNCERTAIN','UNCERTAIN_DETECTION','CAMERA_STATUS_CHANGED','DEVICE_STATUS_CHANGED','MODEL_STATUS_CHANGED','MACHINE_STATE_CHANGED','MACHINE_CONDITION')");
            t.HasCheckConstraint("CK_monitoring_events_category", "\"Category\" IN ('VISION','SYSTEM')");
            t.HasCheckConstraint("CK_monitoring_events_source", "\"SourceType\" IN ('SIMULATED','REAL')");
            t.HasCheckConstraint("CK_monitoring_events_producer", "\"Producer\" IN ('API','SIMULATOR','DEVICE_PIPELINE','CAMERA_SERVICE','CV_SERVICE','IOT_PIPELINE')");
            t.HasCheckConstraint("CK_monitoring_events_producer_source",
                "(\"Producer\" <> 'SIMULATOR' OR \"SourceType\" = 'SIMULATED') AND (\"Producer\" NOT IN ('API','CV_SERVICE') OR \"SourceType\" = 'REAL')");
            // The computer-vision service never names a worker.
            t.HasCheckConstraint("CK_monitoring_events_cv_anonymous", "\"Producer\" <> 'CV_SERVICE' OR \"WorkerId\" IS NULL");
            t.HasCheckConstraint("CK_monitoring_events_severity", "\"Severity\" IN ('INFO','WARNING','CRITICAL')");
            t.HasCheckConstraint("CK_monitoring_events_confidence", "\"Confidence\" IS NULL OR (\"Confidence\" >= 0 AND \"Confidence\" <= 1)");
            t.HasCheckConstraint("CK_monitoring_events_worker_verified",
                "(\"WorkerId\" IS NULL) = (\"IdentityMethod\" IS NULL) AND (\"IdentityMethod\" IS NULL OR \"IdentityMethod\" = 'RFID_BADGE')");
            t.HasCheckConstraint("CK_monitoring_events_uncertain_anonymous", "\"EventType\" NOT IN ('UNCERTAIN_DETECTION','ACTIVITY_UNCERTAIN','GARMENT_ACTIVITY_UNCERTAIN') OR \"WorkerId\" IS NULL");
            t.HasCheckConstraint("CK_monitoring_events_simulated_anonymous", "\"SourceType\" <> 'SIMULATED' OR \"WorkerId\" IS NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(32).IsRequired();
        b.Property(x => x.Category).HasMaxLength(8).IsRequired();
        b.Property(x => x.SourceType).HasMaxLength(12).IsRequired();
        b.Property(x => x.Producer).HasMaxLength(20).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.TrackId).HasMaxLength(64);
        b.Property(x => x.IdentityMethod).HasMaxLength(24);
        b.Property(x => x.Severity).HasMaxLength(10).IsRequired();
        b.Property(x => x.Summary).HasMaxLength(300).IsRequired();
        b.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ModelName).HasMaxLength(80);
        b.Property(x => x.ModelVersion).HasMaxLength(40);
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.OccurredAt).HasColumnType("timestamptz");
        b.Property(x => x.ReceivedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.IdempotencyKey }).IsUnique().HasDatabaseName(IdempotencyIndex);
        b.HasIndex(x => new { x.FactoryId, x.OccurredAt });
        b.HasIndex(x => new { x.FactoryId, x.EventType, x.OccurredAt });
        b.HasIndex(x => new { x.LineId, x.OccurredAt });
        b.HasIndex(x => new { x.CameraId, x.OccurredAt });
        b.HasIndex(x => new { x.DeviceId, x.OccurredAt });
        b.HasIndex(x => new { x.MachineId, x.OccurredAt });
        b.HasIndex(x => x.WorkstationId);
        b.HasIndex(x => x.ZoneId);
        b.HasIndex(x => x.WorkerId);
        b.HasIndex(x => x.SimulationRunId);
        b.HasIndex(x => new { x.CameraId, x.TrackId });
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFloor>().WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Camera>().WithMany().HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CameraZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkerCard>().WithMany().HasForeignKey(x => x.WorkerCardId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MonitoringSimulationRun>().WithMany().HasForeignKey(x => x.SimulationRunId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MonitoringEventReviewConfiguration : IEntityTypeConfiguration<MonitoringEventReview>
{
    public void Configure(EntityTypeBuilder<MonitoringEventReview> b)
    {
        b.ToTable("monitoring_event_reviews", t =>
            t.HasCheckConstraint("CK_monitoring_event_reviews_decision", "\"Decision\" IN ('CONFIRMED','FALSE_POSITIVE','STILL_UNCERTAIN','NEEDS_FOLLOW_UP')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Decision).HasMaxLength(20).IsRequired();
        b.Property(x => x.Note).HasMaxLength(1000);
        b.Property(x => x.ReviewedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.ReviewedByRole).HasMaxLength(40);
        b.Property(x => x.ReviewedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.EventId, x.ReviewedAt });
        b.HasIndex(x => x.FactoryId);
        b.HasOne<MonitoringEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MonitoringAlertConfiguration : IEntityTypeConfiguration<MonitoringAlert>
{
    public const string OneActiveIndex = "IX_monitoring_alerts_one_active";
    public const string NumberSequence = "monitoring_alert_number_seq";

    public void Configure(EntityTypeBuilder<MonitoringAlert> b)
    {
        b.ToTable("monitoring_alerts", t =>
        {
            t.HasCheckConstraint("CK_monitoring_alerts_status", "\"Status\" IN ('OPEN','ACKNOWLEDGED','ESCALATED','RESOLVED','DISMISSED')");
            t.HasCheckConstraint("CK_monitoring_alerts_severity", "\"Severity\" IN ('LOW','MEDIUM','HIGH','CRITICAL')");
            t.HasCheckConstraint("CK_monitoring_alerts_category", "\"Category\" IN ('VISION','SYSTEM')");
            t.HasCheckConstraint("CK_monitoring_alerts_source", "\"SourceType\" IN ('SIMULATED','REAL')");
            // A closed alert carries who closed it and why.
            t.HasCheckConstraint("CK_monitoring_alerts_resolved",
                "\"Status\" <> 'RESOLVED' OR (\"ResolvedAt\" IS NOT NULL AND \"ResolutionCode\" IS NOT NULL AND \"ResolutionNote\" IS NOT NULL AND \"AcknowledgedAt\" IS NOT NULL)");
            t.HasCheckConstraint("CK_monitoring_alerts_dismissed", "\"Status\" <> 'DISMISSED' OR (\"DismissedAt\" IS NOT NULL AND \"DismissReason\" IS NOT NULL)");
            t.HasCheckConstraint("CK_monitoring_alerts_escalated", "\"Status\" <> 'ESCALATED' OR \"EscalationLevel\" > 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Number).HasDefaultValueSql($"nextval('{NumberSequence}')").ValueGeneratedOnAdd();
        b.Property(x => x.AlertType).HasMaxLength(40).IsRequired();
        b.Property(x => x.Category).HasMaxLength(8).IsRequired();
        b.Property(x => x.Severity).HasMaxLength(10).IsRequired();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.SourceType).HasMaxLength(12).IsRequired();
        b.Property(x => x.DedupKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasMaxLength(14).IsRequired();
        b.Property(x => x.AcknowledgedByEmail).HasMaxLength(256);
        b.Property(x => x.EscalatedByEmail).HasMaxLength(256);
        b.Property(x => x.EscalatedToRole).HasMaxLength(40);
        b.Property(x => x.ResolvedByEmail).HasMaxLength(256);
        b.Property(x => x.ResolutionCode).HasMaxLength(20);
        b.Property(x => x.ResolutionNote).HasMaxLength(1000);
        b.Property(x => x.DismissedByEmail).HasMaxLength(256);
        b.Property(x => x.DismissReason).HasMaxLength(1000);
        b.Property(x => x.Evidence).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.Version).IsConcurrencyToken();
        foreach (var p in new[] { nameof(MonitoringAlert.FirstOccurredAt), nameof(MonitoringAlert.LastOccurredAt), nameof(MonitoringAlert.AcknowledgedAt),
                     nameof(MonitoringAlert.EscalatedAt), nameof(MonitoringAlert.ResolvedAt), nameof(MonitoringAlert.DismissedAt),
                     nameof(MonitoringAlert.CreatedAt), nameof(MonitoringAlert.UpdatedAt) })
            b.Property(p).HasColumnType("timestamptz");
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.DedupKey }).IsUnique().HasDatabaseName(OneActiveIndex)
            .HasFilter("\"Status\" IN ('OPEN','ACKNOWLEDGED','ESCALATED')");
        b.HasIndex(x => new { x.FactoryId, x.Status, x.Severity });
        b.HasIndex(x => new { x.FactoryId, x.CreatedAt });
        b.HasIndex(x => new { x.LineId, x.Status });
        b.HasIndex(x => new { x.MachineId, x.Status });
        b.HasIndex(x => x.CameraId);
        b.HasIndex(x => x.DeviceId);
        b.HasIndex(x => x.EventId);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFloor>().WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Camera>().WithMany().HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CameraZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MonitoringEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MonitoringEvent>().WithMany().HasForeignKey(x => x.LastEventId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MonitoringAlertActionConfiguration : IEntityTypeConfiguration<MonitoringAlertAction>
{
    public void Configure(EntityTypeBuilder<MonitoringAlertAction> b)
    {
        b.ToTable("monitoring_alert_actions", t =>
        {
            t.HasCheckConstraint("CK_monitoring_alert_actions_action",
                "\"Action\" IN ('CREATED','REPEATED','SOURCE_RECOVERED','ACKNOWLEDGED','ESCALATED','RESOLVED','DISMISSED')");
            t.HasCheckConstraint("CK_monitoring_alert_actions_status", "\"ToStatus\" IN ('OPEN','ACKNOWLEDGED','ESCALATED','RESOLVED','DISMISSED')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Action).HasMaxLength(20).IsRequired();
        b.Property(x => x.FromStatus).HasMaxLength(14);
        b.Property(x => x.ToStatus).HasMaxLength(14).IsRequired();
        b.Property(x => x.Note).HasMaxLength(1000);
        b.Property(x => x.EscalatedToRole).HasMaxLength(40);
        b.Property(x => x.ResolutionCode).HasMaxLength(20);
        b.Property(x => x.ActorEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.ActorRole).HasMaxLength(40);
        b.Property(x => x.At).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.AlertId, x.At });
        b.HasIndex(x => x.FactoryId);
        b.HasOne<MonitoringAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MonitoringEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MonitoringSimulationRunConfiguration : IEntityTypeConfiguration<MonitoringSimulationRun>
{
    public const string OneRunningIndex = "IX_monitoring_simulation_runs_one_running";

    public void Configure(EntityTypeBuilder<MonitoringSimulationRun> b)
    {
        b.ToTable("monitoring_simulation_runs", t =>
        {
            t.HasCheckConstraint("CK_monitoring_simulation_runs_status", "\"Status\" IN ('RUNNING','STOPPED','COMPLETED')");
            t.HasCheckConstraint("CK_monitoring_simulation_runs_rate", "\"EventsPerMinute\" BETWEEN 1 AND 120");
            t.HasCheckConstraint("CK_monitoring_simulation_runs_duration", "\"DurationMinutes\" BETWEEN 1 AND 120");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasMaxLength(10).IsRequired();
        b.Property(x => x.CameraIds).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.EventTypes).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.StartedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.StoppedByEmail).HasMaxLength(256);
        b.Property(x => x.StopReason).HasMaxLength(500);
        foreach (var p in new[] { nameof(MonitoringSimulationRun.StartedAt), nameof(MonitoringSimulationRun.EndsAt), nameof(MonitoringSimulationRun.NextEventAt),
                     nameof(MonitoringSimulationRun.StoppedAt) })
            b.Property(p).HasColumnType("timestamptz");
        b.HasIndex(x => x.FactoryId).IsUnique().HasDatabaseName(OneRunningIndex).HasFilter("\"Status\" = 'RUNNING'");
        b.HasIndex(x => new { x.Status, x.NextEventAt });
        b.HasIndex(x => new { x.FactoryId, x.StartedAt });
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
