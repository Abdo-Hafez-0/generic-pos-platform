using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPricingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pri_PriceLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pri_PriceLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pri_Prices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PriceListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    MinimumQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pri_Prices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pri_Prices_pri_PriceLists_PriceListId",
                        column: x => x.PriceListId,
                        principalTable: "pri_PriceLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pri_PriceLists_Code",
                table: "pri_PriceLists",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pri_PriceLists_IsDefault",
                table: "pri_PriceLists",
                column: "IsDefault");

            migrationBuilder.CreateIndex(
                name: "IX_pri_Prices_PriceListId",
                table: "pri_Prices",
                column: "PriceListId");

            migrationBuilder.CreateIndex(
                name: "IX_pri_Prices_ProductId_PriceListId",
                table: "pri_Prices",
                columns: new[] { "ProductId", "PriceListId" });

            migrationBuilder.CreateIndex(
                name: "IX_pri_Prices_Status",
                table: "pri_Prices",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pri_Prices");

            migrationBuilder.DropTable(
                name: "pri_PriceLists");
        }
    }
}
