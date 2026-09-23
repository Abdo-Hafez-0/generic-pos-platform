using Catalog.Domain.Entities;
using Catalog.Domain.Enums;
using Catalog.Domain.ValueObjects;

namespace Catalog.Tests.Domain;

/// <summary>Tests for Product aggregate root business invariants.</summary>
public sealed class ProductDomainTests
{
    private static readonly CategoryId ValidCategoryId = new(Guid.NewGuid());
    private static readonly UnitId ValidUnitId = new(Guid.NewGuid());

    [Fact(DisplayName = "Product: valid creation succeeds")]
    public void Create_ValidProduct_Succeeds()
    {
        var result = Product.Create("SKU-001", "Test Product", ValidCategoryId, ValidUnitId, 10.00m);

        Assert.True(result.IsSuccess);
        Assert.Equal("SKU-001", result.Value.Sku);
        Assert.Equal("Test Product", result.Value.Name);
        Assert.Equal(ProductStatus.Active, result.Value.Status);
        Assert.NotEqual(ProductId.Empty, result.Value.Id);
    }

    [Fact(DisplayName = "Product: SKU is normalized to uppercase")]
    public void Create_NormalizesSku_ToUppercase()
    {
        var result = Product.Create("sku-abc", "Product", ValidCategoryId, ValidUnitId, 5.00m);

        Assert.True(result.IsSuccess);
        Assert.Equal("SKU-ABC", result.Value.Sku);
    }

    [Fact(DisplayName = "Product: empty SKU fails")]
    public void Create_EmptySku_ReturnsFailure()
    {
        var result = Product.Create("", "Test", ValidCategoryId, ValidUnitId, 10.00m);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.SkuEmpty", result.Error.Code);
    }

    [Fact(DisplayName = "Product: whitespace-only SKU fails")]
    public void Create_WhitespaceSku_ReturnsFailure()
    {
        var result = Product.Create("   ", "Test", ValidCategoryId, ValidUnitId, 10.00m);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.SkuEmpty", result.Error.Code);
    }

    [Fact(DisplayName = "Product: empty name fails")]
    public void Create_EmptyName_ReturnsFailure()
    {
        var result = Product.Create("SKU-001", "", ValidCategoryId, ValidUnitId, 10.00m);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.NameEmpty", result.Error.Code);
    }

    [Fact(DisplayName = "Product: negative sale price fails")]
    public void Create_NegativeSalePrice_ReturnsFailure()
    {
        var result = Product.Create("SKU-001", "Test", ValidCategoryId, ValidUnitId, -1.00m);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.NegativeSalePrice", result.Error.Code);
    }

    [Fact(DisplayName = "Product: zero sale price is valid")]
    public void Create_ZeroSalePrice_Succeeds()
    {
        var result = Product.Create("SKU-001", "Free Item", ValidCategoryId, ValidUnitId, 0m);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value.SalePrice);
    }

    [Fact(DisplayName = "Product: negative cost price fails")]
    public void Create_NegativeCostPrice_ReturnsFailure()
    {
        var result = Product.Create("SKU-001", "Test", ValidCategoryId, ValidUnitId, 10.00m, -5.00m);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.NegativeCostPrice", result.Error.Code);
    }

    [Fact(DisplayName = "Product: empty category fails")]
    public void Create_EmptyCategory_ReturnsFailure()
    {
        var result = Product.Create("SKU-001", "Test", CategoryId.Empty, ValidUnitId, 10.00m);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.CategoryRequired", result.Error.Code);
    }

    [Fact(DisplayName = "Product: empty unit fails")]
    public void Create_EmptyUnit_ReturnsFailure()
    {
        var result = Product.Create("SKU-001", "Test", ValidCategoryId, UnitId.Empty, 10.00m);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.UnitRequired", result.Error.Code);
    }

    [Fact(DisplayName = "Product: creation raises ProductCreatedEvent")]
    public void Create_RaisesProductCreatedEvent()
    {
        var result = Product.Create("SKU-001", "Test Product", ValidCategoryId, ValidUnitId, 10.00m);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Value.DomainEvents);
    }

    [Fact(DisplayName = "Product: deactivate changes status to Inactive")]
    public void Deactivate_ActiveProduct_ChangesStatusToInactive()
    {
        var product = Product.Create("SKU-001", "Test", ValidCategoryId, ValidUnitId, 10.00m).Value;

        var result = product.Deactivate();

        Assert.True(result.IsSuccess);
        Assert.Equal(ProductStatus.Inactive, product.Status);
    }

    [Fact(DisplayName = "Product: cannot deactivate a discontinued product")]
    public void Deactivate_DiscontinuedProduct_ReturnsFailure()
    {
        var product = Product.Create("SKU-001", "Test", ValidCategoryId, ValidUnitId, 10.00m).Value;
        product.Discontinue();

        var result = product.Deactivate();

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Product.AlreadyDiscontinued", result.Error.Code);
    }

    [Fact(DisplayName = "Product: assign barcode succeeds")]
    public void AssignBarcode_ValidValue_Succeeds()
    {
        var product = Product.Create("SKU-001", "Test", ValidCategoryId, ValidUnitId, 10.00m).Value;

        var result = product.AssignBarcode("1234567890123", BarcodeFormat.EAN13);

        Assert.True(result.IsSuccess);
        Assert.Single(product.Barcodes);
        Assert.Equal("1234567890123", product.Barcodes[0].Value);
    }

    [Fact(DisplayName = "Product: duplicate barcode value fails")]
    public void AssignBarcode_DuplicateValue_ReturnsFailure()
    {
        var product = Product.Create("SKU-001", "Test", ValidCategoryId, ValidUnitId, 10.00m).Value;
        product.AssignBarcode("1234567890123", BarcodeFormat.EAN13);

        var result = product.AssignBarcode("1234567890123", BarcodeFormat.EAN13);

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Barcode.Duplicate", result.Error.Code);
    }

    [Fact(DisplayName = "Product: clear domain events removes all events")]
    public void ClearDomainEvents_RemovesAllEvents()
    {
        var product = Product.Create("SKU-001", "Test", ValidCategoryId, ValidUnitId, 10.00m).Value;

        product.ClearDomainEvents();

        Assert.Empty(product.DomainEvents);
    }
}
