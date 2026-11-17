using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQcFindingInsight : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefectType",
                table: "qc_inspection_findings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Meaning",
                table: "qc_inspection_findings",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProbableCauses",
                table: "qc_inspection_findings",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecommendedAction",
                table: "qc_inspection_findings",
                type: "character varying(600)",
                maxLength: 600,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefectType",
                table: "qc_inspection_findings");

            migrationBuilder.DropColumn(
                name: "Meaning",
                table: "qc_inspection_findings");

            migrationBuilder.DropColumn(
                name: "ProbableCauses",
                table: "qc_inspection_findings");

            migrationBuilder.DropColumn(
                name: "RecommendedAction",
                table: "qc_inspection_findings");
        }
    }
}
