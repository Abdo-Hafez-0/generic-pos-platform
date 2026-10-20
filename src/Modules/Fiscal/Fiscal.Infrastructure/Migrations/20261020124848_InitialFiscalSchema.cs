using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fiscal.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialFiscalSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fis_Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Country = table.Column<int>(type: "INTEGER", nullable: false),
                    TaxNumber = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    BranchCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ActivityCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    SellerNameArabic = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AddressArabic = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fis_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fis_Tills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Counter = table.Column<long>(type: "INTEGER", nullable: false),
                    LastDocumentHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Registration = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fis_Tills", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fis_Settings");

            migrationBuilder.DropTable(
                name: "fis_Tills");
        }
    }
}
