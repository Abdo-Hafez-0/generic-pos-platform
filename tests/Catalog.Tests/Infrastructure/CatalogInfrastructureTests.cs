using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Catalog.Application.Repositories;
using Catalog.Contracts.Interfaces;
using Catalog.Infrastructure.Persistence;
using Platform.Core.Modules;

namespace Catalog.Tests.Infrastructure;

/// <summary>Tests for Catalog.Infrastructure persistence and DI registration.</summary>
public sealed class CatalogInfrastructureTests : IAsyncLifetime
{
    private CatalogTestDatabase _db = null!;

    public async Task InitializeAsync() => _db = await CatalogTestDatabase.CreateAsync();
    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact(DisplayName = "Infrastructure: CatalogDbContext has Products DbSet")]
    public void CatalogDbContext_HasProductsDbSet()
    {
        using var scope = _db.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.NotNull(ctx.Products);
    }

    [Fact(DisplayName = "Infrastructure: CatalogDbContext has Categories DbSet")]
    public void CatalogDbContext_HasCategoriesDbSet()
    {
        using var scope = _db.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.NotNull(ctx.Categories);
    }

    [Fact(DisplayName = "Infrastructure: CatalogDbContext has Units DbSet")]
    public void CatalogDbContext_HasUnitsDbSet()
    {
        using var scope = _db.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.NotNull(ctx.Units);
    }

    [Fact(DisplayName = "Infrastructure: CatalogDbContext has Barcodes DbSet")]
    public void CatalogDbContext_HasBarcodesDbSet()
    {
        using var scope = _db.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.NotNull(ctx.Barcodes);
    }

    [Fact(DisplayName = "Infrastructure: IProductRepository is registered")]
    public void DI_IProductRepository_IsRegistered()
    {
        using var scope = _db.CreateScope();
        var repo = scope.ServiceProvider.GetService<IProductRepository>();
        Assert.NotNull(repo);
    }

    [Fact(DisplayName = "Infrastructure: ICategoryRepository is registered")]
    public void DI_ICategoryRepository_IsRegistered()
    {
        using var scope = _db.CreateScope();
        var repo = scope.ServiceProvider.GetService<ICategoryRepository>();
        Assert.NotNull(repo);
    }

    [Fact(DisplayName = "Infrastructure: IUnitRepository is registered")]
    public void DI_IUnitRepository_IsRegistered()
    {
        using var scope = _db.CreateScope();
        var repo = scope.ServiceProvider.GetService<IUnitRepository>();
        Assert.NotNull(repo);
    }

    [Fact(DisplayName = "Infrastructure: IProductLookup is registered")]
    public void DI_IProductLookup_IsRegistered()
    {
        using var scope = _db.CreateScope();
        var lookup = scope.ServiceProvider.GetService<IProductLookup>();
        Assert.NotNull(lookup);
    }

    [Fact(DisplayName = "Infrastructure: IProductBarcodeResolver is registered")]
    public void DI_IProductBarcodeResolver_IsRegistered()
    {
        using var scope = _db.CreateScope();
        var resolver = scope.ServiceProvider.GetService<IProductBarcodeResolver>();
        Assert.NotNull(resolver);
    }

    [Fact(DisplayName = "Infrastructure: Database schema has cat_Products table")]
    public async Task Schema_HasCatProductsTable()
    {
        using var scope = _db.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        // Check via EF metadata
        var entityType = ctx.Model.FindEntityType(typeof(Catalog.Domain.Entities.Product));
        Assert.NotNull(entityType);
        Assert.Equal("cat_Products", entityType!.GetTableName());
    }

    [Fact(DisplayName = "Infrastructure: Database schema has cat_Categories table")]
    public async Task Schema_HasCatCategoriesTable()
    {
        using var scope = _db.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var entityType = ctx.Model.FindEntityType(typeof(Catalog.Domain.Entities.Category));
        Assert.NotNull(entityType);
        Assert.Equal("cat_Categories", entityType!.GetTableName());
    }

    [Fact(DisplayName = "Infrastructure: Products table has unique index on SKU")]
    public void Schema_Products_HasUniqueSkuIndex()
    {
        using var scope = _db.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var entityType = ctx.Model.FindEntityType(typeof(Catalog.Domain.Entities.Product))!;
        var indexes = entityType.GetIndexes();
        var skuIndex = indexes.FirstOrDefault(i => i.Properties.Any(p => p.Name == nameof(Catalog.Domain.Entities.Product.Sku)));

        Assert.NotNull(skuIndex);
        Assert.True(skuIndex!.IsUnique);
    }
}
