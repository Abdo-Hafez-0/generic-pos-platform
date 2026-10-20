using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fiscal.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fis_ProductCodes",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fis_ProductCodes", x => x.ProductId);
                });

            migrationBuilder.CreateTable(
                name: "fis_TaxCodes",
                columns: table => new
                {
                    TaxRateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EgyptTaxType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    EgyptSubType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SaudiCategory = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    SaudiExemptionCode = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SaudiExemptionReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fis_TaxCodes", x => x.TaxRateId);
                });

            migrationBuilder.CreateTable(
                name: "fis_UnitCodes",
                columns: table => new
                {
                    UnitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fis_UnitCodes", x => x.UnitId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fis_ProductCodes");

            migrationBuilder.DropTable(
                name: "fis_TaxCodes");

            migrationBuilder.DropTable(
                name: "fis_UnitCodes");
        }
    }
}
