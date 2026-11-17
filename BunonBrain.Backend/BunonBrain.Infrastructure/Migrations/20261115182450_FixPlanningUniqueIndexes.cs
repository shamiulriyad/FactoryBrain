using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixPlanningUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_production_targets_current",
                table: "production_targets",
                column: "TargetKey",
                unique: true,
                filter: "NOT \"IsSuperseded\" AND \"ApprovalStatus\" IN ('NOT_REQUIRED','APPROVED')");

            migrationBuilder.CreateIndex(
                name: "UX_daily_plans_published",
                table: "daily_production_plans",
                columns: new[] { "FactoryId", "PlanDate" },
                unique: true,
                filter: "\"Status\" = 'PUBLISHED'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_production_targets_current",
                table: "production_targets");

            migrationBuilder.DropIndex(
                name: "UX_daily_plans_published",
                table: "daily_production_plans");
        }
    }
}
