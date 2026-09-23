using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventorySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inv_InventoryBalances",
                columns: table => new
                {
                    StockItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OnHand = table.Column<decimal>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inv_InventoryBalances", x => x.StockItemId);
                });

            migrationBuilder.CreateTable(
                name: "inv_Locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inv_Locations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inv_StockAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StockItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdjustmentQuantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AdjustedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inv_StockAdjustments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inv_StockItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CatalogProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inv_StockItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inv_StockMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StockItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MovementType = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inv_StockMovements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inv_Warehouses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inv_Warehouses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inv_Locations_WarehouseId_Code",
                table: "inv_Locations",
                columns: new[] { "WarehouseId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inv_StockAdjustments_StockItemId",
                table: "inv_StockAdjustments",
                column: "StockItemId");

            migrationBuilder.CreateIndex(
                name: "IX_inv_StockItems_CatalogProductId_WarehouseId",
                table: "inv_StockItems",
                columns: new[] { "CatalogProductId", "WarehouseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inv_StockMovements_OccurredAt",
                table: "inv_StockMovements",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_inv_StockMovements_StockItemId",
                table: "inv_StockMovements",
                column: "StockItemId");

            migrationBuilder.CreateIndex(
                name: "IX_inv_Warehouses_Code",
                table: "inv_Warehouses",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inv_InventoryBalances");

            migrationBuilder.DropTable(
                name: "inv_Locations");

            migrationBuilder.DropTable(
                name: "inv_StockAdjustments");

            migrationBuilder.DropTable(
                name: "inv_StockItems");

            migrationBuilder.DropTable(
                name: "inv_StockMovements");

            migrationBuilder.DropTable(
                name: "inv_Warehouses");
        }
    }
}
