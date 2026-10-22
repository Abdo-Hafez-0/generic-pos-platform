using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fiscal.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalZatcaOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Certificate",
                table: "fis_Tills",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CertificateExpiresAt",
                table: "fis_Tills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitBranch",
                table: "fis_Tills",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitIndustry",
                table: "fis_Tills",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitLocation",
                table: "fis_Tills",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnitName",
                table: "fis_Tills",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Certificate",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "CertificateExpiresAt",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "UnitBranch",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "UnitIndustry",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "UnitLocation",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "UnitName",
                table: "fis_Tills");
        }
    }
}
