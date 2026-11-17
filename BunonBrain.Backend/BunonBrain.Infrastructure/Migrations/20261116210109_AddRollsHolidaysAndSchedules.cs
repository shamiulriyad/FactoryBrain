using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRollsHolidaysAndSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduleFrequency",
                table: "document_type_settings",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "DAILY");

            migrationBuilder.AddColumn<string>(
                name: "ScheduleScope",
                table: "document_type_settings",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "FACTORY");

            migrationBuilder.AddColumn<int>(
                name: "WeekStartDay",
                table: "document_type_settings",
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.CreateTable(
                name: "document_schedule_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    LineKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FirstAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_schedule_runs", x => x.Id);
                    table.CheckConstraint("CK_document_schedule_runs_period", "\"PeriodTo\" >= \"PeriodFrom\"");
                    table.CheckConstraint("CK_document_schedule_runs_status", "\"Status\" IN ('CREATED','EXISTS','NO_DATA','FAILED')");
                    table.ForeignKey(
                        name: "FK_document_schedule_runs_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_document_schedule_runs_factory_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "factory_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "factory_holidays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_holidays", x => x.Id);
                    table.CheckConstraint("CK_factory_holidays_kind", "\"Kind\" IN ('PUBLIC','FACTORY')");
                    table.ForeignKey(
                        name: "FK_factory_holidays_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_rolls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    RollNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    RollKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    LotNumber = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    LotKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SupplierKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Color = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ShadeGroup = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    WidthCm = table.Column<decimal>(type: "numeric(8,2)", precision: 8, scale: 2, nullable: true),
                    Warehouse = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Location = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ReceivedQuantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    GrnDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrnReference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_rolls", x => x.Id);
                    table.CheckConstraint("CK_inventory_rolls_quantity", "\"ReceivedQuantity\" > 0");
                    table.CheckConstraint("CK_inventory_rolls_width", "\"WidthCm\" IS NULL OR \"WidthCm\" > 0");
                    table.ForeignKey(
                        name: "FK_inventory_rolls_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_rolls_factory_documents_GrnDocumentId",
                        column: x => x.GrnDocumentId,
                        principalTable: "factory_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_rolls_inventory_items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "inventory_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_roll_movements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    RollId = table.Column<Guid>(type: "uuid", nullable: false),
                    StockMovementId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentReference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RowNo = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    MovementDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_roll_movements", x => x.Id);
                    table.CheckConstraint("CK_inventory_roll_movements_kind", "\"Kind\" IN ('RECEIPT','ISSUE')");
                    table.CheckConstraint("CK_inventory_roll_movements_quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_inventory_roll_movements_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_roll_movements_factory_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "factory_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_roll_movements_inventory_rolls_RollId",
                        column: x => x.RollId,
                        principalTable: "inventory_rolls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_roll_movements_stock_movements_StockMovementId",
                        column: x => x.StockMovementId,
                        principalTable: "stock_movements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_document_type_settings_frequency",
                table: "document_type_settings",
                sql: "\"ScheduleFrequency\" IN ('DAILY','WEEKLY','MONTHLY')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_document_type_settings_scope",
                table: "document_type_settings",
                sql: "\"ScheduleScope\" IN ('FACTORY','EACH_LINE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_document_type_settings_week_start",
                table: "document_type_settings",
                sql: "\"WeekStartDay\" BETWEEN 0 AND 6");

            migrationBuilder.CreateIndex(
                name: "IX_document_schedule_runs_DocumentId",
                table: "document_schedule_runs",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_document_schedule_runs_FactoryId_LastAttemptAt",
                table: "document_schedule_runs",
                columns: new[] { "FactoryId", "LastAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "UX_document_schedule_runs_period",
                table: "document_schedule_runs",
                columns: new[] { "FactoryId", "TypeCode", "PeriodFrom", "PeriodTo", "LineKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_factory_holidays_day",
                table: "factory_holidays",
                columns: new[] { "FactoryId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_roll_movements_FactoryId_RollId",
                table: "inventory_roll_movements",
                columns: new[] { "FactoryId", "RollId" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_roll_movements_RollId",
                table: "inventory_roll_movements",
                column: "RollId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_roll_movements_StockMovementId",
                table: "inventory_roll_movements",
                column: "StockMovementId");

            migrationBuilder.CreateIndex(
                name: "UX_inventory_roll_movements_document_row",
                table: "inventory_roll_movements",
                columns: new[] { "DocumentId", "RowNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_rolls_FactoryId_ItemId",
                table: "inventory_rolls",
                columns: new[] { "FactoryId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_rolls_GrnDocumentId",
                table: "inventory_rolls",
                column: "GrnDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_rolls_ItemId",
                table: "inventory_rolls",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "UX_inventory_rolls_identity",
                table: "inventory_rolls",
                columns: new[] { "FactoryId", "SupplierKey", "LotKey", "RollKey" },
                unique: true);
            // Roll stock is a ledger: the roll movements and the received rolls are never edited or deleted
            // (same rule as stock_movements; corrections are new documents).
            migrationBuilder.Sql(@"
                CREATE TRIGGER inventory_roll_movements_no_update_delete BEFORE UPDATE OR DELETE ON inventory_roll_movements
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER inventory_roll_movements_no_truncate BEFORE TRUNCATE ON inventory_roll_movements
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER inventory_rolls_no_update_delete BEFORE UPDATE OR DELETE ON inventory_rolls
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER inventory_rolls_no_truncate BEFORE TRUNCATE ON inventory_rolls
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS inventory_roll_movements_no_update_delete ON inventory_roll_movements;
                DROP TRIGGER IF EXISTS inventory_roll_movements_no_truncate ON inventory_roll_movements;
                DROP TRIGGER IF EXISTS inventory_rolls_no_update_delete ON inventory_rolls;
                DROP TRIGGER IF EXISTS inventory_rolls_no_truncate ON inventory_rolls;
            ");

            migrationBuilder.DropTable(
                name: "document_schedule_runs");

            migrationBuilder.DropTable(
                name: "factory_holidays");

            migrationBuilder.DropTable(
                name: "inventory_roll_movements");

            migrationBuilder.DropTable(
                name: "inventory_rolls");

            migrationBuilder.DropCheckConstraint(
                name: "CK_document_type_settings_frequency",
                table: "document_type_settings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_document_type_settings_scope",
                table: "document_type_settings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_document_type_settings_week_start",
                table: "document_type_settings");

            migrationBuilder.DropColumn(
                name: "ScheduleFrequency",
                table: "document_type_settings");

            migrationBuilder.DropColumn(
                name: "ScheduleScope",
                table: "document_type_settings");

            migrationBuilder.DropColumn(
                name: "WeekStartDay",
                table: "document_type_settings");
        }
    }
}
