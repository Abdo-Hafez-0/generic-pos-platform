using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Core.Modules;
using POS.Application.Abstractions;
using POS.Application.Repositories;
using POS.Contracts.Interfaces;
using POS.Domain.Entities;
using POS.Domain.Enums;
using POS.Domain.ValueObjects;
using POS.Infrastructure.DependencyInjection;
using POS.Infrastructure.Module;
using POS.Infrastructure.Persistence;

namespace POS.Tests.Infrastructure;

public sealed class PosInfrastructureTests
{
    private static readonly string[] ExpectedTables = ["pos_Sessions", "pos_Carts", "pos_CartItems"];

    private static PosCart CartWithItem(decimal qty = 2.5m, decimal price = 12.3456m)
    {
        var cart = PosCart.Start(PosSessionId.New()).Value;
        Assert.True(cart.AddItem(Guid.NewGuid(), "SKU-1", "Widget", new CartQuantity(qty), new Money(price)).IsSuccess);
        return cart;
    }

    // --- Migration ---

    private static async Task<(SqliteConnection Connection, POSDbContext Context)> OpenMigratedAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<POSDbContext>().UseSqlite(connection).Options;
        var context = new POSDbContext(options);
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
    public async Task Migration_CreatesOnlyPosTables_WithPosPrefix()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;

        var tables = (await NamesAsync(connection, "table"))
            .Where(t => !t.StartsWith("__EFMigrations", StringComparison.Ordinal)).ToList();

        foreach (var expected in ExpectedTables) Assert.Contains(expected, tables);
        Assert.Equal(ExpectedTables.Length, tables.Count);
        Assert.All(tables, t => Assert.StartsWith("pos_", t));
        Assert.DoesNotContain(tables, t => t.StartsWith("cat_") || t.StartsWith("inv_") || t.StartsWith("sal_"));
        Assert.DoesNotContain(tables, t => t.Contains("Payment", StringComparison.OrdinalIgnoreCase));
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
                Assert.StartsWith("pos_", reader.GetString(reader.GetOrdinal("table")));
        }
    }

    [Fact]
    public async Task Migration_IsRecordedInHistory_AndNothingIsPending()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;

        Assert.Contains(await context.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialPOSSchema", StringComparison.Ordinal));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public void Migrations_MatchModel_AndLiveInPosInfrastructure()
    {
        var options = new DbContextOptionsBuilder<POSDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var context = new POSDbContext(options);

        Assert.NotEmpty(context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges(), "The EF model has changes not captured by a migration.");
        Assert.Equal("POS.Infrastructure", typeof(POSDbContext).Assembly.GetName().Name);
    }

    [Fact]
    public async Task Migration_CartItemsAreCascadeDeleted_WithCart()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;
        context.Carts.Add(CartWithItem());
        await context.SaveChangesAsync();

        context.Carts.Remove(await context.Carts.Include(c => c.Items).SingleAsync());
        await context.SaveChangesAsync();

        Assert.Equal(0, await context.CartItems.CountAsync());
    }

    // --- Database initializer ---

    [Fact]
    public async Task POSDatabaseInitializer_AppliesMigration_ToFreshDatabase_AndIsRepeatable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pos-init-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<POSDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var initializer = new POSDatabaseInitializer(provider, NullLogger<POSDatabaseInitializer>.Instance);

            await initializer.StartAsync(CancellationToken.None);
            await initializer.StartAsync(CancellationToken.None);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<POSDbContext>();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialPOSSchema", StringComparison.Ordinal));
            Assert.Equal(0, await db.Sessions.CountAsync());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- Service registration ---

    private static ServiceProvider BuildRegistered(PosTestDatabase stubs, string dbPath)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:DatabaseFolder"] = "Custom",
            ["Database:CustomFolderPath"] = Path.GetDirectoryName(dbPath)!,
            ["Database:DatabaseFileName"] = Path.GetFileName(dbPath)
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPOSModule(config);
        // The real host provides authorization (AddPlatformSecurity); a permissive one stands in for it here.
        services.AddSingleton<Platform.Application.Abstractions.Authorization.IAuthorizationService, global::Tests.Common.Security.AllowAllAuthorizationService>();
        // Other modules are supplied by their own hosting modules in the real host; here: stubs of their contracts.
        services.AddSingleton<Catalog.Contracts.Interfaces.IProductLookup>(stubs.Catalog);
        services.AddSingleton<Catalog.Contracts.Interfaces.IProductBarcodeResolver>(stubs.Catalog);
        services.AddSingleton<Inventory.Contracts.Interfaces.IStockAvailabilityChecker>(stubs.Inventory);
        services.AddSingleton<Inventory.Contracts.Interfaces.IStockIssueService>(stubs.Inventory);
        services.AddSingleton<Sales.Contracts.Interfaces.ISalesService>(stubs.Sales);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
    }

    [Fact]
    public async Task AddPOSModule_RegistersContractsHandlersModuleAndInitializer()
    {
        await using var stubs = await PosTestDatabase.CreateAsync();
        var path = Path.Combine(Path.GetTempPath(), $"pos-di-{Guid.NewGuid():N}.db");
        await using var provider = BuildRegistered(stubs, path);

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPOSService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPOSReader>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPosUnitOfWork>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPosSessionRepository>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPosCartRepository>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<POS.Application.Commands.CheckoutCartCommandHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<POS.Application.Queries.GetCurrentCartQueryHandler>());
        Assert.IsType<POSModule>(provider.GetServices<IModule>().Single(m => m is POSModule));
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is POSDatabaseInitializer);
    }

    [Fact]
    public void POSHostingModule_RegistersServices()
    {
        var config = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        new POSHostingModule().RegisterServices(new HostBuilderContext(new Dictionary<object, object>()) { Configuration = config }, services);

        Assert.Contains(services, d => d.ServiceType == typeof(IPOSService));
        Assert.Contains(services, d => d.ServiceType == typeof(POSDbContext));
    }

    // --- Persistence / mappings / repositories ---

    [Fact]
    public async Task SessionRepository_RoundTrips()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var session = PosSession.Open("alice", Guid.NewGuid()).Value;
        using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPosSessionRepository>().AddAsync(session);
            await scope.ServiceProvider.GetRequiredService<IPosUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var repo = read.ServiceProvider.GetRequiredService<IPosSessionRepository>();
        var loaded = await repo.GetByIdAsync(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal("alice", loaded!.CashierReference);
        Assert.Equal(session.WarehouseId, loaded.WarehouseId);
        Assert.Equal(PosSessionStatus.Open, loaded.Status);
        Assert.Null(await repo.GetByIdAsync(PosSessionId.New()));
    }

    [Fact]
    public async Task CartRepository_RoundTrips_CartWithItems_AndValueObjectMappings()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var cart = CartWithItem(qty: 2.5m, price: 12.3456m);
        using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPosCartRepository>().AddAsync(cart);
            await scope.ServiceProvider.GetRequiredService<IPosUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var loaded = await read.ServiceProvider.GetRequiredService<IPosCartRepository>().GetByIdAsync(cart.Id);

        Assert.NotNull(loaded);
        var item = Assert.Single(loaded!.Items);
        Assert.Equal(2.5m, item.Quantity.Value);
        Assert.Equal(12.3456m, item.UnitPrice.Amount);
        Assert.Equal("SKU-1", item.ProductSku);
        Assert.Equal("Widget", item.ProductName);
        Assert.Equal(cart.Total, loaded.Total);
        Assert.Equal(cart.SessionId, loaded.SessionId);
        Assert.Equal(PosCartStatus.Open, loaded.Status);
    }

    [Fact]
    public async Task CartRepository_TracksItemChanges_ThroughUnitOfWork()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var cart = CartWithItem(qty: 1m, price: 5m);
        var productId = cart.Items[0].CatalogProductId;
        using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPosCartRepository>().AddAsync(cart);
            await scope.ServiceProvider.GetRequiredService<IPosUnitOfWork>().SaveChangesAsync();
        }

        using (var scope = db.CreateScope())
        {
            var loaded = (await scope.ServiceProvider.GetRequiredService<IPosCartRepository>().GetByIdAsync(cart.Id))!;
            loaded.ChangeQuantity(productId, new CartQuantity(4m));
            loaded.AddItem(Guid.NewGuid(), "SKU-2", "Other", new CartQuantity(1m), new Money(1m));
            await scope.ServiceProvider.GetRequiredService<IPosUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var reloaded = (await read.ServiceProvider.GetRequiredService<IPosCartRepository>().GetByIdAsync(cart.Id))!;
        Assert.Equal(2, reloaded.Items.Count);
        Assert.Equal(4m, reloaded.FindItem(productId)!.Quantity.Value);
    }

    [Fact]
    public async Task CartRepository_GetOpenCartForSession_IgnoresCheckedOutCarts_AndOtherSessions()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var sessionId = PosSessionId.New();
        var checkedOut = PosCart.Start(sessionId).Value;
        checkedOut.AddItem(Guid.NewGuid(), "S", "N", new CartQuantity(1m), new Money(1m));
        checkedOut.MarkCheckedOut(Guid.NewGuid());
        var open = PosCart.Start(sessionId).Value;
        var other = PosCart.Start(PosSessionId.New()).Value;
        using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IPosCartRepository>();
            await repo.AddAsync(checkedOut);
            await repo.AddAsync(open);
            await repo.AddAsync(other);
            await scope.ServiceProvider.GetRequiredService<IPosUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var repo2 = read.ServiceProvider.GetRequiredService<IPosCartRepository>();

        Assert.Equal(open.Id, (await repo2.GetOpenCartForSessionAsync(sessionId))!.Id);
        Assert.Null(await repo2.GetOpenCartForSessionAsync(PosSessionId.New()));
    }

    [Fact]
    public async Task CheckedOutCart_PersistsSaleIdAndStatus()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var cart = CartWithItem();
        var saleId = Guid.NewGuid();
        cart.MarkCheckedOut(saleId);
        using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IPosCartRepository>().AddAsync(cart);
            await scope.ServiceProvider.GetRequiredService<IPosUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var loaded = (await read.ServiceProvider.GetRequiredService<IPosCartRepository>().GetByIdAsync(cart.Id))!;

        Assert.Equal(PosCartStatus.CheckedOut, loaded.Status);
        Assert.Equal(saleId, loaded.SaleId);
        Assert.NotNull(loaded.CheckedOutAt);
    }

    [Fact]
    public async Task UnitOfWork_SaveChanges_ReturnsAffectedRows()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPosSessionRepository>()
            .AddAsync(PosSession.Open("c", Guid.NewGuid()).Value);

        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IPosUnitOfWork>().SaveChangesAsync());
    }

    // --- Module ---

    [Fact]
    public async Task POSModule_Lifecycle_And_Manifest_DeclareExpectedDependencies()
    {
        var module = new POSModule();
        Assert.Equal(ModuleRuntimeStatus.Registered, module.Status);
        await module.InitializeAsync();
        Assert.Equal(ModuleRuntimeStatus.Enabled, module.Status);
        await module.StartAsync();
        Assert.Equal(ModuleRuntimeStatus.Running, module.Status);
        await module.StopAsync();
        Assert.Equal(ModuleRuntimeStatus.Stopped, module.Status);

        var manifest = module.Manifest;
        Assert.Equal("pos", manifest.ModuleId.Value);
        Assert.Equal(new ModuleVersion(1, 0, 0), manifest.Version);
        var deps = manifest.Dependencies.Select(d => d.RequiredModuleId.Value).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "catalog", "inventory", "sales" }, deps);
        Assert.DoesNotContain("payments", deps);
    }
}
