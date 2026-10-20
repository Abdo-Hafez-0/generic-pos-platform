using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fiscal.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fis_Documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    OriginalSourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OriginalReference = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Country = table.Column<int>(type: "INTEGER", nullable: false),
                    TillId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<long>(type: "INTEGER", nullable: true),
                    PreviousHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    QrPayload = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Total = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    IssuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    StateReason = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fis_Documents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fis_Documents_Kind_SourceId",
                table: "fis_Documents",
                columns: new[] { "Kind", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fis_Documents_State_CreatedAt",
                table: "fis_Documents",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_fis_Documents_TillId_Number",
                table: "fis_Documents",
                columns: new[] { "TillId", "Number" },
                unique: true,
                filter: "Number IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fis_Documents");
        }
    }
}
