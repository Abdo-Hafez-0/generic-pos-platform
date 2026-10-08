using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Purchasing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedQuantity",
                table: "pur_PurchaseOrderLines",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "pur_SupplierReturns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PurchaseOrderNumber = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SupplierId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SupplierName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    WarehouseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pur_SupplierReturns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pur_SupplierReturns_pur_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "pur_PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pur_SupplierReturnLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SupplierReturnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PurchaseOrderLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductSku = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    UnitCost = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pur_SupplierReturnLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_pur_SupplierReturnLines_pur_SupplierReturns_SupplierReturnId",
                        column: x => x.SupplierReturnId,
                        principalTable: "pur_SupplierReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pur_SupplierReturnLines_SupplierReturnId",
                table: "pur_SupplierReturnLines",
                column: "SupplierReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_pur_SupplierReturns_CreatedAt",
                table: "pur_SupplierReturns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_pur_SupplierReturns_Number",
                table: "pur_SupplierReturns",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pur_SupplierReturns_PurchaseOrderId",
                table: "pur_SupplierReturns",
                column: "PurchaseOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pur_SupplierReturnLines");

            migrationBuilder.DropTable(
                name: "pur_SupplierReturns");

            migrationBuilder.DropColumn(
                name: "ReturnedQuantity",
                table: "pur_PurchaseOrderLines");
        }
    }
}
