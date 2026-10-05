using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Tests.Application;

/// <summary>
/// Tests for the stock-receipt capability added in Stage 8C so Purchasing can receive goods through
/// Inventory.Contracts (IStockReceiptService) instead of touching Inventory data.
/// </summary>
public sealed class ReceiveStockTests : IAsyncLifetime
{
    private InventoryTestDatabase _db = null!;
    private static readonly Guid ProductId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var lookup = new StubProductLookup();
        lookup.Register(ProductId, "SKU-RECV", "Receivable");
        _db = await InventoryTestDatabase.CreateAsync(lookup);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<Guid> WarehouseAsync()
    {
        using var scope = _db.CreateScope();
        var created = await scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>()
            .HandleAsync(new CreateWarehouseCommand("Main", "MAIN"));
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    private async Task<decimal> OnHandAsync()
    {
        using var scope = _db.CreateScope();
        var levels = await scope.ServiceProvider.GetRequiredService<GetAllStockLevelsQueryHandler>().HandleAsync(new GetAllStockLevelsQuery());
        return levels.Value.Single().OnHand;
    }

    [Fact]
    public async Task ReceiveStock_IncreasesTheBalance_AndRecordsAStockInMovement()
    {
        var warehouse = await WarehouseAsync();
        using var scope = _db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStockReceiptService>();

        var first = await service.ReceiveStockAsync(ProductId, warehouse, 10m, "PO-1");
        var second = await service.ReceiveStockAsync(ProductId, warehouse, 5m, "PO-2");

        Assert.True(first.IsSuccess);
        Assert.NotEqual(Guid.Empty, first.MovementId);
        Assert.True(second.IsSuccess);
        Assert.Equal(15m, await OnHandAsync());
        var movements = await scope.ServiceProvider.GetRequiredService<IStockMovementReader>().GetRecentMovementsAsync();
        Assert.Equal(new[] { "PO-1", "PO-2" }, movements.Select(m => m.Reference!).OrderBy(r => r).ToArray());
        Assert.All(movements, m => Assert.Equal("StockIn", m.MovementType));
    }

    [Fact]
    public async Task ReceiveStock_InvalidInput_FailsWithContractResult_AndChangesNothing()
    {
        var warehouse = await WarehouseAsync();
        using var scope = _db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStockReceiptService>();

        var zero = await service.ReceiveStockAsync(ProductId, warehouse, 0m);
        var unknownProduct = await service.ReceiveStockAsync(Guid.NewGuid(), warehouse, 1m);
        var unknownWarehouse = await service.ReceiveStockAsync(ProductId, Guid.NewGuid(), 1m);

        Assert.False(zero.IsSuccess);
        Assert.Equal(Guid.Empty, zero.MovementId);
        Assert.False(unknownProduct.IsSuccess);
        Assert.False(unknownWarehouse.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(unknownProduct.ErrorCode));
        Assert.Empty((await scope.ServiceProvider.GetRequiredService<GetAllStockLevelsQueryHandler>().HandleAsync(new GetAllStockLevelsQuery())).Value);
    }

    [Fact]
    public async Task ReceiveThenIssue_RoundTrips_ThroughBothContracts()
    {
        var warehouse = await WarehouseAsync();
        using var scope = _db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IStockReceiptService>().ReceiveStockAsync(ProductId, warehouse, 8m);

        var issued = await scope.ServiceProvider.GetRequiredService<IStockIssueService>().IssueStockAsync(ProductId, warehouse, 3m);

        Assert.True(issued.IsSuccess);
        Assert.Equal(5m, await OnHandAsync());
    }
}
