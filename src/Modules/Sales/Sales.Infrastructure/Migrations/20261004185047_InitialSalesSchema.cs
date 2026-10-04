using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSalesSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sal_Returns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalSaleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sal_Returns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sal_Sales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancellationReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sal_Sales", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sal_SalesTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SaleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GrandTotal = table.Column<decimal>(type: "TEXT", nullable: false),
                    TaxTotal = table.Column<decimal>(type: "TEXT", nullable: false),
                    TransactedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sal_SalesTransactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sal_ReturnItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReturnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalSaleItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CatalogProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sal_ReturnItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sal_ReturnItems_sal_Returns_ReturnId",
                        column: x => x.ReturnId,
                        principalTable: "sal_Returns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sal_SaleItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SaleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CatalogProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProductSku = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    Discount = table.Column<decimal>(type: "TEXT", nullable: false),
                    TaxRate = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sal_SaleItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sal_SaleItems_sal_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "sal_Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sal_ReturnItems_OriginalSaleItemId",
                table: "sal_ReturnItems",
                column: "OriginalSaleItemId");

            migrationBuilder.CreateIndex(
                name: "IX_sal_ReturnItems_ReturnId",
                table: "sal_ReturnItems",
                column: "ReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_sal_Returns_OriginalSaleId",
                table: "sal_Returns",
                column: "OriginalSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_sal_Returns_Status",
                table: "sal_Returns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_sal_SaleItems_CatalogProductId",
                table: "sal_SaleItems",
                column: "CatalogProductId");

            migrationBuilder.CreateIndex(
                name: "IX_sal_SaleItems_SaleId",
                table: "sal_SaleItems",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_sal_Sales_CreatedAt",
                table: "sal_Sales",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_sal_Sales_Status",
                table: "sal_Sales",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_sal_SalesTransactions_SaleId",
                table: "sal_SalesTransactions",
                column: "SaleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sal_SalesTransactions_TransactedAt",
                table: "sal_SalesTransactions",
                column: "TransactedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sal_ReturnItems");

            migrationBuilder.DropTable(
                name: "sal_SaleItems");

            migrationBuilder.DropTable(
                name: "sal_SalesTransactions");

            migrationBuilder.DropTable(
                name: "sal_Returns");

            migrationBuilder.DropTable(
                name: "sal_Sales");
        }
    }
}
