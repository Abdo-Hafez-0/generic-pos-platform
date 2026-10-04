using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialPOSSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pos_Carts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CheckedOutAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SaleId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pos_Carts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pos_Sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CashierReference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    WarehouseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pos_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "pos_CartItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CartId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CatalogProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductSku = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pos_CartItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pos_CartItems_pos_Carts_CartId",
                        column: x => x.CartId,
                        principalTable: "pos_Carts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pos_CartItems_CartId",
                table: "pos_CartItems",
                column: "CartId");

            migrationBuilder.CreateIndex(
                name: "IX_pos_CartItems_CatalogProductId",
                table: "pos_CartItems",
                column: "CatalogProductId");

            migrationBuilder.CreateIndex(
                name: "IX_pos_Carts_SessionId",
                table: "pos_Carts",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_pos_Carts_Status",
                table: "pos_Carts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_pos_Sessions_Status",
                table: "pos_Sessions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pos_CartItems");

            migrationBuilder.DropTable(
                name: "pos_Sessions");

            migrationBuilder.DropTable(
                name: "pos_Carts");
        }
    }
}
