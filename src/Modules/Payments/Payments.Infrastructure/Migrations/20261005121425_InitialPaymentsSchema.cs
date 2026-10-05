using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPaymentsSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pay_Payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReferenceType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    MethodDetail = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    TenderedAmount = table.Column<decimal>(type: "TEXT", nullable: true),
                    RecordedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VoidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VoidReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pay_Payments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pay_Payments_RecordedAt",
                table: "pay_Payments",
                column: "RecordedAt");

            migrationBuilder.CreateIndex(
                name: "IX_pay_Payments_ReferenceType_ReferenceId",
                table: "pay_Payments",
                columns: new[] { "ReferenceType", "ReferenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_pay_Payments_Status",
                table: "pay_Payments",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pay_Payments");
        }
    }
}
