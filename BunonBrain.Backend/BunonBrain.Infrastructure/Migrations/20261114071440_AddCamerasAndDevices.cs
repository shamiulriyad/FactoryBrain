using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCamerasAndDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cameras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CameraType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FloorId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StatusReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    InstalledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReferenceImageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReferenceImageContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ReferenceImageWidth = table.Column<int>(type: "integer", nullable: true),
                    ReferenceImageHeight = table.Column<int>(type: "integer", nullable: true),
                    ReferenceImageSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ReferenceFrameSeconds = table.Column<double>(type: "double precision", nullable: true),
                    ReferenceImageUpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cameras", x => x.Id);
                    table.CheckConstraint("CK_cameras_reference_size", "\"ReferenceImageKey\" IS NULL OR (\"ReferenceImageWidth\" > 0 AND \"ReferenceImageHeight\" > 0)");
                    table.CheckConstraint("CK_cameras_reference_source", "\"ReferenceImageSource\" IS NULL OR \"ReferenceImageSource\" IN ('Upload','VideoFrame')");
                    table.CheckConstraint("CK_cameras_status", "\"Status\" IN ('Active','Inactive','Maintenance')");
                    table.CheckConstraint("CK_cameras_type", "\"CameraType\" IN ('FixedCctv','IpCamera','UsbCamera','RtspCamera','Webcam','RecordedVideo')");
                    table.ForeignKey(
                        name: "FK_cameras_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cameras_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cameras_production_floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "production_floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cameras_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "camera_coverage",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CameraId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    RemovedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camera_coverage", x => x.Id);
                    table.CheckConstraint("CK_camera_coverage_kind", "\"Kind\" IN ('Line','Workstation')");
                    table.CheckConstraint("CK_camera_coverage_target", "(\"Kind\" = 'Workstation') = (\"WorkstationId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_camera_coverage_cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camera_coverage_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camera_coverage_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camera_coverage_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "camera_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CameraId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StreamUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StreamHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    CredentialsCiphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    WebcamLabel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    VideoKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    VideoContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    VideoFileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    VideoSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camera_sources", x => x.Id);
                    table.CheckConstraint("CK_camera_sources_credentials", "\"CredentialsCiphertext\" IS NULL OR \"SourceType\" IN ('Rtsp','HttpStream')");
                    table.CheckConstraint("CK_camera_sources_type", "\"SourceType\" IN ('None','Rtsp','LocalWebcam','RecordedVideo','HttpStream')");
                    table.CheckConstraint("CK_camera_sources_url", "(\"SourceType\" IN ('Rtsp','HttpStream')) = (\"StreamUrl\" IS NOT NULL)");
                    table.CheckConstraint("CK_camera_sources_video", "(\"SourceType\" = 'RecordedVideo') = (\"VideoKey\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_camera_sources_cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camera_sources_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "camera_zones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CameraId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ZoneType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Polygon = table.Column<string>(type: "jsonb", nullable: false),
                    Color = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camera_zones", x => x.Id);
                    table.CheckConstraint("CK_camera_zones_polygon", "jsonb_typeof(\"Polygon\") = 'array' AND jsonb_array_length(\"Polygon\") BETWEEN 3 AND 64");
                    table.CheckConstraint("CK_camera_zones_type", "\"ZoneType\" IN ('Workstation','ProductionLine','Walkway','Restricted','Other')");
                    table.CheckConstraint("CK_camera_zones_version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_camera_zones_cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camera_zones_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camera_zones_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camera_zones_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Category = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StatusReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ConnectionStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: true),
                    CameraId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttendanceReaderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Manufacturer = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Model = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    SerialNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    FirmwareVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    InstalledOn = table.Column<DateOnly>(type: "date", nullable: true),
                    HeartbeatIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    OfflineAfterSeconds = table.Column<int>(type: "integer", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    LastConnectedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    LastDisconnectedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ConnectionChangedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    OfflineDeadline = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    LastErrorAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.Id);
                    table.CheckConstraint("CK_devices_camera", "\"CameraId\" IS NULL OR \"Category\" = 'Camera'");
                    table.CheckConstraint("CK_devices_category", "\"Category\" IN ('RfidReader','MachineSensor','ProductionCounter','Esp32Gateway','IndustrialGateway','Camera','Other')");
                    table.CheckConstraint("CK_devices_connection", "\"ConnectionStatus\" IN ('Online','Offline','Unknown')");
                    table.CheckConstraint("CK_devices_heartbeat", "\"HeartbeatIntervalSeconds\" BETWEEN 5 AND 86400 AND \"OfflineAfterSeconds\" > \"HeartbeatIntervalSeconds\" AND \"OfflineAfterSeconds\" <= 604800");
                    table.CheckConstraint("CK_devices_metadata", "jsonb_typeof(\"Metadata\") = 'object'");
                    table.CheckConstraint("CK_devices_reader", "\"AttendanceReaderId\" IS NULL OR \"Category\" = 'RfidReader'");
                    table.CheckConstraint("CK_devices_status", "\"Status\" IN ('Active','Maintenance','Disabled')");
                    table.ForeignKey(
                        name: "FK_devices_attendance_readers_AttendanceReaderId",
                        column: x => x.AttendanceReaderId,
                        principalTable: "attendance_readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_devices_cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_devices_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_devices_machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_devices_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_devices_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "camera_zone_revisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    CameraId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ZoneType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Polygon = table.Column<string>(type: "jsonb", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ReferenceImageKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camera_zone_revisions", x => x.Id);
                    table.CheckConstraint("CK_camera_zone_revisions_action", "\"Action\" IN ('Created','Updated','Deactivated','Reactivated')");
                    table.ForeignKey(
                        name: "FK_camera_zone_revisions_camera_zones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "camera_zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camera_zone_revisions_cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_camera_zone_revisions_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Severity = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FromState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ToState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_events", x => x.Id);
                    table.CheckConstraint("CK_device_events_severity", "\"Severity\" IN ('Info','Warning','Error')");
                    table.CheckConstraint("CK_device_events_simulated", "(\"Source\" <> 'Simulator' OR \"IsSimulated\") AND (\"Source\" <> 'Device' OR NOT \"IsSimulated\")");
                    table.CheckConstraint("CK_device_events_source", "\"Source\" IN ('Device','Simulator','System','Manual')");
                    table.CheckConstraint("CK_device_events_type", "\"EventType\" IN ('ConnectionOnline','ConnectionOffline','HeartbeatMissed','Error','StatusChanged','FirmwareChanged')");
                    table.ForeignKey(
                        name: "FK_device_events_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_device_events_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_heartbeats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FirmwareVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Metrics = table.Column<string>(type: "jsonb", nullable: true),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_heartbeats", x => x.Id);
                    table.CheckConstraint("CK_device_heartbeats_simulated", "(\"Source\" = 'Simulator') = \"IsSimulated\"");
                    table.CheckConstraint("CK_device_heartbeats_source", "\"Source\" IN ('Device','Simulator')");
                    table.ForeignKey(
                        name: "FK_device_heartbeats_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_device_heartbeats_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_camera_coverage_FactoryId",
                table: "camera_coverage",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_camera_coverage_LineId_RemovedAt",
                table: "camera_coverage",
                columns: new[] { "LineId", "RemovedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_camera_coverage_WorkstationId_RemovedAt",
                table: "camera_coverage",
                columns: new[] { "WorkstationId", "RemovedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_camera_coverage_one_active_line",
                table: "camera_coverage",
                columns: new[] { "CameraId", "LineId" },
                unique: true,
                filter: "\"RemovedAt\" IS NULL AND \"Kind\" = 'Line'");

            migrationBuilder.CreateIndex(
                name: "IX_camera_coverage_one_active_workstation",
                table: "camera_coverage",
                columns: new[] { "CameraId", "WorkstationId" },
                unique: true,
                filter: "\"RemovedAt\" IS NULL AND \"WorkstationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_camera_sources_CameraId_Version",
                table: "camera_sources",
                columns: new[] { "CameraId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camera_sources_FactoryId_SourceType",
                table: "camera_sources",
                columns: new[] { "FactoryId", "SourceType" });

            migrationBuilder.CreateIndex(
                name: "IX_camera_sources_one_current",
                table: "camera_sources",
                column: "CameraId",
                unique: true,
                filter: "\"RetiredAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_camera_zone_revisions_CameraId_ChangedAt",
                table: "camera_zone_revisions",
                columns: new[] { "CameraId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_camera_zone_revisions_FactoryId",
                table: "camera_zone_revisions",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_camera_zone_revisions_ZoneId_Version",
                table: "camera_zone_revisions",
                columns: new[] { "ZoneId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camera_zones_CameraId_IsActive",
                table: "camera_zones",
                columns: new[] { "CameraId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_camera_zones_FactoryId_ZoneType",
                table: "camera_zones",
                columns: new[] { "FactoryId", "ZoneType" });

            migrationBuilder.CreateIndex(
                name: "IX_camera_zones_LineId",
                table: "camera_zones",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_camera_zones_WorkstationId",
                table: "camera_zones",
                column: "WorkstationId");

            migrationBuilder.CreateIndex(
                name: "IX_cameras_DepartmentId",
                table: "cameras",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_cameras_FactoryId_Code",
                table: "cameras",
                columns: new[] { "FactoryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cameras_FactoryId_Status",
                table: "cameras",
                columns: new[] { "FactoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_cameras_FloorId",
                table: "cameras",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_cameras_LineId",
                table: "cameras",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_device_events_DeviceId_MessageId",
                table: "device_events",
                columns: new[] { "DeviceId", "MessageId" },
                unique: true,
                filter: "\"MessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_device_events_DeviceId_OccurredAt",
                table: "device_events",
                columns: new[] { "DeviceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_device_events_FactoryId_EventType",
                table: "device_events",
                columns: new[] { "FactoryId", "EventType" });

            migrationBuilder.CreateIndex(
                name: "IX_device_events_FactoryId_OccurredAt",
                table: "device_events",
                columns: new[] { "FactoryId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_device_heartbeats_DeviceId_MessageId",
                table: "device_heartbeats",
                columns: new[] { "DeviceId", "MessageId" },
                unique: true,
                filter: "\"MessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_device_heartbeats_DeviceId_OccurredAt",
                table: "device_heartbeats",
                columns: new[] { "DeviceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_device_heartbeats_FactoryId_ReceivedAt",
                table: "device_heartbeats",
                columns: new[] { "FactoryId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_devices_AttendanceReaderId",
                table: "devices",
                column: "AttendanceReaderId",
                unique: true,
                filter: "\"AttendanceReaderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_devices_CameraId",
                table: "devices",
                column: "CameraId",
                unique: true,
                filter: "\"CameraId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_devices_ConnectionStatus_OfflineDeadline",
                table: "devices",
                columns: new[] { "ConnectionStatus", "OfflineDeadline" });

            migrationBuilder.CreateIndex(
                name: "IX_devices_FactoryId_Category",
                table: "devices",
                columns: new[] { "FactoryId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_devices_FactoryId_Code",
                table: "devices",
                columns: new[] { "FactoryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_devices_LineId",
                table: "devices",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_devices_MachineId",
                table: "devices",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_devices_WorkstationId",
                table: "devices",
                column: "WorkstationId");

            // ── History is append-only ──────────────────────────────────────
            // Zone revisions, device heartbeats and device events are facts the
            // future AI and maintenance modules read: no UPDATE, DELETE or
            // TRUNCATE, whatever the application does.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION camera_device_history_reject_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '% is append-only (% rejected)', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER camera_zone_revisions_no_update_delete
                    BEFORE UPDATE OR DELETE ON camera_zone_revisions
                    FOR EACH ROW EXECUTE FUNCTION camera_device_history_reject_mutation();
                CREATE TRIGGER camera_zone_revisions_no_truncate
                    BEFORE TRUNCATE ON camera_zone_revisions
                    FOR EACH STATEMENT EXECUTE FUNCTION camera_device_history_reject_mutation();
                CREATE TRIGGER device_heartbeats_no_update_delete
                    BEFORE UPDATE OR DELETE ON device_heartbeats
                    FOR EACH ROW EXECUTE FUNCTION camera_device_history_reject_mutation();
                CREATE TRIGGER device_heartbeats_no_truncate
                    BEFORE TRUNCATE ON device_heartbeats
                    FOR EACH STATEMENT EXECUTE FUNCTION camera_device_history_reject_mutation();
                CREATE TRIGGER device_events_no_update_delete
                    BEFORE UPDATE OR DELETE ON device_events
                    FOR EACH ROW EXECUTE FUNCTION camera_device_history_reject_mutation();
                CREATE TRIGGER device_events_no_truncate
                    BEFORE TRUNCATE ON device_events
                    FOR EACH STATEMENT EXECUTE FUNCTION camera_device_history_reject_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS device_events_no_truncate ON device_events;
                DROP TRIGGER IF EXISTS device_events_no_update_delete ON device_events;
                DROP TRIGGER IF EXISTS device_heartbeats_no_truncate ON device_heartbeats;
                DROP TRIGGER IF EXISTS device_heartbeats_no_update_delete ON device_heartbeats;
                DROP TRIGGER IF EXISTS camera_zone_revisions_no_truncate ON camera_zone_revisions;
                DROP TRIGGER IF EXISTS camera_zone_revisions_no_update_delete ON camera_zone_revisions;
                DROP FUNCTION IF EXISTS camera_device_history_reject_mutation();
                """);

            migrationBuilder.DropTable(
                name: "camera_coverage");

            migrationBuilder.DropTable(
                name: "camera_sources");

            migrationBuilder.DropTable(
                name: "camera_zone_revisions");

            migrationBuilder.DropTable(
                name: "device_events");

            migrationBuilder.DropTable(
                name: "device_heartbeats");

            migrationBuilder.DropTable(
                name: "camera_zones");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropTable(
                name: "cameras");
        }
    }
}
