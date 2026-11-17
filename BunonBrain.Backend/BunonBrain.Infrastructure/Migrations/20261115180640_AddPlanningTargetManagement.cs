using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningTargetManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "ColorVariants",
                table: "production_styles",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "Complexity",
                table: "production_styles",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "production_styles",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GarmentCategory",
                table: "production_styles",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "QualityCheckpoints",
                table: "production_styles",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "SizeRange",
                table: "production_styles",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<Guid>(
                name: "FactoryId",
                table: "production_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrderReference",
                table: "production_orders",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PlannedCompletionDate",
                table: "production_orders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Priority",
                table: "production_orders",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "NORMAL");

            migrationBuilder.AddColumn<string>(
                name: "ProductCategory",
                table: "production_orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductDescription",
                table: "production_orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "production_orders",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StatusReason",
                table: "production_orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "daily_production_plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RevisionNo = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    BasedOnPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevisionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_production_plans", x => x.Id);
                    table.CheckConstraint("CK_daily_plans_revision", "\"RevisionNo\" >= 1");
                    table.CheckConstraint("CK_daily_plans_status", "\"Status\" IN ('DRAFT','UNDER_REVIEW','APPROVED','PUBLISHED','REVISED','CANCELLED')");
                    table.ForeignKey(
                        name: "FK_daily_production_plans_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "line_capacity_settings",
                columns: table => new
                {
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EfficiencyPct = table.Column<double>(type: "double precision", nullable: true),
                    PlannedOperators = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_line_capacity_settings", x => x.LineId);
                    table.CheckConstraint("CK_line_capacity_settings_efficiency", "\"EfficiencyPct\" IS NULL OR (\"EfficiencyPct\" > 0 AND \"EfficiencyPct\" <= 150)");
                    table.CheckConstraint("CK_line_capacity_settings_operators", "\"PlannedOperators\" IS NULL OR (\"PlannedOperators\" BETWEEN 1 AND 500)");
                    table.ForeignKey(
                        name: "FK_line_capacity_settings_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_line_capacity_settings_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "line_output_points",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DeactivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeactivatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_line_output_points", x => x.Id);
                    table.ForeignKey(
                        name: "FK_line_output_points_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_line_output_points_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_line_output_points_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_allocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    PlannedStart = table.Column<DateOnly>(type: "date", nullable: false),
                    PlannedEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    Priority = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    SupervisorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupervisorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OverAllocated = table.Column<bool>(type: "boolean", nullable: false),
                    OverrideReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OverrideByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_allocations", x => x.Id);
                    table.CheckConstraint("CK_production_allocations_dates", "\"PlannedEnd\" >= \"PlannedStart\"");
                    table.CheckConstraint("CK_production_allocations_override", "NOT \"OverAllocated\" OR (\"OverrideReason\" IS NOT NULL AND \"OverrideByEmail\" IS NOT NULL)");
                    table.CheckConstraint("CK_production_allocations_priority", "\"Priority\" IN ('LOW','NORMAL','HIGH','URGENT')");
                    table.CheckConstraint("CK_production_allocations_quantity", "\"Quantity\" > 0 AND \"Quantity\" <= 10000000");
                    table.CheckConstraint("CK_production_allocations_status", "\"Status\" IN ('PLANNED','ACTIVE','COMPLETED','CANCELLED')");
                    table.ForeignKey(
                        name: "FK_production_allocations_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_allocations_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_allocations_production_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_exceptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Severity = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    AllocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlanItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SupportingData = table.Column<string>(type: "jsonb", nullable: false),
                    SuggestedInvestigation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastDetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Occurrences = table.Column<int>(type: "integer", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResolutionNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AutoResolved = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_exceptions", x => x.Id);
                    table.CheckConstraint("CK_production_exceptions_severity", "\"Severity\" IN ('LOW','MEDIUM','HIGH')");
                    table.CheckConstraint("CK_production_exceptions_source", "\"SourceType\" IN ('REAL','SIMULATED')");
                    table.CheckConstraint("CK_production_exceptions_status", "\"Status\" IN ('OPEN','ACKNOWLEDGED','RESOLVED')");
                    table.ForeignKey(
                        name: "FK_production_exceptions_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_exceptions_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_exceptions_production_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_order_status_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_order_status_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_order_status_changes_production_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "daily_plan_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Details = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_plan_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_daily_plan_events_daily_production_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "daily_production_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "daily_plan_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanDate = table.Column<DateOnly>(type: "date", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetQuantity = table.Column<int>(type: "integer", nullable: false),
                    TargetSource = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupervisorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_plan_items", x => x.Id);
                    table.CheckConstraint("CK_daily_plan_items_source", "\"TargetSource\" IN ('MANUAL','TARGET')");
                    table.CheckConstraint("CK_daily_plan_items_target", "\"TargetQuantity\" > 0 AND \"TargetQuantity\" <= 10000000");
                    table.ForeignKey(
                        name: "FK_daily_plan_items_daily_production_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "daily_production_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_daily_plan_items_production_allocations_AllocationId",
                        column: x => x.AllocationId,
                        principalTable: "production_allocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_daily_plan_items_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_daily_plan_items_production_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_daily_plan_items_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_allocation_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PreviousValues = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    NewValues = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_allocation_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_allocation_changes_production_allocations_Alloca~",
                        column: x => x.AllocationId,
                        principalTable: "production_allocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_targets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SupersedesId = table.Column<Guid>(type: "uuid", nullable: true),
                    Level = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Period = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FloorId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    AllocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    ApprovalStatus = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    IsSuperseded = table.Column<bool>(type: "boolean", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ChangeReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_targets", x => x.Id);
                    table.CheckConstraint("CK_production_targets_approval", "\"ApprovalStatus\" IN ('NOT_REQUIRED','PENDING','APPROVED','REJECTED')");
                    table.CheckConstraint("CK_production_targets_dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
                    table.CheckConstraint("CK_production_targets_level", "\"Level\" IN ('FACTORY','DEPARTMENT','FLOOR','LINE','SHIFT','WORKSTATION','MACHINE')");
                    table.CheckConstraint("CK_production_targets_period", "\"Period\" IN ('DAILY','SHIFT','HOURLY')");
                    table.CheckConstraint("CK_production_targets_quantity", "\"Quantity\" > 0 AND \"Quantity\" <= 10000000");
                    table.CheckConstraint("CK_production_targets_revision", "\"Revision\" >= 1");
                    table.ForeignKey(
                        name: "FK_production_targets_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_targets_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_targets_machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_targets_production_allocations_AllocationId",
                        column: x => x.AllocationId,
                        principalTable: "production_allocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_targets_production_floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "production_floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_targets_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_targets_production_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_targets_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_targets_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_target_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    PreviousQuantity = table.Column<int>(type: "integer", nullable: true),
                    NewQuantity = table.Column<int>(type: "integer", nullable: false),
                    PreviousEffectiveFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    NewEffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_target_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_production_target_revisions_production_targets_TargetId",
                        column: x => x.TargetId,
                        principalTable: "production_targets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_production_orders_FactoryId_Status_DueDate",
                table: "production_orders",
                columns: new[] { "FactoryId", "Status", "DueDate" });

            // The order lifecycle replaced open / closed: an open order is in progress, a closed one is completed.
            // An order recorded before factories were attached joins the factory when the installation has exactly one.
            migrationBuilder.Sql(@"
                UPDATE production_orders SET ""Status"" = 'InProgress' WHERE ""Status"" = 'Open';
                UPDATE production_orders SET ""Status"" = 'Completed' WHERE ""Status"" = 'Closed';
                UPDATE production_orders SET ""FactoryId"" = (SELECT ""Id"" FROM factories ORDER BY ""CreatedAt"" LIMIT 1)
                 WHERE ""FactoryId"" IS NULL AND (SELECT count(*) FROM factories) = 1;
                INSERT INTO production_order_status_changes (""Id"", ""OrderId"", ""FromStatus"", ""ToStatus"", ""Reason"", ""ChangedAt"", ""ChangedByUserId"", ""ChangedByEmail"")
                SELECT gen_random_uuid(), o.""Id"", NULL, CASE o.""Status"" WHEN 'InProgress' THEN 'in_progress' WHEN 'Completed' THEN 'completed' ELSE 'cancelled' END,
                       'Recorded before the order lifecycle existed', o.""CreatedAt"", o.""CreatedByUserId"", o.""CreatedByEmail""
                  FROM production_orders o;
            ");

            migrationBuilder.AddCheckConstraint(
                name: "CK_production_orders_dates",
                table: "production_orders",
                sql: "\"PlannedCompletionDate\" IS NULL OR \"StartDate\" IS NULL OR \"PlannedCompletionDate\" >= \"StartDate\"");

            migrationBuilder.AddCheckConstraint(
                name: "CK_production_orders_priority",
                table: "production_orders",
                sql: "\"Priority\" IN ('LOW','NORMAL','HIGH','URGENT')");

            migrationBuilder.CreateIndex(
                name: "IX_daily_plan_events_PlanId_At",
                table: "daily_plan_events",
                columns: new[] { "PlanId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_daily_plan_items_AllocationId",
                table: "daily_plan_items",
                column: "AllocationId");

            migrationBuilder.CreateIndex(
                name: "IX_daily_plan_items_FactoryId_PlanDate_LineId_ShiftId",
                table: "daily_plan_items",
                columns: new[] { "FactoryId", "PlanDate", "LineId", "ShiftId" });

            migrationBuilder.CreateIndex(
                name: "IX_daily_plan_items_LineId",
                table: "daily_plan_items",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_daily_plan_items_OrderId",
                table: "daily_plan_items",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_daily_plan_items_PlanId_LineId_ShiftId",
                table: "daily_plan_items",
                columns: new[] { "PlanId", "LineId", "ShiftId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_daily_plan_items_ShiftId",
                table: "daily_plan_items",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_daily_production_plans_FactoryId_PlanDate_RevisionNo",
                table: "daily_production_plans",
                columns: new[] { "FactoryId", "PlanDate", "RevisionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_daily_production_plans_Reference",
                table: "daily_production_plans",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_daily_plans_open",
                table: "daily_production_plans",
                columns: new[] { "FactoryId", "PlanDate" },
                unique: true,
                filter: "\"Status\" IN ('DRAFT','UNDER_REVIEW','APPROVED')");

            migrationBuilder.CreateIndex(
                name: "IX_line_capacity_settings_FactoryId",
                table: "line_capacity_settings",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_line_output_points_DeviceId",
                table: "line_output_points",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_line_output_points_FactoryId",
                table: "line_output_points",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "UX_line_output_points_active",
                table: "line_output_points",
                columns: new[] { "LineId", "DeviceId" },
                unique: true,
                filter: "\"IsActive\"");

            migrationBuilder.CreateIndex(
                name: "IX_production_allocation_changes_AllocationId_ChangedAt",
                table: "production_allocation_changes",
                columns: new[] { "AllocationId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_allocation_changes_OrderId",
                table: "production_allocation_changes",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_production_allocations_FactoryId_LineId_PlannedStart_Planne~",
                table: "production_allocations",
                columns: new[] { "FactoryId", "LineId", "PlannedStart", "PlannedEnd" });

            migrationBuilder.CreateIndex(
                name: "IX_production_allocations_LineId",
                table: "production_allocations",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_production_allocations_OrderId_Status",
                table: "production_allocations",
                columns: new[] { "OrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_production_exceptions_FactoryId_Status_Severity",
                table: "production_exceptions",
                columns: new[] { "FactoryId", "Status", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_production_exceptions_LineId",
                table: "production_exceptions",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_production_exceptions_OrderId",
                table: "production_exceptions",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "UX_production_exceptions_open",
                table: "production_exceptions",
                column: "DedupeKey",
                unique: true,
                filter: "\"Status\" <> 'RESOLVED'");

            migrationBuilder.CreateIndex(
                name: "IX_production_order_status_changes_OrderId_ChangedAt",
                table: "production_order_status_changes",
                columns: new[] { "OrderId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_target_revisions_TargetId",
                table: "production_target_revisions",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_production_target_revisions_TargetKey_At",
                table: "production_target_revisions",
                columns: new[] { "TargetKey", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_production_targets_AllocationId",
                table: "production_targets",
                column: "AllocationId");

            migrationBuilder.CreateIndex(
                name: "IX_production_targets_DepartmentId",
                table: "production_targets",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_production_targets_FactoryId_Level_LineId_EffectiveFrom",
                table: "production_targets",
                columns: new[] { "FactoryId", "Level", "LineId", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_production_targets_FloorId",
                table: "production_targets",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_production_targets_LineId",
                table: "production_targets",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_production_targets_MachineId",
                table: "production_targets",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_production_targets_OrderId",
                table: "production_targets",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_production_targets_ShiftId",
                table: "production_targets",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_production_targets_TargetKey_Revision",
                table: "production_targets",
                columns: new[] { "TargetKey", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_targets_WorkstationId",
                table: "production_targets",
                column: "WorkstationId");

            migrationBuilder.CreateIndex(
                name: "UX_production_targets_pending",
                table: "production_targets",
                column: "TargetKey",
                unique: true,
                filter: "\"ApprovalStatus\" = 'PENDING'");

            migrationBuilder.AddForeignKey(
                name: "FK_production_orders_factories_FactoryId",
                table: "production_orders",
                column: "FactoryId",
                principalTable: "factories",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Histories are append-only: a status change, allocation change, target revision or plan event is never edited or removed
            // (the monitoring module's reject function is reused).
            migrationBuilder.Sql(@"
                CREATE TRIGGER production_order_status_changes_no_update_delete BEFORE UPDATE OR DELETE ON production_order_status_changes
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER production_allocation_changes_no_update_delete BEFORE UPDATE OR DELETE ON production_allocation_changes
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER production_target_revisions_no_update_delete BEFORE UPDATE OR DELETE ON production_target_revisions
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER daily_plan_events_no_update_delete BEFORE UPDATE OR DELETE ON daily_plan_events
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS production_order_status_changes_no_update_delete ON production_order_status_changes;
                DROP TRIGGER IF EXISTS production_allocation_changes_no_update_delete ON production_allocation_changes;
                DROP TRIGGER IF EXISTS production_target_revisions_no_update_delete ON production_target_revisions;
                DROP TRIGGER IF EXISTS daily_plan_events_no_update_delete ON daily_plan_events;
                UPDATE production_orders SET ""Status"" = 'Open' WHERE ""Status"" IN ('Draft','Planned','InProgress','OnHold');
                UPDATE production_orders SET ""Status"" = 'Closed' WHERE ""Status"" = 'Completed';
            ");

            migrationBuilder.DropForeignKey(
                name: "FK_production_orders_factories_FactoryId",
                table: "production_orders");

            migrationBuilder.DropTable(
                name: "daily_plan_events");

            migrationBuilder.DropTable(
                name: "daily_plan_items");

            migrationBuilder.DropTable(
                name: "line_capacity_settings");

            migrationBuilder.DropTable(
                name: "line_output_points");

            migrationBuilder.DropTable(
                name: "production_allocation_changes");

            migrationBuilder.DropTable(
                name: "production_exceptions");

            migrationBuilder.DropTable(
                name: "production_order_status_changes");

            migrationBuilder.DropTable(
                name: "production_target_revisions");

            migrationBuilder.DropTable(
                name: "daily_production_plans");

            migrationBuilder.DropTable(
                name: "production_targets");

            migrationBuilder.DropTable(
                name: "production_allocations");

            migrationBuilder.DropIndex(
                name: "IX_production_orders_FactoryId_Status_DueDate",
                table: "production_orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_production_orders_dates",
                table: "production_orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_production_orders_priority",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "ColorVariants",
                table: "production_styles");

            migrationBuilder.DropColumn(
                name: "Complexity",
                table: "production_styles");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "production_styles");

            migrationBuilder.DropColumn(
                name: "GarmentCategory",
                table: "production_styles");

            migrationBuilder.DropColumn(
                name: "QualityCheckpoints",
                table: "production_styles");

            migrationBuilder.DropColumn(
                name: "SizeRange",
                table: "production_styles");

            migrationBuilder.DropColumn(
                name: "FactoryId",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "OrderReference",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "PlannedCompletionDate",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "ProductCategory",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "ProductDescription",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "production_orders");

            migrationBuilder.DropColumn(
                name: "StatusReason",
                table: "production_orders");
        }
    }
}
