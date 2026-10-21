using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fiscal.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalAddressAndSerial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceSerial",
                table: "fis_Tills",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdditionalInformation",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuildingNumber",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Floor",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Governorate",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Landmark",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Room",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Street",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            // MISS-11c: the one address line of 11a is kept as the street (the other fields are filled on the Electronic receipts screen)
            migrationBuilder.Sql("UPDATE fis_Settings SET Street = substr(AddressArabic, 1, 200) WHERE AddressArabic IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "AddressArabic",
                table: "fis_Settings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressArabic",
                table: "fis_Settings",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.Sql("UPDATE fis_Settings SET AddressArabic = Street WHERE Street IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "DeviceSerial",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "AdditionalInformation",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "BuildingNumber",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "City",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "Floor",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "Governorate",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "Landmark",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "Room",
                table: "fis_Settings");

            migrationBuilder.DropColumn(
                name: "Street",
                table: "fis_Settings");
        }
    }
}
