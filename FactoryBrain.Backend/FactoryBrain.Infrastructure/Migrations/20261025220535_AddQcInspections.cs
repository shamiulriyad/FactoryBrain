using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FactoryBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQcInspections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "qc_defect_categories",
                columns: table => new
                {
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    NameBn = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DefaultSeverity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qc_defect_categories", x => x.Code);
                    table.CheckConstraint("CK_qc_defect_categories_code", "\"Code\" ~ '^[a-z][a-z0-9_]{1,39}$'");
                });

            migrationBuilder.CreateTable(
                name: "qc_inspections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProductType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Stage = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ProductionOrder = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequestedCategoryCodes = table.Column<List<string>>(type: "text[]", nullable: false),
                    ImageStorageKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ImageContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ImageSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ImageSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImageWidth = table.Column<int>(type: "integer", nullable: false),
                    ImageHeight = table.Column<int>(type: "integer", nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ProcessingStartedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    AnalyzedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ModelVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LocalizationSupported = table.Column<bool>(type: "boolean", nullable: true),
                    ConfidenceThreshold = table.Column<double>(type: "double precision", nullable: true),
                    InferenceMs = table.Column<int>(type: "integer", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FollowUpApprovalId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qc_inspections", x => x.Id);
                    table.CheckConstraint("CK_qc_inspections_attempts", "\"Attempts\" >= 0");
                    table.CheckConstraint("CK_qc_inspections_image_dims", "\"ImageWidth\" > 0 AND \"ImageHeight\" > 0");
                    table.CheckConstraint("CK_qc_inspections_image_size", "\"ImageSizeBytes\" > 0");
                    table.CheckConstraint("CK_qc_inspections_review", "\"Status\" <> 'Reviewed' OR (\"Outcome\" IS NOT NULL AND \"ReviewedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_qc_inspections_threshold", "\"ConfidenceThreshold\" IS NULL OR (\"ConfidenceThreshold\" >= 0 AND \"ConfidenceThreshold\" <= 1)");
                });

            migrationBuilder.CreateTable(
                name: "qc_inspection_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CategoryCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RawLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    BoxX = table.Column<double>(type: "double precision", nullable: true),
                    BoxY = table.Column<double>(type: "double precision", nullable: true),
                    BoxWidth = table.Column<double>(type: "double precision", nullable: true),
                    BoxHeight = table.Column<double>(type: "double precision", nullable: true),
                    Polygon = table.Column<double[]>(type: "double precision[]", nullable: true),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CorrectedCategoryCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    CorrectedSeverity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ReviewerNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qc_inspection_findings", x => x.Id);
                    table.CheckConstraint("CK_qc_findings_box", "(\"BoxX\" IS NULL AND \"BoxY\" IS NULL AND \"BoxWidth\" IS NULL AND \"BoxHeight\" IS NULL) OR (\"BoxX\" >= 0 AND \"BoxY\" >= 0 AND \"BoxWidth\" > 0 AND \"BoxHeight\" > 0 AND \"BoxX\" + \"BoxWidth\" <= 1.0001 AND \"BoxY\" + \"BoxHeight\" <= 1.0001)");
                    table.CheckConstraint("CK_qc_findings_confidence", "\"Confidence\" IS NULL OR (\"Confidence\" >= 0 AND \"Confidence\" <= 1)");
                    table.CheckConstraint("CK_qc_findings_source", "(\"Source\" = 'Inspector' AND \"Confidence\" IS NULL) OR (\"Source\" = 'Model' AND \"RawLabel\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_qc_inspection_findings_qc_defect_categories_CategoryCode",
                        column: x => x.CategoryCode,
                        principalTable: "qc_defect_categories",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_qc_inspection_findings_qc_defect_categories_CorrectedCatego~",
                        column: x => x.CorrectedCategoryCode,
                        principalTable: "qc_defect_categories",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_qc_inspection_findings_qc_inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalTable: "qc_inspections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_qc_defect_categories_Name",
                table: "qc_defect_categories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspection_findings_CategoryCode",
                table: "qc_inspection_findings",
                column: "CategoryCode");

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspection_findings_CorrectedCategoryCode",
                table: "qc_inspection_findings",
                column: "CorrectedCategoryCode");

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspection_findings_InspectionId",
                table: "qc_inspection_findings",
                column: "InspectionId");

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspections_CreatedAt",
                table: "qc_inspections",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspections_CreatedByUserId_IdempotencyKey",
                table: "qc_inspections",
                columns: new[] { "CreatedByUserId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspections_LineId_CreatedAt",
                table: "qc_inspections",
                columns: new[] { "LineId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspections_Reference",
                table: "qc_inspections",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_qc_inspections_Status_CreatedAt",
                table: "qc_inspections",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "qc_inspection_findings");

            migrationBuilder.DropTable(
                name: "qc_defect_categories");

            migrationBuilder.DropTable(
                name: "qc_inspections");
        }
    }
}
