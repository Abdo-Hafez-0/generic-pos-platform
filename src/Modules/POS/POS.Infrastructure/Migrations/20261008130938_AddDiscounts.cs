using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace POS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CartDiscountKind",
                table: "pos_Carts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CartDiscountValue",
                table: "pos_Carts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LineDiscountKind",
                table: "pos_CartItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LineDiscountValue",
                table: "pos_CartItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CartDiscountKind",
                table: "pos_Carts");

            migrationBuilder.DropColumn(
                name: "CartDiscountValue",
                table: "pos_Carts");

            migrationBuilder.DropColumn(
                name: "LineDiscountKind",
                table: "pos_CartItems");

            migrationBuilder.DropColumn(
                name: "LineDiscountValue",
                table: "pos_CartItems");
        }
    }
}
