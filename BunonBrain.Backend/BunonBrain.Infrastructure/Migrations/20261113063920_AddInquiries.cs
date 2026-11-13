using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInquiries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inquiries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Company = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    JobTitle = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourcePath = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inquiries", x => x.Id);
                    table.CheckConstraint("CK_inquiries_kind", "\"Kind\" IN ('demo','consultation','contact','support','problem','feature','feedback','partnership')");
                    table.CheckConstraint("CK_inquiries_reply_channel", "\"Email\" IS NOT NULL OR \"Phone\" IS NOT NULL");
                    table.CheckConstraint("CK_inquiries_status", "\"Status\" IN ('new','in_progress','closed')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_inquiries_Kind",
                table: "inquiries",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_inquiries_Status_CreatedAt",
                table: "inquiries",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inquiries");
        }
    }
}
