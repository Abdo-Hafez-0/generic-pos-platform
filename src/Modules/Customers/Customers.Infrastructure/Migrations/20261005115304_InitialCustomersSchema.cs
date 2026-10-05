using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Customers.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCustomersSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cus_Customers",
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
                    table.PrimaryKey("PK_cus_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "cus_CustomerAddresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("PK_cus_CustomerAddresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cus_CustomerAddresses_cus_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "cus_Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cus_CustomerContacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Role = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cus_CustomerContacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cus_CustomerContacts_cus_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "cus_Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cus_CustomerAddresses_CustomerId",
                table: "cus_CustomerAddresses",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_cus_CustomerContacts_CustomerId",
                table: "cus_CustomerContacts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_cus_Customers_Code",
                table: "cus_Customers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cus_Customers_Name",
                table: "cus_Customers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_cus_Customers_Status",
                table: "cus_Customers",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cus_CustomerAddresses");

            migrationBuilder.DropTable(
                name: "cus_CustomerContacts");

            migrationBuilder.DropTable(
                name: "cus_Customers");
        }
    }
}
