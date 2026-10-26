using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMerchandising : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "merch_orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Buyer = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BuyerKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BuyerOrderNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StyleRef = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProductDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: true),
                    Unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DeliveryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(14,4)", precision: 14, scale: 4, nullable: true),
                    Destination = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    BreakdownJson = table.Column<string>(type: "jsonb", nullable: true),
                    FieldSourcesJson = table.Column<string>(type: "jsonb", nullable: false),
                    RequiredDocumentTypes = table.Column<List<string>>(type: "text[]", nullable: false),
                    ProductionOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merch_orders", x => x.Id);
                    table.CheckConstraint("CK_merch_orders_currency", "\"Currency\" IS NULL OR \"Currency\" ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("CK_merch_orders_quantity", "\"Quantity\" IS NULL OR (\"Quantity\" >= 1 AND \"Quantity\" <= 10000000)");
                    table.CheckConstraint("CK_merch_orders_unit", "\"Unit\" IS NULL OR \"Unit\" IN ('pcs','dozen','sets','packs','pairs')");
                    table.CheckConstraint("CK_merch_orders_unit_price", "\"UnitPrice\" IS NULL OR \"UnitPrice\" > 0");
                    table.ForeignKey(
                        name: "FK_merch_orders_production_orders_ProductionOrderId",
                        column: x => x.ProductionOrderId,
                        principalTable: "production_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "merch_conflicts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ValuesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResolvedValue = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: true),
                    ResolutionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merch_conflicts", x => x.Id);
                    table.CheckConstraint("CK_merch_conflicts_resolved", "\"Status\" <> 'Resolved' OR (\"ResolvedAt\" IS NOT NULL AND \"ResolvedValue\" IS NOT NULL AND \"ResolvedByEmail\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_merch_conflicts_merch_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "merch_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "merch_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CurrentVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    VersionCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merch_documents", x => x.Id);
                    table.CheckConstraint("CK_merch_documents_versions", "\"VersionCount\" >= 0");
                    table.ForeignKey(
                        name: "FK_merch_documents_merch_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "merch_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "merch_milestones",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CompletedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merch_milestones", x => x.Id);
                    table.CheckConstraint("CK_merch_milestones_done", "\"Status\" <> 'Done' OR \"CompletedOn\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_merch_milestones_merch_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "merch_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "merch_document_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    ChangeNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StorageKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Format = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ProcessingStartedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PageCount = table.Column<int>(type: "integer", nullable: true),
                    TextLength = table.Column<int>(type: "integer", nullable: true),
                    TextTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    ExtractionCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExtractionMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ModelVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ExtractionMs = table.Column<int>(type: "integer", nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ConfirmedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConfirmedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UploadedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UploadedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merch_document_versions", x => x.Id);
                    table.CheckConstraint("CK_merch_versions_attempts", "\"Attempts\" >= 0");
                    table.CheckConstraint("CK_merch_versions_confirmed", "\"Status\" <> 'Confirmed' OR (\"ConfirmedAt\" IS NOT NULL AND \"ConfirmedByEmail\" IS NOT NULL)");
                    table.CheckConstraint("CK_merch_versions_format", "\"Format\" IN ('pdf','txt','csv','docx','image')");
                    table.CheckConstraint("CK_merch_versions_number", "\"VersionNumber\" >= 1");
                    table.CheckConstraint("CK_merch_versions_size", "\"SizeBytes\" > 0");
                    table.ForeignKey(
                        name: "FK_merch_document_versions_merch_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "merch_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "merch_document_pages",
                columns: table => new
                {
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PageNumber = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merch_document_pages", x => new { x.VersionId, x.PageNumber });
                    table.CheckConstraint("CK_merch_pages_number", "\"PageNumber\" >= 1");
                    table.ForeignKey(
                        name: "FK_merch_document_pages_merch_document_versions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "merch_document_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "merch_extracted_fields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RawValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    NormalizedValue = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    PageNumber = table.Column<int>(type: "integer", nullable: true),
                    Snippet = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: true),
                    EvidenceStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Issues = table.Column<string[]>(type: "text[]", nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CorrectedValue = table.Column<string>(type: "character varying(200000)", maxLength: 200000, nullable: true),
                    ReviewerNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ReviewedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_merch_extracted_fields", x => x.Id);
                    table.CheckConstraint("CK_merch_fields_confidence", "\"Confidence\" IS NULL OR (\"Confidence\" >= 0 AND \"Confidence\" <= 1)");
                    table.CheckConstraint("CK_merch_fields_corrected", "\"Decision\" <> 'Corrected' OR \"CorrectedValue\" IS NOT NULL");
                    table.CheckConstraint("CK_merch_fields_evidence", "\"EvidenceStatus\" IN ('verified','not_found','none')");
                    table.CheckConstraint("CK_merch_fields_reviewer", "\"Source\" <> 'Reviewer' OR \"Confidence\" IS NULL");
                    table.CheckConstraint("CK_merch_fields_snippet", "\"Snippet\" IS NULL OR \"EvidenceStatus\" = 'verified'");
                    table.ForeignKey(
                        name: "FK_merch_extracted_fields_merch_document_versions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "merch_document_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_merch_conflicts_OrderId_Status",
                table: "merch_conflicts",
                columns: new[] { "OrderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_merch_conflicts_Status_DetectedAt",
                table: "merch_conflicts",
                columns: new[] { "Status", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_merch_conflicts_open",
                table: "merch_conflicts",
                columns: new[] { "OrderId", "FieldKey" },
                unique: true,
                filter: "\"Status\" = 'Open'");

            migrationBuilder.CreateIndex(
                name: "IX_merch_document_versions_DocumentId_VersionNumber",
                table: "merch_document_versions",
                columns: new[] { "DocumentId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_merch_document_versions_OrderId_IsCurrent",
                table: "merch_document_versions",
                columns: new[] { "OrderId", "IsCurrent" });

            migrationBuilder.CreateIndex(
                name: "IX_merch_document_versions_Sha256",
                table: "merch_document_versions",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_merch_document_versions_Status_CreatedAt",
                table: "merch_document_versions",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_merch_document_versions_UploadedByUserId_IdempotencyKey",
                table: "merch_document_versions",
                columns: new[] { "UploadedByUserId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_merch_document_versions_current",
                table: "merch_document_versions",
                column: "DocumentId",
                unique: true,
                filter: "\"IsCurrent\"");

            migrationBuilder.CreateIndex(
                name: "IX_merch_documents_OrderId_Type",
                table: "merch_documents",
                columns: new[] { "OrderId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_merch_extracted_fields_VersionId_FieldKey",
                table: "merch_extracted_fields",
                columns: new[] { "VersionId", "FieldKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_merch_milestones_OrderId_DueDate",
                table: "merch_milestones",
                columns: new[] { "OrderId", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_merch_milestones_Status_DueDate",
                table: "merch_milestones",
                columns: new[] { "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_merch_orders_BuyerKey_BuyerOrderNumber",
                table: "merch_orders",
                columns: new[] { "BuyerKey", "BuyerOrderNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_merch_orders_ProductionOrderId",
                table: "merch_orders",
                column: "ProductionOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_merch_orders_Reference",
                table: "merch_orders",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_merch_orders_Status_DeliveryDate",
                table: "merch_orders",
                columns: new[] { "Status", "DeliveryDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "merch_conflicts");

            migrationBuilder.DropTable(
                name: "merch_document_pages");

            migrationBuilder.DropTable(
                name: "merch_extracted_fields");

            migrationBuilder.DropTable(
                name: "merch_milestones");

            migrationBuilder.DropTable(
                name: "merch_document_versions");

            migrationBuilder.DropTable(
                name: "merch_documents");

            migrationBuilder.DropTable(
                name: "merch_orders");
        }
    }
}
