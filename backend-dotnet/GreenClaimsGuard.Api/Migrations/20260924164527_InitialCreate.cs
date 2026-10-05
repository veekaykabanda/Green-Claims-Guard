using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreenClaimsGuard.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analysis_logs",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    input_text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    overall_risk = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    traffic_light = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    compliance_score = table.Column<int>(type: "int", nullable: true),
                    total_issues = table.Column<int>(type: "int", nullable: true),
                    rule_findings_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    grouped_findings_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ai_explanation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    suggested_rewrite = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "compliance_reviews",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    product_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    final_description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    overall_status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    decisions_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    validation_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compliance_reviews", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "GreenClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Claim = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GptRewrite = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GreenClaims", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "public_claims_seed",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    product_id = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    claim_sentence = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    issue_type = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    baseline = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    reduction_percentage = table.Column<double>(type: "float", nullable: true),
                    timeframe = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    evidence_reference = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    source_url = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    source_title = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    captured_date = table.Column<DateTime>(type: "date", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_public_claims_seed", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_public_claims_seed_product_id",
                table: "public_claims_seed",
                column: "product_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analysis_logs");

            migrationBuilder.DropTable(
                name: "compliance_reviews");

            migrationBuilder.DropTable(
                name: "GreenClaims");

            migrationBuilder.DropTable(
                name: "public_claims_seed");
        }
    }
}
