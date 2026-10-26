using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductionPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "production_lines",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Workstations = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_lines", x => x.Id);
                    table.CheckConstraint("CK_production_lines_id", "\"Id\" ~ '^[a-z0-9][a-z0-9_-]{0,31}$'");
                    table.CheckConstraint("CK_production_lines_workstations", "\"Workstations\" BETWEEN 1 AND 300");
                });

            migrationBuilder.CreateTable(
                name: "production_shifts",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    StartLocal = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndLocal = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    BreakStartLocal = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    BreakMinutes = table.Column<int>(type: "integer", nullable: false),
                    Weekdays = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_shifts", x => x.Code);
                    table.CheckConstraint("CK_production_shifts_break", "\"BreakMinutes\" >= 0 AND \"BreakMinutes\" <= 240");
                    table.CheckConstraint("CK_production_shifts_code", "\"Code\" ~ '^[a-z0-9][a-z0-9_-]{0,15}$'");
                    table.CheckConstraint("CK_production_shifts_times", "\"StartLocal\" <> \"EndLocal\"");
                    table.CheckConstraint("CK_production_shifts_weekdays", "\"Weekdays\" BETWEEN 1 AND 127");
                });

            migrationBuilder.CreateTable(
                name: "production_styles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ProductType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovedSmv = table.Column<double>(type: "double precision", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_styles", x => x.Id);
                    table.CheckConstraint("CK_production_styles_smv", "\"ApprovedSmv\" IS NULL OR (\"ApprovedSmv\" > 0 AND \"ApprovedSmv\" <= 600)");
                });

            migrationBuilder.CreateTable(
                name: "production_balance_proposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StyleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Workstations = table.Column<int>(type: "integer", nullable: false),
                    TargetOutputPerHour = table.Column<double>(type: "double precision", nullable: true),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BaselineLayoutId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ApprovalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    AppliedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StatusReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_balance_proposals", x => x.Id);
                    table.CheckConstraint("CK_balance_proposals_workstations", "\"Workstations\" BETWEEN 1 AND 300");
                    table.ForeignKey(
                        name: "FK_production_balance_proposals_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_balance_proposals_production_styles_StyleId",
                        column: x => x.StyleId,
                        principalTable: "production_styles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_line_layouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StyleId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    Origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProposalId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SupersededAt = table.Column<DateTime>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_line_layouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_line_layouts_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_line_layouts_production_styles_StyleId",
                        column: x => x.StyleId,
                        principalTable: "production_styles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StyleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Buyer = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_orders", x => x.Id);
                    table.CheckConstraint("CK_production_orders_quantity", "\"Quantity\" > 0 AND \"Quantity\" <= 10000000");
                    table.ForeignKey(
                        name: "FK_production_orders_production_styles_StyleId",
                        column: x => x.StyleId,
                        principalTable: "production_styles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_style_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StyleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Smv = table.Column<double>(type: "double precision", nullable: false),
                    MachineType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Predecessors = table.Column<int[]>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_style_operations", x => x.Id);
                    table.CheckConstraint("CK_style_operations_sequence", "\"Sequence\" BETWEEN 1 AND 9999");
                    table.CheckConstraint("CK_style_operations_smv", "\"Smv\" > 0 AND \"Smv\" <= 120");
                    table.ForeignKey(
                        name: "FK_production_style_operations_production_styles_StyleId",
                        column: x => x.StyleId,
                        principalTable: "production_styles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "production_line_layout_stations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LayoutId = table.Column<Guid>(type: "uuid", nullable: false),
                    StationNo = table.Column<int>(type: "integer", nullable: false),
                    OperationIds = table.Column<Guid[]>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_line_layout_stations", x => x.Id);
                    table.CheckConstraint("CK_layout_station_no", "\"StationNo\" BETWEEN 1 AND 300");
                    table.ForeignKey(
                        name: "FK_production_line_layout_stations_production_line_layouts_Lay~",
                        column: x => x.LayoutId,
                        principalTable: "production_line_layouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "production_output_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IntervalStartUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    IntervalEndUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ProductionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ShiftCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    ReworkQuantity = table.Column<int>(type: "integer", nullable: false),
                    RejectQuantity = table.Column<int>(type: "integer", nullable: false),
                    OperatorsPresent = table.Column<int>(type: "integer", nullable: true),
                    DowntimeMinutes = table.Column<double>(type: "double precision", nullable: true),
                    DowntimeReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SourceRef = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CorrectionOfId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsSuperseded = table.Column<bool>(type: "boolean", nullable: false),
                    SupersededById = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_output_events", x => x.Id);
                    table.CheckConstraint("CK_output_downtime", "\"DowntimeMinutes\" IS NULL OR \"DowntimeMinutes\" >= 0");
                    table.CheckConstraint("CK_output_interval", "\"IntervalEndUtc\" > \"IntervalStartUtc\"");
                    table.CheckConstraint("CK_output_operators", "\"OperatorsPresent\" IS NULL OR \"OperatorsPresent\" BETWEEN 1 AND 500");
                    table.CheckConstraint("CK_output_quantities", "\"Quantity\" >= 0 AND \"ReworkQuantity\" >= 0 AND \"RejectQuantity\" >= 0");
                    table.CheckConstraint("CK_output_superseded", "\"IsSuperseded\" = (\"SupersededById\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_production_output_events_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_output_events_production_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PlannedQuantity = table.Column<int>(type: "integer", nullable: false),
                    Operators = table.Column<int>(type: "integer", nullable: false),
                    TargetEfficiencyPct = table.Column<double>(type: "double precision", nullable: false),
                    ShiftCodes = table.Column<List<string>>(type: "text[]", nullable: false),
                    OvertimeMinutesPerDay = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ActivationApprovalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ActivatedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ActivatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ClosedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_plans", x => x.Id);
                    table.CheckConstraint("CK_production_plans_dates", "\"EndDate\" >= \"StartDate\"");
                    table.CheckConstraint("CK_production_plans_efficiency", "\"TargetEfficiencyPct\" > 0 AND \"TargetEfficiencyPct\" <= 150");
                    table.CheckConstraint("CK_production_plans_operators", "\"Operators\" BETWEEN 1 AND 500");
                    table.CheckConstraint("CK_production_plans_overtime", "\"OvertimeMinutesPerDay\" BETWEEN 0 AND 480");
                    table.CheckConstraint("CK_production_plans_quantity", "\"PlannedQuantity\" > 0");
                    table.CheckConstraint("CK_production_plans_shifts", "cardinality(\"ShiftCodes\") > 0");
                    table.ForeignKey(
                        name: "FK_production_plans_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_plans_production_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_operation_observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StyleId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CycleSeconds = table.Column<double>(type: "double precision", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    OperatorsAssigned = table.Column<int>(type: "integer", nullable: false),
                    QueueBefore = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_operation_observations", x => x.Id);
                    table.CheckConstraint("CK_observations_cycle", "\"CycleSeconds\" > 0 AND \"CycleSeconds\" <= 7200");
                    table.CheckConstraint("CK_observations_operators", "\"OperatorsAssigned\" BETWEEN 1 AND 100");
                    table.CheckConstraint("CK_observations_queue", "\"QueueBefore\" IS NULL OR \"QueueBefore\" >= 0");
                    table.CheckConstraint("CK_observations_samples", "\"SampleCount\" BETWEEN 1 AND 1000");
                    table.ForeignKey(
                        name: "FK_production_operation_observations_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_operation_observations_production_style_operatio~",
                        column: x => x.OperationId,
                        principalTable: "production_style_operations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_operation_observations_production_styles_StyleId",
                        column: x => x.StyleId,
                        principalTable: "production_styles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_plan_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PreviousValues = table.Column<string>(type: "jsonb", nullable: true),
                    ProposedValues = table.Column<string>(type: "jsonb", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BaseVersion = table.Column<long>(type: "bigint", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    DecidedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_plan_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_plan_changes_production_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "production_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_proposals_LineId_StyleId_CreatedAt",
                table: "production_balance_proposals",
                columns: new[] { "LineId", "StyleId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_proposals_Reference",
                table: "production_balance_proposals",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_balance_proposals_StyleId",
                table: "production_balance_proposals",
                column: "StyleId");

            migrationBuilder.CreateIndex(
                name: "IX_production_line_layout_stations_LayoutId_StationNo",
                table: "production_line_layout_stations",
                columns: new[] { "LayoutId", "StationNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_line_layouts_one_current",
                table: "production_line_layouts",
                columns: new[] { "LineId", "StyleId" },
                unique: true,
                filter: "\"IsCurrent\"");

            migrationBuilder.CreateIndex(
                name: "IX_production_line_layouts_StyleId",
                table: "production_line_layouts",
                column: "StyleId");

            migrationBuilder.CreateIndex(
                name: "IX_production_lines_Name",
                table: "production_lines",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_operation_observations_LineId_StyleId_OperationI~",
                table: "production_operation_observations",
                columns: new[] { "LineId", "StyleId", "OperationId", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_production_operation_observations_OperationId",
                table: "production_operation_observations",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_production_operation_observations_StyleId",
                table: "production_operation_observations",
                column: "StyleId");

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_OrderNumber",
                table: "production_orders",
                column: "OrderNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_Status_DueDate",
                table: "production_orders",
                columns: new[] { "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_StyleId",
                table: "production_orders",
                column: "StyleId");

            migrationBuilder.CreateIndex(
                name: "IX_output_events_live_interval",
                table: "production_output_events",
                columns: new[] { "LineId", "OrderId", "Stage", "IntervalStartUtc", "IntervalEndUtc" },
                unique: true,
                filter: "NOT \"IsSuperseded\"");

            migrationBuilder.CreateIndex(
                name: "IX_production_output_events_CorrectionOfId",
                table: "production_output_events",
                column: "CorrectionOfId");

            migrationBuilder.CreateIndex(
                name: "IX_production_output_events_LineId_IntervalStartUtc",
                table: "production_output_events",
                columns: new[] { "LineId", "IntervalStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_production_output_events_OrderId_Stage_IntervalStartUtc",
                table: "production_output_events",
                columns: new[] { "OrderId", "Stage", "IntervalStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_production_output_events_ProductionDate_LineId",
                table: "production_output_events",
                columns: new[] { "ProductionDate", "LineId" });

            migrationBuilder.CreateIndex(
                name: "IX_production_output_events_Source_SourceRef",
                table: "production_output_events",
                columns: new[] { "Source", "SourceRef" },
                unique: true,
                filter: "\"SourceRef\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_production_plan_changes_PlanId_RequestedAt",
                table: "production_plan_changes",
                columns: new[] { "PlanId", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_plan_changes_one_pending",
                table: "production_plan_changes",
                column: "PlanId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_production_plans_LineId_Status_StartDate_EndDate",
                table: "production_plans",
                columns: new[] { "LineId", "Status", "StartDate", "EndDate" });

            migrationBuilder.CreateIndex(
                name: "IX_production_plans_OrderId_Status",
                table: "production_plans",
                columns: new[] { "OrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_production_plans_Reference",
                table: "production_plans",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_shifts_Name",
                table: "production_shifts",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_style_operations_StyleId_Sequence",
                table: "production_style_operations",
                columns: new[] { "StyleId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_styles_Code",
                table: "production_styles",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_balance_proposals");

            migrationBuilder.DropTable(
                name: "production_line_layout_stations");

            migrationBuilder.DropTable(
                name: "production_operation_observations");

            migrationBuilder.DropTable(
                name: "production_output_events");

            migrationBuilder.DropTable(
                name: "production_plan_changes");

            migrationBuilder.DropTable(
                name: "production_shifts");

            migrationBuilder.DropTable(
                name: "production_line_layouts");

            migrationBuilder.DropTable(
                name: "production_style_operations");

            migrationBuilder.DropTable(
                name: "production_plans");

            migrationBuilder.DropTable(
                name: "production_lines");

            migrationBuilder.DropTable(
                name: "production_orders");

            migrationBuilder.DropTable(
                name: "production_styles");
        }
    }
}
