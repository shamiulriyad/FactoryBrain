using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCvServiceProducer : Migration
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

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_cv_anonymous",
                table: "monitoring_events",
                sql: "\"Producer\" <> 'CV_SERVICE' OR \"WorkerId\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events",
                sql: "\"Producer\" IN ('API','SIMULATOR','DEVICE_PIPELINE','CAMERA_SERVICE','CV_SERVICE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_producer_source",
                table: "monitoring_events",
                sql: "(\"Producer\" <> 'SIMULATOR' OR \"SourceType\" = 'SIMULATED') AND (\"Producer\" NOT IN ('API','CV_SERVICE') OR \"SourceType\" = 'REAL')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_cv_anonymous",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events");

            migrationBuilder.DropCheckConstraint(
                name: "CK_monitoring_events_producer_source",
                table: "monitoring_events");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_producer",
                table: "monitoring_events",
                sql: "\"Producer\" IN ('API','SIMULATOR','DEVICE_PIPELINE','CAMERA_SERVICE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_monitoring_events_producer_source",
                table: "monitoring_events",
                sql: "(\"Producer\" <> 'SIMULATOR' OR \"SourceType\" = 'SIMULATED') AND (\"Producer\" <> 'API' OR \"SourceType\" = 'REAL')");
        }
    }
}
