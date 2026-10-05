using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashManagement.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCashManagementSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cash_Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DrawerCode = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    OpenedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    OpeningFloat = table.Column<decimal>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1001, nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CountedAmount = table.Column<decimal>(type: "TEXT", nullable: true),
                    ExpectedAmount = table.Column<decimal>(type: "TEXT", nullable: true),
                    Variance = table.Column<decimal>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cash_Movements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReferenceType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ReferenceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RecordedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_Movements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cash_Movements_cash_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "cash_Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cash_Movements_ReferenceType_ReferenceId",
                table: "cash_Movements",
                columns: new[] { "ReferenceType", "ReferenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_Movements_SessionId",
                table: "cash_Movements",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_cash_Sessions_DrawerCode_Status",
                table: "cash_Sessions",
                columns: new[] { "DrawerCode", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_cash_Sessions_OpenedAt",
                table: "cash_Sessions",
                column: "OpenedAt");

            migrationBuilder.CreateIndex(
                name: "UX_cash_Sessions_OpenDrawer",
                table: "cash_Sessions",
                column: "DrawerCode",
                unique: true,
                filter: "\"Status\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_Movements");

            migrationBuilder.DropTable(
                name: "cash_Sessions");
        }
    }
}
