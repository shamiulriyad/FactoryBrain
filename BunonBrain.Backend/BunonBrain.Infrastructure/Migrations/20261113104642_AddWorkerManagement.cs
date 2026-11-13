using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkerManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workers_FactoryId_EmployeeCode",
                table: "workers");

            migrationBuilder.CreateSequence(
                name: "worker_number_seq");

            migrationBuilder.AlterColumn<string>(
                name: "EmployeeCode",
                table: "workers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "workers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DateOfBirth",
                table: "workers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentId",
                table: "workers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "workers",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmergencyContactName",
                table: "workers",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmergencyContactPhone",
                table: "workers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmergencyContactRelation",
                table: "workers",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmploymentType",
                table: "workers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Permanent");

            migrationBuilder.AddColumn<Guid>(
                name: "FloorId",
                table: "workers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "workers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoContentType",
                table: "workers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhotoKey",
                table: "workers",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PresentAddress",
                table: "workers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "workers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StatusChangedAt",
                table: "workers",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusReason",
                table: "workers",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkerCode",
                table: "workers",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "WorkerNumber",
                table: "workers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "departments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_departments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_departments_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_floors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_floors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_floors_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "worker_cards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CardType = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    IssuedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeactivatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    DeactivatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeactivationReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_cards", x => x.Id);
                    table.CheckConstraint("CK_worker_cards_type", "\"CardType\" IN ('RFID','NFC')");
                    table.ForeignKey(
                        name: "FK_worker_cards_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_cards_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "worker_status_history",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EffectiveOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_worker_status_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_worker_status_history_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // ── Existing workers keep their data ────────────────────────────
            // Status replaces the old IsActive flag.
            migrationBuilder.Sql(
                """
                UPDATE workers SET "Status" = CASE WHEN "IsActive" THEN 'Active' ELSE 'Inactive' END,
                                   "StatusChangedAt" = "UpdatedAt";
                """);

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "workers");

            // Every existing worker gets a permanent Worker ID, oldest first.
            migrationBuilder.Sql(
                """
                UPDATE workers w
                SET "WorkerNumber" = s.n,
                    "WorkerCode" = 'W-' || CASE WHEN s.n < 100 THEN lpad(s.n::text, 3, '0') ELSE s.n::text END
                FROM (
                    SELECT o."Id", nextval('worker_number_seq') AS n
                    FROM (SELECT "Id" FROM workers ORDER BY "CreatedAt", "Id") o
                ) s
                WHERE w."Id" = s."Id";
                """);

            // The status history starts with what is known today.
            migrationBuilder.Sql(
                """
                INSERT INTO worker_status_history ("WorkerId", "FactoryId", "FromStatus", "ToStatus", "Reason", "EffectiveOn", "ChangedAt", "ChangedByUserId", "ChangedByEmail")
                SELECT "Id", "FactoryId", NULL, "Status", 'Status when status history was introduced', NULL, now(), NULL, 'system:worker-migration'
                FROM workers;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_workers_DepartmentId",
                table: "workers",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_workers_FactoryId_EmployeeCode",
                table: "workers",
                columns: new[] { "FactoryId", "EmployeeCode" },
                unique: true,
                filter: "\"EmployeeCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workers_FactoryId_Status",
                table: "workers",
                columns: new[] { "FactoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_workers_FloorId",
                table: "workers",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_workers_WorkerCode",
                table: "workers",
                column: "WorkerCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workers_WorkerNumber",
                table: "workers",
                column: "WorkerNumber",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_workers_employment_type",
                table: "workers",
                sql: "\"EmploymentType\" IN ('Permanent','Probation','Contract','Temporary','Apprentice')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workers_gender",
                table: "workers",
                sql: "\"Gender\" IS NULL OR \"Gender\" IN ('Female','Male','Other')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workers_status",
                table: "workers",
                sql: "\"Status\" IN ('Active','Inactive','Suspended','Resigned','Terminated')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workers_worker_number",
                table: "workers",
                sql: "\"WorkerNumber\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_departments_FactoryId_Code",
                table: "departments",
                columns: new[] { "FactoryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_departments_FactoryId_Name",
                table: "departments",
                columns: new[] { "FactoryId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_floors_FactoryId_Code",
                table: "production_floors",
                columns: new[] { "FactoryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_floors_FactoryId_Name",
                table: "production_floors",
                columns: new[] { "FactoryId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_worker_cards_CardUid",
                table: "worker_cards",
                column: "CardUid",
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_worker_cards_FactoryId",
                table: "worker_cards",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_worker_cards_WorkerId",
                table: "worker_cards",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_worker_status_history_WorkerId_Id",
                table: "worker_status_history",
                columns: new[] { "WorkerId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_workers_departments_DepartmentId",
                table: "workers",
                column: "DepartmentId",
                principalTable: "departments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workers_production_floors_FloorId",
                table: "workers",
                column: "FloorId",
                principalTable: "production_floors",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workers_departments_DepartmentId",
                table: "workers");

            migrationBuilder.DropForeignKey(
                name: "FK_workers_production_floors_FloorId",
                table: "workers");

            migrationBuilder.DropTable(
                name: "departments");

            migrationBuilder.DropTable(
                name: "production_floors");

            migrationBuilder.DropTable(
                name: "worker_cards");

            migrationBuilder.DropTable(
                name: "worker_status_history");

            migrationBuilder.DropIndex(
                name: "IX_workers_DepartmentId",
                table: "workers");

            migrationBuilder.DropIndex(
                name: "IX_workers_FactoryId_EmployeeCode",
                table: "workers");

            migrationBuilder.DropIndex(
                name: "IX_workers_FactoryId_Status",
                table: "workers");

            migrationBuilder.DropIndex(
                name: "IX_workers_FloorId",
                table: "workers");

            migrationBuilder.DropIndex(
                name: "IX_workers_WorkerCode",
                table: "workers");

            migrationBuilder.DropIndex(
                name: "IX_workers_WorkerNumber",
                table: "workers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workers_employment_type",
                table: "workers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workers_gender",
                table: "workers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workers_status",
                table: "workers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workers_worker_number",
                table: "workers");

            // Restore the columns the old model needs from the data they were replaced by.
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "workers",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(
                """
                UPDATE workers SET "IsActive" = ("Status" = 'Active'),
                                   "EmployeeCode" = COALESCE("EmployeeCode", "WorkerCode");
                """);

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "DateOfBirth",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "DepartmentId",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "EmergencyContactName",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "EmergencyContactPhone",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "EmergencyContactRelation",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "EmploymentType",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "FloorId",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "PhotoContentType",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "PhotoKey",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "PresentAddress",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "StatusChangedAt",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "StatusReason",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "WorkerCode",
                table: "workers");

            migrationBuilder.DropColumn(
                name: "WorkerNumber",
                table: "workers");

            migrationBuilder.DropSequence(
                name: "worker_number_seq");

            migrationBuilder.AlterColumn<string>(
                name: "EmployeeCode",
                table: "workers",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_workers_FactoryId_EmployeeCode",
                table: "workers",
                columns: new[] { "FactoryId", "EmployeeCode" },
                unique: true);
        }
    }
}
