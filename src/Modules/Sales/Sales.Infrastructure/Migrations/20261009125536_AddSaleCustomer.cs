using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleCustomer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomerCode",
                table: "sal_Sales",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "sal_Sales",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerName",
                table: "sal_Sales",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sal_Sales_CustomerId",
                table: "sal_Sales",
                column: "CustomerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sal_Sales_CustomerId",
                table: "sal_Sales");

            migrationBuilder.DropColumn(
                name: "CustomerCode",
                table: "sal_Sales");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "sal_Sales");

            migrationBuilder.DropColumn(
                name: "CustomerName",
                table: "sal_Sales");
        }
    }
}
