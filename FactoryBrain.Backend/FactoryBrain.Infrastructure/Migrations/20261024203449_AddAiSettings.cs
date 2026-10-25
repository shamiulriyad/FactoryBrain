using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_settings",
                columns: table => new
                {
                    Id = table.Column<short>(type: "smallint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    LlmProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LlmModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LlmApiKeyEncrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    LlmUpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    EmbeddingProvider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EmbeddingDimensions = table.Column<int>(type: "integer", nullable: true),
                    EmbeddingApiKeyEncrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    EmbeddingUpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReindexLeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReindexLeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_settings", x => x.Id);
                    table.CheckConstraint("CK_ai_settings_singleton", "\"Id\" = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_settings");
        }
    }
}
