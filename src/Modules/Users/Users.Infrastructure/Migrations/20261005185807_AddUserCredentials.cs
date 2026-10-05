using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Users.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "usr_UserCredentials",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PasswordChangedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastFailedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSignInAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usr_UserCredentials", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_usr_UserCredentials_usr_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "usr_Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "usr_UserCredentials");
        }
    }
}
