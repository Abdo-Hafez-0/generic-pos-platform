using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Tests.Application;

/// <summary>FIX-01c: the stock overview behind the stock screen - readable names, filters, and stock never hidden.</summary>
public sealed class StockOverviewTests
{
    private static async Task<Guid> WarehouseAsync(InventoryTestDatabase db, string name, string code)
    {
        using var scope = db.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<CreateWarehouseCommandHandler>().HandleAsync(new CreateWarehouseCommand(name, code))).Value;
    }

    private static async Task ReceiveAsync(InventoryTestDatabase db, Guid product, Guid warehouse, decimal quantity)
    {
        using var scope = db.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<AddStockCommandHandler>().HandleAsync(new AddStockCommand(product, warehouse, quantity))).IsSuccess);
    }

    private static async Task<IReadOnlyList<StockOverviewItem>> OverviewAsync(InventoryTestDatabase db, string? search = null, Guid? warehouse = null)
    {
        using var scope = db.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<GetStockOverviewQueryHandler>().HandleAsync(new GetStockOverviewQuery(search, warehouse));
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    [Fact]
    public async Task Stock_is_shown_with_product_and_warehouse_names_ordered_by_product_then_warehouse()
    {
        var (cola, water) = (Guid.NewGuid(), Guid.NewGuid());
        var lookup = new StubProductLookup();
        lookup.Register(cola, "COLA-1", "Cola");
        lookup.Register(water, "WATER-1", "Water");
        await using var db = await InventoryTestDatabase.CreateAsync(lookup);
        var main = await WarehouseAsync(db, "Main shop", "MAIN");
        var kiosk = await WarehouseAsync(db, "Kiosk", "KIOSK");
        await ReceiveAsync(db, water, main, 4m);
        await ReceiveAsync(db, cola, main, 10m);
        await ReceiveAsync(db, cola, kiosk, 2m);
        await ReceiveAsync(db, cola, main, 1m);

        var all = await OverviewAsync(db);

        Assert.Equal([("Cola", "Kiosk", 2m), ("Cola", "Main shop", 11m), ("Water", "Main shop", 4m)],
            all.Select(i => (i.ProductName, i.WarehouseName, i.OnHand)));
        Assert.Equal("COLA-1", all[0].Sku);
    }

    [Fact]
    public async Task The_overview_filters_by_warehouse_and_by_sku_or_name()
    {
        var (cola, water) = (Guid.NewGuid(), Guid.NewGuid());
        var lookup = new StubProductLookup();
        lookup.Register(cola, "COLA-1", "Cola");
        lookup.Register(water, "WATER-1", "Water");
        await using var db = await InventoryTestDatabase.CreateAsync(lookup);
        var main = await WarehouseAsync(db, "Main shop", "MAIN");
        var kiosk = await WarehouseAsync(db, "Kiosk", "KIOSK");
        await ReceiveAsync(db, cola, main, 1m);
        await ReceiveAsync(db, water, kiosk, 1m);

        Assert.Equal(["Water"], (await OverviewAsync(db, warehouse: kiosk)).Select(i => i.ProductName));
        Assert.Equal(["Cola"], (await OverviewAsync(db, search: "col")).Select(i => i.ProductName));
        Assert.Equal(["Water"], (await OverviewAsync(db, search: "water-")).Select(i => i.ProductName));
        Assert.Empty(await OverviewAsync(db, search: "cola", warehouse: kiosk));
    }
}
