using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreenClaimsGuard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddVerifiedFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "facts_verified_at",
                table: "products",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "facts_verified_by_user_id",
                table: "products",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "products",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "product_certifications",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    product_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_certifications", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_certifications_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "product_materials",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    product_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    material = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    percentage = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_materials", x => x.id);
                    table.ForeignKey(
                        name: "FK_product_materials_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_certifications_product_id",
                table: "product_certifications",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "IX_product_materials_product_id",
                table: "product_materials",
                column: "product_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_certifications");

            migrationBuilder.DropTable(
                name: "product_materials");

            migrationBuilder.DropColumn(
                name: "facts_verified_at",
                table: "products");

            migrationBuilder.DropColumn(
                name: "facts_verified_by_user_id",
                table: "products");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "products");
        }
    }
}
