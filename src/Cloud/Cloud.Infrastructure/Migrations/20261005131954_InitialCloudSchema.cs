using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cloud.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCloudSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "adm_AuditLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adm_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "adm_Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    ContactName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adm_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "adm_Modules",
                columns: table => new
                {
                    ModuleId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_adm_Modules", x => x.ModuleId);
                });

            migrationBuilder.CreateTable(
                name: "bak_AccessTokens",
                columns: table => new
                {
                    LicenseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bak_AccessTokens", x => x.LicenseId);
                });

            migrationBuilder.CreateTable(
                name: "bak_Backups",
                columns: table => new
                {
                    BackupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LicenseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ClientVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bak_Backups", x => x.BackupId);
                });

            migrationBuilder.CreateTable(
                name: "lic_Licenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ActivationKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProductId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ValidFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    ValidUntil = table.Column<long>(type: "INTEGER", nullable: false),
                    ModulesJson = table.Column<string>(type: "TEXT", nullable: false),
                    FeaturesJson = table.Column<string>(type: "TEXT", nullable: false),
                    InstallationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ActivatedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastIssuedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    RowVersion = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lic_Licenses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "upd_Packages",
                columns: table => new
                {
                    PackageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PackageType = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TargetFramework = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MinimumHostVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EnvelopeJson = table.Column<string>(type: "TEXT", nullable: false),
                    KeyId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Publisher = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ReleaseNotes = table.Column<string>(type: "TEXT", nullable: true),
                    PublishedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    WithdrawnAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_upd_Packages", x => x.PackageId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_adm_AuditLog_At",
                table: "adm_AuditLog",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_adm_AuditLog_EntityType_EntityId",
                table: "adm_AuditLog",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_adm_Customers_Name",
                table: "adm_Customers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bak_AccessTokens_TokenHash",
                table: "bak_AccessTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bak_Backups_LicenseId_CreatedAt",
                table: "bak_Backups",
                columns: new[] { "LicenseId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_lic_Licenses_ActivationKeyHash",
                table: "lic_Licenses",
                column: "ActivationKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lic_Licenses_CustomerId",
                table: "lic_Licenses",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_lic_Licenses_InstallationId",
                table: "lic_Licenses",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_upd_Packages_Status",
                table: "upd_Packages",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_upd_Packages_TargetId_Version_TargetFramework",
                table: "upd_Packages",
                columns: new[] { "TargetId", "Version", "TargetFramework" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "adm_AuditLog");

            migrationBuilder.DropTable(
                name: "adm_Customers");

            migrationBuilder.DropTable(
                name: "adm_Modules");

            migrationBuilder.DropTable(
                name: "bak_AccessTokens");

            migrationBuilder.DropTable(
                name: "bak_Backups");

            migrationBuilder.DropTable(
                name: "lic_Licenses");

            migrationBuilder.DropTable(
                name: "upd_Packages");
        }
    }
}
