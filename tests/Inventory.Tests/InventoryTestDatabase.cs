using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Inventory.Application.Abstractions;
using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Application.Repositories;
using Inventory.Contracts.Interfaces;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Repositories;
using Inventory.Infrastructure.Services;

namespace Inventory.Tests;

/// <summary>
/// Creates an InventoryDbContext backed by an in-memory SQLite database for tests.
/// Uses EnsureCreated() to set up schema from EF model (no migrations needed for tests).
/// Each test class gets a fresh, isolated database.
///
/// The IProductLookup dependency is stubbed via StubProductLookup so Inventory tests
/// do not require a live CatalogDbContext. This keeps tests isolated and fast.
/// </summary>
public sealed class InventoryTestDatabase : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;

    private InventoryTestDatabase(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public static async Task<InventoryTestDatabase> CreateAsync(
        StubProductLookup? productLookup = null)
    {
        var services = new ServiceCollection();

        // Unique in-memory SQLite database per test (shared cache needed for multi-connection)
        var dbName = $"Data Source=file:inventory-test-{Guid.NewGuid():N}?mode=memory&cache=shared";

        services.AddDbContext<InventoryDbContext>(options =>
        {
            options.UseSqlite(dbName);
            options.EnableSensitiveDataLogging();
        });

        // Infrastructure: Unit of Work + Repositories
        services.AddScoped<IInventoryUnitOfWork, InventoryUnitOfWork>();
        services.AddScoped<IWarehouseRepository, EfWarehouseRepository>();
        services.AddScoped<ILocationRepository, EfLocationRepository>();
        services.AddScoped<IStockItemRepository, EfStockItemRepository>();
        services.AddScoped<IStockMovementRepository, EfStockMovementRepository>();
        services.AddScoped<IStockAdjustmentRepository, EfStockAdjustmentRepository>();
        services.AddScoped<IInventoryBalanceRepository, EfInventoryBalanceRepository>();

        // Contracts implementations
        services.AddScoped<IInventoryReader, InventoryReader>();
        services.AddScoped<IStockAvailabilityChecker, StockAvailabilityChecker>();
        services.AddScoped<IStockMovementReader, StockMovementReader>();

        // Stub IProductLookup (Catalog is not loaded in Inventory tests)
        var lookup = productLookup ?? new StubProductLookup();
        services.AddSingleton<IProductLookup>(lookup);

        // Application: Command handlers
        services.AddTransient<CreateWarehouseCommandHandler>();
        services.AddTransient<CreateLocationCommandHandler>();
        services.AddTransient<AddStockCommandHandler>();
        services.AddTransient<AdjustStockCommandHandler>();

        // Application: Query handlers
        services.AddTransient<GetWarehousesQueryHandler>();
        services.AddTransient<GetStockLevelQueryHandler>();
        services.AddTransient<GetAllStockLevelsQueryHandler>();
        services.AddTransient<GetStockMovementsQueryHandler>();

        var provider = services.BuildServiceProvider();

        // Create the schema via EnsureCreated (mirrors CatalogTestDatabase pattern)
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        return new InventoryTestDatabase(provider);
    }

    public IServiceScope CreateScope() => _serviceProvider.CreateScope();

    public async ValueTask DisposeAsync() => await _serviceProvider.DisposeAsync();
}

/// <summary>
/// Test stub for IProductLookup. Pre-register known product IDs before running a test.
/// The stub avoids any dependency on CatalogDbContext in Inventory tests.
/// </summary>
public sealed class StubProductLookup : IProductLookup
{
    private readonly Dictionary<Guid, ProductLookupResult> _products = [];

    /// <summary>Pre-registers a product so the stub returns it during the test.</summary>
    public void Register(
        Guid productId,
        string sku = "TEST-SKU",
        string name = "Test Product")
    {
        _products[productId] = new ProductLookupResult(
            ProductId: productId,
            Sku: sku,
            Name: name,
            Description: null,
            CategoryId: Guid.NewGuid(),
            CategoryName: "Test Category",
            UnitId: Guid.NewGuid(),
            UnitName: "Each",
            UnitAbbreviation: "ea",
            SalePrice: 10m,
            CostPrice: null,
            Status: ProductStatusContract.Active);
    }

    public Task<ProductLookupResult?> FindByIdAsync(Guid productId, CancellationToken cancellationToken = default)
        => Task.FromResult(_products.TryGetValue(productId, out var result) ? result : null);

    public Task<ProductLookupResult?> FindBySkuAsync(string sku, CancellationToken cancellationToken = default)
        => Task.FromResult(_products.Values.FirstOrDefault(p => p.Sku == sku));
}
