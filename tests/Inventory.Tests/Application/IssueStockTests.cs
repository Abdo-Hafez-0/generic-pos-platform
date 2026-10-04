using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Tests.Application;

/// <summary>
/// Tests for the stock-issue capability added in Stage 5D so POS can reduce stock through
/// Inventory.Contracts (IStockIssueService) instead of touching Inventory data directly.
/// </summary>
public sealed class IssueStockTests : IAsyncLifetime
{
    private InventoryTestDatabase _db = null!;
    private static readonly Guid ProductId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var lookup = new StubProductLookup();
        lookup.Register(ProductId, "SKU-ISSUE", "Issuable");
        _db = await InventoryTestDatabase.CreateAsync(lookup);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<Guid> SeedStockAsync(decimal quantity)
    {
        Guid warehouseId;
        using (var scope = _db.CreateScope())
        {
            var created = await scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>()
                .HandleAsync(new CreateWarehouseCommand("Main", "MAIN"));
            Assert.True(created.IsSuccess);
            warehouseId = created.Value;
        }

        using (var scope = _db.CreateScope())
        {
            var added = await scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>()
                .HandleAsync(new AddStockCommand(ProductId, warehouseId, quantity));
            Assert.True(added.IsSuccess);
        }

        return warehouseId;
    }

    private async Task<decimal> OnHandAsync()
    {
        using var scope = _db.CreateScope();
        var levels = await scope.ServiceProvider.GetRequiredService<GetAllStockLevelsQueryHandler>()
            .HandleAsync(new GetAllStockLevelsQuery());
        return levels.Value.Single().OnHand;
    }

    [Fact]
    public async Task IssueStock_DecreasesBalance_AndRecordsStockOutMovement()
    {
        var warehouseId = await SeedStockAsync(10m);
        using var scope = _db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IssueStockCommandHandler>()
            .HandleAsync(new IssueStockCommand(ProductId, warehouseId, 4m, "POS sale"));

        Assert.True(result.IsSuccess);
        Assert.Equal(6m, await OnHandAsync());

        var movements = await scope.ServiceProvider.GetRequiredService<IStockMovementReader>().GetRecentMovementsAsync();
        var movement = Assert.Single(movements, m => m.Reference == "POS sale");
        Assert.Equal("StockOut", movement.MovementType);
        Assert.Equal(4m, movement.Quantity);
    }

    [Fact]
    public async Task IssueStock_MoreThanOnHand_Fails_AndChangesNothing()
    {
        var warehouseId = await SeedStockAsync(3m);
        using var scope = _db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IssueStockCommandHandler>()
            .HandleAsync(new IssueStockCommand(ProductId, warehouseId, 5m));

        Assert.True(result.IsFailure);
        Assert.Equal(3m, await OnHandAsync());
    }

    [Fact]
    public async Task IssueStock_ExactOnHand_LeavesZero()
    {
        var warehouseId = await SeedStockAsync(3m);
        using var scope = _db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IssueStockCommandHandler>()
            .HandleAsync(new IssueStockCommand(ProductId, warehouseId, 3m));

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, await OnHandAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task IssueStock_NonPositiveQuantity_Fails(double quantity)
    {
        var warehouseId = await SeedStockAsync(3m);
        using var scope = _db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IssueStockCommandHandler>()
            .HandleAsync(new IssueStockCommand(ProductId, warehouseId, (decimal)quantity));

        Assert.True(result.IsFailure);
        Assert.Equal(3m, await OnHandAsync());
    }

    [Fact]
    public async Task IssueStock_NoStockItem_Fails()
    {
        var warehouseId = await SeedStockAsync(3m);
        using var scope = _db.CreateScope();

        var unknownProduct = await scope.ServiceProvider.GetRequiredService<IssueStockCommandHandler>()
            .HandleAsync(new IssueStockCommand(Guid.NewGuid(), warehouseId, 1m));
        var unknownWarehouse = await scope.ServiceProvider.GetRequiredService<IssueStockCommandHandler>()
            .HandleAsync(new IssueStockCommand(ProductId, Guid.NewGuid(), 1m));

        Assert.Equal("Inventory.IssueStock.StockItemNotFound", unknownProduct.Error.Code);
        Assert.Equal("Inventory.IssueStock.StockItemNotFound", unknownWarehouse.Error.Code);
    }

    [Fact]
    public async Task StockIssueService_Contract_Success_And_FailureTranslation()
    {
        var warehouseId = await SeedStockAsync(2m);
        using var scope = _db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IStockIssueService>();

        var ok = await service.IssueStockAsync(ProductId, warehouseId, 2m, "ref");
        var fail = await service.IssueStockAsync(ProductId, warehouseId, 1m, "ref");

        Assert.True(ok.IsSuccess);
        Assert.NotEqual(Guid.Empty, ok.MovementId);
        Assert.False(fail.IsSuccess);
        Assert.Equal(Guid.Empty, fail.MovementId);
        Assert.False(string.IsNullOrWhiteSpace(fail.ErrorCode));
        Assert.False(string.IsNullOrWhiteSpace(fail.ErrorMessage));
    }

    [Fact]
    public async Task IssueStock_Then_AvailabilityCheckerReflectsNewBalance()
    {
        var warehouseId = await SeedStockAsync(5m);
        using var scope = _db.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IStockIssueService>().IssueStockAsync(ProductId, warehouseId, 4m);

        var checker = scope.ServiceProvider.GetRequiredService<IStockAvailabilityChecker>();
        Assert.True(await checker.IsAvailableAsync(ProductId, warehouseId, 1m));
        Assert.False(await checker.IsAvailableAsync(ProductId, warehouseId, 2m));
    }
}
