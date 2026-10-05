using AdminPortal.Application;
using BackupServer.Application;
using Cloud.Contracts.Admin;
using Cloud.Infrastructure;
using Cloud.Infrastructure.Persistence;
using LicenseServer.Application;
using Licensing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cloud.Tests;

public sealed class ConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingDatabaseConnectionString_FailsFast(string? value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCloudDatabase(Config(("CloudDatabase:ConnectionString", value))));

        Assert.Contains("CloudDatabase:ConnectionString", ex.Message);
    }

    [Fact]
    public void MissingPackageDirectory_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddPackageStore(Config()));

        Assert.Contains("UpdateServer:PackageDirectory", ex.Message);
    }

    [Fact]
    public void MissingBackupDirectory_FailsFast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddBackupStore(Config()));

        Assert.Contains("BackupServer:StorageDirectory", ex.Message);
    }

    [Fact]
    public void Defaults_AreUsed_WhenNothingIsConfigured()
    {
        using var w = new CloudWorld();

        Assert.Equal(BackupServerOptions.Default, w.Get<BackupServerOptions>());
        Assert.Equal(PackageAdminOptions.Default, w.Get<PackageAdminOptions>());
        Assert.Equal(new BackupServerOptions(256L * 1024 * 1024, 10, "cloud-backup"), w.Get<BackupServerOptions>());
        Assert.Equal(512L * 1024 * 1024, w.Get<PackageAdminOptions>().MaxPackageBytes);
    }

    [Fact]
    public void Overrides_AreRead_FromConfiguration()
    {
        using var w = new CloudWorld(new Dictionary<string, string?>
        {
            ["BackupServer:MaxBackupBytes"] = "1234",
            ["BackupServer:MaxBackupsPerLicense"] = "2",
            ["BackupServer:RequiredModule"] = "vault",
            ["UpdateServer:MaxPackageBytes"] = "999"
        });

        Assert.Equal(new BackupServerOptions(1234, 2, "vault"), w.Get<BackupServerOptions>());
        Assert.Equal(999, w.Get<PackageAdminOptions>().MaxPackageBytes);
    }

    [Fact]
    public void ConfiguredAdminKeys_AndTrustedPackageKeys_AreLoaded()
    {
        using var w = new CloudWorld(trustPublisher: true);

        Assert.Equal(1, w.Get<AdminKeyAuthenticator>().KeyCount);
        Assert.Equal("tester", w.Get<AdminKeyAuthenticator>().Authenticate(CloudWorld.AdminKey)!.Name);
        Assert.Equal(CloudWorld.PublisherKeyId, Assert.Single(w.Get<UpdateServer.Application.PackageSigningPolicy>().TrustedKeys).KeyId);
    }

    [Fact]
    public void WithoutConfiguredAdminKeys_NoOneAuthenticates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cloud-tests-" + Guid.NewGuid().ToString("N"));
        var withoutKeys = new ConfigurationBuilder()
            .AddInMemoryCollection(CloudWorld.DefaultSettings(dir).Where(kv => !kv.Key.StartsWith("AdminPortal:")))
            .Build();
        var services = new ServiceCollection();
        services.AddCloudDatabase(withoutKeys);
        services.AddAdminPortalServices(withoutKeys);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(0, provider.GetRequiredService<AdminKeyAuthenticator>().KeyCount);
        Assert.Null(provider.GetRequiredService<AdminKeyAuthenticator>().Authenticate(CloudWorld.AdminKey));
    }
}

public sealed class PersistenceTests : IDisposable
{
    private readonly CloudWorld _w = new();

    public void Dispose() => _w.Dispose();

    private async Task<List<string>> Query(string sql, CloudWorld? world = null)
    {
        await using var db = (world ?? _w).Get<IDbContextFactory<CloudDbContext>>().CreateDbContext();
        return await db.Database.SqlQueryRaw<string>(sql).ToListAsync();
    }

    [Fact]
    public async Task Migration_CreatesTheServerTables_OnlyUnderServerPrefixes()
    {
        var tables = await Query("SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");

        Assert.Equal(
            ["__EFMigrationsHistory", "__EFMigrationsLock", "adm_AuditLog", "adm_Customers", "adm_Modules", "bak_AccessTokens", "bak_Backups", "lic_Licenses", "upd_Packages"],
            tables);
        Assert.Equal(["InitialCloudSchema"], (await Query("SELECT MigrationId AS Value FROM __EFMigrationsHistory")).Select(m => m[(m.IndexOf('_') + 1)..]));
    }

    [Fact]
    public async Task TheServerDatabase_IsWriteAheadLogged_SoTheHostsCanShareIt()
        => Assert.Equal(["wal"], await Query("PRAGMA journal_mode"));

    [Fact]
    public async Task StartingAnotherHostOnTheSameDatabase_IsHarmless_AndSeesTheSameData()
    {
        var customer = await _w.NewCustomerAsync("Shared");

        using var second = new CloudWorld(new Dictionary<string, string?>(_w.Settings));

        Assert.Equal("Shared", ResultAssert.Ok(await second.Customers.GetAsync(customer.Id)).Name);
        Assert.Single(await Query("SELECT MigrationId AS Value FROM __EFMigrationsHistory", second));
    }

    [Fact]
    public async Task WhenMigrationIsDisabled_NothingIsCreated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cloud-tests-" + Guid.NewGuid().ToString("N"));
        using var w = new CloudWorld(new Dictionary<string, string?>(CloudWorld.DefaultSettings(dir)) { ["CloudDatabase:MigrateOnStartup"] = "false" });

        await Assert.ThrowsAnyAsync<SqliteException>(() => w.Customers.ListAsync(null, false, null, null));
    }

    [Fact]
    public async Task ChangesMadeThroughOneHost_AreVisibleToAnotherRunningHost_WithoutARestart()
    {
        var license = await _w.NewLicenseAsync();
        var installation = await _w.ActivateAsync(license);
        using var otherHost = new CloudWorld(new Dictionary<string, string?>(_w.Settings));

        // The vendor revokes through the administration host...
        await otherHost.Licenses.RevokeAsync(otherHost.Actor, license.License.LicenseId, "fraud");

        // ...and the license host (a different instance) issues the revoked state at the very next renewal.
        var renewal = await _w.Issuance().RenewAsync(new RenewalRequest(license.License.LicenseId, installation, 1));
        Assert.Equal(LicenseStatusClaim.Revoked, LicenseSerializer.TryParsePayload(renewal.License!.Payload)!.Status);
    }

    [Fact]
    public async Task CustomerSearch_TreatsLikeWildcards_AsLiteralText_AndIgnoresCase()
    {
        await _w.NewCustomerAsync("100% Organic");
        await _w.NewCustomerAsync("100x Organic");
        await _w.NewCustomerAsync("Snake_case Ltd");
        await _w.NewCustomerAsync("SnakeXcase Ltd");

        Assert.Equal(["100% Organic"], (await _w.Customers.ListAsync("100%", false, null, null)).Items.Select(c => c.Name));
        Assert.Equal(["Snake_case Ltd"], (await _w.Customers.ListAsync("e_c", false, null, null)).Items.Select(c => c.Name));
        Assert.Equal(["100% Organic", "100x Organic"], (await _w.Customers.ListAsync("ORGANIC", false, null, null)).Items.Select(c => c.Name));
        Assert.Empty((await _w.Customers.ListAsync(@"back\slash", false, null, null)).Items);
    }

    [Fact]
    public async Task CustomerNames_AreUniqueAtTheDatabaseLevel_Too()
    {
        var customer = await _w.NewCustomerAsync("Unique");
        var factory = _w.Get<IDbContextFactory<CloudDbContext>>();
        await using var db = factory.CreateDbContext();
        db.Customers.Add(new Customer { Id = Guid.NewGuid(), Name = "UNIQUE", CreatedAt = customer.CreatedAt, UpdatedAt = customer.CreatedAt });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task PackageVersion_IsUniquePerTarget_AtTheDatabaseLevel_Too()
    {
        var first = await _w.PublishOkAsync("catalog", "1.0.0");
        var catalog = _w.Get<UpdateServer.Application.IPackageCatalog>();
        var existing = (await catalog.FindAsync(first.PackageId))!;
        var duplicate = existing with { Package = existing.Package with { PackageId = Guid.NewGuid() } };

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => catalog.AddAsync(duplicate));
    }
}
