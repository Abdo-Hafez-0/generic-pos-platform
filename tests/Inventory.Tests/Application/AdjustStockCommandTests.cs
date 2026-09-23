using Microsoft.Extensions.DependencyInjection;
using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Domain.Enums;

namespace Inventory.Tests.Application;

/// <summary>
/// Integration tests for AdjustStockCommandHandler.
/// Uses in-memory SQLite. Tests signed adjustment, negative-stock guard, and audit trail.
/// </summary>
public sealed class AdjustStockCommandTests : IAsyncLifetime
{
    private InventoryTestDatabase _db = null!;
    private StubProductLookup _productLookup = null!;
    private Guid _warehouseId;
    private Guid _stockItemId;

    private static readonly Guid KnownProductId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _productLookup = new StubProductLookup();
        _productLookup.Register(KnownProductId, "SKU-ADJ", "Adjustable Product");
        _db = await InventoryTestDatabase.CreateAsync(_productLookup);

        // Pre-create a warehouse and add 100 units of stock
        using var scope = _db.CreateScope();

        var warehouseHandler = scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>();
        var wResult = await warehouseHandler.HandleAsync(new CreateWarehouseCommand("Adjust WH", "ADJWH"));
        _warehouseId = wResult.Value;

        var addHandler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();
        await addHandler.HandleAsync(new AddStockCommand(KnownProductId, _warehouseId, 100m));

        // Capture the stock item ID
        var levelsHandler = scope.ServiceProvider.GetRequiredService<GetAllStockLevelsQueryHandler>();
        var levels = await levelsHandler.HandleAsync(new GetAllStockLevelsQuery());
        _stockItemId = levels.Value[0].StockItemId;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // -----------------------------------------------------------------------
    // AdjustStockCommandHandler tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AdjustStock_PositiveDelta_IncreasesBalance()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AdjustStockCommandHandler>();

        var result = await handler.HandleAsync(new AdjustStockCommand(
            StockItemId: _stockItemId,
            AdjustmentQuantity: +20m,
            Reason: AdjustmentReason.Found));

        Assert.True(result.IsSuccess);

        var levelHandler = scope.ServiceProvider.GetRequiredService<GetStockLevelQueryHandler>();
        var level = await levelHandler.HandleAsync(new GetStockLevelQuery(_stockItemId));
        Assert.Equal(120m, level.Value!.OnHand);
    }

    [Fact]
    public async Task AdjustStock_NegativeDelta_DecreasesBalance()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AdjustStockCommandHandler>();

        var result = await handler.HandleAsync(new AdjustStockCommand(
            StockItemId: _stockItemId,
            AdjustmentQuantity: -30m,
            Reason: AdjustmentReason.DamageWrite));

        Assert.True(result.IsSuccess);

        var levelHandler = scope.ServiceProvider.GetRequiredService<GetStockLevelQueryHandler>();
        var level = await levelHandler.HandleAsync(new GetStockLevelQuery(_stockItemId));
        Assert.Equal(70m, level.Value!.OnHand);
    }

    [Fact]
    public async Task AdjustStock_NegativeDeltaExceedsStock_ReturnsFailure_BalanceUnchanged()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AdjustStockCommandHandler>();

        // Attempt to remove 200 units when only 100 are available
        var result = await handler.HandleAsync(new AdjustStockCommand(
            StockItemId: _stockItemId,
            AdjustmentQuantity: -200m,
            Reason: AdjustmentReason.CycleCount));

        Assert.True(result.IsFailure);

        // Balance must remain at 100 — no partial commit
        var levelHandler = scope.ServiceProvider.GetRequiredService<GetStockLevelQueryHandler>();
        var level = await levelHandler.HandleAsync(new GetStockLevelQuery(_stockItemId));
        Assert.Equal(100m, level.Value!.OnHand);
    }

    [Fact]
    public async Task AdjustStock_UnknownStockItem_ReturnsNotFound()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AdjustStockCommandHandler>();

        var result = await handler.HandleAsync(new AdjustStockCommand(
            StockItemId: Guid.NewGuid(),
            AdjustmentQuantity: 5m,
            Reason: AdjustmentReason.CycleCount));

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.AdjustStock.StockItemNotFound", result.Error.Code);
    }

    [Fact]
    public async Task AdjustStock_RecordsAdjustmentMovement()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AdjustStockCommandHandler>();
        await handler.HandleAsync(new AdjustStockCommand(_stockItemId, -10m, AdjustmentReason.CycleCount));

        var movementHandler = scope.ServiceProvider.GetRequiredService<GetStockMovementsQueryHandler>();
        var movements = await movementHandler.HandleAsync(new GetStockMovementsQuery(_stockItemId));

        // Two movements: initial StockIn + new Adjustment
        Assert.True(movements.IsSuccess);
        Assert.Equal(2, movements.Value.Count);
        Assert.Contains(movements.Value, m => m.MovementType == "Adjustment");
    }

    [Fact]
    public async Task AdjustStock_OtherReasonRequiresNotes()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AdjustStockCommandHandler>();

        var result = await handler.HandleAsync(new AdjustStockCommand(
            StockItemId: _stockItemId,
            AdjustmentQuantity: 5m,
            Reason: AdjustmentReason.Other,
            Notes: null));

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.StockAdjustment.NotesRequiredForOther", result.Error.Code);
    }
}
