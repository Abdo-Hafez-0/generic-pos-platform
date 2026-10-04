using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Inventory.Contracts.Interfaces;
using Sales.Application.Abstractions;
using Sales.Application.Commands;
using Sales.Application.Queries;
using Sales.Application.Repositories;
using Sales.Contracts.Interfaces;
using Sales.Infrastructure.Persistence;
using Sales.Infrastructure.Repositories;
using Sales.Infrastructure.Services;

namespace Sales.Tests;

/// <summary>
/// Creates a SalesDbContext backed by an in-memory SQLite database for tests.
/// Uses EnsureCreated() to set up schema from EF model (no migrations needed for tests).
/// Each test class gets a fresh, isolated database.
///
/// IProductLookup and IStockAvailabilityChecker are stubbed so Sales tests do not
/// require live Catalog/Inventory contexts. This keeps tests isolated and fast.
/// </summary>
public sealed class SalesTestDatabase : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;

    private SalesTestDatabase(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public static async Task<SalesTestDatabase> CreateAsync(
        StubProductLookup? productLookup = null,
        StubStockAvailabilityChecker? stockChecker = null)
    {
        var services = new ServiceCollection();

        // Unique in-memory SQLite database per test
        var dbName = $"Data Source=file:sales-test-{Guid.NewGuid():N}?mode=memory&cache=shared";

        services.AddDbContext<SalesDbContext>(options =>
        {
            options.UseSqlite(dbName);
            options.EnableSensitiveDataLogging();
        });

        // Infrastructure: Unit of Work + Repositories
        services.AddScoped<ISalesUnitOfWork, SalesUnitOfWork>();
        services.AddScoped<ISaleRepository, EfSaleRepository>();
        services.AddScoped<IReturnRepository, EfReturnRepository>();
        services.AddScoped<ISalesTransactionRepository, EfSalesTransactionRepository>();

        // Contracts implementations
        services.AddScoped<ISalesReader, SalesReader>();
        services.AddScoped<ISalesService, SalesService>();

        // Stubs for cross-module contracts
        var lookup = productLookup ?? new StubProductLookup();
        services.AddSingleton<IProductLookup>(lookup);

        var checker = stockChecker ?? new StubStockAvailabilityChecker(available: true);
        services.AddSingleton<IStockAvailabilityChecker>(checker);

        // Application: Command handlers
        services.AddTransient<CreateSaleCommandHandler>();
        services.AddTransient<AddSaleItemCommandHandler>();
        services.AddTransient<ConfirmSaleCommandHandler>();
        services.AddTransient<CompleteSaleCommandHandler>();
        services.AddTransient<CancelSaleCommandHandler>();

        // Application: Query handlers
        services.AddTransient<GetSaleByIdQueryHandler>();
        services.AddTransient<GetAllSalesQueryHandler>();

        var provider = services.BuildServiceProvider();

        // Create schema via EnsureCreated
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        return new SalesTestDatabase(provider);
    }

    public IServiceScope CreateScope() => _serviceProvider.CreateScope();

    public async ValueTask DisposeAsync() => await _serviceProvider.DisposeAsync();
}

/// <summary>
/// Stub for IProductLookup. Pre-register known product IDs before running a test.
/// Avoids any dependency on CatalogDbContext in Sales tests.
/// </summary>
public sealed class StubProductLookup : IProductLookup
{
    private readonly Dictionary<Guid, ProductLookupResult> _products = [];

    public void Register(
        Guid productId,
        string sku = "SALES-SKU",
        string name = "Test Product",
        decimal salePrice = 100m)
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
            SalePrice: salePrice,
            CostPrice: null,
            Status: ProductStatusContract.Active);
    }

    public Task<ProductLookupResult?> FindByIdAsync(Guid productId, CancellationToken cancellationToken = default)
        => Task.FromResult(_products.TryGetValue(productId, out var result) ? result : null);

    public Task<ProductLookupResult?> FindBySkuAsync(string sku, CancellationToken cancellationToken = default)
        => Task.FromResult(_products.Values.FirstOrDefault(p => p.Sku == sku));
}

/// <summary>
/// Stub for IStockAvailabilityChecker. Controls whether stock is available for tests.
/// </summary>
public sealed class StubStockAvailabilityChecker(bool available = true) : IStockAvailabilityChecker
{
    public Task<bool> IsAvailableAsync(
        Guid catalogProductId,
        Guid warehouseId,
        decimal quantity,
        CancellationToken cancellationToken = default)
        => Task.FromResult(available);
}
