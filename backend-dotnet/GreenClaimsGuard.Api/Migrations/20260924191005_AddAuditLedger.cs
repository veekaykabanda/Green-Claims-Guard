using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GreenClaimsGuard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_ledger",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    product_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    action = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    outcome = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    copy_snapshot = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    copy_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    market = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    rules_status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ai_status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    rules_version = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    issues_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    justification = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    user_id = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    occurred_at = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_ledger", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_ledger_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_ledger_occurred_at",
                table: "audit_ledger",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_audit_ledger_product_id_occurred_at",
                table: "audit_ledger",
                columns: new[] { "product_id", "occurred_at" });

            // Insert-only, enforced by the database itself: not even a direct SQL UPDATE or DELETE can
            // change history. (An INSTEAD OF trigger fires before the change, so nothing is modified.)
            migrationBuilder.Sql(@"
CREATE TRIGGER trg_audit_ledger_insert_only ON audit_ledger
INSTEAD OF UPDATE, DELETE
AS
BEGIN
    THROW 51000, N'audit_ledger is insert-only: rows cannot be updated or deleted.', 1;
END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER trg_audit_ledger_insert_only;");

            migrationBuilder.DropTable(
                name: "audit_ledger");
        }
    }
}
