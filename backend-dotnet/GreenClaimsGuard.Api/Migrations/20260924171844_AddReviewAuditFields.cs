using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreenClaimsGuard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "actor_user_id",
                table: "compliance_reviews",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_status",
                table: "compliance_reviews",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "market",
                table: "compliance_reviews",
                type: "nvarchar(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "open_issue_count",
                table: "compliance_reviews",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "override_reason",
                table: "compliance_reviews",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rules_version",
                table: "compliance_reviews",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "actor_user_id",
                table: "compliance_reviews");

            migrationBuilder.DropColumn(
                name: "ai_status",
                table: "compliance_reviews");

            migrationBuilder.DropColumn(
                name: "market",
                table: "compliance_reviews");

            migrationBuilder.DropColumn(
                name: "open_issue_count",
                table: "compliance_reviews");

            migrationBuilder.DropColumn(
                name: "override_reason",
                table: "compliance_reviews");

            migrationBuilder.DropColumn(
                name: "rules_version",
                table: "compliance_reviews");
        }
    }
}
