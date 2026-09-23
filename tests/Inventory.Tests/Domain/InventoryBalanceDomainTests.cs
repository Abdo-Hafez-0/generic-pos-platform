using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;

namespace Inventory.Tests.Domain;

/// <summary>
/// Unit tests for InventoryBalance aggregate — the maintained read-model of current stock.
/// </summary>
public sealed class InventoryBalanceDomainTests
{
    private static readonly StockItemId ValidStockItemId = StockItemId.New();

    // -----------------------------------------------------------------------
    // InventoryBalance.Create
    // -----------------------------------------------------------------------

    [Fact]
    public void Create_ValidStockItemId_StartsAtZero()
    {
        var result = InventoryBalance.Create(ValidStockItemId);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Value.OnHand.Value);
        Assert.Equal(ValidStockItemId, result.Value.StockItemId);
    }

    [Fact]
    public void Create_EmptyStockItemId_ReturnsFailure()
    {
        var result = InventoryBalance.Create(StockItemId.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.Balance.StockItemRequired", result.Error.Code);
    }

    // -----------------------------------------------------------------------
    // InventoryBalance.Increase
    // -----------------------------------------------------------------------

    [Fact]
    public void Increase_PositiveAmount_IncreasesOnHand()
    {
        var balance = InventoryBalance.Create(ValidStockItemId).Value;
        var amount = Quantity.Create(10m).Value;

        var result = balance.Increase(amount);

        Assert.True(result.IsSuccess);
        Assert.Equal(10m, balance.OnHand.Value);
    }

    [Fact]
    public void Increase_ZeroAmount_ReturnsFailure()
    {
        var balance = InventoryBalance.Create(ValidStockItemId).Value;

        var result = balance.Increase(Quantity.Zero);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Increase_MultipleTimes_AccumulatesCorrectly()
    {
        var balance = InventoryBalance.Create(ValidStockItemId).Value;
        balance.Increase(Quantity.Create(5m).Value);
        balance.Increase(Quantity.Create(3m).Value);

        Assert.Equal(8m, balance.OnHand.Value);
    }

    // -----------------------------------------------------------------------
    // InventoryBalance.Decrease
    // -----------------------------------------------------------------------

    [Fact]
    public void Decrease_WithinStock_DecreasesOnHand()
    {
        var balance = InventoryBalance.Create(ValidStockItemId).Value;
        balance.Increase(Quantity.Create(10m).Value);

        var result = balance.Decrease(Quantity.Create(4m).Value);

        Assert.True(result.IsSuccess);
        Assert.Equal(6m, balance.OnHand.Value);
    }

    [Fact]
    public void Decrease_MoreThanAvailable_ReturnsFailure_NegativeStockPrevented()
    {
        var balance = InventoryBalance.Create(ValidStockItemId).Value;
        balance.Increase(Quantity.Create(5m).Value);

        var result = balance.Decrease(Quantity.Create(10m).Value);

        Assert.True(result.IsFailure);
        // Verify balance was NOT modified
        Assert.Equal(5m, balance.OnHand.Value);
    }

    [Fact]
    public void Decrease_ExactBalance_ReachesZero()
    {
        var balance = InventoryBalance.Create(ValidStockItemId).Value;
        balance.Increase(Quantity.Create(7m).Value);

        var result = balance.Decrease(Quantity.Create(7m).Value);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, balance.OnHand.Value);
    }

    // -----------------------------------------------------------------------
    // InventoryBalance.ApplyAdjustment
    // -----------------------------------------------------------------------

    [Fact]
    public void ApplyAdjustment_PositiveDelta_IncreaseOnHand()
    {
        var balance = InventoryBalance.Create(ValidStockItemId).Value;

        var result = balance.ApplyAdjustment(+15m);

        Assert.True(result.IsSuccess);
        Assert.Equal(15m, balance.OnHand.Value);
    }

    [Fact]
    public void ApplyAdjustment_NegativeDelta_DecreasesOnHand()
    {
        var balance = InventoryBalance.Create(ValidStockItemId).Value;
        balance.Increase(Quantity.Create(20m).Value);

        var result = balance.ApplyAdjustment(-8m);

        Assert.True(result.IsSuccess);
        Assert.Equal(12m, balance.OnHand.Value);
    }

    [Fact]
    public void ApplyAdjustment_NegativeDeltaExceedsStock_ReturnsFailure()
    {
        var balance = InventoryBalance.Create(ValidStockItemId).Value;
        balance.Increase(Quantity.Create(5m).Value);

        var result = balance.ApplyAdjustment(-10m);

        Assert.True(result.IsFailure);
        // Balance must not be modified
        Assert.Equal(5m, balance.OnHand.Value);
    }

    [Fact]
    public void ApplyAdjustment_Zero_ReturnsFailure()
    {
        var balance = InventoryBalance.Create(ValidStockItemId).Value;

        var result = balance.ApplyAdjustment(0m);

        Assert.True(result.IsFailure);
    }
}
