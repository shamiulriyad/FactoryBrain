using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIotMachineMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events");

            migrationBuilder.AddColumn<Guid>(
                name: "MachineId",
                table: "monitoring_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MachineId",
                table: "monitoring_alerts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_iot_configurations",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Protocol = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    ConnectionVia = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    GatewayDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    MqttClientId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TelemetryIntervalSeconds = table.Column<int>(type: "integer", nullable: true),
                    Settings = table.Column<string>(type: "jsonb", nullable: false),
                    KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    KeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    KeyIssuedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    KeyIssuedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_iot_configurations", x => x.DeviceId);
                    table.CheckConstraint("CK_device_iot_configurations_gateway", "\"GatewayDeviceId\" IS NULL OR \"GatewayDeviceId\" <> \"DeviceId\"");
                    table.CheckConstraint("CK_device_iot_configurations_protocol", "\"Protocol\" IN ('MQTT','HTTP','SIMULATOR')");
                    table.CheckConstraint("CK_device_iot_configurations_type", "\"DeviceType\" IN ('SEWING_MACHINE_SENSOR','RUN_STOP_SENSOR','POWER_SENSOR','PIECE_COUNTER','CUTTING_MACHINE_SENSOR','PRESSING_MACHINE_SENSOR','RFID_READER','ESP32','INDUSTRIAL_GATEWAY','MQTT_DEVICE','GENERIC_SENSOR')");
                    table.CheckConstraint("CK_device_iot_configurations_via", "\"ConnectionVia\" IN ('DIRECT','ESP32','GATEWAY')");
                    table.ForeignKey(
                        name: "FK_device_iot_configurations_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_device_iot_configurations_devices_GatewayDeviceId",
                        column: x => x.GatewayDeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_device_iot_configurations_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "machine_monitoring_profiles",
                columns: table => new
                {
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineKind = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    CountingMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PulsesPerPiece = table.Column<int>(type: "integer", nullable: false),
                    RunningPowerWatts = table.Column<double>(type: "double precision", nullable: true),
                    IdlePowerWatts = table.Column<double>(type: "double precision", nullable: true),
                    StaleAfterSeconds = table.Column<int>(type: "integer", nullable: true),
                    StopAlertAfterSeconds = table.Column<int>(type: "integer", nullable: true),
                    LongIdleAlertSeconds = table.Column<int>(type: "integer", nullable: true),
                    MaxTemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    MaxVibrationMmS = table.Column<double>(type: "double precision", nullable: true),
                    CounterSilenceAlertSeconds = table.Column<int>(type: "integer", nullable: true),
                    FlapTransitionsPer5Min = table.Column<int>(type: "integer", nullable: true),
                    AlertsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_machine_monitoring_profiles", x => x.MachineId);
                    table.CheckConstraint("CK_machine_monitoring_profiles_counting", "\"CountingMode\" IN ('PIECE','PULSES_PER_PIECE','CYCLE_NOT_PIECE')");
                    table.CheckConstraint("CK_machine_monitoring_profiles_kind", "\"MachineKind\" IN ('SEWING','CUTTING','PRESSING','OTHER')");
                    table.CheckConstraint("CK_machine_monitoring_profiles_power", "\"IdlePowerWatts\" IS NULL OR (\"RunningPowerWatts\" IS NOT NULL AND \"IdlePowerWatts\" < \"RunningPowerWatts\")");
                    table.CheckConstraint("CK_machine_monitoring_profiles_ppp", "\"PulsesPerPiece\" BETWEEN 1 AND 1000");
                    table.ForeignKey(
                        name: "FK_machine_monitoring_profiles_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_machine_monitoring_profiles_machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "machine_state_transitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    FromState = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    ToState = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TelemetryId = table.Column<long>(type: "bigint", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_machine_state_transitions", x => x.Id);
                    table.CheckConstraint("CK_machine_state_transitions_from", "\"FromState\" IS NULL OR \"FromState\" IN ('RUNNING','STOPPED','IDLE','MAINTENANCE','ERROR','OFFLINE','UNKNOWN')");
                    table.CheckConstraint("CK_machine_state_transitions_source", "\"SourceType\" IN ('REAL','SIMULATED')");
                    table.CheckConstraint("CK_machine_state_transitions_to", "\"ToState\" IN ('RUNNING','STOPPED','IDLE','MAINTENANCE','ERROR','OFFLINE','UNKNOWN')");
                    table.ForeignKey(
                        name: "FK_machine_state_transitions_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_machine_state_transitions_machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "machine_states",
                columns: table => new
                {
                    MachineId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Since = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SourceDeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastStateEventAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    LastTelemetryAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    LastCountAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    PowerWatts = table.Column<double>(type: "double precision", nullable: true),
                    CurrentAmps = table.Column<double>(type: "double precision", nullable: true),
                    TemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    VibrationMmS = table.Column<double>(type: "double precision", nullable: true),
                    ReadingsAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InMaintenance = table.Column<bool>(type: "boolean", nullable: false),
                    MaintenanceReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MaintenanceSince = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    MaintenanceByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ActiveConditions = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_machine_states", x => x.MachineId);
                    table.CheckConstraint("CK_machine_states_maintenance", "NOT \"InMaintenance\" OR \"State\" = 'MAINTENANCE'");
                    table.CheckConstraint("CK_machine_states_source", "\"SourceType\" IN ('REAL','SIMULATED')");
                    table.CheckConstraint("CK_machine_states_state", "\"State\" IN ('RUNNING','STOPPED','IDLE','MAINTENANCE','ERROR','OFFLINE','UNKNOWN')");
                    table.ForeignKey(
                        name: "FK_machine_states_devices_SourceDeviceId",
                        column: x => x.SourceDeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_machine_states_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_machine_states_machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "machine_telemetry",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ReportedState = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    PowerWatts = table.Column<double>(type: "double precision", nullable: true),
                    CurrentAmps = table.Column<double>(type: "double precision", nullable: true),
                    TemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    VibrationMmS = table.Column<double>(type: "double precision", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Metrics = table.Column<string>(type: "jsonb", nullable: true),
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Transport = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_machine_telemetry", x => x.Id);
                    table.CheckConstraint("CK_machine_telemetry_outcome", "\"Outcome\" IN ('APPLIED','LATE','NO_MACHINE','MAINTENANCE','SOURCE_MISMATCH')");
                    table.CheckConstraint("CK_machine_telemetry_simulated", "(\"Transport\" = 'SIMULATOR') = (\"SourceType\" = 'SIMULATED')");
                    table.CheckConstraint("CK_machine_telemetry_source", "\"SourceType\" IN ('REAL','SIMULATED')");
                    table.CheckConstraint("CK_machine_telemetry_transport", "\"Transport\" IN ('MQTT','HTTP','SIMULATOR')");
                    table.ForeignKey(
                        name: "FK_machine_telemetry_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_machine_telemetry_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_corrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProductionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    ShiftCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    HourStart = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    PiecesDelta = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedByRole = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_corrections", x => x.Id);
                    table.CheckConstraint("CK_production_corrections_delta", "\"PiecesDelta\" <> 0 AND \"PiecesDelta\" BETWEEN -100000 AND 100000");
                    table.CheckConstraint("CK_production_corrections_source", "\"SourceType\" IN ('REAL','SIMULATED')");
                    table.ForeignKey(
                        name: "FK_production_corrections_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_corrections_machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_corrections_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_corrections_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_corrections_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_counter_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkstationId = table.Column<Guid>(type: "uuid", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FloorId = table.Column<Guid>(type: "uuid", nullable: true),
                    CounterMode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    RawValue = table.Column<long>(type: "bigint", nullable: false),
                    Pulses = table.Column<long>(type: "bigint", nullable: false),
                    Pieces = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CountingMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PulsesPerPiece = table.Column<int>(type: "integer", nullable: false),
                    ShiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    ShiftCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProductionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Transport = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_counter_events", x => x.Id);
                    table.CheckConstraint("CK_production_counter_events_counting", "\"CountingMode\" IN ('PIECE','PULSES_PER_PIECE','CYCLE_NOT_PIECE')");
                    table.CheckConstraint("CK_production_counter_events_counts", "\"Pulses\" >= 0 AND \"Pieces\" >= 0 AND (\"Status\" = 'COUNTED' OR \"Pieces\" = 0)");
                    table.CheckConstraint("CK_production_counter_events_mode", "\"CounterMode\" IN ('INCREMENT','ABSOLUTE','RESET')");
                    table.CheckConstraint("CK_production_counter_events_simulated", "\"Transport\" <> 'SIMULATOR' OR \"SourceType\" = 'SIMULATED'");
                    table.CheckConstraint("CK_production_counter_events_source", "\"SourceType\" IN ('REAL','SIMULATED')");
                    table.CheckConstraint("CK_production_counter_events_status", "\"Status\" IN ('COUNTED','BASELINE','RESET','OUT_OF_ORDER','SUSPECT_JUMP')");
                    table.CheckConstraint("CK_production_counter_events_transport", "\"Transport\" IN ('MQTT','HTTP','SIMULATOR','MANUAL')");
                    table.ForeignKey(
                        name: "FK_production_counter_events_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_counter_events_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_counter_events_machines_MachineId",
                        column: x => x.MachineId,
                        principalTable: "machines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_counter_events_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_counter_events_shifts_ShiftId",
                        column: x => x.ShiftId,
                        principalTable: "shifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_counter_events_workstations_WorkstationId",
                        column: x => x.WorkstationId,
                        principalTable: "workstations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "production_counter_snapshots",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    MachineId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastValue = table.Column<long>(type: "bigint", nullable: true),
                    LastOccurredAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    PulseRemainder = table.Column<int>(type: "integer", nullable: false),
                    TotalPulses = table.Column<long>(type: "bigint", nullable: false),
                    TotalPieces = table.Column<long>(type: "bigint", nullable: false),
                    LastResetAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    LastCountAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_counter_snapshots", x => x.DeviceId);
                    table.CheckConstraint("CK_production_counter_snapshots_totals", "\"TotalPulses\" >= 0 AND \"TotalPieces\" >= 0 AND \"PulseRemainder\" >= 0");
                    table.ForeignKey(
                        name: "FK_production_counter_snapshots_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_production_counter_snapshots_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_events_MachineId_OccurredAt",
                table: "monitoring_events",
                columns: new[] { "MachineId", "OccurredAt" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events",
                sql: "\"Producer\" IN ('API','SIMULATOR','DEVICE_PIPELINE','CAMERA_SERVICE','CV_SERVICE','IOT_PIPELINE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events",
                sql: "\"EventType\" IN ('PERSON_DETECTED','TRACK_UPDATED','PERSON_LOST','ZONE_ENTERED','ZONE_EXITED','ACTIVITY_CLASSIFIED','ACTIVITY_STATE_CHANGED','ACTIVITY_UNCERTAIN','GARMENT_ACTIVITY_CLASSIFIED','GARMENT_ACTIVITY_CHANGED','GARMENT_ACTIVITY_UNCERTAIN','UNCERTAIN_DETECTION','CAMERA_STATUS_CHANGED','DEVICE_STATUS_CHANGED','MODEL_STATUS_CHANGED','MACHINE_STATE_CHANGED','MACHINE_CONDITION')");

            migrationBuilder.CreateIndex(
                name: "IX_monitoring_alerts_MachineId_Status",
                table: "monitoring_alerts",
                columns: new[] { "MachineId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_device_iot_configurations_GatewayDeviceId",
                table: "device_iot_configurations",
                column: "GatewayDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_device_iot_configurations_mqtt_client",
                table: "device_iot_configurations",
                columns: new[] { "FactoryId", "MqttClientId" },
                unique: true,
                filter: "\"MqttClientId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_machine_monitoring_profiles_FactoryId",
                table: "machine_monitoring_profiles",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_machine_state_transitions_FactoryId_OccurredAt",
                table: "machine_state_transitions",
                columns: new[] { "FactoryId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_machine_state_transitions_LineId_OccurredAt",
                table: "machine_state_transitions",
                columns: new[] { "LineId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_machine_state_transitions_MachineId_OccurredAt",
                table: "machine_state_transitions",
                columns: new[] { "MachineId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_machine_states_FactoryId_State",
                table: "machine_states",
                columns: new[] { "FactoryId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_machine_states_SourceDeviceId",
                table: "machine_states",
                column: "SourceDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_machine_telemetry_DeviceId_MessageId",
                table: "machine_telemetry",
                columns: new[] { "DeviceId", "MessageId" },
                unique: true,
                filter: "\"MessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_machine_telemetry_FactoryId",
                table: "machine_telemetry",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_machine_telemetry_MachineId_OccurredAt",
                table: "machine_telemetry",
                columns: new[] { "MachineId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_machine_telemetry_ReceivedAt",
                table: "machine_telemetry",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_production_corrections_FactoryId_ProductionDate",
                table: "production_corrections",
                columns: new[] { "FactoryId", "ProductionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_production_corrections_LineId_ProductionDate",
                table: "production_corrections",
                columns: new[] { "LineId", "ProductionDate" });

            migrationBuilder.CreateIndex(
                name: "IX_production_corrections_MachineId",
                table: "production_corrections",
                column: "MachineId");

            migrationBuilder.CreateIndex(
                name: "IX_production_corrections_ShiftId",
                table: "production_corrections",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_production_corrections_WorkstationId",
                table: "production_corrections",
                column: "WorkstationId");

            migrationBuilder.CreateIndex(
                name: "IX_production_counter_events_DeviceId_OccurredAt",
                table: "production_counter_events",
                columns: new[] { "DeviceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_counter_events_FactoryId_IdempotencyKey",
                table: "production_counter_events",
                columns: new[] { "FactoryId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_counter_events_FactoryId_ProductionDate_ShiftId",
                table: "production_counter_events",
                columns: new[] { "FactoryId", "ProductionDate", "ShiftId" });

            migrationBuilder.CreateIndex(
                name: "IX_production_counter_events_FactoryId_SourceType_OccurredAt",
                table: "production_counter_events",
                columns: new[] { "FactoryId", "SourceType", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_counter_events_LineId_OccurredAt",
                table: "production_counter_events",
                columns: new[] { "LineId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_counter_events_MachineId_OccurredAt",
                table: "production_counter_events",
                columns: new[] { "MachineId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_production_counter_events_ShiftId",
                table: "production_counter_events",
                column: "ShiftId");

            migrationBuilder.CreateIndex(
                name: "IX_production_counter_events_WorkstationId",
                table: "production_counter_events",
                column: "WorkstationId");

            migrationBuilder.CreateIndex(
                name: "IX_production_counter_snapshots_FactoryId",
                table: "production_counter_snapshots",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_production_counter_snapshots_MachineId",
                table: "production_counter_snapshots",
                column: "MachineId");

            migrationBuilder.AddForeignKey(
                name: "FK_monitoring_alerts_machines_MachineId",
                table: "monitoring_alerts",
                column: "MachineId",
                principalTable: "machines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_monitoring_events_machines_MachineId",
                table: "monitoring_events",
                column: "MachineId",
                principalTable: "machines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Raw machine telemetry is never changed (the retention job deletes old rows);
            // state transitions, counter events and corrections are append-only history.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER machine_telemetry_no_update
                    BEFORE UPDATE ON machine_telemetry
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER machine_state_transitions_no_update_delete
                    BEFORE UPDATE OR DELETE ON machine_state_transitions
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER machine_state_transitions_no_truncate
                    BEFORE TRUNCATE ON machine_state_transitions
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER production_counter_events_no_update_delete
                    BEFORE UPDATE OR DELETE ON production_counter_events
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER production_counter_events_no_truncate
                    BEFORE TRUNCATE ON production_counter_events
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER production_corrections_no_update_delete
                    BEFORE UPDATE OR DELETE ON production_corrections
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER production_corrections_no_truncate
                    BEFORE TRUNCATE ON production_corrections
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS machine_telemetry_no_update ON machine_telemetry;
                DROP TRIGGER IF EXISTS machine_state_transitions_no_update_delete ON machine_state_transitions;
                DROP TRIGGER IF EXISTS machine_state_transitions_no_truncate ON machine_state_transitions;
                DROP TRIGGER IF EXISTS production_counter_events_no_update_delete ON production_counter_events;
                DROP TRIGGER IF EXISTS production_counter_events_no_truncate ON production_counter_events;
                DROP TRIGGER IF EXISTS production_corrections_no_update_delete ON production_corrections;
                DROP TRIGGER IF EXISTS production_corrections_no_truncate ON production_corrections;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_monitoring_alerts_machines_MachineId",
                table: "monitoring_alerts");

            migrationBuilder.DropForeignKey(
                name: "FK_monitoring_events_machines_MachineId",
                table: "monitoring_events");

            migrationBuilder.DropTable(
                name: "device_iot_configurations");

            migrationBuilder.DropTable(
                name: "machine_monitoring_profiles");

            migrationBuilder.DropTable(
                name: "machine_state_transitions");

            migrationBuilder.DropTable(
                name: "machine_states");

            migrationBuilder.DropTable(
                name: "machine_telemetry");

            migrationBuilder.DropTable(
                name: "production_corrections");

            migrationBuilder.DropTable(
                name: "production_counter_events");

            migrationBuilder.DropTable(
                name: "production_counter_snapshots");

            migrationBuilder.DropIndex(
                name: "IX_monitoring_events_MachineId_OccurredAt",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events");

            migrationBuilder.DropIndex(
                name: "IX_monitoring_alerts_MachineId_Status",
                table: "monitoring_alerts");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "monitoring_events");

            migrationBuilder.DropColumn(
                name: "MachineId",
                table: "monitoring_alerts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events",
                sql: "\"Producer\" IN ('API','SIMULATOR','DEVICE_PIPELINE','CAMERA_SERVICE','CV_SERVICE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events",
                sql: "\"EventType\" IN ('PERSON_DETECTED','TRACK_UPDATED','PERSON_LOST','ZONE_ENTERED','ZONE_EXITED','ACTIVITY_CLASSIFIED','ACTIVITY_STATE_CHANGED','ACTIVITY_UNCERTAIN','GARMENT_ACTIVITY_CLASSIFIED','GARMENT_ACTIVITY_CHANGED','GARMENT_ACTIVITY_UNCERTAIN','UNCERTAIN_DETECTION','CAMERA_STATUS_CHANGED','DEVICE_STATUS_CHANGED','MODEL_STATUS_CHANGED')");
        }
    }
}
