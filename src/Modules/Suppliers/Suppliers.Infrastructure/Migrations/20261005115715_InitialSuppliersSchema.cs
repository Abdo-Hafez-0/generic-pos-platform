using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Suppliers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialSuppliersSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sup_Suppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sup_Suppliers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sup_SupplierAddresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SupplierId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Line1 = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Line2 = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Region = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PostalCode = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Country = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sup_SupplierAddresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sup_SupplierAddresses_sup_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "sup_Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sup_SupplierContacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SupplierId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Role = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sup_SupplierContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sup_SupplierContacts_sup_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "sup_Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sup_SupplierAddresses_SupplierId",
                table: "sup_SupplierAddresses",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_sup_SupplierContacts_SupplierId",
                table: "sup_SupplierContacts",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_sup_Suppliers_Code",
                table: "sup_Suppliers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sup_Suppliers_Name",
                table: "sup_Suppliers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_sup_Suppliers_Status",
                table: "sup_Suppliers",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sup_SupplierAddresses");

            migrationBuilder.DropTable(
                name: "sup_SupplierContacts");

            migrationBuilder.DropTable(
                name: "sup_Suppliers");
        }
    }
}
