using Microsoft.Extensions.DependencyInjection;
using Catalog.Application.Commands;
using Catalog.Application.Queries;
using Catalog.Domain.Enums;

namespace Catalog.Tests.Application;

/// <summary>Integration tests for application use cases using real CatalogDbContext (SQLite in-memory).</summary>
public sealed class ProductApplicationTests : IAsyncLifetime
{
    private CatalogTestDatabase _db = null!;

    public async Task InitializeAsync() => _db = await CatalogTestDatabase.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<Guid> SeedCategoryAsync()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateCategoryCommandHandler>();
        var result = await handler.HandleAsync(new CreateCategoryCommand("Electronics"));
        return result.IsSuccess ? result.Value.Value : Guid.Empty;
    }

    private async Task<Guid> SeedUnitAsync()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateUnitCommandHandler>();
        var result = await handler.HandleAsync(new CreateUnitCommand("Piece", "pcs"));
        return result.IsSuccess ? result.Value.Value : Guid.Empty;
    }

    [Fact(DisplayName = "Application: CreateProductCommand succeeds for valid product")]
    public async Task CreateProduct_Valid_ReturnsProductId()
    {
        var categoryId = await SeedCategoryAsync();
        var unitId = await SeedUnitAsync();

        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateProductCommandHandler>();

        var result = await handler.HandleAsync(new CreateProductCommand(
            "TEST-001", "Test Product", categoryId, unitId, 25.00m));

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value.Value);
    }

    [Fact(DisplayName = "Application: CreateProductCommand fails for duplicate SKU")]
    public async Task CreateProduct_DuplicateSku_ReturnsConflict()
    {
        var categoryId = await SeedCategoryAsync();
        var unitId = await SeedUnitAsync();

        using (var scope = _db.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateProductCommandHandler>();
            await handler.HandleAsync(new CreateProductCommand("SKU-DUPE", "First", categoryId, unitId, 10.00m));
        }

        using var scope2 = _db.CreateScope();
        var handler2 = scope2.ServiceProvider.GetRequiredService<CreateProductCommandHandler>();
        var result = await handler2.HandleAsync(new CreateProductCommand("SKU-DUPE", "Second", categoryId, unitId, 15.00m));

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.DuplicateSku", result.Error.Code);
    }

    [Fact(DisplayName = "Application: CreateProductCommand fails when category not found")]
    public async Task CreateProduct_CategoryNotFound_ReturnsFailure()
    {
        var unitId = await SeedUnitAsync();

        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateProductCommandHandler>();

        var result = await handler.HandleAsync(new CreateProductCommand(
            "SKU-001", "Test", Guid.NewGuid(), unitId, 10.00m));

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.CategoryNotFound", result.Error.Code);
    }

    [Fact(DisplayName = "Application: GetProductByIdQuery returns product after creation")]
    public async Task GetProductById_ExistingProduct_ReturnsDto()
    {
        var categoryId = await SeedCategoryAsync();
        var unitId = await SeedUnitAsync();

        Guid productId;
        using (var scope = _db.CreateScope())
        {
            var createHandler = scope.ServiceProvider.GetRequiredService<CreateProductCommandHandler>();
            var createResult = await createHandler.HandleAsync(new CreateProductCommand(
                "SKU-READ", "Readable Product", categoryId, unitId, 99.00m));
            productId = createResult.Value.Value;
        }

        using var scope2 = _db.CreateScope();
        var queryHandler = scope2.ServiceProvider.GetRequiredService<GetProductByIdQueryHandler>();
        var result = await queryHandler.HandleAsync(new GetProductByIdQuery(productId));

        Assert.True(result.IsSuccess);
        Assert.Equal("SKU-READ", result.Value.Sku);
        Assert.Equal("Readable Product", result.Value.Name);
        Assert.Equal(99.00m, result.Value.SalePrice);
    }

    [Fact(DisplayName = "Application: GetProductByIdQuery returns NotFound for missing product")]
    public async Task GetProductById_NotFound_ReturnsFailure()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetProductByIdQueryHandler>();

        var result = await handler.HandleAsync(new GetProductByIdQuery(Guid.NewGuid()));

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.NotFound", result.Error.Code);
    }

    [Fact(DisplayName = "Application: AssignBarcodeCommand assigns barcode to product")]
    public async Task AssignBarcode_ValidProduct_Succeeds()
    {
        var categoryId = await SeedCategoryAsync();
        var unitId = await SeedUnitAsync();

        Guid productId;
        using (var scope = _db.CreateScope())
        {
            var createHandler = scope.ServiceProvider.GetRequiredService<CreateProductCommandHandler>();
            var createResult = await createHandler.HandleAsync(new CreateProductCommand(
                "SKU-BARCODE", "Barcode Product", categoryId, unitId, 50.00m));
            productId = createResult.Value.Value;
        }

        using var scope2 = _db.CreateScope();
        var barcodeHandler = scope2.ServiceProvider.GetRequiredService<AssignBarcodeCommandHandler>();
        var result = await barcodeHandler.HandleAsync(new AssignBarcodeCommand(
            productId, "4006381333931", BarcodeFormat.EAN13));

        Assert.True(result.IsSuccess);
    }

    [Fact(DisplayName = "Application: FindProductByBarcodeQuery resolves barcode to product")]
    public async Task FindByBarcode_AssignedBarcode_ReturnsProduct()
    {
        var categoryId = await SeedCategoryAsync();
        var unitId = await SeedUnitAsync();

        Guid productId;
        using (var scope = _db.CreateScope())
        {
            var createHandler = scope.ServiceProvider.GetRequiredService<CreateProductCommandHandler>();
            var createResult = await createHandler.HandleAsync(new CreateProductCommand(
                "SKU-BC", "Barcode Product", categoryId, unitId, 30.00m));
            productId = createResult.Value.Value;
        }

        using (var scope = _db.CreateScope())
        {
            var barcodeHandler = scope.ServiceProvider.GetRequiredService<AssignBarcodeCommandHandler>();
            await barcodeHandler.HandleAsync(new AssignBarcodeCommand(productId, "1234567890123", BarcodeFormat.EAN13));
        }

        using var scope3 = _db.CreateScope();
        var queryHandler = scope3.ServiceProvider.GetRequiredService<FindProductByBarcodeQueryHandler>();
        var result = await queryHandler.HandleAsync(new FindProductByBarcodeQuery("1234567890123"));

        Assert.True(result.IsSuccess);
        Assert.Equal(productId, result.Value.ProductId);
        Assert.Equal("SKU-BC", result.Value.Sku);
    }

    [Fact(DisplayName = "Application: CreateCategoryCommand creates category")]
    public async Task CreateCategory_Valid_Succeeds()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateCategoryCommandHandler>();

        var result = await handler.HandleAsync(new CreateCategoryCommand("Food & Beverages", "Consumables"));

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value.Value);
    }

    [Fact(DisplayName = "Application: CreateUnitCommand creates unit")]
    public async Task CreateUnit_Valid_Succeeds()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateUnitCommandHandler>();

        var result = await handler.HandleAsync(new CreateUnitCommand("Kilogram", "kg"));

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value.Value);
    }

    [Fact(DisplayName = "Application: GetAllCategoriesQuery returns active categories")]
    public async Task GetAllCategories_ReturnsActiveCategories()
    {
        using (var scope = _db.CreateScope())
        {
            var createHandler = scope.ServiceProvider.GetRequiredService<CreateCategoryCommandHandler>();
            await createHandler.HandleAsync(new CreateCategoryCommand("Cat A"));
            await createHandler.HandleAsync(new CreateCategoryCommand("Cat B"));
        }

        using var scope2 = _db.CreateScope();
        var queryHandler = scope2.ServiceProvider.GetRequiredService<GetAllCategoriesQueryHandler>();
        var result = await queryHandler.HandleAsync(new GetAllCategoriesQuery());

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Count >= 2);
    }
}
