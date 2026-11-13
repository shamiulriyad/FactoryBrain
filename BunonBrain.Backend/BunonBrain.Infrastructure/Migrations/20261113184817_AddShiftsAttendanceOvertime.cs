using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddShiftsAttendanceOvertime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attendance_policies",
                columns: table => new
                {
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DuplicateWindowSeconds = table.Column<int>(type: "integer", nullable: false),
                    MissingCheckOutAfterMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxOpenSessionHours = table.Column<int>(type: "integer", nullable: false),
                    MaxBackdateDays = table.Column<int>(type: "integer", nullable: false),
                    EarlyCheckInWindowMinutes = table.Column<int>(type: "integer", nullable: false),
                    OvertimeRoundingMinutes = table.Column<int>(type: "integer", nullable: false),
                    MinOvertimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxDailyOvertimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxWeeklyOvertimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_policies", x => x.FactoryId);
                    table.ForeignKey(
                        name: "FK_attendance_policies_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_readers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Direction = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    Location = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_readers", x => x.Id);
                    table.CheckConstraint("CK_attendance_readers_direction", "\"Direction\" IN ('Entry','Exit','Both')");
                    table.CheckConstraint("CK_attendance_readers_kind", "\"Kind\" IN ('Simulated','Rfid','Nfc','Gateway')");
                    table.CheckConstraint("CK_attendance_readers_simulated", "(\"Kind\" = 'Simulated') = \"IsSimulated\"");
                    table.ForeignKey(
                        name: "FK_attendance_readers_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "overtime_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PlannedStart = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    PlannedEnd = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    RequestedMinutes = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ApprovedMinutes = table.Column<int>(type: "integer", nullable: true),
                    ApprovedStart = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ApprovedEnd = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ActualMinutes = table.Column<int>(type: "integer", nullable: true),
                    PayableMinutes = table.Column<int>(type: "integer", nullable: true),
                    PayableNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PayableConfirmedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PayableConfirmedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    PayableConfirmedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_requests", x => x.Id);
                    table.CheckConstraint("CK_overtime_requests_minutes", "\"RequestedMinutes\" > 0 AND (\"ApprovedMinutes\" IS NULL OR \"ApprovedMinutes\" <= \"RequestedMinutes\")");
                    table.CheckConstraint("CK_overtime_requests_period", "\"PlannedEnd\" > \"PlannedStart\"");
                    table.CheckConstraint("CK_overtime_requests_status", "\"Status\" IN ('Requested','PendingApproval','Approved','Rejected','InProgress','Completed','Cancelled')");
                    table.ForeignKey(
                        name: "FK_overtime_requests_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_overtime_requests_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    StartLocal = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndLocal = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    LateGraceMinutes = table.Column<int>(type: "integer", nullable: false),
                    EarlyLeaveGraceMinutes = table.Column<int>(type: "integer", nullable: false),
                    WorkingDays = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ProductionShiftCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shifts", x => x.Id);
                    table.CheckConstraint("CK_shifts_days", "\"WorkingDays\" BETWEEN 1 AND 127");
                    table.CheckConstraint("CK_shifts_grace", "\"LateGraceMinutes\" BETWEEN 0 AND 120 AND \"EarlyLeaveGraceMinutes\" BETWEEN 0 AND 120");
                    table.CheckConstraint("CK_shifts_times", "\"StartLocal\" <> \"EndLocal\"");
                    table.ForeignKey(
                        name: "FK_shifts_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shifts_production_shifts_ProductionShiftCode",
                        column: x => x.ProductionShiftCode,
                        principalTable: "production_shifts",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "overtime_approvals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Minutes = table.Column<int>(type: "integer", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overtime_approvals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_overtime_approvals_overtime_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "overtime_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shift_breaks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    BreakType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    StartLocal = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsPaid = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shift_breaks", x => x.Id);
                    table.CheckConstraint("CK_shift_breaks_duration", "\"DurationMinutes\" BETWEEN 1 AND 240");
                    table.CheckConstraint("CK_shift_breaks_type", "\"BreakType\" IN ('Lunch','Tea','Prayer','Personal','Other')");
                    table.ForeignKey(
                        name: "FK_shift_breaks_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "worker_shift_assignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveTo = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("PK_worker_shift_assignments", x => x.Id);
                    table.CheckConstraint("CK_worker_shift_assignments_dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
                    table.CheckConstraint("CK_worker_shift_assignments_status", "\"Status\" IN ('Active','Ended','Superseded')");
                    table.ForeignKey(
                        name: "FK_worker_shift_assignments_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_shift_assignments_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_shift_assignments_worker_shift_assignments_PreviousA~",
                        column: x => x.PreviousAssignmentId,
                        principalTable: "worker_shift_assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_worker_shift_assignments_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ShiftAssignmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ScheduledStart = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ScheduledEnd = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    CheckInAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    CheckOutAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    CheckInSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CheckOutSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NeedsReview = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    HasSimulatedEvents = table.Column<bool>(type: "boolean", nullable: false),
                    IsCorrected = table.Column<bool>(type: "boolean", nullable: false),
                    AttendanceMinutes = table.Column<int>(type: "integer", nullable: false),
                    ScheduledMinutes = table.Column<int>(type: "integer", nullable: false),
                    ScheduledBreakMinutes = table.Column<int>(type: "integer", nullable: false),
                    BreakMinutes = table.Column<int>(type: "integer", nullable: false),
                    OverBreakMinutes = table.Column<int>(type: "integer", nullable: false),
                    NetWorkingMinutes = table.Column<int>(type: "integer", nullable: false),
                    LateMinutes = table.Column<int>(type: "integer", nullable: false),
                    EarlyDepartureMinutes = table.Column<int>(type: "integer", nullable: false),
                    BeyondShiftMinutes = table.Column<int>(type: "integer", nullable: false),
                    ApprovedOvertimeMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsLate = table.Column<bool>(type: "boolean", nullable: false),
                    IsEarlyDeparture = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_sessions", x => x.Id);
                    table.CheckConstraint("CK_attendance_sessions_open", "\"Status\" <> 'Open' OR (\"CheckInAt\" IS NOT NULL AND \"CheckOutAt\" IS NULL)");
                    table.CheckConstraint("CK_attendance_sessions_order", "\"CheckInAt\" IS NULL OR \"CheckOutAt\" IS NULL OR \"CheckOutAt\" > \"CheckInAt\"");
                    table.CheckConstraint("CK_attendance_sessions_status", "\"Status\" IN ('Open','Completed','MissingCheckOut','MissingCheckIn')");
                    table.ForeignKey(
                        name: "FK_attendance_sessions_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_sessions_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_sessions_worker_shift_assignments_ShiftAssignmen~",
                        column: x => x.ShiftAssignmentId,
                        principalTable: "worker_shift_assignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_sessions_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_corrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ProposedCheckIn = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ProposedCheckOut = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    OriginalCheckIn = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    OriginalCheckOut = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_corrections", x => x.Id);
                    table.CheckConstraint("CK_attendance_corrections_status", "\"Status\" IN ('Pending','Approved','Rejected')");
                    table.CheckConstraint("CK_attendance_corrections_values", "\"ProposedCheckIn\" IS NOT NULL OR \"ProposedCheckOut\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_attendance_corrections_attendance_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "attendance_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_corrections_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_corrections_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "break_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttendanceSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShiftBreakId = table.Column<Guid>(type: "uuid", nullable: true),
                    BreakType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    AllowedMinutes = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EndSource = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_break_sessions", x => x.Id);
                    table.CheckConstraint("CK_break_sessions_order", "\"EndAt\" IS NULL OR \"EndAt\" >= \"StartAt\"");
                    table.CheckConstraint("CK_break_sessions_status", "\"Status\" IN ('Active','Completed','AutoClosed')");
                    table.CheckConstraint("CK_break_sessions_type", "\"BreakType\" IN ('Lunch','Tea','Prayer','Personal','Other')");
                    table.ForeignKey(
                        name: "FK_break_sessions_attendance_sessions_AttendanceSessionId",
                        column: x => x.AttendanceSessionId,
                        principalTable: "attendance_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_break_sessions_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_break_sessions_shift_breaks_ShiftBreakId",
                        column: x => x.ShiftBreakId,
                        principalTable: "shift_breaks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_break_sessions_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsSimulated = table.Column<bool>(type: "boolean", nullable: false),
                    ReaderId = table.Column<Guid>(type: "uuid", nullable: true),
                    CardUid = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OutcomeCode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    OutcomeMessage = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    BreakSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_events", x => x.Id);
                    table.CheckConstraint("CK_attendance_events_outcome", "\"Outcome\" IN ('Accepted','Duplicate','Rejected')");
                    table.CheckConstraint("CK_attendance_events_simulated", "(\"Source\" = 'Simulator') = \"IsSimulated\"");
                    table.CheckConstraint("CK_attendance_events_source", "\"Source\" IN ('Simulator','Device','Manual','Correction','System')");
                    table.CheckConstraint("CK_attendance_events_type", "\"EventType\" IN ('CheckIn','CheckOut','BreakStart','BreakEnd')");
                    table.ForeignKey(
                        name: "FK_attendance_events_attendance_readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "attendance_readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_events_attendance_sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "attendance_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_events_break_sessions_BreakSessionId",
                        column: x => x.BreakSessionId,
                        principalTable: "break_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_events_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_events_workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_corrections_FactoryId_Status",
                table: "attendance_corrections",
                columns: new[] { "FactoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_corrections_SessionId",
                table: "attendance_corrections",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_corrections_WorkerId",
                table: "attendance_corrections",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_BreakSessionId",
                table: "attendance_events",
                column: "BreakSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_FactoryId_IdempotencyKey",
                table: "attendance_events",
                columns: new[] { "FactoryId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_FactoryId_OccurredAt",
                table: "attendance_events",
                columns: new[] { "FactoryId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_ReaderId",
                table: "attendance_events",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_SessionId",
                table: "attendance_events",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_WorkerId_OccurredAt",
                table: "attendance_events",
                columns: new[] { "WorkerId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_readers_FactoryId_Code",
                table: "attendance_readers",
                columns: new[] { "FactoryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_FactoryId_NeedsReview",
                table: "attendance_sessions",
                columns: new[] { "FactoryId", "NeedsReview" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_FactoryId_WorkDate",
                table: "attendance_sessions",
                columns: new[] { "FactoryId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_LineId_WorkDate",
                table: "attendance_sessions",
                columns: new[] { "LineId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_ShiftAssignmentId",
                table: "attendance_sessions",
                column: "ShiftAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_ShiftId",
                table: "attendance_sessions",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_WorkerId_WorkDate",
                table: "attendance_sessions",
                columns: new[] { "WorkerId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_one_open",
                table: "attendance_sessions",
                column: "WorkerId",
                unique: true,
                filter: "\"Status\" = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_break_sessions_AttendanceSessionId",
                table: "break_sessions",
                column: "AttendanceSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_break_sessions_FactoryId_StartAt",
                table: "break_sessions",
                columns: new[] { "FactoryId", "StartAt" });

            migrationBuilder.CreateIndex(
                name: "IX_break_sessions_ShiftBreakId",
                table: "break_sessions",
                column: "ShiftBreakId");

            migrationBuilder.CreateIndex(
                name: "IX_break_sessions_one_active",
                table: "break_sessions",
                column: "WorkerId",
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_overtime_approvals_RequestId_Id",
                table: "overtime_approvals",
                columns: new[] { "RequestId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_requests_FactoryId_Status",
                table: "overtime_requests",
                columns: new[] { "FactoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_requests_LineId_Status",
                table: "overtime_requests",
                columns: new[] { "LineId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_overtime_requests_WorkerId_WorkDate",
                table: "overtime_requests",
                columns: new[] { "WorkerId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_shift_breaks_ShiftId",
                table: "shift_breaks",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_shifts_FactoryId_Code",
                table: "shifts",
                columns: new[] { "FactoryId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shifts_FactoryId_Name",
                table: "shifts",
                columns: new[] { "FactoryId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shifts_ProductionShiftCode",
                table: "shifts",
                column: "ProductionShiftCode");

            migrationBuilder.CreateIndex(
                name: "IX_worker_shift_assignments_FactoryId_Status",
                table: "worker_shift_assignments",
                columns: new[] { "FactoryId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_worker_shift_assignments_PreviousAssignmentId",
                table: "worker_shift_assignments",
                column: "PreviousAssignmentId");

            migrationBuilder.CreateIndex(
                name: "IX_worker_shift_assignments_ShiftId_Status",
                table: "worker_shift_assignments",
                columns: new[] { "ShiftId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_worker_shift_assignments_WorkerId_EffectiveFrom",
                table: "worker_shift_assignments",
                columns: new[] { "WorkerId", "EffectiveFrom" });

            // ── A worker has one shift per date ─────────────────────────────
            // Assignment periods [EffectiveFrom, EffectiveTo] (inclusive; open-ended
            // when EffectiveTo is null) of one worker never overlap. btree_gist is
            // installed by AddWorkstationAssignments.
            migrationBuilder.Sql(
                """
                ALTER TABLE worker_shift_assignments
                  ADD CONSTRAINT "EX_worker_shift_assignments_no_overlap"
                  EXCLUDE USING gist ("WorkerId" WITH =, daterange("EffectiveFrom", "EffectiveTo", '[]') WITH &&);
                """);

            // ── Raw attendance events are append-only ───────────────────────
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION attendance_events_reject_mutation() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'attendance_events is append-only (% rejected)', TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER attendance_events_no_update_delete
                    BEFORE UPDATE OR DELETE ON attendance_events
                    FOR EACH ROW EXECUTE FUNCTION attendance_events_reject_mutation();
                CREATE TRIGGER attendance_events_no_truncate
                    BEFORE TRUNCATE ON attendance_events
                    FOR EACH STATEMENT EXECUTE FUNCTION attendance_events_reject_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS attendance_events_no_truncate ON attendance_events;
                DROP TRIGGER IF EXISTS attendance_events_no_update_delete ON attendance_events;
                DROP FUNCTION IF EXISTS attendance_events_reject_mutation();
                """);

            migrationBuilder.DropTable(
                name: "attendance_corrections");

            migrationBuilder.DropTable(
                name: "attendance_events");

            migrationBuilder.DropTable(
                name: "attendance_policies");

            migrationBuilder.DropTable(
                name: "overtime_approvals");

            migrationBuilder.DropTable(
                name: "attendance_readers");

            migrationBuilder.DropTable(
                name: "break_sessions");

            migrationBuilder.DropTable(
                name: "overtime_requests");

            migrationBuilder.DropTable(
                name: "attendance_sessions");

            migrationBuilder.DropTable(
                name: "shift_breaks");

            migrationBuilder.DropTable(
                name: "worker_shift_assignments");

            migrationBuilder.DropTable(
                name: "shifts");
        }
    }
}
