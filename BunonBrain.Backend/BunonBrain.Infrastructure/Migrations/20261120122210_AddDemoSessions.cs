using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDemoSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "demo_environment",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    BaselineVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LastResetAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    NextResetAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_demo_environment", x => x.Id);
                    table.CheckConstraint("CK_demo_environment_single_row", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "demo_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CurrentUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClientIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RoleSwitches = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_demo_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_demo_sessions_users_CurrentUserId",
                        column: x => x.CurrentUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_demo_sessions_CurrentUserId",
                table: "demo_sessions",
                column: "CurrentUserId");

            migrationBuilder.CreateIndex(
                name: "IX_demo_sessions_ExpiresAt",
                table: "demo_sessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_demo_sessions_RefreshTokenHash",
                table: "demo_sessions",
                column: "RefreshTokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "demo_environment");

            migrationBuilder.DropTable(
                name: "demo_sessions");
        }
    }
}
