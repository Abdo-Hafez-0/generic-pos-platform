using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Core.Modules;
using Purchasing.Infrastructure;
using Purchasing.Infrastructure.Module;
using Purchasing.Infrastructure.Persistence;

namespace Purchasing.Tests.Infrastructure;

/// <summary>Generated module-plumbing tests: schema ownership, migration, initializer, hosting, lifecycle and manifest.</summary>
public sealed class PurchasingModuleInfrastructureTests
{

    private static readonly string[] ExpectedTables = ["pur_PurchaseOrders", "pur_PurchaseOrderLines", "pur_SupplierReturns", "pur_SupplierReturnLines"];

    private static async Task<(SqliteConnection Connection, PurchasingDbContext Context)> OpenMigratedAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new PurchasingDbContext(new DbContextOptionsBuilder<PurchasingDbContext>().UseSqlite(connection).Options);
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
        Assert.All(tables, t => Assert.StartsWith("pur_", t));
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
                Assert.StartsWith("pur_", reader.GetString(reader.GetOrdinal("table")));
        }
    }

    [Fact]
    public async Task Migration_IsRecorded_NothingIsPending_AndMatchesTheModel()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;

        Assert.Contains(await context.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialPurchasingSchema", StringComparison.Ordinal));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.False(context.Database.HasPendingModelChanges(), "The EF model has changes not captured by a migration.");
    }

    [Fact]
    public void DbContext_OwnsOnly_ThePrefixedTables()
    {
        using var context = new PurchasingDbContext(new DbContextOptionsBuilder<PurchasingDbContext>().UseSqlite("Data Source=:memory:").Options);

        var mapped = context.Model.GetEntityTypes().Select(e => e.GetTableName()!).Distinct().ToList();

        Assert.Equal(ExpectedTables.OrderBy(t => t), mapped.OrderBy(t => t));
        Assert.Equal("Purchasing.Infrastructure", typeof(PurchasingDbContext).Assembly.GetName().Name);
    }

    [Fact]
    public async Task DatabaseInitializer_AppliesTheMigration_ToAFreshDatabase_AndIsRepeatable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"purchasing-init-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<PurchasingDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var initializer = new PurchasingDatabaseInitializer(provider, NullLogger<PurchasingDatabaseInitializer>.Instance);

            await initializer.StartAsync(CancellationToken.None);
            await initializer.StartAsync(CancellationToken.None);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PurchasingDbContext>();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialPurchasingSchema", StringComparison.Ordinal));
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

        new PurchasingHostingModule().RegisterServices(
            new HostBuilderContext(new Dictionary<object, object>()) { Configuration = new ConfigurationBuilder().Build() }, services);

        Assert.Contains(services, d => d.ServiceType == typeof(PurchasingDbContext));
        Assert.Contains(services, d => d.ServiceType == typeof(IModule) && d.ImplementationType == typeof(PurchasingModule));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PurchasingDatabaseInitializer));
    }

    [Fact]
    public async Task Module_Lifecycle_FollowsTheRuntimeStatusSequence()
    {
        var module = new PurchasingModule();
        Assert.Equal(ModuleRuntimeStatus.Registered, module.Status);
        await module.InitializeAsync();
        Assert.Equal(ModuleRuntimeStatus.Enabled, module.Status);
        await module.StartAsync();
        Assert.Equal(ModuleRuntimeStatus.Running, module.Status);
        await module.StopAsync();
        Assert.Equal(ModuleRuntimeStatus.Stopped, module.Status);
    }

    [Fact]
    public async Task Upgrade_to_part_deliveries_keeps_what_was_received_before()
    {
        // FIX-09 AddPartialReceiving on a database written by the earlier version: a received order, and an order the resumable
        // receiving (before Stage 12) left Submitted with one of two lines received
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new PurchasingDbContext(new DbContextOptionsBuilder<PurchasingDbContext>().UseSqlite(connection).Options);
        await context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync("20261005120224_InitialPurchasingSchema");

        var (received, partly) = (Guid.NewGuid(), Guid.NewGuid());
        static string Id(Guid g) => g.ToString().ToUpperInvariant();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO pur_PurchaseOrders (Id, Number, SupplierId, SupplierCode, SupplierName, Status, TotalAmount, CreatedAt, UpdatedAt, ReceivedAt) VALUES " +
                $"('{Id(received)}', 'PO-1', '{Id(Guid.NewGuid())}', 'S', 'Supplier', 3, '24.5', '2026-10-01', '2026-10-01', '2026-10-02'), " +
                $"('{Id(partly)}', 'PO-2', '{Id(Guid.NewGuid())}', 'S', 'Supplier', 2, '17.0', '2026-10-01', '2026-10-01', NULL);" +
                "INSERT INTO pur_PurchaseOrderLines (Id, PurchaseOrderId, ProductId, ProductSku, ProductName, Quantity, UnitCost, ReceivedAt) VALUES " +
                $"('{Id(Guid.NewGuid())}', '{Id(received)}', '{Id(Guid.NewGuid())}', 'A', 'A', '7.0', '3.5', '2026-10-02'), " +
                $"('{Id(Guid.NewGuid())}', '{Id(partly)}', '{Id(Guid.NewGuid())}', 'B', 'B', '3.0', '1.25', '2026-10-02'), " +
                $"('{Id(Guid.NewGuid())}', '{Id(partly)}', '{Id(Guid.NewGuid())}', 'C', 'C', '2.0', '6.625', NULL);";
            await cmd.ExecuteNonQueryAsync();
        }

        await context.Database.MigrateAsync();

        var orders = await context.PurchaseOrders.Include(o => o.Lines).AsNoTracking().ToListAsync();
        var r = orders.Single(o => o.Id.Value == received);
        var p = orders.Single(o => o.Id.Value == partly);
        Assert.Equal((Purchasing.Domain.Enums.PurchaseOrderStatus.Received, 24.5m), (r.Status, r.ReceivedAmount.Amount));
        Assert.Equal((7m, true), (r.Lines[0].ReceivedQuantity, r.Lines[0].IsReceived));
        Assert.Equal((Purchasing.Domain.Enums.PurchaseOrderStatus.PartiallyReceived, 3.75m), (p.Status, p.ReceivedAmount.Amount));
        Assert.Equal([3m, 0m], p.Lines.OrderBy(l => l.ProductSku).Select(l => l.ReceivedQuantity).ToArray());
        Assert.Equal(2m, p.Lines.Single(l => l.ProductSku == "C").OutstandingQuantity);
    }

    [Fact]
    public void Manifest_DeclaresIdentityVersionDependenciesAndFeatures()
    {
        var manifest = new PurchasingModule().Manifest;

        Assert.Equal("purchasing", manifest.ModuleId.Value);
        Assert.Equal(new ModuleVersion(1, 0, 0), manifest.Version);
        Assert.Equal(new string[] { "catalog", "inventory", "suppliers" }, manifest.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x).ToArray());
        Assert.Equal(new string[] { "purchasing.orders", "purchasing.receiving" }, manifest.ProvidedFeatures.Select(f => f.Id.Value).ToArray());
    }

    [Fact]
    public void Manifest_CarriesNoPackageSecurityFields()
    {
        // Package hashes/signatures belong to the update system (Updates.Contracts), never to the runtime manifest.
        var names = typeof(PurchasingModuleManifest).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain(names, n => n.Contains("Hash") || n.Contains("Signature") || n.Contains("KeyId"));
    }
}
