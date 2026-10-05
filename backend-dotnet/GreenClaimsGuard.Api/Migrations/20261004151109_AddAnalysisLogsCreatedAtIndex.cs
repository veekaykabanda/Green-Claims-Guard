using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreenClaimsGuard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisLogsCreatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // legacy_product_key/validation_json drops that EF also scaffolded here are an unrelated,
            // pre-existing model/DB drift (from an earlier cleanup) -- deliberately left out of this
            // migration so it does exactly one thing: add the missing index.
            migrationBuilder.CreateIndex(
                name: "IX_analysis_logs_created_at",
                table: "analysis_logs",
                column: "created_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_analysis_logs_created_at",
                table: "analysis_logs");
        }
    }
}
