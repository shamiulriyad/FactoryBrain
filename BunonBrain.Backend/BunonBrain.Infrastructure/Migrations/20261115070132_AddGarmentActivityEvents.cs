using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGarmentActivityEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_uncertain_anonymous",
                table: "monitoring_events");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events",
                sql: "\"EventType\" IN ('PERSON_DETECTED','TRACK_UPDATED','PERSON_LOST','ZONE_ENTERED','ZONE_EXITED','ACTIVITY_CLASSIFIED','ACTIVITY_STATE_CHANGED','ACTIVITY_UNCERTAIN','GARMENT_ACTIVITY_CLASSIFIED','GARMENT_ACTIVITY_CHANGED','GARMENT_ACTIVITY_UNCERTAIN','UNCERTAIN_DETECTION','CAMERA_STATUS_CHANGED','DEVICE_STATUS_CHANGED','MODEL_STATUS_CHANGED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_uncertain_anonymous",
                table: "monitoring_events",
                sql: "\"EventType\" NOT IN ('UNCERTAIN_DETECTION','ACTIVITY_UNCERTAIN','GARMENT_ACTIVITY_UNCERTAIN') OR \"WorkerId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_uncertain_anonymous",
                table: "monitoring_events");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_type",
                table: "monitoring_events",
                sql: "\"EventType\" IN ('PERSON_DETECTED','TRACK_UPDATED','PERSON_LOST','ZONE_ENTERED','ZONE_EXITED','ACTIVITY_CLASSIFIED','ACTIVITY_STATE_CHANGED','ACTIVITY_UNCERTAIN','UNCERTAIN_DETECTION','CAMERA_STATUS_CHANGED','DEVICE_STATUS_CHANGED','MODEL_STATUS_CHANGED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_uncertain_anonymous",
                table: "monitoring_events",
                sql: "\"EventType\" NOT IN ('UNCERTAIN_DETECTION','ACTIVITY_UNCERTAIN') OR \"WorkerId\" IS NULL");
        }
    }
}
