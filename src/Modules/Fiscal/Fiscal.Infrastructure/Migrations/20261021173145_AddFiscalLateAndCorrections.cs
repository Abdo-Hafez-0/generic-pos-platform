using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fiscal.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalLateAndCorrections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fis_Documents_Kind_SourceId",
                table: "fis_Documents");

            migrationBuilder.DropIndex(
                name: "IX_fis_Documents_TillId_Number",
                table: "fis_Documents");

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedByDocumentId",
                table: "fis_Documents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacesDocumentId",
                table: "fis_Documents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Request",
                table: "fis_Documents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SendLate",
                table: "fis_Documents",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_fis_Documents_Kind_SourceId",
                table: "fis_Documents",
                columns: new[] { "Kind", "SourceId" },
                unique: true,
                filter: "State <> 7");

            migrationBuilder.CreateIndex(
                name: "IX_fis_Documents_TillId_Number",
                table: "fis_Documents",
                columns: new[] { "TillId", "Number" },
                unique: true,
                filter: "Number IS NOT NULL AND State <> 7");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fis_Documents_Kind_SourceId",
                table: "fis_Documents");

            migrationBuilder.DropIndex(
                name: "IX_fis_Documents_TillId_Number",
                table: "fis_Documents");

            migrationBuilder.DropColumn(
                name: "ReplacedByDocumentId",
                table: "fis_Documents");

            migrationBuilder.DropColumn(
                name: "ReplacesDocumentId",
                table: "fis_Documents");

            migrationBuilder.DropColumn(
                name: "Request",
                table: "fis_Documents");

            migrationBuilder.DropColumn(
                name: "SendLate",
                table: "fis_Documents");

            migrationBuilder.CreateIndex(
                name: "IX_fis_Documents_Kind_SourceId",
                table: "fis_Documents",
                columns: new[] { "Kind", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fis_Documents_TillId_Number",
                table: "fis_Documents",
                columns: new[] { "TillId", "Number" },
                unique: true,
                filter: "Number IS NOT NULL");
        }
    }
}
