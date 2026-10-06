using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Purchasing.Application.Commands;
using Suppliers.Application.Commands;
using Tests.Common.Security;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// Stage 12 - application lifecycle on the real desktop composition and a real database file that survives between "runs": shutdown, restart,
/// interrupted operations, double submits. After every interruption the database is inspected directly and the operation is repeated: it must
/// complete exactly once - no duplicate sale, payment or stock deduction, and no missing one.
/// </summary>
[Collection(HostCollection.Name)]
[Trait("Category", "Recovery")]
public sealed class RestartAndRecoveryTests
{
    private static async Task<string> RunAsync(Func<OfflineDesktop, Task> work, TestClock clock, SignedLicenseWorld licenses, string? folder, IEnumerable<Client.Host.Hosting.IHostingModule>? extra = null)
    {
        await using var desktop = await OfflineDesktop.StartAsync(folder, keepFiles: true, licenses, clock, extra);
        await work(desktop);
        return desktop.Host.Folder;
    }

    private static string DatabaseOf(string folder) => Path.Combine(folder, "GenericPOS", "integration.db");

    private static async Task<long> ScalarCountAsync(string folder, string sql)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={DatabaseOf(folder)};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<decimal> StockAsync(string folder)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection($"Data Source={DatabaseOf(folder)};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(CAST(OnHand AS REAL)) FROM inv_InventoryBalances";
        return Convert.ToDecimal(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ normal shutdown and restart

    [Fact]
    public async Task ApplicationRestart_PreservesCompletedSale_AndTheShopKeepsSelling()
    {
        var clock = new TestClock(OfflineDesktop.Start);
        using var licenses = OfflineDesktop.NewLicenses(clock);
        string folder = null!;
        try
        {
            Shop shop = null!;
            Guid saleId = Guid.Empty;
            folder = await RunAsync(async first =>
            {
                shop = await CreateShopAsync(first.Services);
                var (_, cartId) = await OpenCartAsync(first.Services, shop, 3m);
                var checkout = await CheckoutAsync(first.Services, cartId);
                Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
                saleId = checkout.SaleId;
            }, clock, licenses, folder: null);

            // a clean shutdown leaves no unfinished transaction behind
            SqliteConnection.ClearAllPools();
            Assert.False(File.Exists(DatabaseOf(folder) + "-journal"));
            Assert.Equal("ok", (await IntegrationHostPragmaAsync(folder)));

            // "the next morning": a new process on the same files
            await RunAsync(async second =>
            {
                var state = await BusinessState.ReadAsync(second.Host);
                Assert.Equal((1L, 1L, 1L, 1L, 2L), (state.Sales, state.SaleItems, state.SalesTransactions, state.Payments, state.StockMovements));
                Assert.Equal(7m, await OnHandAsync(second.Host, shop.ProductId));

                using (var scope = second.Services.CreateScope())
                {
                    var sale = await scope.ServiceProvider.GetRequiredService<Sales.Contracts.Interfaces.ISalesReader>().FindByIdAsync(saleId);
                    Assert.Equal(Sales.Contracts.Models.SaleStatusContract.Completed, sale!.Status);
                }

                var (_, cartId) = await OpenCartAsync(second.Services, shop, 2m);
                Assert.True((await CheckoutAsync(second.Services, cartId)).IsSuccess);
                Assert.Equal(5m, await OnHandAsync(second.Host, shop.ProductId));
                Assert.Equal(2, (await BusinessState.ReadAsync(second.Host)).Sales);
            }, clock, licenses, folder);
        }
        finally
        {
            if (folder is not null) IntegrationHost.DeleteFolder(folder);
        }
    }

    private static async Task<string> IntegrationHostPragmaAsync(string folder)
    {
        await using var connection = new SqliteConnection($"Data Source={DatabaseOf(folder)};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    // ------------------------------------------------------------------ restart after a failed or interrupted transaction

    [Theory]
    [InlineData(InterruptCommit.Mode.Fail)]    // the commit could not be written: the application is still running
    [InlineData(InterruptCommit.Mode.Crash)]   // the process vanished with the transaction open
    public async Task ApplicationRestart_AfterAnInterruptedSale_ShowsNoSale_AndTheRetryCompletesExactlyOnce(InterruptCommit.Mode interruption)
    {
        var clock = new TestClock(OfflineDesktop.Start);
        using var licenses = OfflineDesktop.NewLicenses(clock);
        string folder = null!;
        try
        {
            Shop shop = null!;
            Guid cartId = Guid.Empty;
            var interrupt = new InterruptCommit();
            folder = await RunAsync(async first =>
            {
                shop = await CreateShopAsync(first.Services);
                (_, cartId) = await OpenCartAsync(first.Services, shop, 2m);

                interrupt.Next = interruption;
                var failed = await CheckoutAsync(first.Services, cartId);
                interrupt.Next = InterruptCommit.Mode.None;

                Assert.False(failed.IsSuccess);
                Assert.Equal("POS.Checkout.NotSaved", failed.ErrorCode);
                Assert.Empty(first.Printer.Printed);   // no receipt for a sale that does not exist
                Assert.Equal(0, first.Drawer.Opened);
            }, clock, licenses, folder: null, extra: [interrupt]);

            Assert.Equal(0, await ScalarCountAsync(folder, "SELECT COUNT(*) FROM sal_Sales"));
            Assert.Equal(0, await ScalarCountAsync(folder, "SELECT COUNT(*) FROM pay_Payments"));
            Assert.Equal(1, await ScalarCountAsync(folder, "SELECT COUNT(*) FROM inv_StockMovements"));   // the opening stock only
            Assert.Equal(10m, await StockAsync(folder));

            await RunAsync(async second =>
            {
                // the cart survived the interruption exactly as the cashier left it, and the sale goes through once
                var checkout = await CheckoutAsync(second.Services, cartId);
                Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
                Assert.Single(second.Printer.Printed);
                Assert.Equal(8m, await OnHandAsync(second.Host, shop.ProductId));
                Assert.Equal(1, (await BusinessState.ReadAsync(second.Host)).Sales);

                var again = await CheckoutAsync(second.Services, cartId);   // a second click changes nothing
                Assert.Equal("POS.Checkout.CartNotOpen", again.ErrorCode);
                Assert.Equal(8m, await OnHandAsync(second.Host, shop.ProductId));
            }, clock, licenses, folder);
        }
        finally
        {
            if (folder is not null) IntegrationHost.DeleteFolder(folder);
        }
    }

    [Fact]
    public async Task ApplicationRestart_AfterAnInterruptedPurchaseReceipt_HasNoPartialReceipt_AndTheRetryReceivesOnce()
    {
        var clock = new TestClock(OfflineDesktop.Start);
        using var licenses = OfflineDesktop.NewLicenses(clock);
        string folder = null!;
        try
        {
            Shop first = null!, second = null!;
            Guid orderId = Guid.Empty;
            var interrupt = new InterruptCommit();
            folder = await RunAsync(async run =>
            {
                first = await CreateShopAsync(run.Services, "COLA-1", "Cola", stock: 5m);
                second = await CreateShopAsync(run.Services, "WATER-1", "Water", stock: 5m, warehouseId: first.WarehouseId);
                using var scope = run.Services.CreateScope();
                var p = scope.ServiceProvider;
                var supplier = await p.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("SUP-1", "Acme"));
                var order = await p.GetRequiredService<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(supplier.Value));
                orderId = order.Value;
                await p.GetRequiredService<AddPurchaseOrderLineCommandHandler>().HandleAsync(new AddPurchaseOrderLineCommand(orderId, "COLA-1", 20m, 1.2m));
                await p.GetRequiredService<AddPurchaseOrderLineCommandHandler>().HandleAsync(new AddPurchaseOrderLineCommand(orderId, "WATER-1", 10m, 0.5m));
                await p.GetRequiredService<SubmitPurchaseOrderCommandHandler>().HandleAsync(new SubmitPurchaseOrderCommand(orderId));

                interrupt.Next = InterruptCommit.Mode.Crash;
                using var receiveScope = run.Services.CreateScope();
                await Assert.ThrowsAnyAsync<Exception>(() => receiveScope.ServiceProvider.GetRequiredService<ReceivePurchaseOrderCommandHandler>()
                    .HandleAsync(new ReceivePurchaseOrderCommand(orderId, first.WarehouseId)));
                interrupt.Next = InterruptCommit.Mode.None;
            }, clock, licenses, folder: null, extra: [interrupt]);

            Assert.Equal(0, await ScalarCountAsync(folder, "SELECT COUNT(*) FROM pur_PurchaseOrderLines WHERE ReceivedAt IS NOT NULL"));
            Assert.Equal(1, await ScalarCountAsync(folder, "SELECT COUNT(*) FROM pur_PurchaseOrders WHERE Status = 2"));
            Assert.Equal(2, await ScalarCountAsync(folder, "SELECT COUNT(*) FROM inv_StockMovements"));    // the two opening balances only

            await RunAsync(async run =>
            {
                using var scope = run.Services.CreateScope();
                var received = await scope.ServiceProvider.GetRequiredService<ReceivePurchaseOrderCommandHandler>()
                    .HandleAsync(new ReceivePurchaseOrderCommand(orderId, first.WarehouseId));
                Assert.True(received.IsSuccess, received.IsFailure ? received.Error.ToString() : null);
                Assert.Equal(25m, await OnHandAsync(run.Host, first.ProductId));
                Assert.Equal(15m, await OnHandAsync(run.Host, second.ProductId));
                Assert.Equal(4, await CountAsync(run.Host, "inv_StockMovements"));
            }, clock, licenses, folder);
        }
        finally
        {
            if (folder is not null) IntegrationHost.DeleteFolder(folder);
        }
    }

    // ------------------------------------------------------------------ idempotency: the same checkout submitted twice

    [Fact]
    public async Task TwoCheckoutsOfTheSameCart_SubmittedAtTheSameTime_SellItOnce()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 1m);

        var results = await Task.WhenAll(
            Task.Run(() => CheckoutAsync(desktop.Services, cartId)),
            Task.Run(() => CheckoutAsync(desktop.Services, cartId)));

        Assert.Equal(1, results.Count(r => r.IsSuccess));
        var loser = results.Single(r => !r.IsSuccess);
        Assert.Equal("POS.Checkout.CartNotOpen", loser.ErrorCode);
        var state = await BusinessState.ReadAsync(desktop.Host);
        Assert.Equal((1L, 1L, 2L), (state.Sales, state.Payments, state.StockMovements));   // one sale, one payment, opening stock + one issue
        Assert.Equal(9m, await OnHandAsync(desktop.Host, shop.ProductId));
        Assert.Single(desktop.Printer.Printed);
    }
}
