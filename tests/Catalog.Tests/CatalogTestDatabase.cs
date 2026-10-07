using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Catalog.Application.Abstractions;
using Catalog.Application.Commands;
using Catalog.Application.Queries;
using Catalog.Application.Repositories;
using Catalog.Contracts.Interfaces;
using Catalog.Infrastructure.Persistence;
using Catalog.Infrastructure.Repositories;
using Catalog.Infrastructure.Services;

namespace Catalog.Tests;

/// <summary>
/// Creates a CatalogDbContext backed by an in-memory SQLite database for tests.
/// Uses EnsureCreated() to set up schema from EF model.
/// Each test gets a fresh, isolated database.
/// </summary>
public sealed class CatalogTestDatabase : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;

    private CatalogTestDatabase(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public static async Task<CatalogTestDatabase> CreateAsync(Platform.Application.Abstractions.Authorization.IAuthorizationService? authorization = null)
    {
        var services = new ServiceCollection();

        // Unique in-memory SQLite database per test class (shared cache needed for multi-connection tests)
        var dbName = $"Data Source=file:catalog-test-{Guid.NewGuid():N}?mode=memory&cache=shared";

        services.AddDbContext<CatalogDbContext>(options =>
        {
            options.UseSqlite(dbName);
            options.EnableSensitiveDataLogging();
        });

        // Infrastructure: Unit of Work + Repositories
        services.AddScoped<ICatalogUnitOfWork, CatalogUnitOfWork>();
        services.AddScoped<IProductRepository, EfProductRepository>();
        services.AddScoped<ICategoryRepository, EfCategoryRepository>();
        services.AddScoped<IUnitRepository, EfUnitRepository>();
        services.AddScoped<IBarcodeRepository, EfBarcodeRepository>();

        // Contracts
        services.AddScoped<IProductLookup, CatalogProductLookup>();
        services.AddScoped<IProductBarcodeResolver, CatalogBarcodeResolver>();

        // Application: Command handlers (needed by integration tests)
        services.AddTransient<CreateProductCommandHandler>();
        services.AddTransient<UpdateProductCommandHandler>();
        services.AddTransient<DeactivateProductCommandHandler>();
        services.AddTransient<CreateCategoryCommandHandler>();
        services.AddTransient<CreateUnitCommandHandler>();
        services.AddTransient<AssignBarcodeCommandHandler>();

        // Application: Query handlers
        services.AddTransient<GetProductByIdQueryHandler>();
        services.AddTransient<GetProductBySkuQueryHandler>();
        services.AddTransient<SearchProductsQueryHandler>();
        services.AddTransient<FindProductByBarcodeQueryHandler>();
        services.AddTransient<GetAllCategoriesQueryHandler>();
        services.AddTransient<GetAllUnitsQueryHandler>();

        // Business tests are not about security; security tests pass their own (scripted or real) authorization.
        services.AddSingleton(authorization ?? new global::Tests.Common.Security.AllowAllAuthorizationService());

        var provider = services.BuildServiceProvider();

        // Create the schema using EnsureCreated (no migrations needed for tests)
        using var scope = provider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await dbContext.Database.EnsureCreatedAsync();

        return new CatalogTestDatabase(provider);
    }

    public IServiceScope CreateScope() => _serviceProvider.CreateScope();

    public async ValueTask DisposeAsync() => await _serviceProvider.DisposeAsync();
}
