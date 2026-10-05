using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Core.Modules;
using CashManagement.Infrastructure;
using CashManagement.Infrastructure.Module;
using CashManagement.Infrastructure.Persistence;

namespace CashManagement.Tests.Infrastructure;

/// <summary>Generated module-plumbing tests: schema ownership, migration, initializer, hosting, lifecycle and manifest.</summary>
public sealed class CashManagementModuleInfrastructureTests
{

    private static readonly string[] ExpectedTables = ["cash_Sessions", "cash_Movements"];

    private static async Task<(SqliteConnection Connection, CashManagementDbContext Context)> OpenMigratedAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new CashManagementDbContext(new DbContextOptionsBuilder<CashManagementDbContext>().UseSqlite(connection).Options);
        await context.Database.MigrateAsync();
        return (connection, context);
    }

    private static async Task<List<string>> NamesAsync(SqliteConnection connection, string type)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='{type}' AND name NOT LIKE 'sqlite_%'";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public async Task Migration_Creates_OnlyTheModulesOwnTables_WithThePrefix()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;

        var tables = (await NamesAsync(connection, "table")).Where(t => !t.StartsWith("__EFMigrations", StringComparison.Ordinal)).ToList();

        Assert.Equal(ExpectedTables.OrderBy(t => t), tables.OrderBy(t => t));
        Assert.All(tables, t => Assert.StartsWith("cash_", t));
        Assert.DoesNotContain(tables, t => new[] { "cat_", "inv_", "sal_", "pos_" }.Any(p => t.StartsWith(p, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Migration_HasNoCrossModuleForeignKeys()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;

        foreach (var table in ExpectedTables)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA foreign_key_list('{table}')";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                Assert.StartsWith("cash_", reader.GetString(reader.GetOrdinal("table")));
        }
    }

    [Fact]
    public async Task Migration_IsRecorded_NothingIsPending_AndMatchesTheModel()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;

        Assert.Contains(await context.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialCashManagementSchema", StringComparison.Ordinal));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "The EF model has changes not captured by a migration.");
    }

    [Fact]
    public void DbContext_OwnsOnly_ThePrefixedTables()
    {
        using var context = new CashManagementDbContext(new DbContextOptionsBuilder<CashManagementDbContext>().UseSqlite("Data Source=:memory:").Options);

        var mapped = context.Model.GetEntityTypes().Select(e => e.GetTableName()!).Distinct().ToList();

        Assert.Equal(ExpectedTables.OrderBy(t => t), mapped.OrderBy(t => t));
        Assert.Equal("CashManagement.Infrastructure", typeof(CashManagementDbContext).Assembly.GetName().Name);
    }

    [Fact]
    public async Task DatabaseInitializer_AppliesTheMigration_ToAFreshDatabase_AndIsRepeatable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cash-management-init-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<CashManagementDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var initializer = new CashManagementDatabaseInitializer(provider, NullLogger<CashManagementDatabaseInitializer>.Instance);

            await initializer.StartAsync(CancellationToken.None);
            await initializer.StartAsync(CancellationToken.None);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CashManagementDbContext>();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialCashManagementSchema", StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void HostingModule_RegistersTheModule_AndItsDbContext()
    {
        var services = new ServiceCollection();

        new CashManagementHostingModule().RegisterServices(
            new HostBuilderContext(new Dictionary<object, object>()) { Configuration = new ConfigurationBuilder().Build() }, services);

        Assert.Contains(services, d => d.ServiceType == typeof(CashManagementDbContext));
        Assert.Contains(services, d => d.ServiceType == typeof(IModule) && d.ImplementationType == typeof(CashManagementModule));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CashManagementDatabaseInitializer));
    }

    [Fact]
    public async Task Module_Lifecycle_FollowsTheRuntimeStatusSequence()
    {
        var module = new CashManagementModule();
        Assert.Equal(ModuleRuntimeStatus.Registered, module.Status);
        await module.InitializeAsync();
        Assert.Equal(ModuleRuntimeStatus.Enabled, module.Status);
        await module.StartAsync();
        Assert.Equal(ModuleRuntimeStatus.Running, module.Status);
        await module.StopAsync();
        Assert.Equal(ModuleRuntimeStatus.Stopped, module.Status);
    }

    [Fact]
    public void Manifest_DeclaresIdentityVersionDependenciesAndFeatures()
    {
        var manifest = new CashManagementModule().Manifest;

        Assert.Equal("cash-management", manifest.ModuleId.Value);
        Assert.Equal(new ModuleVersion(1, 0, 0), manifest.Version);
        Assert.Equal(new string[] {  }, manifest.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x).ToArray());
        Assert.Equal(new string[] { "cash.sessions" }, manifest.ProvidedFeatures.Select(f => f.Id.Value).ToArray());
    }

    [Fact]
    public void Manifest_CarriesNoPackageSecurityFields()
    {
        // Package hashes/signatures belong to the update system (Updates.Contracts), never to the runtime manifest.
        var names = typeof(CashManagementModuleManifest).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("Hash") || n.Contains("Signature") || n.Contains("KeyId"));
    }
}
