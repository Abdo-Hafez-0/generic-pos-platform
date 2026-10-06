using Microsoft.Extensions.DependencyInjection;
using Purchasing.Application.Commands;
using Suppliers.Application.Commands;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// Stage 12 - transaction atomicity on the REAL host and the REAL SQLite file. A failure is injected INSIDE the database (a trigger that
/// aborts the chosen write), at each step of a multi-module business operation, and the database is then inspected directly:
/// a failed operation must leave exactly the state it started with; the retry must then succeed exactly once.
/// </summary>
[Collection(HostCollection.Name)]
[Trait("Category", "Failure")]
public sealed class AtomicityFailureTests
{
    private static async Task AssertNothingChangedAsync(IntegrationHost host, Shop shop, BusinessState before, Guid cartId, decimal expectedOnHand = 10m)
    {
        Assert.Equal(before, await BusinessState.ReadAsync(host));
        Assert.Equal(expectedOnHand, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(1, await CountAsync(host, "pos_Carts", $"Id = '{cartId.ToString().ToUpperInvariant()}' AND Status = 1"));
    }

    private static async Task AssertOneCompletedSaleAsync(IntegrationHost host, Shop shop, BusinessState before, decimal expectedOnHand)
    {
        var after = await BusinessState.ReadAsync(host);
        Assert.Equal(before.Sales + 1, after.Sales);
        Assert.Equal(before.SaleItems + 1, after.SaleItems);
        Assert.Equal(before.SalesTransactions + 1, after.SalesTransactions);
        Assert.Equal(before.Payments + 1, after.Payments);
        Assert.Equal(before.StockMovements + 1, after.StockMovements);
        Assert.Equal(before.CheckedOutCarts + 1, after.CheckedOutCarts);
        Assert.Equal(expectedOnHand, await OnHandAsync(host, shop.ProductId));
    }

    // ------------------------------------------------------------------ one failure at each step of a cash sale

    [Theory]
    [InlineData("sal_Sales", "INSERT")]            // the very first write
    [InlineData("sal_SaleItems", "INSERT")]
    [InlineData("pay_Payments", "INSERT")]         // after the sale + lines
    [InlineData("inv_StockMovements", "INSERT")]   // after sale + lines + payment
    [InlineData("inv_InventoryBalances", "UPDATE")]
    [InlineData("sal_SalesTransactions", "INSERT")] // after stock has been issued
    [InlineData("pos_Carts", "UPDATE")]            // the very last write: everything else was already written
    public async Task SaleRemainsAtomic_WhenAnyStepOfTheCheckoutFails_AndTheRetrySucceedsExactlyOnce(string table, string operation)
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);
        var before = await BusinessState.ReadAsync(host);

        await using (operation == "INSERT" ? await FailInsertsAsync(host, table) : await FailUpdatesAsync(host, table))
        {
            var failed = await CheckoutAsync(host.Services, cartId);

            Assert.False(failed.IsSuccess);
            await AssertNothingChangedAsync(host, shop, before, cartId);
        }

        // Recovery: the same cart sells normally once the fault is gone, and only once.
        var retried = await CheckoutAsync(host.Services, cartId);
        Assert.True(retried.IsSuccess, retried.ErrorMessage);
        await AssertOneCompletedSaleAsync(host, shop, before, expectedOnHand: 9m);

        var again = await CheckoutAsync(host.Services, cartId);
        Assert.Equal("POS.Checkout.CartNotOpen", again.ErrorCode);
        await AssertOneCompletedSaleAsync(host, shop, before, expectedOnHand: 9m);
    }

    [Fact]
    public async Task SaleDoesNotModifyInventory_WhenAnUnexpectedDatabaseErrorOccurs_AndTheMessageIsSafe()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);

        await using var fault = await FailInsertsAsync(host, "sal_SalesTransactions");
        var failed = await CheckoutAsync(host.Services, cartId);

        Assert.Equal("POS.Checkout.NotSaved", failed.ErrorCode);
        Assert.Contains("nothing was changed", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        // No database internals, table names or exception text reach the cashier.
        Assert.DoesNotContain("sal_", failed.ErrorMessage);
        Assert.DoesNotContain("injected", failed.ErrorMessage);
        Assert.DoesNotContain("SQLite", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10m, await OnHandAsync(host, shop.ProductId));
    }

    [Fact]
    public async Task SaleWithSeveralLines_RollsBackEveryIssuedLine_WhenALaterLineFails()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var cola = await CreateShopAsync(host.Services, "COLA-1", "Cola");
        var water = await CreateShopAsync(host.Services, "WATER-1", "Water", salePrice: 1m, warehouseId: cola.WarehouseId);

        Guid cartId;
        using (var scope = host.Services.CreateScope())
        {
            var pos = scope.ServiceProvider.GetRequiredService<POS.Contracts.Interfaces.IPOSService>();
            var session = await pos.OpenSessionAsync("cashier-1", cola.WarehouseId);
            var cart = await pos.StartCartAsync(session.SessionId);
            Assert.True((await pos.AddProductAsync(cart.CartId, "COLA-1", 2m)).IsSuccess);
            Assert.True((await pos.AddProductAsync(cart.CartId, "WATER-1", 3m)).IsSuccess);
            cartId = cart.CartId;
        }

        var before = await BusinessState.ReadAsync(host);
        var movementsNow = before.StockMovements;

        // The first line's stock-out is written; the second line's is refused by the database.
        await using (await FailInsertsAsync(host, "inv_StockMovements", $"(SELECT COUNT(*) FROM inv_StockMovements) >= {movementsNow + 1}"))
        {
            var failed = await CheckoutAsync(host.Services, cartId);

            Assert.False(failed.IsSuccess);
            Assert.Equal(before, await BusinessState.ReadAsync(host));
            Assert.Equal(10m, await OnHandAsync(host, cola.ProductId));   // the issued first line was rolled back by the database
            Assert.Equal(10m, await OnHandAsync(host, water.ProductId));
        }

        var retried = await CheckoutAsync(host.Services, cartId);
        Assert.True(retried.IsSuccess, retried.ErrorMessage);
        Assert.Equal(8m, await OnHandAsync(host, cola.ProductId));
        Assert.Equal(7m, await OnHandAsync(host, water.ProductId));
        Assert.Equal(before.StockMovements + 2, (await BusinessState.ReadAsync(host)).StockMovements);
    }

    [Fact]
    public async Task ACheckoutWithoutPayment_IsEquallyAtomic()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 2m);
        var before = await BusinessState.ReadAsync(host);

        await using (await FailUpdatesAsync(host, "pos_Carts"))
        {
            Assert.False((await CheckoutAsync(host.Services, cartId, tendered: null)).IsSuccess);
            await AssertNothingChangedAsync(host, shop, before, cartId);
        }

        Assert.True((await CheckoutAsync(host.Services, cartId, tendered: null)).IsSuccess);
        Assert.Equal(8m, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(before.Sales + 1, (await BusinessState.ReadAsync(host)).Sales);
    }

    // ------------------------------------------------------------------ purchasing

    private sealed record Order(Guid OrderId, Shop First, Shop Second);

    private static async Task<Order> SubmittedOrderAsync(IntegrationHost host)
    {
        var first = await CreateShopAsync(host.Services, "COLA-1", "Cola", stock: 5m);
        var second = await CreateShopAsync(host.Services, "WATER-1", "Water", stock: 5m, warehouseId: first.WarehouseId);

        using var scope = host.Services.CreateScope();
        var p = scope.ServiceProvider;
        var supplier = await p.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("SUP-1", "Acme"));
        var order = await p.GetRequiredService<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(supplier.Value));
        Assert.True((await p.GetRequiredService<AddPurchaseOrderLineCommandHandler>().HandleAsync(new AddPurchaseOrderLineCommand(order.Value, "COLA-1", 20m, 1.2m))).IsSuccess);
        Assert.True((await p.GetRequiredService<AddPurchaseOrderLineCommandHandler>().HandleAsync(new AddPurchaseOrderLineCommand(order.Value, "WATER-1", 10m, 0.5m))).IsSuccess);
        Assert.True((await p.GetRequiredService<SubmitPurchaseOrderCommandHandler>().HandleAsync(new SubmitPurchaseOrderCommand(order.Value))).IsSuccess);
        return new Order(order.Value, first, second);
    }

    private sealed record Outcome(bool IsSuccess, string? Message);

    private static async Task<Outcome> ReceiveAsync(IntegrationHost host, Order order)
    {
        using var scope = host.Services.CreateScope();
        var received = await scope.ServiceProvider.GetRequiredService<ReceivePurchaseOrderCommandHandler>()
            .HandleAsync(new ReceivePurchaseOrderCommand(order.OrderId, order.First.WarehouseId));
        return new Outcome(received.IsSuccess, received.IsSuccess ? null : received.Error.Description);
    }

    [Fact]
    public async Task PurchaseReceiptRemainsAtomic_WhenALaterLineFails_AndTheRetryReceivesEverythingExactlyOnce()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var order = await SubmittedOrderAsync(host);
        var movements = await CountAsync(host, "inv_StockMovements");

        await using (await FailInsertsAsync(host, "inv_StockMovements", $"(SELECT COUNT(*) FROM inv_StockMovements) >= {movements + 1}"))
        {
            // An unexpected database failure surfaces as an exception (the screen translates it); what matters is that nothing was kept.
            await Assert.ThrowsAnyAsync<Exception>(() => ReceiveAsync(host, order));

            Assert.Equal(5m, await OnHandAsync(host, order.First.ProductId));    // line 1's stock-in was rolled back
            Assert.Equal(5m, await OnHandAsync(host, order.Second.ProductId));
            Assert.Equal(movements, await CountAsync(host, "inv_StockMovements"));
            Assert.Equal(0, await CountAsync(host, "pur_PurchaseOrderLines", "ReceivedAt IS NOT NULL"));
            Assert.Equal(1, await CountAsync(host, "pur_PurchaseOrders", "Status = 2"));   // still Submitted, not Received
        }

        Assert.True((await ReceiveAsync(host, order)).IsSuccess);
        Assert.Equal(25m, await OnHandAsync(host, order.First.ProductId));
        Assert.Equal(15m, await OnHandAsync(host, order.Second.ProductId));
        Assert.Equal(movements + 2, await CountAsync(host, "inv_StockMovements"));

        // Receiving again must not add stock a second time.
        await ReceiveAsync(host, order);
        Assert.Equal(25m, await OnHandAsync(host, order.First.ProductId));
        Assert.Equal(15m, await OnHandAsync(host, order.Second.ProductId));
    }

    [Fact]
    public async Task PurchaseReceiptRemainsAtomic_WhenTheOrderCannotBeSaved()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var order = await SubmittedOrderAsync(host);

        await using (await FailUpdatesAsync(host, "pur_PurchaseOrders"))
        {
            // The order row is the LAST thing written: both stock movements already happened inside the transaction.
            await Assert.ThrowsAnyAsync<Exception>(() => ReceiveAsync(host, order));

            Assert.Equal(5m, await OnHandAsync(host, order.First.ProductId));
            Assert.Equal(5m, await OnHandAsync(host, order.Second.ProductId));
        }

        Assert.True((await ReceiveAsync(host, order)).IsSuccess);
        Assert.Equal(25m, await OnHandAsync(host, order.First.ProductId));
        Assert.Equal(15m, await OnHandAsync(host, order.Second.ProductId));
    }
}
