using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

// Machine monitoring and IoT. Rules the database itself guarantees:
//   * one telemetry row per device and message id, one counter event per
//     factory and idempotency key (a resent message is never counted twice);
//   * one MQTT client id per factory, one current state / profile / counter
//     snapshot per machine or device;
//   * raw telemetry is never updated (deleted only by the retention job);
//     state transitions, counter events and corrections are append-only
//     (triggers in the migration);
//   * states, modes and sources are from the documented sets.

public class DeviceIotConfigurationConfiguration : IEntityTypeConfiguration<DeviceIotConfiguration>
{
    public const string MqttClientIndex = "IX_device_iot_configurations_mqtt_client";

    public void Configure(EntityTypeBuilder<DeviceIotConfiguration> b)
    {
        b.ToTable("device_iot_configurations", t =>
        {
            t.HasCheckConstraint("CK_device_iot_configurations_type",
                "\"DeviceType\" IN ('SEWING_MACHINE_SENSOR','RUN_STOP_SENSOR','POWER_SENSOR','PIECE_COUNTER','CUTTING_MACHINE_SENSOR','PRESSING_MACHINE_SENSOR','RFID_READER','ESP32','INDUSTRIAL_GATEWAY','MQTT_DEVICE','GENERIC_SENSOR')");
            t.HasCheckConstraint("CK_device_iot_configurations_protocol", "\"Protocol\" IN ('MQTT','HTTP','SIMULATOR')");
            t.HasCheckConstraint("CK_device_iot_configurations_via", "\"ConnectionVia\" IN ('DIRECT','ESP32','GATEWAY')");
            t.HasCheckConstraint("CK_device_iot_configurations_gateway", "\"GatewayDeviceId\" IS NULL OR \"GatewayDeviceId\" <> \"DeviceId\"");
        });
        b.HasKey(x => x.DeviceId);
        b.Property(x => x.DeviceType).HasMaxLength(32).IsRequired();
        b.Property(x => x.Protocol).HasMaxLength(12).IsRequired();
        b.Property(x => x.ConnectionVia).HasMaxLength(12).IsRequired();
        b.Property(x => x.MqttClientId).HasMaxLength(64);
        b.Property(x => x.Settings).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.KeyHash).HasMaxLength(64);
        b.Property(x => x.KeyPrefix).HasMaxLength(16);
        b.Property(x => x.KeyIssuedByEmail).HasMaxLength(256);
        b.Property(x => x.UpdatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.KeyIssuedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.FactoryId, x.MqttClientId }).IsUnique().HasFilter("\"MqttClientId\" IS NOT NULL").HasDatabaseName(MqttClientIndex);
        b.HasIndex(x => x.GatewayDeviceId);
        b.HasOne<Device>().WithOne().HasForeignKey<DeviceIotConfiguration>(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.GatewayDeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MachineMonitoringProfileConfiguration : IEntityTypeConfiguration<MachineMonitoringProfile>
{
    public void Configure(EntityTypeBuilder<MachineMonitoringProfile> b)
    {
        b.ToTable("machine_monitoring_profiles", t =>
        {
            t.HasCheckConstraint("CK_machine_monitoring_profiles_kind", "\"MachineKind\" IN ('SEWING','CUTTING','PRESSING','OTHER')");
            t.HasCheckConstraint("CK_machine_monitoring_profiles_counting", "\"CountingMode\" IN ('PIECE','PULSES_PER_PIECE','CYCLE_NOT_PIECE')");
            t.HasCheckConstraint("CK_machine_monitoring_profiles_ppp", "\"PulsesPerPiece\" BETWEEN 1 AND 1000");
            t.HasCheckConstraint("CK_machine_monitoring_profiles_power",
                "\"IdlePowerWatts\" IS NULL OR (\"RunningPowerWatts\" IS NOT NULL AND \"IdlePowerWatts\" < \"RunningPowerWatts\")");
        });
        b.HasKey(x => x.MachineId);
        b.Property(x => x.MachineKind).HasMaxLength(12).IsRequired();
        b.Property(x => x.CountingMode).HasMaxLength(20).IsRequired();
        b.Property(x => x.UpdatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => x.FactoryId);
        b.HasOne<Machine>().WithOne().HasForeignKey<MachineMonitoringProfile>(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MachineStateConfiguration : IEntityTypeConfiguration<MachineState>
{
    public const string States = "'RUNNING','STOPPED','IDLE','MAINTENANCE','ERROR','OFFLINE','UNKNOWN'";

    public void Configure(EntityTypeBuilder<MachineState> b)
    {
        b.ToTable("machine_states", t =>
        {
            t.HasCheckConstraint("CK_machine_states_state", $"\"State\" IN ({States})");
            t.HasCheckConstraint("CK_machine_states_source", "\"SourceType\" IN ('REAL','SIMULATED')");
            t.HasCheckConstraint("CK_machine_states_maintenance", "NOT \"InMaintenance\" OR \"State\" = 'MAINTENANCE'");
        });
        b.HasKey(x => x.MachineId);
        b.Property(x => x.State).HasMaxLength(12).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(300).IsRequired();
        b.Property(x => x.Trigger).HasMaxLength(24).IsRequired();
        b.Property(x => x.SourceType).HasMaxLength(10).IsRequired();
        b.Property(x => x.ErrorCode).HasMaxLength(64);
        b.Property(x => x.MaintenanceReason).HasMaxLength(300);
        b.Property(x => x.MaintenanceByEmail).HasMaxLength(256);
        b.Property(x => x.ActiveConditions).HasColumnType("jsonb").IsRequired();
        foreach (var p in new[] { nameof(MachineState.Since), nameof(MachineState.LastStateEventAt), nameof(MachineState.LastTelemetryAt),
                     nameof(MachineState.LastCountAt), nameof(MachineState.ReadingsAt), nameof(MachineState.MaintenanceSince), nameof(MachineState.UpdatedAt) })
            b.Property(p).HasColumnType("timestamptz");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => new { x.FactoryId, x.State });
        b.HasOne<Machine>().WithOne().HasForeignKey<MachineState>(x => x.MachineId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.SourceDeviceId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class MachineStateTransitionConfiguration : IEntityTypeConfiguration<MachineStateTransition>
{
    public void Configure(EntityTypeBuilder<MachineStateTransition> b)
    {
        b.ToTable("machine_state_transitions", t =>
        {
            t.HasCheckConstraint("CK_machine_state_transitions_to", $"\"ToState\" IN ({MachineStateConfiguration.States})");
            t.HasCheckConstraint("CK_machine_state_transitions_from", $"\"FromState\" IS NULL OR \"FromState\" IN ({MachineStateConfiguration.States})");
            t.HasCheckConstraint("CK_machine_state_transitions_source", "\"SourceType\" IN ('REAL','SIMULATED')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityByDefaultColumn();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.FromState).HasMaxLength(12);
        b.Property(x => x.ToState).HasMaxLength(12).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(300).IsRequired();
        b.Property(x => x.Trigger).HasMaxLength(24).IsRequired();
        b.Property(x => x.SourceType).HasMaxLength(10).IsRequired();
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.OccurredAt).HasColumnType("timestamptz");
        b.Property(x => x.RecordedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.MachineId, x.OccurredAt });
        b.HasIndex(x => new { x.FactoryId, x.OccurredAt });
        b.HasIndex(x => new { x.LineId, x.OccurredAt });
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class MachineTelemetryConfiguration : IEntityTypeConfiguration<MachineTelemetry>
{
    public const string MessageIndex = "IX_machine_telemetry_DeviceId_MessageId";

    public void Configure(EntityTypeBuilder<MachineTelemetry> b)
    {
        b.ToTable("machine_telemetry", t =>
        {
            t.HasCheckConstraint("CK_machine_telemetry_source", "\"SourceType\" IN ('REAL','SIMULATED')");
            t.HasCheckConstraint("CK_machine_telemetry_transport", "\"Transport\" IN ('MQTT','HTTP','SIMULATOR')");
            // A simulator message is always simulated data, and simulated data only ever comes from the simulator.
            t.HasCheckConstraint("CK_machine_telemetry_simulated", "(\"Transport\" = 'SIMULATOR') = (\"SourceType\" = 'SIMULATED')");
            t.HasCheckConstraint("CK_machine_telemetry_outcome", "\"Outcome\" IN ('APPLIED','LATE','NO_MACHINE','MAINTENANCE','SOURCE_MISMATCH')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityByDefaultColumn();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.ReportedState).HasMaxLength(12);
        b.Property(x => x.ErrorCode).HasMaxLength(64);
        b.Property(x => x.Metrics).HasColumnType("jsonb");
        b.Property(x => x.MessageId).HasMaxLength(100);
        b.Property(x => x.SourceType).HasMaxLength(10).IsRequired();
        b.Property(x => x.Transport).HasMaxLength(10).IsRequired();
        b.Property(x => x.Outcome).HasMaxLength(12).IsRequired();
        b.Property(x => x.OccurredAt).HasColumnType("timestamptz");
        b.Property(x => x.ReceivedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.DeviceId, x.MessageId }).IsUnique().HasFilter("\"MessageId\" IS NOT NULL").HasDatabaseName(MessageIndex);
        b.HasIndex(x => new { x.MachineId, x.OccurredAt });
        b.HasIndex(x => x.ReceivedAt);
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionCounterEventConfiguration : IEntityTypeConfiguration<ProductionCounterEvent>
{
    public const string IdempotencyIndex = "IX_production_counter_events_FactoryId_IdempotencyKey";

    public void Configure(EntityTypeBuilder<ProductionCounterEvent> b)
    {
        b.ToTable("production_counter_events", t =>
        {
            t.HasCheckConstraint("CK_production_counter_events_mode", "\"CounterMode\" IN ('INCREMENT','ABSOLUTE','RESET')");
            t.HasCheckConstraint("CK_production_counter_events_status", "\"Status\" IN ('COUNTED','BASELINE','RESET','OUT_OF_ORDER','SUSPECT_JUMP')");
            t.HasCheckConstraint("CK_production_counter_events_source", "\"SourceType\" IN ('REAL','SIMULATED')");
            t.HasCheckConstraint("CK_production_counter_events_transport", "\"Transport\" IN ('MQTT','HTTP','SIMULATOR','MANUAL')");
            t.HasCheckConstraint("CK_production_counter_events_simulated", "\"Transport\" <> 'SIMULATOR' OR \"SourceType\" = 'SIMULATED'");
            t.HasCheckConstraint("CK_production_counter_events_counts", "\"Pulses\" >= 0 AND \"Pieces\" >= 0 AND (\"Status\" = 'COUNTED' OR \"Pieces\" = 0)");
            t.HasCheckConstraint("CK_production_counter_events_counting", "\"CountingMode\" IN ('PIECE','PULSES_PER_PIECE','CYCLE_NOT_PIECE')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityByDefaultColumn();
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.CounterMode).HasMaxLength(10).IsRequired();
        b.Property(x => x.Status).HasMaxLength(14).IsRequired();
        b.Property(x => x.Note).HasMaxLength(300);
        b.Property(x => x.CountingMode).HasMaxLength(20).IsRequired();
        b.Property(x => x.ShiftCode).HasMaxLength(32);
        b.Property(x => x.SourceType).HasMaxLength(10).IsRequired();
        b.Property(x => x.Transport).HasMaxLength(10).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.MessageId).HasMaxLength(100);
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.OccurredAt).HasColumnType("timestamptz");
        b.Property(x => x.ReceivedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.IdempotencyKey }).IsUnique().HasDatabaseName(IdempotencyIndex);
        // Report queries: by factory and time, by line, by machine, by shift day.
        b.HasIndex(x => new { x.FactoryId, x.SourceType, x.OccurredAt });
        b.HasIndex(x => new { x.LineId, x.OccurredAt });
        b.HasIndex(x => new { x.MachineId, x.OccurredAt });
        b.HasIndex(x => new { x.DeviceId, x.OccurredAt });
        b.HasIndex(x => new { x.FactoryId, x.ProductionDate, x.ShiftId });
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionCounterSnapshotConfiguration : IEntityTypeConfiguration<ProductionCounterSnapshot>
{
    public void Configure(EntityTypeBuilder<ProductionCounterSnapshot> b)
    {
        b.ToTable("production_counter_snapshots", t =>
            t.HasCheckConstraint("CK_production_counter_snapshots_totals", "\"TotalPulses\" >= 0 AND \"TotalPieces\" >= 0 AND \"PulseRemainder\" >= 0"));
        b.HasKey(x => x.DeviceId);
        b.Property(x => x.LastOccurredAt).HasColumnType("timestamptz");
        b.Property(x => x.LastResetAt).HasColumnType("timestamptz");
        b.Property(x => x.LastCountAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.Version).IsConcurrencyToken();
        b.HasIndex(x => x.MachineId);
        b.HasOne<Device>().WithOne().HasForeignKey<ProductionCounterSnapshot>(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductionCorrectionConfiguration : IEntityTypeConfiguration<ProductionCorrection>
{
    public void Configure(EntityTypeBuilder<ProductionCorrection> b)
    {
        b.ToTable("production_corrections", t =>
        {
            t.HasCheckConstraint("CK_production_corrections_delta", "\"PiecesDelta\" <> 0 AND \"PiecesDelta\" BETWEEN -100000 AND 100000");
            t.HasCheckConstraint("CK_production_corrections_source", "\"SourceType\" IN ('REAL','SIMULATED')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.ShiftCode).HasMaxLength(32);
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.Property(x => x.SourceType).HasMaxLength(10).IsRequired();
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.CreatedByRole).HasMaxLength(64);
        b.Property(x => x.HourStart).HasColumnType("timestamptz");
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.ProductionDate });
        b.HasIndex(x => new { x.LineId, x.ProductionDate });
        b.HasIndex(x => x.MachineId);
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
    }
}
