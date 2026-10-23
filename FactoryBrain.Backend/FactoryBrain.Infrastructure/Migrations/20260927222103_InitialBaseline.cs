using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace FactoryBrain.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "activity_events",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    ActorLabel = table.Column<string>(type: "text", nullable: false),
                    Verb = table.Column<string>(type: "text", nullable: false),
                    VerbBn = table.Column<string>(type: "text", nullable: false),
                    Target = table.Column<string>(type: "text", nullable: true),
                    TargetBn = table.Column<string>(type: "text", nullable: true),
                    Outcome = table.Column<string>(type: "text", nullable: true),
                    IsoDate = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    NameBn = table.Column<string>(type: "text", nullable: false),
                    Purpose = table.Column<string>(type: "text", nullable: false),
                    PurposeBn = table.Column<string>(type: "text", nullable: false),
                    Risk = table.Column<int>(type: "integer", nullable: false),
                    Execution = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    ContextSlices = table.Column<List<string>>(type: "text[]", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    TasksToday = table.Column<int>(type: "integer", nullable: false),
                    RecentCount = table.Column<int>(type: "integer", nullable: false),
                    Glyph = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "approvals",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    InsightId = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    TitleBn = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: true),
                    RiskTier = table.Column<int>(type: "integer", nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approvals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Region = table.Column<string>(type: "text", nullable: false),
                    TotalOrders = table.Column<int>(type: "integer", nullable: false),
                    LtvBdt = table.Column<decimal>(type: "numeric", nullable: false),
                    ChurnRisk = table.Column<double>(type: "double precision", nullable: false),
                    LastOrderDays = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "floor_alerts",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    ApprovalId = table.Column<string>(type: "text", nullable: true),
                    InsightId = table.Column<string>(type: "text", nullable: true),
                    BodyEn = table.Column<string>(type: "text", nullable: false),
                    BodyBn = table.Column<string>(type: "text", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    Read = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_floor_alerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "insights",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    AgentId = table.Column<string>(type: "text", nullable: false),
                    AgentLabel = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    TitleBn = table.Column<string>(type: "text", nullable: false),
                    Finding = table.Column<string>(type: "text", nullable: false),
                    FindingBn = table.Column<string>(type: "text", nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    Pinned = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_insights", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inventory",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    Stock = table.Column<int>(type: "integer", nullable: false),
                    ReorderPoint = table.Column<int>(type: "integer", nullable: false),
                    ReorderQty = table.Column<int>(type: "integer", nullable: false),
                    DaysUntilStockout = table.Column<int>(type: "integer", nullable: false),
                    RecentDailyDemand = table.Column<double>(type: "double precision", nullable: false),
                    AtRisk = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "line_board_metrics",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    EfficiencyPct = table.Column<int>(type: "integer", nullable: false),
                    SahTarget = table.Column<int>(type: "integer", nullable: false),
                    SahActual = table.Column<int>(type: "integer", nullable: false),
                    WipBundles = table.Column<int>(type: "integer", nullable: false),
                    Bottleneck = table.Column<int>(type: "integer", nullable: false),
                    NptMinutes = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_line_board_metrics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "manual_documents",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TitleEn = table.Column<string>(type: "text", nullable: false),
                    TitleBn = table.Column<string>(type: "text", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    BodyEn = table.Column<string>(type: "text", nullable: false),
                    BodyBn = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    Department = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false)
                    // EmbeddingProvider / EmbeddingModel / Dims are added
                    // by the AddEmbeddingMetadata migration that follows.
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_documents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    CustomerId = table.Column<string>(type: "text", nullable: false),
                    ProductId = table.Column<string>(type: "text", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    TotalBdt = table.Column<decimal>(type: "numeric", nullable: false),
                    Region = table.Column<string>(type: "text", nullable: false),
                    Channel = table.Column<string>(type: "text", nullable: false),
                    DaysAgo = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "policies",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    TitleBn = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    BodyBn = table.Column<string>(type: "text", nullable: false),
                    EffectiveDate = table.Column<DateTime>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Sku = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    PriceBdt = table.Column<decimal>(type: "numeric", nullable: false),
                    CostBdt = table.Column<decimal>(type: "numeric", nullable: false),
                    LeadTimeDays = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "qc_defects",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Operation = table.Column<string>(type: "text", nullable: false),
                    LineId = table.Column<string>(type: "text", nullable: false),
                    InspectedTotal = table.Column<int>(type: "integer", nullable: false),
                    DefectsTotal = table.Column<int>(type: "integer", nullable: false),
                    MajorTotal = table.Column<int>(type: "integer", nullable: false),
                    MinorTotal = table.Column<int>(type: "integer", nullable: false),
                    ReworkTotal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qc_defects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sensor_readings",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    EntityId = table.Column<string>(type: "text", nullable: false),
                    Metric = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<double>(type: "double precision", nullable: false),
                    Unit = table.Column<string>(type: "text", nullable: false),
                    Ts = table.Column<DateTime>(type: "timestamptz", nullable: false),
                    SimTick = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sensor_readings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "suppliers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Region = table.Column<string>(type: "text", nullable: false),
                    LeadTimeDays = table.Column<int>(type: "integer", nullable: false),
                    OnTimeRate = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suppliers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "vision_results",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    SampleFile = table.Column<string>(type: "text", nullable: false),
                    AnomalyLabelEn = table.Column<string>(type: "text", nullable: false),
                    AnomalyLabelBn = table.Column<string>(type: "text", nullable: false),
                    RepairStepsEn = table.Column<List<string>>(type: "text[]", nullable: false),
                    RepairStepsBn = table.Column<List<string>>(type: "text[]", nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: false),
                    MachineId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vision_results", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "insight_evidence",
                columns: table => new
                {
                    InsightId = table.Column<string>(type: "text", nullable: false),
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Domain = table.Column<string>(type: "text", nullable: false),
                    Count = table.Column<int>(type: "integer", nullable: false),
                    Filter = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: true),
                    PreviewIds = table.Column<List<string>>(type: "text[]", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_insight_evidence", x => new { x.InsightId, x.Id });
                    table.ForeignKey(
                        name: "FK_insight_evidence_insights_InsightId",
                        column: x => x.InsightId,
                        principalTable: "insights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "insight_factors",
                columns: table => new
                {
                    InsightId = table.Column<string>(type: "text", nullable: false),
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Label = table.Column<string>(type: "text", nullable: false),
                    LabelBn = table.Column<string>(type: "text", nullable: false),
                    Magnitude = table.Column<string>(type: "text", nullable: false),
                    MagnitudeBn = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_insight_factors", x => new { x.InsightId, x.Id });
                    table.ForeignKey(
                        name: "FK_insight_factors_insights_InsightId",
                        column: x => x.InsightId,
                        principalTable: "insights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "insight_recommendations",
                columns: table => new
                {
                    InsightId = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    TitleBn = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    ActionBn = table.Column<string>(type: "text", nullable: false),
                    RiskTier = table.Column<int>(type: "integer", nullable: false),
                    TargetStage = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_insight_recommendations", x => x.InsightId);
                    table.ForeignKey(
                        name: "FK_insight_recommendations_insights_InsightId",
                        column: x => x.InsightId,
                        principalTable: "insights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "document_chunks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    DocumentId = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Department = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<string>(type: "text", nullable: false),
                    Tags = table.Column<List<string>>(type: "text[]", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(384)", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_chunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_chunks_manual_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "manual_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "qc_defect_weeks",
                columns: table => new
                {
                    WeekStart = table.Column<string>(type: "text", nullable: false),
                    QcDefectId = table.Column<string>(type: "text", nullable: false),
                    Inspected = table.Column<int>(type: "integer", nullable: false),
                    Defects = table.Column<int>(type: "integer", nullable: false),
                    Major = table.Column<int>(type: "integer", nullable: false),
                    Minor = table.Column<int>(type: "integer", nullable: false),
                    Rework = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_qc_defect_weeks", x => new { x.WeekStart, x.QcDefectId });
                    table.ForeignKey(
                        name: "FK_qc_defect_weeks_qc_defects_QcDefectId",
                        column: x => x.QcDefectId,
                        principalTable: "qc_defects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_chunks_Department_Category",
                table: "document_chunks",
                columns: new[] { "Department", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_document_chunks_DocumentId",
                table: "document_chunks",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_floor_alerts_CreatedAt",
                table: "floor_alerts",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_orders_DaysAgo",
                table: "orders",
                column: "DaysAgo");

            migrationBuilder.CreateIndex(
                name: "IX_orders_Region",
                table: "orders",
                column: "Region");

            migrationBuilder.CreateIndex(
                name: "IX_qc_defect_weeks_QcDefectId",
                table: "qc_defect_weeks",
                column: "QcDefectId");

            migrationBuilder.CreateIndex(
                name: "IX_qc_defects_Operation_LineId",
                table: "qc_defects",
                columns: new[] { "Operation", "LineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sensor_readings_SimTick",
                table: "sensor_readings",
                column: "SimTick");

            migrationBuilder.CreateIndex(
                name: "IX_sensor_readings_Source_EntityId",
                table: "sensor_readings",
                columns: new[] { "Source", "EntityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_events");

            migrationBuilder.DropTable(
                name: "agents");

            migrationBuilder.DropTable(
                name: "approvals");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropTable(
                name: "document_chunks");

            migrationBuilder.DropTable(
                name: "floor_alerts");

            migrationBuilder.DropTable(
                name: "insight_evidence");

            migrationBuilder.DropTable(
                name: "insight_factors");

            migrationBuilder.DropTable(
                name: "insight_recommendations");

            migrationBuilder.DropTable(
                name: "inventory");

            migrationBuilder.DropTable(
                name: "line_board_metrics");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "policies");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "qc_defect_weeks");

            migrationBuilder.DropTable(
                name: "sensor_readings");

            migrationBuilder.DropTable(
                name: "suppliers");

            migrationBuilder.DropTable(
                name: "vision_results");

            migrationBuilder.DropTable(
                name: "manual_documents");

            migrationBuilder.DropTable(
                name: "insights");

            migrationBuilder.DropTable(
                name: "qc_defects");
        }
    }
}
