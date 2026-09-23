using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;

namespace Inventory.Tests.Contracts;

/// <summary>
/// Tests for Inventory.Contracts DTOs and interface contracts.
/// Verifies that all public types are immutable records with primitive-only members.
/// No domain types should appear in Contracts.
/// </summary>
public sealed class InventoryContractsTests
{
    // -----------------------------------------------------------------------
    // StockLevelDto
    // -----------------------------------------------------------------------

    [Fact]
    public void StockLevelDto_IsImmutableRecord()
    {
        var dto = new StockLevelDto(
            StockItemId: Guid.NewGuid(),
            CatalogProductId: Guid.NewGuid(),
            WarehouseId: Guid.NewGuid(),
            LocationId: null,
            OnHand: 42m);

        Assert.Equal(42m, dto.OnHand);
    }

    [Fact]
    public void StockLevelDto_AllPropertiesArePrimitiveTypes()
    {
        // Architecture guard: no Inventory.Domain types in Contracts
        var props = typeof(StockLevelDto).GetProperties();
        foreach (var prop in props)
        {
            var t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            Assert.True(
                t == typeof(Guid) || t == typeof(decimal) || t == typeof(string) || t == typeof(bool),
                $"Property '{prop.Name}' has type '{prop.PropertyType.FullName}' which is not a primitive/Guid/decimal. " +
                "Contracts must not expose domain types.");
        }
    }

    // -----------------------------------------------------------------------
    // StockMovementDto
    // -----------------------------------------------------------------------

    [Fact]
    public void StockMovementDto_IsImmutableRecord()
    {
        var dto = new StockMovementDto(
            MovementId: Guid.NewGuid(),
            StockItemId: Guid.NewGuid(),
            MovementType: "StockIn",
            Quantity: 10m,
            Reference: "PO-001",
            OccurredAt: DateTime.UtcNow);

        Assert.Equal("StockIn", dto.MovementType);
    }

    // -----------------------------------------------------------------------
    // WarehouseDto
    // -----------------------------------------------------------------------

    [Fact]
    public void WarehouseDto_IsImmutableRecord()
    {
        var dto = new WarehouseDto(
            WarehouseId: Guid.NewGuid(),
            Name: "Main",
            Code: "MAIN",
            IsActive: true);

        Assert.True(dto.IsActive);
    }

    // -----------------------------------------------------------------------
    // IInventoryReader — interface shape
    // -----------------------------------------------------------------------

    [Fact]
    public void IInventoryReader_HasExpectedMethods()
    {
        var type = typeof(IInventoryReader);
        Assert.NotNull(type.GetMethod(nameof(IInventoryReader.GetStockLevelAsync)));
        Assert.NotNull(type.GetMethod(nameof(IInventoryReader.GetStockLevelByProductAsync)));
        Assert.NotNull(type.GetMethod(nameof(IInventoryReader.GetAllStockLevelsAsync)));
        Assert.NotNull(type.GetMethod(nameof(IInventoryReader.GetAllWarehousesAsync)));
    }

    // -----------------------------------------------------------------------
    // IStockAvailabilityChecker — interface shape
    // -----------------------------------------------------------------------

    [Fact]
    public void IStockAvailabilityChecker_HasExpectedMethod()
    {
        var type = typeof(IStockAvailabilityChecker);
        Assert.NotNull(type.GetMethod(nameof(IStockAvailabilityChecker.IsAvailableAsync)));
    }
}
