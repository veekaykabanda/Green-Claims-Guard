using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreenClaimsGuard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProductDrafts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_drafts",
                columns: table => new
                {
                    product_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    text = table.Column<string>(type: "nvarchar(max)", maxLength: 5000, nullable: false),
                    market = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    saved_at = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_drafts", x => x.product_id);
                    table.ForeignKey(
                        name: "FK_product_drafts_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_drafts_saved_at",
                table: "product_drafts",
                column: "saved_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_drafts");
        }
    }
}
