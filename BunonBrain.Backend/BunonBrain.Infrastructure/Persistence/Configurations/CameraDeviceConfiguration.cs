using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

// Cameras and devices. Nothing cascades a delete onto history. Rules the
// database itself guarantees:
//   * camera and device codes are unique within a factory;
//   * one current source per camera, one active coverage row per camera and
//     line / workstation, one device per camera (partial unique indexes);
//   * a simulator message is always marked simulated and a device message
//     never is (check constraints);
//   * zone revisions, heartbeats and device events are append-only
//     (triggers, added in the AddCamerasAndDevices migration);
//   * a repeated message id of one device is stored once (partial unique indexes).

public class CameraConfiguration : IEntityTypeConfiguration<Camera>
{
    public void Configure(EntityTypeBuilder<Camera> b)
    {
        b.ToTable("cameras", t =>
        {
            t.HasCheckConstraint("CK_cameras_type", "\"CameraType\" IN ('FixedCctv','IpCamera','UsbCamera','RtspCamera','Webcam','RecordedVideo')");
            t.HasCheckConstraint("CK_cameras_status", "\"Status\" IN ('Active','Inactive','Maintenance')");
            t.HasCheckConstraint("CK_cameras_reference_source", "\"ReferenceImageSource\" IS NULL OR \"ReferenceImageSource\" IN ('Upload','VideoFrame')");
            t.HasCheckConstraint("CK_cameras_reference_size", "\"ReferenceImageKey\" IS NULL OR (\"ReferenceImageWidth\" > 0 AND \"ReferenceImageHeight\" > 0)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(24).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.CameraType).HasMaxLength(16).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Location).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.StatusReason).HasMaxLength(500);
        b.Property(x => x.ReferenceImageKey).HasMaxLength(64);
        b.Property(x => x.ReferenceImageContentType).HasMaxLength(32);
        b.Property(x => x.ReferenceImageSource).HasMaxLength(16);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.StatusChangedAt).HasColumnType("timestamptz");
        b.Property(x => x.ReferenceImageUpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.Status });
        b.HasIndex(x => x.FloorId);
        b.HasIndex(x => x.LineId);
        b.HasIndex(x => x.DepartmentId);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionFloor>().WithMany().HasForeignKey(x => x.FloorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CameraSourceConfiguration : IEntityTypeConfiguration<CameraSource>
{
    public const string OneCurrentIndex = "IX_camera_sources_one_current";

    public void Configure(EntityTypeBuilder<CameraSource> b)
    {
        b.ToTable("camera_sources", t =>
        {
            t.HasCheckConstraint("CK_camera_sources_type", "\"SourceType\" IN ('None','Rtsp','LocalWebcam','RecordedVideo','HttpStream')");
            t.HasCheckConstraint("CK_camera_sources_url", "(\"SourceType\" IN ('Rtsp','HttpStream')) = (\"StreamUrl\" IS NOT NULL)");
            t.HasCheckConstraint("CK_camera_sources_video", "(\"SourceType\" = 'RecordedVideo') = (\"VideoKey\" IS NOT NULL)");
            t.HasCheckConstraint("CK_camera_sources_credentials", "\"CredentialsCiphertext\" IS NULL OR \"SourceType\" IN ('Rtsp','HttpStream')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.SourceType).HasMaxLength(16).IsRequired();
        b.Property(x => x.StreamUrl).HasMaxLength(500);
        b.Property(x => x.StreamHost).HasMaxLength(253);
        b.Property(x => x.CredentialsCiphertext).HasColumnType("bytea");
        b.Property(x => x.WebcamLabel).HasMaxLength(120);
        b.Property(x => x.VideoKey).HasMaxLength(64);
        b.Property(x => x.VideoContentType).HasMaxLength(32);
        b.Property(x => x.VideoFileName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.RetiredAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.CameraId, x.Version }).IsUnique();
        b.HasIndex(x => x.CameraId).IsUnique().HasDatabaseName(OneCurrentIndex).HasFilter("\"RetiredAt\" IS NULL");
        b.HasIndex(x => new { x.FactoryId, x.SourceType });
        b.HasOne<Camera>().WithMany().HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CameraCoverageConfiguration : IEntityTypeConfiguration<CameraCoverage>
{
    public void Configure(EntityTypeBuilder<CameraCoverage> b)
    {
        b.ToTable("camera_coverage", t =>
        {
            t.HasCheckConstraint("CK_camera_coverage_kind", "\"Kind\" IN ('Line','Workstation')");
            t.HasCheckConstraint("CK_camera_coverage_target", "(\"Kind\" = 'Workstation') = (\"WorkstationId\" IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasMaxLength(16).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32).IsRequired();
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.RemovedByEmail).HasMaxLength(256);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.RemovedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.CameraId, x.WorkstationId }).IsUnique()
            .HasDatabaseName("IX_camera_coverage_one_active_workstation").HasFilter("\"RemovedAt\" IS NULL AND \"WorkstationId\" IS NOT NULL");
        b.HasIndex(x => new { x.CameraId, x.LineId }).IsUnique()
            .HasDatabaseName("IX_camera_coverage_one_active_line").HasFilter("\"RemovedAt\" IS NULL AND \"Kind\" = 'Line'");
        b.HasIndex(x => new { x.LineId, x.RemovedAt });
        b.HasIndex(x => new { x.WorkstationId, x.RemovedAt });
        b.HasOne<Camera>().WithMany().HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CameraZoneConfiguration : IEntityTypeConfiguration<CameraZone>
{
    public void Configure(EntityTypeBuilder<CameraZone> b)
    {
        b.ToTable("camera_zones", t =>
        {
            t.HasCheckConstraint("CK_camera_zones_type", "\"ZoneType\" IN ('Workstation','ProductionLine','Walkway','Restricted','Other')");
            t.HasCheckConstraint("CK_camera_zones_polygon", "jsonb_typeof(\"Polygon\") = 'array' AND jsonb_array_length(\"Polygon\") BETWEEN 3 AND 64");
            t.HasCheckConstraint("CK_camera_zones_version", "\"Version\" >= 1");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.ZoneType).HasMaxLength(16).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Polygon).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.Color).HasMaxLength(7);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.UpdatedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.CameraId, x.IsActive });
        b.HasIndex(x => x.WorkstationId);
        b.HasIndex(x => new { x.FactoryId, x.ZoneType });
        b.HasOne<Camera>().WithMany().HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class CameraZoneRevisionConfiguration : IEntityTypeConfiguration<CameraZoneRevision>
{
    public void Configure(EntityTypeBuilder<CameraZoneRevision> b)
    {
        b.ToTable("camera_zone_revisions", t =>
            t.HasCheckConstraint("CK_camera_zone_revisions_action", "\"Action\" IN ('Created','Updated','Deactivated','Reactivated')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Action).HasMaxLength(16).IsRequired();
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.ZoneType).HasMaxLength(16).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Polygon).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.ReferenceImageKey).HasMaxLength(64);
        b.Property(x => x.ChangedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.ChangedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.ZoneId, x.Version }).IsUnique();
        b.HasIndex(x => new { x.CameraId, x.ChangedAt });
        b.HasOne<CameraZone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Camera>().WithMany().HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> b)
    {
        b.ToTable("devices", t =>
        {
            t.HasCheckConstraint("CK_devices_category", "\"Category\" IN ('RfidReader','MachineSensor','ProductionCounter','Esp32Gateway','IndustrialGateway','Camera','Other')");
            t.HasCheckConstraint("CK_devices_status", "\"Status\" IN ('Active','Maintenance','Disabled')");
            t.HasCheckConstraint("CK_devices_connection", "\"ConnectionStatus\" IN ('Online','Offline','Unknown')");
            t.HasCheckConstraint("CK_devices_heartbeat", "\"HeartbeatIntervalSeconds\" BETWEEN 5 AND 86400 AND \"OfflineAfterSeconds\" > \"HeartbeatIntervalSeconds\" AND \"OfflineAfterSeconds\" <= 604800");
            t.HasCheckConstraint("CK_devices_camera", "\"CameraId\" IS NULL OR \"Category\" = 'Camera'");
            t.HasCheckConstraint("CK_devices_reader", "\"AttendanceReaderId\" IS NULL OR \"Category\" = 'RfidReader'");
            t.HasCheckConstraint("CK_devices_metadata", "jsonb_typeof(\"Metadata\") = 'object'");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(32).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.Property(x => x.Category).HasMaxLength(24).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.StatusReason).HasMaxLength(500);
        b.Property(x => x.ConnectionStatus).HasMaxLength(16).IsRequired();
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Manufacturer).HasMaxLength(80);
        b.Property(x => x.Model).HasMaxLength(80);
        b.Property(x => x.SerialNumber).HasMaxLength(80);
        b.Property(x => x.FirmwareVersion).HasMaxLength(80);
        b.Property(x => x.Metadata).HasColumnType("jsonb").IsRequired();
        b.Property(x => x.LastErrorCode).HasMaxLength(64);
        b.Property(x => x.LastErrorMessage).HasMaxLength(500);
        b.Property(x => x.CreatedByEmail).HasMaxLength(256).IsRequired();
        foreach (var p in new[] { nameof(Device.LastSeenAt), nameof(Device.LastHeartbeatAt), nameof(Device.LastConnectedAt), nameof(Device.LastDisconnectedAt),
                     nameof(Device.ConnectionChangedAt), nameof(Device.OfflineDeadline), nameof(Device.LastErrorAt), nameof(Device.CreatedAt), nameof(Device.UpdatedAt) })
            b.Property(p).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.Category });
        b.HasIndex(x => new { x.ConnectionStatus, x.OfflineDeadline });
        b.HasIndex(x => x.LineId);
        b.HasIndex(x => x.WorkstationId);
        b.HasIndex(x => x.MachineId);
        b.HasIndex(x => x.CameraId).IsUnique().HasFilter("\"CameraId\" IS NOT NULL");
        b.HasIndex(x => x.AttendanceReaderId).IsUnique().HasFilter("\"AttendanceReaderId\" IS NOT NULL");
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionLine>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Workstation>().WithMany().HasForeignKey(x => x.WorkstationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Machine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Camera>().WithMany().HasForeignKey(x => x.CameraId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AttendanceReader>().WithMany().HasForeignKey(x => x.AttendanceReaderId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class DeviceHeartbeatConfiguration : IEntityTypeConfiguration<DeviceHeartbeat>
{
    public void Configure(EntityTypeBuilder<DeviceHeartbeat> b)
    {
        b.ToTable("device_heartbeats", t =>
        {
            t.HasCheckConstraint("CK_device_heartbeats_source", "\"Source\" IN ('Device','Simulator')");
            t.HasCheckConstraint("CK_device_heartbeats_simulated", "(\"Source\" = 'Simulator') = \"IsSimulated\"");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Source).HasMaxLength(16).IsRequired();
        b.Property(x => x.MessageId).HasMaxLength(100);
        b.Property(x => x.FirmwareVersion).HasMaxLength(80);
        b.Property(x => x.Metrics).HasColumnType("jsonb");
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.OccurredAt).HasColumnType("timestamptz");
        b.Property(x => x.ReceivedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.DeviceId, x.OccurredAt });
        b.HasIndex(x => new { x.DeviceId, x.MessageId }).IsUnique().HasFilter("\"MessageId\" IS NOT NULL");
        b.HasIndex(x => new { x.FactoryId, x.ReceivedAt });
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class DeviceEventConfiguration : IEntityTypeConfiguration<DeviceEvent>
{
    public void Configure(EntityTypeBuilder<DeviceEvent> b)
    {
        b.ToTable("device_events", t =>
        {
            t.HasCheckConstraint("CK_device_events_type", "\"EventType\" IN ('ConnectionOnline','ConnectionOffline','HeartbeatMissed','Error','StatusChanged','FirmwareChanged')");
            t.HasCheckConstraint("CK_device_events_severity", "\"Severity\" IN ('Info','Warning','Error')");
            t.HasCheckConstraint("CK_device_events_source", "\"Source\" IN ('Device','Simulator','System','Manual')");
            // A simulator message concerns a simulated device; a device message never does.
            t.HasCheckConstraint("CK_device_events_simulated", "(\"Source\" <> 'Simulator' OR \"IsSimulated\") AND (\"Source\" <> 'Device' OR NOT \"IsSimulated\")");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.EventType).HasMaxLength(24).IsRequired();
        b.Property(x => x.Severity).HasMaxLength(8).IsRequired();
        b.Property(x => x.Source).HasMaxLength(16).IsRequired();
        b.Property(x => x.MessageId).HasMaxLength(100);
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Message).HasMaxLength(500);
        b.Property(x => x.FromState).HasMaxLength(32);
        b.Property(x => x.ToState).HasMaxLength(32);
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.OccurredAt).HasColumnType("timestamptz");
        b.Property(x => x.ReceivedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.DeviceId, x.OccurredAt });
        b.HasIndex(x => new { x.DeviceId, x.MessageId }).IsUnique().HasFilter("\"MessageId\" IS NOT NULL");
        b.HasIndex(x => new { x.FactoryId, x.OccurredAt });
        b.HasIndex(x => new { x.FactoryId, x.EventType });
        b.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
