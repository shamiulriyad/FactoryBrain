using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkstationAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workstations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    FloorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Code = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Capacity = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workstations", x => x.Id);
                    table.CheckConstraint("CK_workstations_capacity", "\"Capacity\" BETWEEN 1 AND 20");
                    table.CheckConstraint("CK_workstations_position", "\"Position\" >= 0");
                    table.ForeignKey(
                        name: "FK_workstations_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workstations_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workstations_production_floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "production_floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workstations_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "machines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    MachineType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Brand = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    SerialNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_machines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_machines_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_machines_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workstation_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    FloorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PreviousAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EndedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workstation_assignments", x => x.Id);
                    table.CheckConstraint("CK_workstation_assignments_open", "(\"Status\" = 'Active') = (\"EndAt\" IS NULL)");
                    table.CheckConstraint("CK_workstation_assignments_period", "\"EndAt\" IS NULL OR \"EndAt\" > \"StartAt\"");
                    table.CheckConstraint("CK_workstation_assignments_status", "\"Status\" IN ('Active','Ended','Transferred')");
                    table.ForeignKey(
                        name: "FK_workstation_assignments_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workstation_assignments_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workstation_assignments_machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workstation_assignments_production_floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "production_floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workstation_assignments_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workstation_assignments_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workstation_assignments_workstation_assignments_PreviousAss~",
                        column: x => x.PreviousAssignmentId,
                        principalTable: "workstation_assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workstation_assignments_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_machines_FactoryId_Code",
                table: "machines",
                columns: new[] { "FactoryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_machines_WorkstationId",
                table: "machines",
                column: "WorkstationId");

            migrationBuilder.CreateIndex(
                name: "IX_workstation_assignments_DepartmentId",
                table: "workstation_assignments",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_workstation_assignments_FactoryId_Status",
                table: "workstation_assignments",
                columns: new[] { "FactoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_workstation_assignments_FloorId",
                table: "workstation_assignments",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_workstation_assignments_LineId_EndAt",
                table: "workstation_assignments",
                columns: new[] { "LineId", "EndAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workstation_assignments_MachineId",
                table: "workstation_assignments",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_workstation_assignments_PreviousAssignmentId",
                table: "workstation_assignments",
                column: "PreviousAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_workstation_assignments_WorkerId_StartAt",
                table: "workstation_assignments",
                columns: new[] { "WorkerId", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workstation_assignments_WorkstationId_EndAt",
                table: "workstation_assignments",
                columns: new[] { "WorkstationId", "EndAt" });

            migrationBuilder.CreateIndex(
                name: "IX_workstation_assignments_one_active_primary",
                table: "workstation_assignments",
                column: "WorkerId",
                unique: true,
                filter: "\"EndAt\" IS NULL AND \"IsPrimary\"");

            migrationBuilder.CreateIndex(
                name: "IX_workstations_DepartmentId",
                table: "workstations",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_workstations_FactoryId_Code",
                table: "workstations",
                columns: new[] { "FactoryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workstations_FloorId",
                table: "workstations",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_workstations_LineId_Position",
                table: "workstations",
                columns: new[] { "LineId", "Position" });

            // ── No overlapping periods for one worker ──────────────────────
            // A worker's primary assignments may touch (a transfer closes one at
            // the instant the next opens: [start, end) ranges) but never overlap.
            // The unique index above already allows only one open assignment;
            // this also covers back-dated starts. btree_gist lets the uuid
            // equality take part in the GiST index. Supabase keeps extensions in
            // the "extensions" schema; elsewhere the default schema is used.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'btree_gist') THEN
                    IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'extensions') THEN
                      CREATE EXTENSION btree_gist WITH SCHEMA extensions;
                    ELSE
                      CREATE EXTENSION btree_gist;
                    END IF;
                  END IF;
                END
                $$;
                """);
            migrationBuilder.Sql(
                """
                ALTER TABLE workstation_assignments
                  ADD CONSTRAINT "EX_workstation_assignments_no_overlap"
                  EXCLUDE USING gist ("WorkerId" WITH =, tstzrange("StartAt", COALESCE("EndAt", 'infinity'::timestamptz), '[)') WITH &&)
                  WHERE ("IsPrimary");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workstation_assignments");

            migrationBuilder.DropTable(
                name: "machines");

            migrationBuilder.DropTable(
                name: "workstations");
        }
    }
}
