using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Core.Modules;
using Users.Infrastructure;
using Users.Infrastructure.Module;
using Users.Infrastructure.Persistence;

namespace Users.Tests.Infrastructure;

/// <summary>Generated module-plumbing tests: schema ownership, migration, initializer, hosting, lifecycle and manifest.</summary>
public sealed class UsersModuleInfrastructureTests
{

    private static readonly string[] ExpectedTables = ["usr_Users", "usr_Roles", "usr_UserRoles", "usr_RolePermissions", "usr_UserCredentials"];

    private static async Task<(SqliteConnection Connection, UsersDbContext Context)> OpenMigratedAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new UsersDbContext(new DbContextOptionsBuilder<UsersDbContext>().UseSqlite(connection).Options);
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
        Assert.All(tables, t => Assert.StartsWith("usr_", t));
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
                Assert.StartsWith("usr_", reader.GetString(reader.GetOrdinal("table")));
        }
    }

    [Fact]
    public async Task Migration_IsRecorded_NothingIsPending_AndMatchesTheModel()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;

        Assert.Contains(await context.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialUsersSchema", StringComparison.Ordinal));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "The EF model has changes not captured by a migration.");
    }

    [Fact]
    public void DbContext_OwnsOnly_ThePrefixedTables()
    {
        using var context = new UsersDbContext(new DbContextOptionsBuilder<UsersDbContext>().UseSqlite("Data Source=:memory:").Options);

        var mapped = context.Model.GetEntityTypes().Select(e => e.GetTableName()!).Distinct().ToList();

        Assert.Equal(ExpectedTables.OrderBy(t => t), mapped.OrderBy(t => t));
        Assert.Equal("Users.Infrastructure", typeof(UsersDbContext).Assembly.GetName().Name);
    }

    [Fact]
    public async Task DatabaseInitializer_AppliesTheMigration_ToAFreshDatabase_AndIsRepeatable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"users-init-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<UsersDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var initializer = new UsersDatabaseInitializer(provider, NullLogger<UsersDatabaseInitializer>.Instance);

            await initializer.StartAsync(CancellationToken.None);
            await initializer.StartAsync(CancellationToken.None);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialUsersSchema", StringComparison.Ordinal));
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

        new UsersHostingModule().RegisterServices(
            new HostBuilderContext(new Dictionary<object, object>()) { Configuration = new ConfigurationBuilder().Build() }, services);

        Assert.Contains(services, d => d.ServiceType == typeof(UsersDbContext));
        Assert.Contains(services, d => d.ServiceType == typeof(IModule) && d.ImplementationType == typeof(UsersModule));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(UsersDatabaseInitializer));
    }

    [Fact]
    public async Task Module_Lifecycle_FollowsTheRuntimeStatusSequence()
    {
        var module = new UsersModule();
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
        var manifest = new UsersModule().Manifest;

        Assert.Equal("users", manifest.ModuleId.Value);
        Assert.Equal(new ModuleVersion(1, 0, 0), manifest.Version);
        Assert.Equal(new string[] {  }, manifest.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x).ToArray());
        Assert.Equal(new string[] { "users.management", "users.roles" }, manifest.ProvidedFeatures.Select(f => f.Id.Value).ToArray());
    }

    [Fact]
    public void Manifest_CarriesNoPackageSecurityFields()
    {
        // Package hashes/signatures belong to the update system (Updates.Contracts), never to the runtime manifest.
        var names = typeof(UsersModuleManifest).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("Hash") || n.Contains("Signature") || n.Contains("KeyId"));
    }
}
