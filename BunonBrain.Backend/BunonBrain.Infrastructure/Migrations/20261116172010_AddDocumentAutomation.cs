using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BunonBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_api_usage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Provider = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Model = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: true),
                    OutputTokens = table.Column<int>(type: "integer", nullable: true),
                    InputChars = table.Column<int>(type: "integer", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    EntityType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    EntityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_api_usage", x => x.Id);
                    table.CheckConstraint("CK_ai_api_usage_attempts", "\"Attempts\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "document_type_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovalLevel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AutoGenerate = table.Column<bool>(type: "boolean", nullable: false),
                    ExpectedDaily = table.Column<bool>(type: "boolean", nullable: false),
                    DecisionDueHours = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_type_settings", x => x.Id);
                    table.CheckConstraint("CK_document_type_settings_approval", "\"ApprovalLevel\" IN ('NONE','SINGLE','REVIEW_AND_APPROVE')");
                    table.CheckConstraint("CK_document_type_settings_due", "\"DecisionDueHours\" BETWEEN 1 AND 720");
                    table.ForeignKey(
                        name: "FK_document_type_settings_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "factory_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    TypeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Category = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: true),
                    LineId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Risk = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ApprovalLevel = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Source = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    CurrentVersion = table.Column<int>(type: "integer", nullable: false),
                    DedupKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MissingCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: false),
                    WarningCount = table.Column<int>(type: "integer", nullable: false),
                    StockPosted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReviewStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewStartedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VerifiedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    VerifiedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AiSummary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AiSummaryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AiSummaryModel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_documents", x => x.Id);
                    table.CheckConstraint("CK_factory_documents_approval", "\"ApprovalLevel\" IN ('NONE','SINGLE','REVIEW_AND_APPROVE')");
                    table.CheckConstraint("CK_factory_documents_period", "\"PeriodFrom\" IS NULL OR \"PeriodTo\" IS NULL OR \"PeriodTo\" >= \"PeriodFrom\"");
                    table.CheckConstraint("CK_factory_documents_rejected_reason", "\"Status\" <> 'REJECTED' OR \"DecisionNote\" IS NOT NULL");
                    table.CheckConstraint("CK_factory_documents_risk", "\"Risk\" IN ('LOW','NORMAL','HIGH')");
                    table.CheckConstraint("CK_factory_documents_source", "\"Source\" IN ('FORM','GENERATED','UPLOAD','TEMPLATE')");
                    table.CheckConstraint("CK_factory_documents_status", "\"Status\" IN ('DRAFT','SUBMITTED','UNDER_REVIEW','APPROVED','REJECTED','ARCHIVED')");
                    table.ForeignKey(
                        name: "FK_factory_documents_departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_factory_documents_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_factory_documents_production_lines_LineId",
                        column: x => x.LineId,
                        principalTable: "production_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inventory_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CodeKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReorderLevel = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_items", x => x.Id);
                    table.CheckConstraint("CK_inventory_items_category", "\"Category\" IN ('FABRIC','ACCESSORIES','TRIMS','PACKING','CHEMICALS','OTHER')");
                    table.CheckConstraint("CK_inventory_items_reorder", "\"ReorderLevel\" IS NULL OR \"ReorderLevel\" >= 0");
                    table.ForeignKey(
                        name: "FK_inventory_items_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Link = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    EntityType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    EntityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DedupKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_notifications_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_notifications_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "factory_document_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    FromStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ToStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    VersionNumber = table.Column<int>(type: "integer", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_document_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_factory_document_events_factory_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "factory_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "factory_document_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Format = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ExtractionStatus = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    ExtractionJson = table.Column<string>(type: "jsonb", nullable: true),
                    ExtractionMethod = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ExtractionModel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ExtractedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExtractionAttempts = table.Column<int>(type: "integer", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SuggestedTypeCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_document_files", x => x.Id);
                    table.CheckConstraint("CK_factory_document_files_extraction", "\"ExtractionStatus\" IN ('NONE','COMPLETED','FAILED')");
                    table.CheckConstraint("CK_factory_document_files_format", "\"Format\" IN ('pdf','docx','xlsx','csv','txt','image')");
                    table.CheckConstraint("CK_factory_document_files_size", "\"SizeBytes\" > 0");
                    table.ForeignKey(
                        name: "FK_factory_document_files_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_factory_document_files_factory_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "factory_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "factory_document_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourcesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ChecksJson = table.Column<string>(type: "jsonb", nullable: false),
                    MissingCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCount = table.Column<int>(type: "integer", nullable: false),
                    WarningCount = table.Column<int>(type: "integer", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChangeNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_factory_document_versions", x => x.Id);
                    table.CheckConstraint("CK_factory_document_versions_number", "\"Number\" >= 1");
                    table.ForeignKey(
                        name: "FK_factory_document_versions_factory_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "factory_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_movements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    FactoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    MovementDate = table.Column<DateOnly>(type: "date", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentReference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    Counterparty = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedByEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stock_movements", x => x.Id);
                    table.CheckConstraint("CK_stock_movements_cost", "\"UnitCost\" IS NULL OR \"UnitCost\" >= 0");
                    table.CheckConstraint("CK_stock_movements_kind", "\"Kind\" IN ('RECEIPT','ISSUE')");
                    table.CheckConstraint("CK_stock_movements_quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_stock_movements_factories_FactoryId",
                        column: x => x.FactoryId,
                        principalTable: "factories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_movements_factory_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "factory_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stock_movements_inventory_items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "inventory_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_usage_FactoryId_OccurredAt",
                table: "ai_api_usage",
                columns: new[] { "FactoryId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_api_usage_OccurredAt",
                table: "ai_api_usage",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_document_type_settings_FactoryId_TypeCode",
                table: "document_type_settings",
                columns: new[] { "FactoryId", "TypeCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_document_events_DocumentId_OccurredAt",
                table: "factory_document_events",
                columns: new[] { "DocumentId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_document_events_FactoryId_OccurredAt",
                table: "factory_document_events",
                columns: new[] { "FactoryId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_document_files_DocumentId_Sha256",
                table: "factory_document_files",
                columns: new[] { "DocumentId", "Sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_document_files_FactoryId",
                table: "factory_document_files",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_factory_document_versions_DocumentId_Number",
                table: "factory_document_versions",
                columns: new[] { "DocumentId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_factory_documents_DepartmentId",
                table: "factory_documents",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_factory_documents_FactoryId_Category_Status",
                table: "factory_documents",
                columns: new[] { "FactoryId", "Category", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_documents_FactoryId_TypeCode_PeriodFrom",
                table: "factory_documents",
                columns: new[] { "FactoryId", "TypeCode", "PeriodFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_factory_documents_LineId",
                table: "factory_documents",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_factory_documents_Reference",
                table: "factory_documents",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_factory_documents_live_dedup",
                table: "factory_documents",
                columns: new[] { "FactoryId", "DedupKey" },
                unique: true,
                filter: "\"DedupKey\" IS NOT NULL AND \"Status\" <> 'ARCHIVED'");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_items_FactoryId_CodeKey",
                table: "inventory_items",
                columns: new[] { "FactoryId", "CodeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_movements_DocumentId_LineNo",
                table: "stock_movements",
                columns: new[] { "DocumentId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_movements_FactoryId_ItemId_MovementDate",
                table: "stock_movements",
                columns: new[] { "FactoryId", "ItemId", "MovementDate" });

            migrationBuilder.CreateIndex(
                name: "IX_stock_movements_ItemId",
                table: "stock_movements",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_FactoryId",
                table: "user_notifications",
                column: "FactoryId");

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_UserId_ReadAt_CreatedAt",
                table: "user_notifications",
                columns: new[] { "UserId", "ReadAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_user_notifications_dedup",
                table: "user_notifications",
                columns: new[] { "UserId", "DedupKey" },
                unique: true,
                filter: "\"DedupKey\" IS NOT NULL");
            // History is append-only: versions, workflow events, AI usage and stock movements can be added, never changed or removed.
            migrationBuilder.Sql(@"
                CREATE TRIGGER factory_document_versions_no_update_delete BEFORE UPDATE OR DELETE ON factory_document_versions
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER factory_document_versions_no_truncate BEFORE TRUNCATE ON factory_document_versions
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER factory_document_events_no_update_delete BEFORE UPDATE OR DELETE ON factory_document_events
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER factory_document_events_no_truncate BEFORE TRUNCATE ON factory_document_events
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER ai_api_usage_no_update_delete BEFORE UPDATE OR DELETE ON ai_api_usage
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER stock_movements_no_update_delete BEFORE UPDATE OR DELETE ON stock_movements
                    FOR EACH ROW EXECUTE FUNCTION monitoring_history_reject_mutation();
                CREATE TRIGGER stock_movements_no_truncate BEFORE TRUNCATE ON stock_movements
                    FOR EACH STATEMENT EXECUTE FUNCTION monitoring_history_reject_mutation();
            ");
        }


        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS factory_document_versions_no_update_delete ON factory_document_versions;
                DROP TRIGGER IF EXISTS factory_document_versions_no_truncate ON factory_document_versions;
                DROP TRIGGER IF EXISTS factory_document_events_no_update_delete ON factory_document_events;
                DROP TRIGGER IF EXISTS factory_document_events_no_truncate ON factory_document_events;
                DROP TRIGGER IF EXISTS ai_api_usage_no_update_delete ON ai_api_usage;
                DROP TRIGGER IF EXISTS stock_movements_no_update_delete ON stock_movements;
                DROP TRIGGER IF EXISTS stock_movements_no_truncate ON stock_movements;
            ");

            migrationBuilder.DropTable(
                name: "ai_api_usage");

            migrationBuilder.DropTable(
                name: "document_type_settings");

            migrationBuilder.DropTable(
                name: "factory_document_events");

            migrationBuilder.DropTable(
                name: "factory_document_files");

            migrationBuilder.DropTable(
                name: "factory_document_versions");

            migrationBuilder.DropTable(
                name: "stock_movements");

            migrationBuilder.DropTable(
                name: "user_notifications");

            migrationBuilder.DropTable(
                name: "factory_documents");

            migrationBuilder.DropTable(
                name: "inventory_items");
        }
    }
}
