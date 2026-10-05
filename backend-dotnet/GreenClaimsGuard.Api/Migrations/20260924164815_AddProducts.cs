using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreenClaimsGuard.Api.Migrations
{
    /// <inheritdoc />
    // Hand-edited: the scaffolded version altered the text product_id straight to a uniqueidentifier,
    // which fails on existing rows. This version creates one product per distinct legacy key first.
    public partial class AddProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    created_by_user_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.id);
                });

            // Keep the old free-text key on migrated rows so the history stays traceable.
            migrationBuilder.RenameColumn(
                name: "product_id",
                table: "compliance_reviews",
                newName: "legacy_product_key");

            migrationBuilder.AlterColumn<string>(
                name: "legacy_product_key",
                table: "compliance_reviews",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128);

            migrationBuilder.AddColumn<Guid>(
                name: "product_id",
                table: "compliance_reviews",
                type: "uniqueidentifier",
                nullable: true);

            // One product per distinct legacy key, ignoring case and surrounding spaces. Rows that
            // shared a key (e.g. every unnamed "draft-product") become one product: they were already
            // indistinguishable, so this cannot be split apart retroactively.
            migrationBuilder.Sql(@"
INSERT INTO products (id, name, created_at)
SELECT NEWID(), MIN(legacy_product_key), SYSUTCDATETIME()
FROM compliance_reviews
GROUP BY LOWER(LTRIM(RTRIM(legacy_product_key)));");

            migrationBuilder.Sql(@"
UPDATE r
SET r.product_id = p.id
FROM compliance_reviews r
JOIN products p ON LOWER(LTRIM(RTRIM(p.name))) = LOWER(LTRIM(RTRIM(r.legacy_product_key)));");

            migrationBuilder.AlterColumn<Guid>(
                name: "product_id",
                table: "compliance_reviews",
                type: "uniqueidentifier",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_compliance_reviews_product_id_created_at",
                table: "compliance_reviews",
                columns: new[] { "product_id", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_compliance_reviews_products_product_id",
                table: "compliance_reviews",
                column: "product_id",
                principalTable: "products",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_compliance_reviews_products_product_id",
                table: "compliance_reviews");

            migrationBuilder.DropIndex(
                name: "IX_compliance_reviews_product_id_created_at",
                table: "compliance_reviews");

            // Rows created after the migration have no legacy key; restore it from the product name.
            migrationBuilder.Sql(@"
UPDATE r
SET r.legacy_product_key = LEFT(p.name, 128)
FROM compliance_reviews r
JOIN products p ON p.id = r.product_id
WHERE r.legacy_product_key IS NULL;");

            migrationBuilder.DropColumn(
                name: "product_id",
                table: "compliance_reviews");

            migrationBuilder.AlterColumn<string>(
                name: "legacy_product_key",
                table: "compliance_reviews",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(128)",
                oldMaxLength: 128,
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "legacy_product_key",
                table: "compliance_reviews",
                newName: "product_id");

            migrationBuilder.DropTable(
                name: "products");
        }
    }
}
