using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityAssociation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "worker_track_association_number_seq");

            migrationBuilder.CreateTable(
                name: "rfid_reader_mappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Precision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FloorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CameraZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    Location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EffectiveFrom = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    EffectiveTo = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EndedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rfid_reader_mappings", x => x.Id);
                    table.CheckConstraint("CK_rfid_reader_mappings_precision", "\"Precision\" IN ('WORKSTATION','ZONE','LINE','FLOOR','AREA')");
                    table.ForeignKey(
                        name: "FK_rfid_reader_mappings_camera_zones_CameraZoneId",
                        column: x => x.CameraZoneId,
                        principalTable: "camera_zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfid_reader_mappings_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfid_reader_mappings_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfid_reader_mappings_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfid_reader_mappings_production_floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "production_floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfid_reader_mappings_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfid_reader_mappings_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "rfid_read_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MappingId = table.Column<Guid>(type: "uuid", nullable: true),
                    CardId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CardHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CardSuffix = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Source = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OutcomeCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    Evaluation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    EvaluationCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rfid_read_events", x => x.Id);
                    table.CheckConstraint("CK_rfid_read_events_evaluation", "\"Evaluation\" IS NULL OR \"Evaluation\" IN ('PENDING','VERIFIED','AMBIGUOUS','UNVERIFIED','MANUAL_REVIEW')");
                    table.CheckConstraint("CK_rfid_read_events_outcome", "\"Outcome\" IN ('ACCEPTED','REJECTED')");
                    table.CheckConstraint("CK_rfid_read_events_rejected", "\"Outcome\" = 'ACCEPTED' OR (\"WorkerId\" IS NULL AND \"Evaluation\" IS NULL)");
                    table.CheckConstraint("CK_rfid_read_events_simulator", "(\"Source\" = 'Simulator') = \"IsSimulated\"");
                    table.CheckConstraint("CK_rfid_read_events_source", "\"Source\" IN ('Device','Api','Simulator')");
                    table.ForeignKey(
                        name: "FK_rfid_read_events_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfid_read_events_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfid_read_events_rfid_reader_mappings_MappingId",
                        column: x => x.MappingId,
                        principalTable: "rfid_reader_mappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfid_read_events_worker_cards_CardId",
                        column: x => x.CardId,
                        principalTable: "worker_cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rfid_read_events_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "worker_track_associations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "nextval('worker_track_association_number_seq')"),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CameraId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrackId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    VerificationMethod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ReasonCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Summary = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    RfidReadEventId = table.Column<long>(type: "bigint", nullable: true),
                    AssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CandidateWorkers = table.Column<string>(type: "jsonb", nullable: false),
                    CandidateTracks = table.Column<string>(type: "jsonb", nullable: false),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_track_associations", x => x.Id);
                    table.CheckConstraint("CK_worker_track_associations_expiry", "\"ExpiresAt\" > \"StartAt\"");
                    table.CheckConstraint("CK_worker_track_associations_method", "\"VerificationMethod\" IS NULL OR \"VerificationMethod\" IN ('RFID_WORKSTATION','MANUAL_REVIEW')");
                    table.CheckConstraint("CK_worker_track_associations_status", "\"Status\" IN ('PENDING','VERIFIED','AMBIGUOUS','UNVERIFIED','EXPIRED','REVOKED','MANUAL_REVIEW')");
                    table.CheckConstraint("CK_worker_track_associations_verified_worker", "\"Status\" <> 'VERIFIED' OR \"WorkerId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_worker_track_associations_camera_zones_ZoneId",
                        column: x => x.ZoneId,
                        principalTable: "camera_zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_track_associations_cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_track_associations_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_track_associations_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_track_associations_rfid_read_events_RfidReadEventId",
                        column: x => x.RfidReadEventId,
                        principalTable: "rfid_read_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_track_associations_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_track_associations_workstation_assignments_Assignmen~",
                        column: x => x.AssignmentId,
                        principalTable: "workstation_assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_track_associations_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "identity_association_evidence",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssociationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Effect = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    RefType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RefId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_association_evidence", x => x.Id);
                    table.CheckConstraint("CK_identity_association_evidence_effect", "\"Effect\" IN ('SUPPORTS','CONTRADICTS','CONTEXT')");
                    table.ForeignKey(
                        name: "FK_identity_association_evidence_worker_track_associations_Ass~",
                        column: x => x.AssociationId,
                        principalTable: "worker_track_associations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "identity_association_history",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssociationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsManual = table.Column<bool>(type: "boolean", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActorRole = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_identity_association_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_identity_association_history_worker_track_associations_Asso~",
                        column: x => x.AssociationId,
                        principalTable: "worker_track_associations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_identity_association_evidence_AssociationId",
                table: "identity_association_evidence",
                column: "AssociationId");

            migrationBuilder.CreateIndex(
                name: "IX_identity_association_history_AssociationId_At",
                table: "identity_association_history",
                columns: new[] { "AssociationId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_identity_association_history_FactoryId_At",
                table: "identity_association_history",
                columns: new[] { "FactoryId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_rfid_read_events_CardId",
                table: "rfid_read_events",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_rfid_read_events_DeviceId_OccurredAt",
                table: "rfid_read_events",
                columns: new[] { "DeviceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_rfid_read_events_FactoryId_Evaluation",
                table: "rfid_read_events",
                columns: new[] { "FactoryId", "Evaluation" });

            migrationBuilder.CreateIndex(
                name: "IX_rfid_read_events_FactoryId_IdempotencyKey",
                table: "rfid_read_events",
                columns: new[] { "FactoryId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rfid_read_events_MappingId_OccurredAt",
                table: "rfid_read_events",
                columns: new[] { "MappingId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_rfid_read_events_WorkerId_OccurredAt",
                table: "rfid_read_events",
                columns: new[] { "WorkerId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_rfid_reader_mappings_CameraZoneId",
                table: "rfid_reader_mappings",
                column: "CameraZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_rfid_reader_mappings_DepartmentId",
                table: "rfid_reader_mappings",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_rfid_reader_mappings_DeviceId_EffectiveFrom",
                table: "rfid_reader_mappings",
                columns: new[] { "DeviceId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_rfid_reader_mappings_FactoryId",
                table: "rfid_reader_mappings",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_rfid_reader_mappings_FloorId",
                table: "rfid_reader_mappings",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_rfid_reader_mappings_LineId",
                table: "rfid_reader_mappings",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_rfid_reader_mappings_WorkstationId",
                table: "rfid_reader_mappings",
                column: "WorkstationId");

            migrationBuilder.CreateIndex(
                name: "IX_rfid_reader_mappings_one_active",
                table: "rfid_reader_mappings",
                column: "DeviceId",
                unique: true,
                filter: "\"EffectiveTo\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_AssignmentId",
                table: "worker_track_associations",
                column: "AssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_CameraId_TrackId_CreatedAt",
                table: "worker_track_associations",
                columns: new[] { "CameraId", "TrackId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_FactoryId_Status_UpdatedAt",
                table: "worker_track_associations",
                columns: new[] { "FactoryId", "Status", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_LineId",
                table: "worker_track_associations",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_Number",
                table: "worker_track_associations",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_RfidReadEventId",
                table: "worker_track_associations",
                column: "RfidReadEventId");

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_WorkerId_CreatedAt",
                table: "worker_track_associations",
                columns: new[] { "WorkerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_WorkstationId",
                table: "worker_track_associations",
                column: "WorkstationId");

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_ZoneId",
                table: "worker_track_associations",
                column: "ZoneId");

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_one_verified_track",
                table: "worker_track_associations",
                columns: new[] { "CameraId", "TrackId" },
                unique: true,
                filter: "\"Status\" = 'VERIFIED'");

            migrationBuilder.CreateIndex(
                name: "IX_worker_track_associations_one_verified_worker_camera",
                table: "worker_track_associations",
                columns: new[] { "WorkerId", "CameraId" },
                unique: true,
                filter: "\"Status\" = 'VERIFIED'");

            // Evidence and history are the record of how identities were decided:
            // append-only, whatever the application does (same rule as monitoring history).
            migrationBuilder.Sql(
                """
                CREATE TRIGGER identity_association_evidence_no_update_delete
                    BEFORE UPDATE OR DELETE ON identity_association_evidence
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER identity_association_evidence_no_truncate
                    BEFORE TRUNCATE ON identity_association_evidence
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER identity_association_history_no_update_delete
                    BEFORE UPDATE OR DELETE ON identity_association_history
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER identity_association_history_no_truncate
                    BEFORE TRUNCATE ON identity_association_history
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS identity_association_evidence_no_update_delete ON identity_association_evidence;
                DROP TRIGGER IF EXISTS identity_association_evidence_no_truncate ON identity_association_evidence;
                DROP TRIGGER IF EXISTS identity_association_history_no_update_delete ON identity_association_history;
                DROP TRIGGER IF EXISTS identity_association_history_no_truncate ON identity_association_history;
                """);

            migrationBuilder.DropTable(
                name: "identity_association_evidence");

            migrationBuilder.DropTable(
                name: "identity_association_history");

            migrationBuilder.DropTable(
                name: "worker_track_associations");

            migrationBuilder.DropTable(
                name: "rfid_read_events");

            migrationBuilder.DropTable(
                name: "rfid_reader_mappings");

            migrationBuilder.DropSequence(
                name: "worker_track_association_number_seq");
        }
    }
}
