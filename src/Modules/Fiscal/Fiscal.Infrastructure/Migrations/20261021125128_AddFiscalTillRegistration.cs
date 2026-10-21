using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fiscal.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalTillRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "fis_Tills",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Environment",
                table: "fis_Tills",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "FirstSentAt",
                table: "fis_Tills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastSignInAt",
                table: "fis_Tills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastSignInOk",
                table: "fis_Tills",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PosModelFramework",
                table: "fis_Tills",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PosOsVersion",
                table: "fis_Tills",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RegisteredAt",
                table: "fis_Tills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegisteredBy",
                table: "fis_Tills",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "Environment",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "FirstSentAt",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "LastSignInAt",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "LastSignInOk",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "PosModelFramework",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "PosOsVersion",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "RegisteredAt",
                table: "fis_Tills");

            migrationBuilder.DropColumn(
                name: "RegisteredBy",
                table: "fis_Tills");
        }
    }
}
