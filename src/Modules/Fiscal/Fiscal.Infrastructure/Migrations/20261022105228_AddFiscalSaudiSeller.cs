using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fiscal.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalSaudiSeller : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdditionalNumber",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "District",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerId",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SellerIdScheme",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 10,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdditionalNumber",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "District",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "SellerId",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "SellerIdScheme",
                table: "fis_Settings");
        }
    }
}
