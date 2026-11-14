using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCentralMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "monitoring_alert_number_seq");

            migrationBuilder.CreateTable(
                name: "monitoring_simulation_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CameraIds = table.Column<string>(type: "jsonb", nullable: false),
                    EventTypes = table.Column<string>(type: "jsonb", nullable: false),
                    EventsPerMinute = table.Column<int>(type: "integer", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    Seed = table.Column<int>(type: "integer", nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    StartedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    NextEventAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    StoppedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    StoppedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StopReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitoring_simulation_runs", x => x.Id);
                    table.CheckConstraint("CK_monitoring_simulation_runs_duration", "\"DurationMinutes\" BETWEEN 1 AND 120");
                    table.CheckConstraint("CK_monitoring_simulation_runs_rate", "\"EventsPerMinute\" BETWEEN 1 AND 120");
                    table.CheckConstraint("CK_monitoring_simulation_runs_status", "\"Status\" IN ('RUNNING','STOPPED','COMPLETED')");
                    table.ForeignKey(
                        name: "FK_monitoring_simulation_runs_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Category = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Producer = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FloorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CameraId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    TrackId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdentityMethod = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    WorkerCardId = table.Column<Guid>(type: "uuid", nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Severity = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    ModelName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ModelVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    SimulationRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitoring_events", x => x.Id);
                    table.CheckConstraint("CK_monitoring_events_category", "\"Category\" IN ('VISION','SYSTEM')");
                    table.CheckConstraint("CK_monitoring_events_confidence", "\"Confidence\" IS NULL OR (\"Confidence\" >= 0 AND \"Confidence\" <= 1)");
                    table.CheckConstraint("CK_monitoring_events_producer", "\"Producer\" IN ('API','SIMULATOR','DEVICE_PIPELINE','CAMERA_SERVICE')");
                    table.CheckConstraint("CK_monitoring_events_producer_source", "(\"Producer\" <> 'SIMULATOR' OR \"SourceType\" = 'SIMULATED') AND (\"Producer\" <> 'API' OR \"SourceType\" = 'REAL')");
                    table.CheckConstraint("CK_monitoring_events_severity", "\"Severity\" IN ('INFO','WARNING','CRITICAL')");
                    table.CheckConstraint("CK_monitoring_events_simulated_anonymous", "\"SourceType\" <> 'SIMULATED' OR \"WorkerId\" IS NULL");
                    table.CheckConstraint("CK_monitoring_events_source", "\"SourceType\" IN ('SIMULATED','REAL')");
                    table.CheckConstraint("CK_monitoring_events_type", "\"EventType\" IN ('PERSON_DETECTED','TRACK_UPDATED','PERSON_LOST','ZONE_ENTERED','ZONE_EXITED','ACTIVITY_CLASSIFIED','UNCERTAIN_DETECTION','CAMERA_STATUS_CHANGED','DEVICE_STATUS_CHANGED','MODEL_STATUS_CHANGED')");
                    table.CheckConstraint("CK_monitoring_events_uncertain_anonymous", "\"EventType\" <> 'UNCERTAIN_DETECTION' OR \"WorkerId\" IS NULL");
                    table.CheckConstraint("CK_monitoring_events_worker_verified", "(\"WorkerId\" IS NULL) = (\"IdentityMethod\" IS NULL) AND (\"IdentityMethod\" IS NULL OR \"IdentityMethod\" = 'RFID_BADGE')");
                    table.ForeignKey(
                        name: "FK_monitoring_events_camera_zones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "camera_zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_events_cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_events_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_events_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_events_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_events_monitoring_simulation_runs_SimulationRunId",
                        column: x => x.SimulationRunId,
                        principalTable: "monitoring_simulation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_events_production_floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "production_floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_events_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_events_worker_cards_WorkerCardId",
                        column: x => x.WorkerCardId,
                        principalTable: "worker_cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_events_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_events_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('monitoring_alert_number_seq')"),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlertType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Category = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Severity = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FloorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CameraId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    LastEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceType = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    FirstOccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    LastOccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    Status = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    EscalationLevel = table.Column<int>(type: "integer", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    AcknowledgedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AcknowledgedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EscalatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    EscalatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EscalatedToRole = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ResolvedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResolutionCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ResolutionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DismissedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    DismissedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DismissReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Evidence = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitoring_alerts", x => x.Id);
                    table.CheckConstraint("CK_monitoring_alerts_category", "\"Category\" IN ('VISION','SYSTEM')");
                    table.CheckConstraint("CK_monitoring_alerts_dismissed", "\"Status\" <> 'DISMISSED' OR (\"DismissedAt\" IS NOT NULL AND \"DismissReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_monitoring_alerts_escalated", "\"Status\" <> 'ESCALATED' OR \"EscalationLevel\" > 0");
                    table.CheckConstraint("CK_monitoring_alerts_resolved", "\"Status\" <> 'RESOLVED' OR (\"ResolvedAt\" IS NOT NULL AND \"ResolutionCode\" IS NOT NULL AND \"ResolutionNote\" IS NOT NULL AND \"AcknowledgedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_monitoring_alerts_severity", "\"Severity\" IN ('LOW','MEDIUM','HIGH','CRITICAL')");
                    table.CheckConstraint("CK_monitoring_alerts_source", "\"SourceType\" IN ('SIMULATED','REAL')");
                    table.CheckConstraint("CK_monitoring_alerts_status", "\"Status\" IN ('OPEN','ACKNOWLEDGED','ESCALATED','RESOLVED','DISMISSED')");
                    table.ForeignKey(
                        name: "FK_monitoring_alerts_camera_zones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "camera_zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alerts_cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alerts_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alerts_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alerts_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alerts_monitoring_events_EventId",
                        column: x => x.EventId,
                        principalTable: "monitoring_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alerts_monitoring_events_LastEventId",
                        column: x => x.LastEventId,
                        principalTable: "monitoring_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alerts_production_floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "production_floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alerts_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alerts_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_event_reviews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ReviewedByRole = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitoring_event_reviews", x => x.Id);
                    table.CheckConstraint("CK_monitoring_event_reviews_decision", "\"Decision\" IN ('CONFIRMED','FALSE_POSITIVE','STILL_UNCERTAIN','NEEDS_FOLLOW_UP')");
                    table.ForeignKey(
                        name: "FK_monitoring_event_reviews_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_event_reviews_monitoring_events_EventId",
                        column: x => x.EventId,
                        principalTable: "monitoring_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "monitoring_alert_actions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    AlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EscalatedToRole = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ResolutionCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    EventId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActorRole = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monitoring_alert_actions", x => x.Id);
                    table.CheckConstraint("CK_monitoring_alert_actions_action", "\"Action\" IN ('CREATED','REPEATED','SOURCE_RECOVERED','ACKNOWLEDGED','ESCALATED','RESOLVED','DISMISSED')");
                    table.CheckConstraint("CK_monitoring_alert_actions_status", "\"ToStatus\" IN ('OPEN','ACKNOWLEDGED','ESCALATED','RESOLVED','DISMISSED')");
                    table.ForeignKey(
                        name: "FK_monitoring_alert_actions_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alert_actions_monitoring_alerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "monitoring_alerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_monitoring_alert_actions_monitoring_events_EventId",
                        column: x => x.EventId,
                        principalTable: "monitoring_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alert_actions_AlertId_At",
                table: "monitoring_alert_actions",
                columns: new[] { "AlertId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alert_actions_EventId",
                table: "monitoring_alert_actions",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alert_actions_FactoryId",
                table: "monitoring_alert_actions",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_CameraId",
                table: "monitoring_alerts",
                column: "CameraId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_DepartmentId",
                table: "monitoring_alerts",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_DeviceId",
                table: "monitoring_alerts",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_EventId",
                table: "monitoring_alerts",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_FactoryId_CreatedAt",
                table: "monitoring_alerts",
                columns: new[] { "FactoryId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_FactoryId_Status_Severity",
                table: "monitoring_alerts",
                columns: new[] { "FactoryId", "Status", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_FloorId",
                table: "monitoring_alerts",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_LastEventId",
                table: "monitoring_alerts",
                column: "LastEventId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_LineId_Status",
                table: "monitoring_alerts",
                columns: new[] { "LineId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_Number",
                table: "monitoring_alerts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_WorkstationId",
                table: "monitoring_alerts",
                column: "WorkstationId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_ZoneId",
                table: "monitoring_alerts",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_one_active",
                table: "monitoring_alerts",
                columns: new[] { "FactoryId", "DedupKey" },
                unique: true,
                filter: "\"Status\" IN ('OPEN','ACKNOWLEDGED','ESCALATED')");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_event_reviews_EventId_ReviewedAt",
                table: "monitoring_event_reviews",
                columns: new[] { "EventId", "ReviewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_event_reviews_FactoryId",
                table: "monitoring_event_reviews",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_CameraId_OccurredAt",
                table: "monitoring_events",
                columns: new[] { "CameraId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_CameraId_TrackId",
                table: "monitoring_events",
                columns: new[] { "CameraId", "TrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_DepartmentId",
                table: "monitoring_events",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_DeviceId_OccurredAt",
                table: "monitoring_events",
                columns: new[] { "DeviceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_FactoryId_EventType_OccurredAt",
                table: "monitoring_events",
                columns: new[] { "FactoryId", "EventType", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_FactoryId_IdempotencyKey",
                table: "monitoring_events",
                columns: new[] { "FactoryId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_FactoryId_OccurredAt",
                table: "monitoring_events",
                columns: new[] { "FactoryId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_FloorId",
                table: "monitoring_events",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_LineId_OccurredAt",
                table: "monitoring_events",
                columns: new[] { "LineId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_SimulationRunId",
                table: "monitoring_events",
                column: "SimulationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_WorkerCardId",
                table: "monitoring_events",
                column: "WorkerCardId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_WorkerId",
                table: "monitoring_events",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_WorkstationId",
                table: "monitoring_events",
                column: "WorkstationId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_ZoneId",
                table: "monitoring_events",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_simulation_runs_FactoryId_StartedAt",
                table: "monitoring_simulation_runs",
                columns: new[] { "FactoryId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_simulation_runs_Status_NextEventAt",
                table: "monitoring_simulation_runs",
                columns: new[] { "Status", "NextEventAt" });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_simulation_runs_one_running",
                table: "monitoring_simulation_runs",
                column: "FactoryId",
                unique: true,
                filter: "\"Status\" = 'RUNNING'");

            // ── Raw events and history are append-only ──────────────────────
            // Monitoring events are kept exactly as received; reviews and alert
            // actions are the record of what people decided. No UPDATE, DELETE
            // or TRUNCATE, whatever the application does. Alerts themselves
            // change state, but only through validated actions (history above).
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION monitoring_history_reject_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION '% is append-only (% rejected)', TG_TABLE_NAME, TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER monitoring_events_no_update_delete
                    BEFORE UPDATE OR DELETE ON monitoring_events
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER monitoring_events_no_truncate
                    BEFORE TRUNCATE ON monitoring_events
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER monitoring_event_reviews_no_update_delete
                    BEFORE UPDATE OR DELETE ON monitoring_event_reviews
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER monitoring_event_reviews_no_truncate
                    BEFORE TRUNCATE ON monitoring_event_reviews
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER monitoring_alert_actions_no_update_delete
                    BEFORE UPDATE OR DELETE ON monitoring_alert_actions
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER monitoring_alert_actions_no_truncate
                    BEFORE TRUNCATE ON monitoring_alert_actions
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS monitoring_alert_actions_no_truncate ON monitoring_alert_actions;
                DROP TRIGGER IF EXISTS monitoring_alert_actions_no_update_delete ON monitoring_alert_actions;
                DROP TRIGGER IF EXISTS monitoring_event_reviews_no_truncate ON monitoring_event_reviews;
                DROP TRIGGER IF EXISTS monitoring_event_reviews_no_update_delete ON monitoring_event_reviews;
                DROP TRIGGER IF EXISTS monitoring_events_no_truncate ON monitoring_events;
                DROP TRIGGER IF EXISTS monitoring_events_no_update_delete ON monitoring_events;
                DROP FUNCTION IF EXISTS monitoring_history_reject_mutation();
                """);

            migrationBuilder.DropTable(
                name: "monitoring_alert_actions");

            migrationBuilder.DropTable(
                name: "monitoring_event_reviews");

            migrationBuilder.DropTable(
                name: "monitoring_alerts");

            migrationBuilder.DropTable(
                name: "monitoring_events");

            migrationBuilder.DropTable(
                name: "monitoring_simulation_runs");

            migrationBuilder.DropSequence(
                name: "monitoring_alert_number_seq");
        }
    }
}
