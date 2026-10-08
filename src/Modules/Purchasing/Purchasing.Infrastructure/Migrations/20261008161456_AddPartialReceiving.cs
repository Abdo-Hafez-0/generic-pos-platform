using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Purchasing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPartialReceiving : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "pur_PurchaseOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosingReason",
                table: "pur_PurchaseOrders",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReceivedAmount",
                table: "pur_PurchaseOrders",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReceivedQuantity",
                table: "pur_PurchaseOrderLines",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            // FIX-09 backfill: before part deliveries a line was received whole, so a received line has its full quantity (an exact text copy).
            migrationBuilder.Sql("UPDATE pur_PurchaseOrderLines SET ReceivedQuantity = Quantity WHERE ReceivedAt IS NOT NULL;");

            // A Received order received its whole total (exact copy). An order left Submitted with some lines received (only possible with
            // the resumable receiving before Stage 12) is partly received; its received value is computed from those lines.
            migrationBuilder.Sql("UPDATE pur_PurchaseOrders SET ReceivedAmount = TotalAmount WHERE Status = 3;");
            migrationBuilder.Sql(
                "UPDATE pur_PurchaseOrders SET Status = 5, ReceivedAmount = (" +
                "SELECT printf('%.4f', SUM(CAST(l.Quantity AS REAL) * CAST(l.UnitCost AS REAL))) FROM pur_PurchaseOrderLines l " +
                "WHERE l.PurchaseOrderId = pur_PurchaseOrders.Id AND l.ReceivedAt IS NOT NULL) " +
                "WHERE Status = 2 AND EXISTS (SELECT 1 FROM pur_PurchaseOrderLines l WHERE l.PurchaseOrderId = pur_PurchaseOrders.Id AND l.ReceivedAt IS NOT NULL);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // back to the earlier statuses: partly received was "Submitted", closed short has no equivalent and stays readable as "Received"
            migrationBuilder.Sql("UPDATE pur_PurchaseOrders SET Status = 2 WHERE Status = 5;");
            migrationBuilder.Sql("UPDATE pur_PurchaseOrders SET Status = 3 WHERE Status = 6;");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "pur_PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ClosingReason",
                table: "pur_PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ReceivedAmount",
                table: "pur_PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "ReceivedQuantity",
                table: "pur_PurchaseOrderLines");
        }
    }
}
