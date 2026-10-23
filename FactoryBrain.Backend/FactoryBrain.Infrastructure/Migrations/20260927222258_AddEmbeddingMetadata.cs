using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Dims",
                table: "manual_documents",
                type: "integer",
                nullable: false,
                defaultValue: 384);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "manual_documents",
                type: "text",
                nullable: false,
                defaultValue: "hash-md5");

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingProvider",
                table: "manual_documents",
                type: "text",
                nullable: false,
                defaultValue: "local");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Dims",
                table: "manual_documents");

            migrationBuilder.DropColumn(
                name: "EmbeddingModel",
                table: "manual_documents");

            migrationBuilder.DropColumn(
                name: "EmbeddingProvider",
                table: "manual_documents");
        }
    }
}
