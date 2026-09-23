using Microsoft.Extensions.DependencyInjection;
using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Domain.Enums;

namespace Inventory.Tests.Application;

/// <summary>
/// Integration tests for AddStockCommandHandler.
/// Uses an in-memory SQLite database (InventoryTestDatabase) and a stub IProductLookup.
/// </summary>
public sealed class AddStockCommandTests : IAsyncLifetime
{
    private InventoryTestDatabase _db = null!;
    private StubProductLookup _productLookup = null!;

    private static readonly Guid KnownProductId = Guid.NewGuid();
    private static readonly Guid UnknownProductId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _productLookup = new StubProductLookup();
        _productLookup.Register(KnownProductId, "SKU-001", "Widget");
        _db = await InventoryTestDatabase.CreateAsync(_productLookup);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // -----------------------------------------------------------------------
    // Helper: create a warehouse and return its ID
    // -----------------------------------------------------------------------

    private async Task<Guid> CreateWarehouseAsync(string name = "Main", string code = "MAIN")
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>();
        var result = await handler.HandleAsync(new CreateWarehouseCommand(name, code));
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    // -----------------------------------------------------------------------
    // AddStockCommandHandler tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task AddStock_ValidProductAndWarehouse_CreatesStockItemAndBalance()
    {
        var warehouseId = await CreateWarehouseAsync();

        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();

        var result = await handler.HandleAsync(new AddStockCommand(
            CatalogProductId: KnownProductId,
            WarehouseId: warehouseId,
            Quantity: 50m));

        Assert.True(result.IsSuccess);

        // Verify balance is 50
        var queryHandler = scope.ServiceProvider.GetRequiredService<GetAllStockLevelsQueryHandler>();
        var levels = await queryHandler.HandleAsync(new GetAllStockLevelsQuery());
        Assert.True(levels.IsSuccess);
        Assert.Single(levels.Value);
        Assert.Equal(50m, levels.Value[0].OnHand);
        Assert.Equal(KnownProductId, levels.Value[0].CatalogProductId);
    }

    [Fact]
    public async Task AddStock_CalledTwice_AccumulatesBalance()
    {
        var warehouseId = await CreateWarehouseAsync("Depot", "DEPOT");

        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();

        await handler.HandleAsync(new AddStockCommand(KnownProductId, warehouseId, 20m));
        await handler.HandleAsync(new AddStockCommand(KnownProductId, warehouseId, 15m));

        var queryHandler = scope.ServiceProvider.GetRequiredService<GetAllStockLevelsQueryHandler>();
        var levels = await queryHandler.HandleAsync(new GetAllStockLevelsQuery());
        Assert.True(levels.IsSuccess);
        Assert.Equal(35m, levels.Value[0].OnHand);
    }

    [Fact]
    public async Task AddStock_UnknownProduct_ReturnsNotFound()
    {
        var warehouseId = await CreateWarehouseAsync("S1", "S1");

        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();

        var result = await handler.HandleAsync(new AddStockCommand(
            CatalogProductId: UnknownProductId,
            WarehouseId: warehouseId,
            Quantity: 10m));

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.AddStock.ProductNotFound", result.Error.Code);
    }

    [Fact]
    public async Task AddStock_UnknownWarehouse_ReturnsNotFound()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();

        var result = await handler.HandleAsync(new AddStockCommand(
            CatalogProductId: KnownProductId,
            WarehouseId: Guid.NewGuid(),
            Quantity: 5m));

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.AddStock.WarehouseNotFound", result.Error.Code);
    }

    [Fact]
    public async Task AddStock_ZeroQuantity_ReturnsValidationError()
    {
        var warehouseId = await CreateWarehouseAsync("S2", "S2");

        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();

        var result = await handler.HandleAsync(new AddStockCommand(
            CatalogProductId: KnownProductId,
            WarehouseId: warehouseId,
            Quantity: 0m));

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task AddStock_RecordsStockMovement()
    {
        var warehouseId = await CreateWarehouseAsync("S3", "S3");

        using var scope = _db.CreateScope();
        var addHandler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();
        await addHandler.HandleAsync(new AddStockCommand(KnownProductId, warehouseId, 10m));

        // Get the StockItem ID to query movements
        var levelsHandler = scope.ServiceProvider.GetRequiredService<GetAllStockLevelsQueryHandler>();
        var levels = await levelsHandler.HandleAsync(new GetAllStockLevelsQuery());
        var stockItemId = levels.Value[0].StockItemId;

        var movementHandler = scope.ServiceProvider.GetRequiredService<GetStockMovementsQueryHandler>();
        var movements = await movementHandler.HandleAsync(new GetStockMovementsQuery(stockItemId));

        Assert.True(movements.IsSuccess);
        Assert.Single(movements.Value);
        Assert.Equal("StockIn", movements.Value[0].MovementType);
        Assert.Equal(10m, movements.Value[0].Quantity);
    }
}
