using Microsoft.Extensions.DependencyInjection;
using Catalog.Application.Commands;
using Catalog.Contracts.Interfaces;
using Catalog.Domain.Enums;

namespace Catalog.Tests.Contracts;

/// <summary>Tests for Catalog.Contracts service implementations.</summary>
public sealed class ContractServiceTests : IAsyncLifetime
{
    private CatalogTestDatabase _db = null!;

    public async Task InitializeAsync() => _db = await CatalogTestDatabase.CreateAsync();
    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<(Guid productId, Guid categoryId, Guid unitId)> SeedProductAsync(string sku)
    {
        Guid categoryId, unitId, productId;

        using (var scope = _db.CreateScope())
        {
            var catHandler = scope.ServiceProvider.GetRequiredService<CreateCategoryCommandHandler>();
            var catResult = await catHandler.HandleAsync(new CreateCategoryCommand("Test Category"));
            categoryId = catResult.Value.Value;
        }

        using (var scope = _db.CreateScope())
        {
            var unitHandler = scope.ServiceProvider.GetRequiredService<CreateUnitCommandHandler>();
            var unitResult = await unitHandler.HandleAsync(new CreateUnitCommand("Piece", "pcs"));
            unitId = unitResult.Value.Value;
        }

        using (var scope = _db.CreateScope())
        {
            var prodHandler = scope.ServiceProvider.GetRequiredService<CreateProductCommandHandler>();
            var prodResult = await prodHandler.HandleAsync(new CreateProductCommand(
                sku, "Contract Test Product", categoryId, unitId, 49.99m));
            productId = prodResult.Value.Value;
        }

        return (productId, categoryId, unitId);
    }

    [Fact(DisplayName = "IProductLookup: FindByIdAsync returns ProductLookupResult")]
    public async Task IProductLookup_FindById_ReturnsResult()
    {
        var (productId, _, _) = await SeedProductAsync("LOOKUP-001");

        using var scope = _db.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IProductLookup>();

        var result = await lookup.FindByIdAsync(productId);

        Assert.NotNull(result);
        Assert.Equal(productId, result!.ProductId);
        Assert.Equal("LOOKUP-001", result.Sku);
        Assert.Equal(49.99m, result.SalePrice);
    }

    [Fact(DisplayName = "IProductLookup: FindByIdAsync returns null for unknown product")]
    public async Task IProductLookup_FindById_ReturnsNull_ForUnknown()
    {
        using var scope = _db.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IProductLookup>();

        var result = await lookup.FindByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact(DisplayName = "IProductLookup: FindBySkuAsync returns ProductLookupResult")]
    public async Task IProductLookup_FindBySku_ReturnsResult()
    {
        await SeedProductAsync("LOOKUP-002");

        using var scope = _db.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IProductLookup>();

        var result = await lookup.FindBySkuAsync("LOOKUP-002");

        Assert.NotNull(result);
        Assert.Equal("LOOKUP-002", result!.Sku);
    }

    [Fact(DisplayName = "IProductBarcodeResolver: ResolveAsync resolves barcode to product")]
    public async Task IProductBarcodeResolver_Resolve_ReturnsProduct()
    {
        var (productId, _, _) = await SeedProductAsync("BARCODE-RESOLVE");

        using (var scope = _db.CreateScope())
        {
            var barcodeHandler = scope.ServiceProvider.GetRequiredService<AssignBarcodeCommandHandler>();
            await barcodeHandler.HandleAsync(new AssignBarcodeCommand(productId, "9876543210987", BarcodeFormat.EAN13));
        }

        using var scope2 = _db.CreateScope();
        var resolver = scope2.ServiceProvider.GetRequiredService<IProductBarcodeResolver>();

        var result = await resolver.ResolveAsync("9876543210987");

        Assert.NotNull(result);
        Assert.Equal(productId, result!.ProductId);
        Assert.Equal("BARCODE-RESOLVE", result.Sku);
    }

    [Fact(DisplayName = "IProductBarcodeResolver: ResolveAsync returns null for unknown barcode")]
    public async Task IProductBarcodeResolver_Resolve_ReturnsNull_ForUnknownBarcode()
    {
        using var scope = _db.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IProductBarcodeResolver>();

        var result = await resolver.ResolveAsync("9999999999999");

        Assert.Null(result);
    }

    [Fact(DisplayName = "ProductLookupResult: Status maps correctly to contract enum")]
    public async Task ProductLookupResult_StatusMapped_Correctly()
    {
        var (productId, _, _) = await SeedProductAsync("STATUS-MAP");

        using var scope = _db.CreateScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IProductLookup>();

        var result = await lookup.FindByIdAsync(productId);

        Assert.NotNull(result);
        Assert.Equal(Catalog.Contracts.Models.ProductStatusContract.Active, result!.Status);
    }
}
