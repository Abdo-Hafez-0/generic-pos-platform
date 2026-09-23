using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;
using Inventory.Domain.Events;

namespace Inventory.Tests.Domain;

/// <summary>
/// Unit tests for StockItem aggregate invariants.
/// CatalogProductId boundary: must remain as Guid, not Catalog.Domain types.
/// </summary>
public sealed class StockItemDomainTests
{
    private static readonly Guid ValidProductId = Guid.NewGuid();
    private static readonly WarehouseId ValidWarehouseId = WarehouseId.New();

    // -----------------------------------------------------------------------
    // StockItem.Create
    // -----------------------------------------------------------------------

    [Fact]
    public void Create_ValidArguments_ReturnsSuccess()
    {
        var result = StockItem.Create(ValidProductId, ValidWarehouseId);

        Assert.True(result.IsSuccess);
        Assert.Equal(ValidProductId, result.Value.CatalogProductId);
        Assert.Equal(ValidWarehouseId, result.Value.WarehouseId);
        Assert.Null(result.Value.LocationId);
        Assert.True(result.Value.IsActive);
    }

    [Fact]
    public void Create_WithLocation_AssignsLocationId()
    {
        var locationId = LocationId.New();
        var result = StockItem.Create(ValidProductId, ValidWarehouseId, locationId);

        Assert.True(result.IsSuccess);
        Assert.Equal(locationId, result.Value.LocationId);
    }

    [Fact]
    public void Create_EmptyProductId_ReturnsFailure()
    {
        var result = StockItem.Create(Guid.Empty, ValidWarehouseId);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.StockItem.ProductRequired", result.Error.Code);
    }

    [Fact]
    public void Create_EmptyWarehouseId_ReturnsFailure()
    {
        var result = StockItem.Create(ValidProductId, WarehouseId.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.StockItem.WarehouseRequired", result.Error.Code);
    }

    // -----------------------------------------------------------------------
    // StockItem.Deactivate / Activate
    // -----------------------------------------------------------------------

    [Fact]
    public void Deactivate_ActiveItem_Succeeds()
    {
        var item = StockItem.Create(ValidProductId, ValidWarehouseId).Value;

        var result = item.Deactivate();

        Assert.True(result.IsSuccess);
        Assert.False(item.IsActive);
    }

    [Fact]
    public void Deactivate_AlreadyInactive_ReturnsConflict()
    {
        var item = StockItem.Create(ValidProductId, ValidWarehouseId).Value;
        item.Deactivate();

        var result = item.Deactivate();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.StockItem.AlreadyInactive", result.Error.Code);
    }

    [Fact]
    public void Activate_InactiveItem_Succeeds()
    {
        var item = StockItem.Create(ValidProductId, ValidWarehouseId).Value;
        item.Deactivate();

        var result = item.Activate();

        Assert.True(result.IsSuccess);
        Assert.True(item.IsActive);
    }

    // -----------------------------------------------------------------------
    // CatalogProductId is a Guid (boundary check)
    // -----------------------------------------------------------------------

    [Fact]
    public void CatalogProductId_IsGuid_NotCatalogDomainType()
    {
        // Architecture invariant: CatalogProductId must be a raw Guid,
        // not any type from Catalog.Domain.
        var item = StockItem.Create(ValidProductId, ValidWarehouseId).Value;
        Assert.IsType<Guid>(item.CatalogProductId);
    }
}
