using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Contracts.Interfaces;
using Inventory.Infrastructure.Persistence;

namespace Inventory.Tests.Infrastructure;

/// <summary>
/// Integration tests for Inventory Infrastructure layer.
/// Verifies: schema creation, repository persistence, cross-module contract implementations.
/// </summary>
public sealed class InventoryInfrastructureTests : IAsyncLifetime
{
    private InventoryTestDatabase _db = null!;
    private static readonly Guid KnownProductId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var lookup = new StubProductLookup();
        lookup.Register(KnownProductId, "SKU-INFRA", "Infra Test Product");
        _db = await InventoryTestDatabase.CreateAsync(lookup);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // -----------------------------------------------------------------------
    // Schema / DbContext tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task InventoryDbContext_SchemaExists_AllTablesPresent()
    {
        using var scope = _db.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();

        // All six tables must be queryable (no exception = table exists)
        await dbContext.Warehouses.AnyAsync();
        await dbContext.Locations.AnyAsync();
        await dbContext.StockItems.AnyAsync();
        await dbContext.StockMovements.AnyAsync();
        await dbContext.StockAdjustments.AnyAsync();
        await dbContext.InventoryBalances.AnyAsync();
    }

    // -----------------------------------------------------------------------
    // Warehouse uniqueness constraint
    // -----------------------------------------------------------------------

    [Fact]
    public async Task CreateWarehouse_DuplicateCode_ReturnsConflict()
    {
        using var scope = _db.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>();

        await handler.HandleAsync(new CreateWarehouseCommand("WH One", "UNIQUE"));
        var duplicate = await handler.HandleAsync(new CreateWarehouseCommand("WH Two", "UNIQUE"));

        Assert.True(duplicate.IsFailure);
        Assert.Equal("Inventory.Warehouse.DuplicateCode", duplicate.Error.Code);
    }

    // -----------------------------------------------------------------------
    // IInventoryReader (cross-module contract)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IInventoryReader_GetAllWarehouses_ReturnsActiveWarehouses()
    {
        using var scope = _db.CreateScope();
        var warehouseHandler = scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>();
        await warehouseHandler.HandleAsync(new CreateWarehouseCommand("Reader WH", "READER"));

        var reader = scope.ServiceProvider.GetRequiredService<IInventoryReader>();
        var warehouses = await reader.GetAllWarehousesAsync();

        Assert.Contains(warehouses, w => w.Code == "READER");
    }

    [Fact]
    public async Task IInventoryReader_GetAllStockLevels_ReturnsDtos()
    {
        using var scope = _db.CreateScope();
        var warehouseHandler = scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>();
        var warehouseResult = await warehouseHandler.HandleAsync(new CreateWarehouseCommand("InvReader WH", "INVR"));

        var addHandler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();
        await addHandler.HandleAsync(new AddStockCommand(KnownProductId, warehouseResult.Value, 25m));

        var reader = scope.ServiceProvider.GetRequiredService<IInventoryReader>();
        var levels = await reader.GetAllStockLevelsAsync();

        Assert.NotEmpty(levels);
        Assert.All(levels, l => Assert.True(l.OnHand >= 0));
    }

    // -----------------------------------------------------------------------
    // IStockAvailabilityChecker (cross-module contract)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IStockAvailabilityChecker_SufficientStock_ReturnsTrue()
    {
        using var scope = _db.CreateScope();
        var warehouseHandler = scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>();
        var whResult = await warehouseHandler.HandleAsync(new CreateWarehouseCommand("Avail WH", "AVAIL"));

        var addHandler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();
        await addHandler.HandleAsync(new AddStockCommand(KnownProductId, whResult.Value, 100m));

        var checker = scope.ServiceProvider.GetRequiredService<IStockAvailabilityChecker>();
        var available = await checker.IsAvailableAsync(KnownProductId, whResult.Value, 50m);

        Assert.True(available);
    }

    [Fact]
    public async Task IStockAvailabilityChecker_InsufficientStock_ReturnsFalse()
    {
        using var scope = _db.CreateScope();
        var warehouseHandler = scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>();
        var whResult = await warehouseHandler.HandleAsync(new CreateWarehouseCommand("Insuf WH", "INSUF"));

        var addHandler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();
        await addHandler.HandleAsync(new AddStockCommand(KnownProductId, whResult.Value, 10m));

        var checker = scope.ServiceProvider.GetRequiredService<IStockAvailabilityChecker>();
        var available = await checker.IsAvailableAsync(KnownProductId, whResult.Value, 50m);

        Assert.False(available);
    }

    [Fact]
    public async Task IStockAvailabilityChecker_UnknownProduct_ReturnsFalse()
    {
        using var scope = _db.CreateScope();
        var warehouseHandler = scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>();
        var whResult = await warehouseHandler.HandleAsync(new CreateWarehouseCommand("Unknown WH", "UNKN"));

        var checker = scope.ServiceProvider.GetRequiredService<IStockAvailabilityChecker>();
        var available = await checker.IsAvailableAsync(Guid.NewGuid(), whResult.Value, 1m);

        Assert.False(available);
    }

    // -----------------------------------------------------------------------
    // IStockMovementReader (cross-module contract)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task IStockMovementReader_GetRecentMovements_ReturnsMovements()
    {
        using var scope = _db.CreateScope();
        var warehouseHandler = scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>();
        var whResult = await warehouseHandler.HandleAsync(new CreateWarehouseCommand("Movement WH", "MVMT"));

        var addHandler = scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>();
        await addHandler.HandleAsync(new AddStockCommand(KnownProductId, whResult.Value, 5m));

        var movementReader = scope.ServiceProvider.GetRequiredService<IStockMovementReader>();
        var movements = await movementReader.GetRecentMovementsAsync(limit: 10);

        Assert.NotEmpty(movements);
        Assert.All(movements, m => Assert.True(m.Quantity > 0));
    }
}
