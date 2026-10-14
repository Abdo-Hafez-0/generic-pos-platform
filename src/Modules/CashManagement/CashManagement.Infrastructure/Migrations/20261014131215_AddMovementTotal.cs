using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMovementTotal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MovementTotal",
                table: "cash_Sessions",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            // MISS-06a: existing shifts get the total of the movements they already hold (in = pay-in 1 and cash sale 3, out = pay-out 2 and
            // refund 4). Amounts carry at most 4 decimals, so rounding the sum to 4 places gives the exact decimal total back.
            migrationBuilder.Sql("""
                UPDATE "cash_Sessions"
                SET "MovementTotal" = COALESCE((
                    SELECT ROUND(SUM(CASE WHEN m."Kind" IN (1, 3) THEN CAST(m."Amount" AS REAL) ELSE -CAST(m."Amount" AS REAL) END), 4)
                    FROM "cash_Movements" AS m
                    WHERE m."SessionId" = "cash_Sessions"."Id"), 0);
                """);

            migrationBuilder.CreateIndex(
                name: "UX_cash_Movements_Reference",
                table: "cash_Movements",
                columns: new[] { "SessionId", "Kind", "ReferenceType", "ReferenceId" },
                unique: true,
                filter: "\"ReferenceType\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_cash_Movements_Reference",
                table: "cash_Movements");

            migrationBuilder.DropColumn(
                name: "MovementTotal",
                table: "cash_Sessions");
        }
    }
}
