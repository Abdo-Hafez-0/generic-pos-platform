using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Application.Security;
using Inventory.Contracts.Interfaces;
using Inventory.Domain.Enums;
using Inventory.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Tests.Common.Security;

namespace Inventory.Tests.Security;

/// <summary>Stock changes are refused by the handlers themselves; module-to-module contract calls ride on the caller's authorization.</summary>
public sealed class InventoryAuthorizationTests
{
    private static readonly Guid Product = Guid.NewGuid();

    private sealed class World : IAsyncDisposable
    {
        public required InventoryTestDatabase Db { get; init; }
        public required ScriptedAuthorizationService Auth { get; init; }
        public required Guid WarehouseId { get; init; }
        public required Guid StockItemId { get; init; }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    /// <summary>A warehouse with 100 units, built with every capability; the test then narrows what is allowed.</summary>
    private static async Task<World> StartAsync(params string[] allowed)
    {
        var lookup = new StubProductLookup();
        lookup.Register(Product, "SKU-SEC", "Secured product");
        var auth = new ScriptedAuthorizationService(InventoryCapabilities.All.Select(c => c.Code).ToArray());
        var db = await InventoryTestDatabase.CreateAsync(lookup, auth);

        Guid warehouse, item;
        using (var scope = db.CreateScope())
        {
            warehouse = (await scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>().HandleAsync(new CreateWarehouseCommand("WH", "WH1"))).Value;
            await scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>().HandleAsync(new AddStockCommand(Product, warehouse, 100m));
            item = (await scope.ServiceProvider.GetRequiredService<GetAllStockLevelsQueryHandler>().HandleAsync(new GetAllStockLevelsQuery())).Value[0].StockItemId;
        }

        auth.Allowed.Clear();
        foreach (var capability in allowed) auth.Allowed.Add(capability);
        auth.Asked.Clear();
        return new World { Db = db, Auth = auth, WarehouseId = warehouse, StockItemId = item };
    }

    private static async Task<decimal> OnHandAsync(World w)
    {
        using var scope = w.Db.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<GetStockLevelQueryHandler>().HandleAsync(new GetStockLevelQuery(w.StockItemId))).Value!.OnHand;
    }

    [Fact]
    public async Task Adjusting_stock_without_the_capability_changes_nothing()
    {
        await using var w = await StartAsync(InventoryCapabilities.ReceiveStock);
        using var scope = w.Db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<AdjustStockCommandHandler>()
            .HandleAsync(new AdjustStockCommand(w.StockItemId, -90m, AdjustmentReason.DamageWrite));

        Assert.Equal(SecurityErrors.ForbiddenCode, result.Error.Code);
        Assert.Equal(InventoryCapabilities.AdjustStock, Assert.Single(w.Auth.Asked));
        Assert.Equal(100m, await OnHandAsync(w));
    }

    [Fact]
    public async Task Adjusting_stock_with_the_capability_works()
    {
        await using var w = await StartAsync(InventoryCapabilities.AdjustStock);
        using var scope = w.Db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<AdjustStockCommandHandler>()
            .HandleAsync(new AdjustStockCommand(w.StockItemId, -10m, AdjustmentReason.DamageWrite));

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        Assert.Equal(90m, await OnHandAsync(w));
    }

    [Fact]
    public async Task Receiving_stock_and_managing_warehouses_each_need_their_capability()
    {
        await using var w = await StartAsync();
        using var scope = w.Db.CreateScope();
        var sp = scope.ServiceProvider;

        var add = await sp.GetRequiredService<AddStockCommandHandler>().HandleAsync(new AddStockCommand(Product, w.WarehouseId, 5m));
        var warehouse = await sp.GetRequiredService<CreateWarehouseCommandHandler>().HandleAsync(new CreateWarehouseCommand("Other", "WH2"));
        var location = await sp.GetRequiredService<CreateLocationCommandHandler>().HandleAsync(new CreateLocationCommand(w.WarehouseId, "Shelf", "S1"));

        Assert.Equal(SecurityErrors.ForbiddenCode, add.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, warehouse.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, location.Error.Code);
        Assert.Equal(100m, await OnHandAsync(w));
        Assert.Single((await sp.GetRequiredService<GetWarehousesQueryHandler>().HandleAsync(new GetWarehousesQuery())).Value);
    }

    [Fact]
    public async Task The_contract_path_other_modules_use_does_not_ask_the_user_for_a_second_permission()
    {
        // A purchasing clerk who may receive a purchase order must not also need inventory.stock.receive: the receipt contract runs
        // inside the purchasing operation that was already authorized.
        await using var w = await StartAsync();
        using var scope = w.Db.CreateScope();
        var receipt = new StockReceiptService(scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>());

        var result = await receipt.ReceiveStockAsync(Product, w.WarehouseId, 25m, "PO-1");

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Empty(w.Auth.Asked);
        Assert.Equal(125m, await OnHandAsync(w));
    }

    [Fact]
    public void Every_capability_is_declared_once_and_owned_by_inventory()
    {
        var catalog = new CapabilityCatalog([new InventoryCapabilityProvider()]);

        Assert.Equal(InventoryCapabilities.All.Count, catalog.All.Count);
        Assert.All(catalog.All, c => Assert.Equal("inventory", c.Module));
        Assert.True(catalog.Find(InventoryCapabilities.AdjustStock)!.IsSensitive);
    }
}
