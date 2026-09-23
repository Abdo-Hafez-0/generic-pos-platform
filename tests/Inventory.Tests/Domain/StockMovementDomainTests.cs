using Inventory.Domain.Entities;
using Inventory.Domain.Enums;
using Inventory.Domain.ValueObjects;
using Inventory.Domain.Events;

namespace Inventory.Tests.Domain;

/// <summary>
/// Unit tests for StockMovement and StockAdjustment aggregates.
/// </summary>
public sealed class StockMovementDomainTests
{
    private static readonly StockItemId ValidStockItemId = StockItemId.New();

    // -----------------------------------------------------------------------
    // StockMovement.Record
    // -----------------------------------------------------------------------

    [Fact]
    public void Record_ValidArguments_ReturnsSuccess()
    {
        var quantity = Quantity.Create(5m).Value;

        var result = StockMovement.Record(ValidStockItemId, MovementType.StockIn, quantity, "PO-123");

        Assert.True(result.IsSuccess);
        Assert.Equal(ValidStockItemId, result.Value.StockItemId);
        Assert.Equal(MovementType.StockIn, result.Value.MovementType);
        Assert.Equal(5m, result.Value.Quantity.Value);
        Assert.Equal("PO-123", result.Value.Reference);
    }

    [Fact]
    public void Record_EmptyStockItemId_ReturnsFailure()
    {
        var quantity = Quantity.Create(1m).Value;

        var result = StockMovement.Record(StockItemId.Empty, MovementType.StockIn, quantity);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.StockMovement.StockItemRequired", result.Error.Code);
    }

    [Fact]
    public void Record_ZeroQuantity_ReturnsFailure()
    {
        var result = StockMovement.Record(ValidStockItemId, MovementType.StockIn, Quantity.Zero);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.StockMovement.QuantityMustBePositive", result.Error.Code);
    }

    [Fact]
    public void Record_ReferenceExceeds200Chars_ReturnsFailure()
    {
        var quantity = Quantity.Create(1m).Value;
        var longRef = new string('X', 201);

        var result = StockMovement.Record(ValidStockItemId, MovementType.StockIn, quantity, longRef);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.StockMovement.ReferenceTooLong", result.Error.Code);
    }

    [Fact]
    public void Record_RaisesStockMovementRecordedEvent()
    {
        var quantity = Quantity.Create(3m).Value;

        var result = StockMovement.Record(ValidStockItemId, MovementType.Adjustment, quantity);

        Assert.True(result.IsSuccess);
        var events = result.Value.DomainEvents;
        Assert.Single(events);
        Assert.IsType<StockMovementRecordedEvent>(events[0]);
    }

    // -----------------------------------------------------------------------
    // StockAdjustment.Create
    // -----------------------------------------------------------------------

    [Fact]
    public void CreateAdjustment_ValidPositiveQuantity_Succeeds()
    {
        var result = StockAdjustment.Create(ValidStockItemId, +10m, AdjustmentReason.Found);

        Assert.True(result.IsSuccess);
        Assert.Equal(+10m, result.Value.AdjustmentQuantity);
        Assert.Equal(AdjustmentReason.Found, result.Value.Reason);
    }

    [Fact]
    public void CreateAdjustment_ValidNegativeQuantity_Succeeds()
    {
        var result = StockAdjustment.Create(ValidStockItemId, -5m, AdjustmentReason.DamageWrite);

        Assert.True(result.IsSuccess);
        Assert.Equal(-5m, result.Value.AdjustmentQuantity);
    }

    [Fact]
    public void CreateAdjustment_ZeroQuantity_ReturnsFailure()
    {
        var result = StockAdjustment.Create(ValidStockItemId, 0m, AdjustmentReason.CycleCount);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.StockAdjustment.ZeroQuantity", result.Error.Code);
    }

    [Fact]
    public void CreateAdjustment_OtherReasonWithoutNotes_ReturnsFailure()
    {
        var result = StockAdjustment.Create(ValidStockItemId, 5m, AdjustmentReason.Other, notes: null);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.StockAdjustment.NotesRequiredForOther", result.Error.Code);
    }

    [Fact]
    public void CreateAdjustment_OtherReasonWithNotes_Succeeds()
    {
        var result = StockAdjustment.Create(ValidStockItemId, 5m, AdjustmentReason.Other, "Warehouse relocation");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void CreateAdjustment_RaisesStockAdjustedEvent()
    {
        var result = StockAdjustment.Create(ValidStockItemId, -3m, AdjustmentReason.CycleCount);

        Assert.True(result.IsSuccess);
        var events = result.Value.DomainEvents;
        Assert.Single(events);
        Assert.IsType<StockAdjustedEvent>(events[0]);
    }
}
