using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCameraAiEventTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_producer_source",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_camera_ai_anonymous",
                table: "monitoring_events",
                sql: "\"Producer\" <> 'CAMERA_AI' OR \"WorkerId\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events",
                sql: "\"Producer\" IN ('API','SIMULATOR','DEVICE_PIPELINE','CAMERA_SERVICE','CV_SERVICE','IOT_PIPELINE','CAMERA_AI')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_producer_source",
                table: "monitoring_events",
                sql: "(\"Producer\" <> 'SIMULATOR' OR \"SourceType\" = 'SIMULATED') AND (\"Producer\" NOT IN ('API','CV_SERVICE','CAMERA_AI') OR \"SourceType\" = 'REAL')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events",
                sql: "\"EventType\" IN ('PERSON_DETECTED','TRACK_UPDATED','PERSON_LOST','ZONE_ENTERED','ZONE_EXITED','ACTIVITY_CLASSIFIED','ACTIVITY_STATE_CHANGED','ACTIVITY_UNCERTAIN','GARMENT_ACTIVITY_CLASSIFIED','GARMENT_ACTIVITY_CHANGED','GARMENT_ACTIVITY_UNCERTAIN','UNCERTAIN_DETECTION','CAMERA_STATUS_CHANGED','DEVICE_STATUS_CHANGED','MODEL_STATUS_CHANGED','MACHINE_STATE_CHANGED','MACHINE_CONDITION','ACTIVITY_CHANGED','PPE_VIOLATION','DETECTION_UNAVAILABLE')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_camera_ai_anonymous",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_producer_source",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events",
                sql: "\"Producer\" IN ('API','SIMULATOR','DEVICE_PIPELINE','CAMERA_SERVICE','CV_SERVICE','IOT_PIPELINE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_producer_source",
                table: "monitoring_events",
                sql: "(\"Producer\" <> 'SIMULATOR' OR \"SourceType\" = 'SIMULATED') AND (\"Producer\" NOT IN ('API','CV_SERVICE') OR \"SourceType\" = 'REAL')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events",
                sql: "\"EventType\" IN ('PERSON_DETECTED','TRACK_UPDATED','PERSON_LOST','ZONE_ENTERED','ZONE_EXITED','ACTIVITY_CLASSIFIED','ACTIVITY_STATE_CHANGED','ACTIVITY_UNCERTAIN','GARMENT_ACTIVITY_CLASSIFIED','GARMENT_ACTIVITY_CHANGED','GARMENT_ACTIVITY_UNCERTAIN','UNCERTAIN_DETECTION','CAMERA_STATUS_CHANGED','DEVICE_STATUS_CHANGED','MODEL_STATUS_CHANGED','MACHINE_STATE_CHANGED','MACHINE_CONDITION')");
        }
    }
}
