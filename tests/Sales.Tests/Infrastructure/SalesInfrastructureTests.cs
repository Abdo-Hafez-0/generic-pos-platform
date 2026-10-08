using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sales.Application.Abstractions;
using Sales.Application.Repositories;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;
using Sales.Domain.Entities;
using Sales.Domain.Enums;
using Sales.Domain.ValueObjects;
using Sales.Infrastructure.Module;
using Sales.Infrastructure.Persistence;
using Platform.Core.Modules;

namespace Sales.Tests.Infrastructure;

public sealed class SalesInfrastructureTests
{
    private static readonly string[] ExpectedTables =
        ["sal_Sales", "sal_SaleItems", "sal_Returns", "sal_ReturnItems", "sal_SalesTransactions"];

    private static Sale NewSaleWithItem(string? reference = null, decimal qty = 2m, decimal price = 10m, decimal tax = 0.2m)
    {
        var sale = Sale.Create(reference).Value;
        Assert.True(sale.AddItem(Guid.NewGuid(), "Widget", "W-1", new SaleQuantity(qty), new Money(price), new Money(1m), tax).IsSuccess);
        return sale;
    }

    // --- Migration ---

    private static async Task<(SqliteConnection Connection, SalesDbContext Context)> OpenMigratedAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<SalesDbContext>().UseSqlite(connection).Options;
        var context = new SalesDbContext(options);
        await context.Database.MigrateAsync();
        return (connection, context);
    }

    private static async Task<List<string>> TableNamesAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        return names;
    }

    [Fact]
    public async Task Migration_CreatesAllSalesTables_WithSalPrefix_AndNothingElse()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;

        var tables = await TableNamesAsync(connection);

        foreach (var expected in ExpectedTables)
            Assert.Contains(expected, tables);

        var businessTables = tables.Where(t => !t.StartsWith("__EFMigrations", StringComparison.Ordinal)).ToList();
        Assert.All(businessTables, t => Assert.StartsWith("sal_", t));
        Assert.Equal(ExpectedTables.Length, businessTables.Count);
    }

    [Fact]
    public async Task Migration_IsRecordedInHistory_AndNothingIsPending()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;

        var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();

        Assert.Contains(applied, m => m.EndsWith("InitialSalesSchema", StringComparison.Ordinal));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public void Migrations_AreInSalesInfrastructureAssembly_AndMatchModel()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var context = new SalesDbContext(options);

        Assert.Equal(typeof(SalesDbContext).Assembly.GetName().Name,
            context.GetType().Assembly.GetName().Name);
        Assert.NotEmpty(context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges(),
            "The EF model has changes not captured by a migration.");
    }

    [Fact]
    public async Task Migration_EnforcesUniqueTransactionPerSale()
    {
        var (connection, context) = await OpenMigratedAsync();
        await using var _c = connection;
        await using var _x = context;
        var saleId = SaleId.New();
        context.SalesTransactions.Add(SalesTransaction.Record(saleId, new Money(10m), Money.Zero).Value);
        context.SalesTransactions.Add(SalesTransaction.Record(saleId, new Money(10m), Money.Zero).Value);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SalesDatabaseInitializer_AppliesMigration_ToFreshDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sales-init-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<SalesDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False"));
            await using var provider = services.BuildServiceProvider();
            var initializer = new SalesDatabaseInitializer(provider, NullLogger<SalesDatabaseInitializer>.Instance);

            await initializer.StartAsync(CancellationToken.None);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialSalesSchema", StringComparison.Ordinal));
            Assert.Equal(0, await db.Sales.CountAsync());

            // Running again must be a safe no-op.
            await initializer.StartAsync(CancellationToken.None);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // --- Persistence / mappings / repositories ---

    [Fact]
    public async Task EfSaleRepository_RoundTrips_SaleWithItems_AndValueObjectMappings()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        var sale = NewSaleWithItem("REF-9", qty: 2.5m, price: 12.3456m, tax: 0.15m);
        var saleId = sale.Id;

        using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISaleRepository>().AddAsync(sale);
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var loaded = await read.ServiceProvider.GetRequiredService<ISaleRepository>().GetByIdAsync(saleId);

        Assert.NotNull(loaded);
        Assert.Equal("REF-9", loaded!.Reference);
        Assert.Equal(SaleStatus.Draft, loaded.Status);
        var item = Assert.Single(loaded.Items);
        Assert.Equal(2.5m, item.Quantity.Value);
        Assert.Equal(12.3456m, item.UnitPrice.Amount);
        Assert.Equal(1m, item.Discount.Amount);
        Assert.Equal(0.15m, item.TaxRate);
        Assert.Equal("Widget", item.ProductName);
        Assert.Equal("W-1", item.ProductSku);
        Assert.Equal(sale.GrandTotal, loaded.GrandTotal);
    }

    [Fact]
    public async Task EfSaleRepository_GetById_Unknown_ReturnsNull()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using var scope = db.CreateScope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<ISaleRepository>().GetByIdAsync(SaleId.New()));
    }

    [Fact]
    public async Task EfSaleRepository_Update_PersistsStatusTransitionAndNewItem()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        var sale = NewSaleWithItem();
        using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISaleRepository>().AddAsync(sale);
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISaleRepository>();
            var loaded = (await repo.GetByIdAsync(sale.Id))!;
            Assert.True(loaded.Confirm().IsSuccess);
            repo.Update(loaded);
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var reloaded = (await read.ServiceProvider.GetRequiredService<ISaleRepository>().GetByIdAsync(sale.Id))!;
        Assert.Equal(SaleStatus.Confirmed, reloaded.Status);
    }

    [Fact]
    public async Task EfSaleRepository_GetAll_ReturnsNewestFirst()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        var older = Sale.Create("older").Value;
        await Task.Delay(15);
        var newer = Sale.Create("newer").Value;
        using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISaleRepository>();
            await repo.AddAsync(older);
            await repo.AddAsync(newer);
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var all = await read.ServiceProvider.GetRequiredService<ISaleRepository>().GetAllAsync();

        Assert.Equal(new[] { "newer", "older" }, all.Select(s => s.Reference!).ToArray());
    }

    [Fact]
    public async Task EfReturnRepository_RoundTrips_ReturnWithItems()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        var ret = Return.Create(SaleId.New(), "notes").Value;
        ret.AddItem(SaleItemId.New(), Guid.NewGuid(), "Widget", new SaleQuantity(1m), new Money(9m), "damaged");

        using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IReturnRepository>().AddAsync(ret);
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IReturnRepository>();
            var loaded = await repo.GetByIdAsync(ret.Id);
            Assert.NotNull(loaded);
            Assert.Equal(ReturnStatus.Pending, loaded!.Status);
            Assert.Equal(9m, Assert.Single(loaded.Items).UnitPrice.Amount);
            Assert.True(loaded.Process().IsSuccess);
            repo.Update(loaded);
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        Assert.Equal(ReturnStatus.Processed,
            (await read.ServiceProvider.GetRequiredService<IReturnRepository>().GetByIdAsync(ret.Id))!.Status);
    }

    [Fact]
    public async Task EfSalesTransactionRepository_AddAndGetBySaleId()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        var saleId = SaleId.New();
        var tx = SalesTransaction.Record(saleId, new Money(55m), new Money(5m), "TX").Value;

        using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISalesTransactionRepository>().AddAsync(tx);
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var repo = read.ServiceProvider.GetRequiredService<ISalesTransactionRepository>();
        var loaded = await repo.GetBySaleIdAsync(saleId);
        Assert.NotNull(loaded);
        Assert.Equal(55m, loaded!.GrandTotal.Amount);
        Assert.Null(await repo.GetBySaleIdAsync(SaleId.New()));
    }

    [Fact]
    public async Task SalesUnitOfWork_SaveChanges_ReturnsAffectedRows_AndSharesContextWithRepositories()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using var scope = db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISaleRepository>().AddAsync(Sale.Create().Value);

        var affected = await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();

        Assert.Equal(1, affected);
    }

    [Fact]
    public async Task SaleItems_AreCascadeDeleted_WithSale()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        var sale = NewSaleWithItem();
        using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISaleRepository>().AddAsync(sale);
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using var del = db.CreateScope();
        var ctx = del.ServiceProvider.GetRequiredService<SalesDbContext>();
        ctx.Sales.Remove(await ctx.Sales.Include(s => s.Items).SingleAsync());
        await ctx.SaveChangesAsync();

        Assert.Equal(0, await ctx.SaleItems.CountAsync());
    }

    // --- SalesReader / SalesService contracts ---

    [Fact]
    public async Task SalesReader_FindById_MapsSummary()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        var sale = NewSaleWithItem("REF-R"); // 2*10 - 1 = 19 paid, tax included (FIX-08b)
        using (var scope = db.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISaleRepository>().AddAsync(sale);
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var summary = await read.ServiceProvider.GetRequiredService<ISalesReader>().FindByIdAsync(sale.Id.Value);

        Assert.NotNull(summary);
        Assert.Equal(SaleStatusContract.Draft, summary!.Status);
        Assert.Equal("REF-R", summary.Reference);
        Assert.Equal(1, summary.ItemCount);
        Assert.Equal(19m, summary.GrandTotal);
        Assert.Null(summary.CompletedAt);
    }

    [Fact]
    public async Task SalesReader_FindById_Unknown_ReturnsNull()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using var scope = db.CreateScope();

        Assert.Null(await scope.ServiceProvider.GetRequiredService<ISalesReader>().FindByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task SalesReader_GetRecent_RespectsLimit_AndOrder()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISaleRepository>();
            for (var i = 0; i < 3; i++)
            {
                await repo.AddAsync(Sale.Create($"S{i}").Value);
                await Task.Delay(15);
            }
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var recent = await read.ServiceProvider.GetRequiredService<ISalesReader>().GetRecentAsync(2);

        Assert.Equal(new[] { "S2", "S1" }, recent.Select(r => r.Reference!).ToArray());
    }

    [Fact]
    public async Task SalesReader_MapsAllStatuses()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        var draft = Sale.Create("draft").Value;
        var confirmed = NewSaleWithItem("confirmed"); confirmed.Confirm();
        var completed = NewSaleWithItem("completed"); completed.Confirm(); completed.Complete();
        var cancelled = Sale.Create("cancelled").Value; cancelled.Cancel("x");
        using (var scope = db.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISaleRepository>();
            foreach (var s in new[] { draft, confirmed, completed, cancelled }) await repo.AddAsync(s);
            await scope.ServiceProvider.GetRequiredService<ISalesUnitOfWork>().SaveChangesAsync();
        }

        using var read = db.CreateScope();
        var byRef = (await read.ServiceProvider.GetRequiredService<ISalesReader>().GetRecentAsync())
            .ToDictionary(r => r.Reference!, r => r.Status);

        Assert.Equal(SaleStatusContract.Draft, byRef["draft"]);
        Assert.Equal(SaleStatusContract.Confirmed, byRef["confirmed"]);
        Assert.Equal(SaleStatusContract.Completed, byRef["completed"]);
        Assert.Equal(SaleStatusContract.Cancelled, byRef["cancelled"]);
    }

    [Fact]
    public async Task SalesService_FullLifecycle_ThroughContract()
    {
        var productId = Guid.NewGuid();
        var lookup = new StubProductLookup();
        lookup.Register(productId, "SVC-1", "Service Product");
        await using var db = await SalesTestDatabase.CreateAsync(lookup);

        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISalesService>();

        var created = await service.CreateSaleAsync("SVC", "notes");
        Assert.True(created.IsSuccess);
        Assert.NotEqual(Guid.Empty, created.SaleId);

        var added = await service.AddItemAsync(created.SaleId, productId, 2m, 10m);
        Assert.True(added.IsSuccess);
        Assert.NotEqual(Guid.Empty, added.SaleItemId);

        Assert.True((await service.ConfirmSaleAsync(created.SaleId)).IsSuccess);
        Assert.True((await service.CompleteSaleAsync(created.SaleId, "TX-S")).IsSuccess);

        var summary = await scope.ServiceProvider.GetRequiredService<ISalesReader>().FindByIdAsync(created.SaleId);
        Assert.Equal(SaleStatusContract.Completed, summary!.Status);
        Assert.Equal(20m, summary.GrandTotal);
    }

    [Fact]
    public async Task SalesService_Cancel_Works_AndFailuresTranslateToContractResults()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<ISalesService>();
        var saleId = (await service.CreateSaleAsync()).SaleId;

        var noItems = await service.ConfirmSaleAsync(saleId);
        Assert.False(noItems.IsSuccess);
        Assert.Equal("Sales.Sale.NoItems", noItems.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(noItems.ErrorMessage));

        var missing = await service.AddItemAsync(Guid.NewGuid(), Guid.NewGuid(), 1m, 1m);
        Assert.False(missing.IsSuccess);
        Assert.Equal(Guid.Empty, missing.SaleItemId);

        Assert.True((await service.CancelSaleAsync(saleId, "changed mind")).IsSuccess);
        Assert.False((await service.CancelSaleAsync(saleId, "again")).IsSuccess);
    }

    [Fact]
    public async Task SalesService_CreateSale_Invalid_ReturnsFailureResult()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ISalesService>()
            .CreateSaleAsync(new string('x', 101));

        Assert.False(result.IsSuccess);
        Assert.Equal(Guid.Empty, result.SaleId);
        Assert.Equal("Sales.Sale.ReferenceTooLong", result.ErrorCode);
    }

    // --- Module ---

    [Fact]
    public async Task SalesModule_Lifecycle_And_Manifest()
    {
        var module = new SalesModule();
        Assert.Equal(ModuleRuntimeStatus.Registered, module.Status);

        await module.InitializeAsync();
        Assert.Equal(ModuleRuntimeStatus.Enabled, module.Status);
        await module.StartAsync();
        Assert.Equal(ModuleRuntimeStatus.Running, module.Status);
        await module.StopAsync();
        Assert.Equal(ModuleRuntimeStatus.Stopped, module.Status);

        var manifest = module.Manifest;
        Assert.Equal("sales", manifest.ModuleId.Value);
        Assert.Equal(new ModuleVersion(1, 0, 0), manifest.Version);
        Assert.Contains(manifest.Dependencies, d => d.RequiredModuleId.Value == "catalog");
        Assert.Contains(manifest.Dependencies, d => d.RequiredModuleId.Value == "inventory");
        Assert.DoesNotContain(manifest.Dependencies, d => d.RequiredModuleId.Value == "payments");
    }
}
