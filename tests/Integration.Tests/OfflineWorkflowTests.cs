using Catalog.Contracts.Interfaces;
using CashManagement.Application.Commands;
using CashManagement.Application.Queries;
using CashManagement.Domain.Enums;
using Client.Licensing.Application;
using Client.Updater.Application;
using Customers.Application.Commands;
using Customers.Application.Queries;
using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using POS.Application.Security;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Pricing.Application.Commands;
using Purchasing.Application.Commands;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;
using Suppliers.Application.Commands;
using Users.Application.Commands;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// Stage 12 - the offline POS proof. The real desktop composition (licensing, updater, their HTTP transports and security wired as in production,
/// a validly licensed installation, real SQLite, every module, fake peripherals) runs on a machine whose network does not exist. Every request any
/// component makes is counted and refused; the essential shop workflows must complete anyway, and the database must hold exactly the result.
/// </summary>
[Collection(HostCollection.Name)]
[Trait("Category", "Offline")]
public sealed class OfflineWorkflowTests
{
    private static async Task<T> InScope<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> action)
    {
        using var scope = services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    // ------------------------------------------------------------------ sales

    [Fact]
    public async Task OfflinePos_CompletesCashSale_WithoutNetwork_AndTheSessionCanBeClosedAndReopened()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var host = desktop.Host;
        var shop = await CreateShopAsync(host.Services);

        // a valid price list price applies
        await InScope(host.Services, async sp =>
        {
            Assert.True((await sp.GetRequiredService<CreatePriceListCommandHandler>().HandleAsync(new CreatePriceListCommand("RETAIL", "Retail"))).IsSuccess);
            var price = await sp.GetRequiredService<CreatePriceCommandHandler>().HandleAsync(new CreatePriceCommand(shop.Sku, 2.0m, DateTime.UtcNow.AddDays(-1)));
            Assert.True(price.IsSuccess, price.IsFailure ? price.Error.ToString() : null);
            return 0;
        });

        Guid sessionId, cartId;
        using (var scope = host.Services.CreateScope())
        {
            var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
            var session = await pos.OpenSessionAsync("cashier-1", shop.WarehouseId);
            var cart = await pos.StartCartAsync(session.SessionId);
            Assert.True((await pos.AddProductAsync(cart.CartId, shop.Sku, 2m)).IsSuccess);       // scan/add
            Assert.True((await pos.ChangeQuantityAsync(cart.CartId, shop.ProductId, 3m)).IsSuccess);   // change quantity
            sessionId = session.SessionId;
            cartId = cart.CartId;

            var checkout = await pos.CheckoutAsync(cart.CartId, payment: new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 10m));

            Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
            Assert.Equal(4m, checkout.ChangeDue);
            Assert.Empty(checkout.HardwareNotices!);

            // the sale as Sales sees it, and the receipt data generated for it
            var sale = await scope.ServiceProvider.GetRequiredService<ISalesReader>().FindByIdAsync(checkout.SaleId);
            Assert.Equal(SaleStatusContract.Completed, sale!.Status);
            Assert.Equal(6.0m, sale.GrandTotal);   // 3 x 2.00, not 3 x 2.50
            var receipt = Assert.Single(desktop.Printer.Printed);
            Assert.Equal(6.0m, receipt.Total);
            Assert.Equal(("Cash", 6.0m, 10m, 4m), (receipt.Payment!.Method, receipt.Payment.Amount, receipt.Payment.Tendered, receipt.Payment.Change));
            Assert.Equal(1, desktop.Drawer.Opened);
        }

        // the database holds exactly one complete sale
        var state = await BusinessState.ReadAsync(host);
        Assert.Equal((1L, 1L, 1L, 1L, 2L, 1L), (state.Sales, state.SaleItems, state.SalesTransactions, state.Payments, state.StockMovements, state.CheckedOutCarts));   // 2 movements: opening stock + the sale
        Assert.Equal(7m, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(1, await CountAsync(host, "pos_Carts", $"Id = '{cartId.ToString().ToUpperInvariant()}' AND Status = 2"));

        // close and reopen the POS session, then sell again in the new one
        using (var scope = host.Services.CreateScope())
        {
            var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
            Assert.True((await pos.CloseSessionAsync(sessionId)).IsSuccess);
            var reopened = await pos.OpenSessionAsync("cashier-1", shop.WarehouseId);
            Assert.True(reopened.IsSuccess, reopened.ErrorMessage);
            Assert.NotEqual(sessionId, reopened.SessionId);
            var cart = await pos.StartCartAsync(reopened.SessionId);
            Assert.True((await pos.AddProductAsync(cart.CartId, shop.Sku, 1m)).IsSuccess);
            Assert.True((await pos.CheckoutAsync(cart.CartId)).IsSuccess);
        }

        Assert.Equal(6m, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(0, desktop.Network.Requests);   // not one HTTP request was needed
    }

    [Fact]
    public async Task OfflinePos_CompletesCardSale_WithoutNetwork_AndDoesNotOpenTheDrawer()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 2m);

        using var scope = desktop.Services.CreateScope();
        var checkout = await scope.ServiceProvider.GetRequiredService<IPOSService>()
            .CheckoutAsync(cartId, payment: new POSPaymentRequest(POSPaymentMethod.Card));

        Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
        Assert.NotNull(checkout.PaymentId);
        Assert.Equal(1, await CountAsync(desktop.Host, "pay_Payments"));
        Assert.Equal(8m, await OnHandAsync(desktop.Host, shop.ProductId));
        Assert.Single(desktop.Printer.Printed);
        Assert.Equal(0, desktop.Drawer.Opened);
        Assert.Equal(0, desktop.Network.Requests);
    }

    // ------------------------------------------------------------------ inventory

    [Fact]
    public async Task OfflineInventory_LookupStockAdjustmentAndHistory_WorkWithoutNetwork()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);

        var product = await InScope(desktop.Services, sp => sp.GetRequiredService<IProductLookup>().FindBySkuAsync(shop.Sku));
        Assert.Equal(shop.ProductId, product!.ProductId);
        var level = await InScope(desktop.Services, sp => sp.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId));
        Assert.Equal(10m, level!.OnHand);

        var adjusted = await InScope(desktop.Services, sp => sp.GetRequiredService<AdjustStockCommandHandler>()
            .HandleAsync(new AdjustStockCommand(level.StockItemId, -3m, Inventory.Domain.Enums.AdjustmentReason.DamageWrite, "dropped")));
        Assert.True(adjusted.IsSuccess, adjusted.IsFailure ? adjusted.Error.ToString() : null);

        Assert.Equal(7m, await OnHandAsync(desktop.Host, shop.ProductId));
        var history = await InScope(desktop.Services, sp => sp.GetRequiredService<GetStockMovementsQueryHandler>().HandleAsync(new GetStockMovementsQuery(level.StockItemId)));
        Assert.True(history.IsSuccess);
        Assert.Equal(2, history.Value.Count);   // the opening stock and the adjustment
        Assert.Contains(history.Value, m => m.MovementType == "Adjustment" && m.Quantity == 3m);
        Assert.Equal(1, await CountAsync(desktop.Host, "inv_StockAdjustments"));
        Assert.Equal(0, desktop.Network.Requests);
    }

    // ------------------------------------------------------------------ purchasing

    [Fact]
    public async Task OfflinePurchasing_CreatesAndReceivesAnOrder_AndStockAndTheOrderAgree()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, stock: 5m);

        var received = await InScope(desktop.Services, async sp =>
        {
            var supplier = await sp.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("SUP-1", "Acme Supplies"));
            var order = await sp.GetRequiredService<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(supplier.Value));
            Assert.True((await sp.GetRequiredService<AddPurchaseOrderLineCommandHandler>().HandleAsync(new AddPurchaseOrderLineCommand(order.Value, shop.Sku, 20m, 1.2m))).IsSuccess);
            Assert.True((await sp.GetRequiredService<SubmitPurchaseOrderCommandHandler>().HandleAsync(new SubmitPurchaseOrderCommand(order.Value))).IsSuccess);
            return await sp.GetRequiredService<ReceivePurchaseOrderCommandHandler>().HandleAsync(new ReceivePurchaseOrderCommand(order.Value, shop.WarehouseId));
        });

        Assert.True(received.IsSuccess, received.IsFailure ? received.Error.ToString() : null);
        Assert.Equal(25m, await OnHandAsync(desktop.Host, shop.ProductId));
        Assert.Equal(1, await CountAsync(desktop.Host, "pur_PurchaseOrders", "Status = 3"));   // Received
        Assert.Equal(1, await CountAsync(desktop.Host, "pur_PurchaseOrderLines", "ReceivedAt IS NOT NULL"));
        Assert.Equal(2, await CountAsync(desktop.Host, "inv_StockMovements"));                 // opening stock + the receipt
        Assert.Equal(0, desktop.Network.Requests);
    }

    // ------------------------------------------------------------------ cash management

    [Fact]
    public async Task OfflineCashManagement_OpenRecordAndCloseASession_TotalsAgree()
    {
        await using var desktop = await OfflineDesktop.StartAsync();

        var sessionId = await InScope(desktop.Services, async sp =>
            (await sp.GetRequiredService<OpenCashSessionCommandHandler>().HandleAsync(new OpenCashSessionCommand("DRAWER-1", "cashier-1", 100m))).Value);
        foreach (var (kind, amount, reason) in new[] { (CashMovementKind.PayIn, 50m, "more change"), (CashMovementKind.PayOut, 20m, "supplier"), (CashMovementKind.CashSale, 30m, null) })
        {
            var recorded = await InScope(desktop.Services, sp => sp.GetRequiredService<RecordCashMovementCommandHandler>()
                .HandleAsync(new RecordCashMovementCommand(sessionId, kind, amount, reason)));
            Assert.True(recorded.IsSuccess, recorded.IsFailure ? recorded.Error.ToString() : null);
        }

        var closed = await InScope(desktop.Services, sp => sp.GetRequiredService<CloseCashSessionCommandHandler>()
            .HandleAsync(new CloseCashSessionCommand(sessionId, CountedAmount: 158m, ClosedBy: "cashier-1")));

        Assert.True(closed.IsSuccess, closed.IsFailure ? closed.Error.ToString() : null);
        Assert.Equal((160m, 158m, -2m), (closed.Value.ExpectedAmount, closed.Value.CountedAmount, closed.Value.Variance));
        var session = await InScope(desktop.Services, sp => sp.GetRequiredService<GetCashSessionQueryHandler>().HandleAsync(new GetCashSessionQuery(sessionId)));
        Assert.Equal(CashSessionStatus.Closed, session!.Status);
        Assert.Equal(3, session.Movements.Count);
        Assert.Equal(3, await CountAsync(desktop.Host, "cash_Movements"));
        Assert.Equal(0, desktop.Network.Requests);
    }

    // ------------------------------------------------------------------ customers/suppliers and the Stage 11 boundaries, offline

    [Fact]
    public async Task OfflineAuthorizationBoundaries_FromStage11_StillApply_ToACashierSigningInWithoutNetwork()
    {
        const string cashierPassword = "till drawer passphrase 42";
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);

        // the administrator works offline: customers and suppliers
        Assert.True((await InScope(desktop.Services, sp => sp.GetRequiredService<CreateCustomerCommandHandler>().HandleAsync(new CreateCustomerCommand("C-1", "Jane Customer")))).IsSuccess);
        Assert.True((await InScope(desktop.Services, sp => sp.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("SUP-1", "Acme")))).IsSuccess);
        var customers = await InScope(desktop.Services, sp => sp.GetRequiredService<ListCustomersQueryHandler>().HandleAsync(new ListCustomersQuery()));
        Assert.Single(customers.Value.Items);

        // a cashier whose role may only sell signs in through the local credentials
        var cashier = await InScope(desktop.Services, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("cashier", "Cashier")));
        var role = await InScope(desktop.Services, sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand("Cashier")));
        foreach (var capability in new[] { POSCapabilities.ManageSession, POSCapabilities.CreateSale })
            await InScope(desktop.Services, sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(role.Value, capability)));
        await InScope(desktop.Services, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(cashier.Value, role.Value)));
        await InScope(desktop.Services, sp => sp.GetRequiredService<SetUserPasswordCommandHandler>().HandleAsync(new SetUserPasswordCommand(cashier.Value, cashierPassword, false)));
        desktop.Services.GetRequiredService<ISessionManager>().SignOut();
        Assert.True((await InScope(desktop.Services, sp => sp.GetRequiredService<SignInCommandHandler>().HandleAsync(new SignInCommand("cashier", cashierPassword)))).IsSuccess);

        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 1m);
        Assert.True((await CheckoutAsync(desktop.Services, cartId, tendered: null)).IsSuccess);    // allowed: a sale
        var personal = await InScope(desktop.Services, sp => sp.GetRequiredService<ListCustomersQueryHandler>().HandleAsync(new ListCustomersQuery()));
        var supplier = await InScope(desktop.Services, sp => sp.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("SUP-2", "Sneaky")));
        Assert.Equal(SecurityErrors.ForbiddenCode, personal.Error.Code);                            // not allowed: personal data
        Assert.Equal(SecurityErrors.ForbiddenCode, supplier.Error.Code);                            // not allowed: master data
        Assert.Equal(1, await CountAsync(desktop.Host, "sup_Suppliers"));
        Assert.Equal(0, desktop.Network.Requests);
    }

    // ------------------------------------------------------------------ cloud-dependent features fail clearly without breaking local work

    [Fact]
    public async Task CloudDependentOperations_FailGracefully_AndLocalOperationRemainsAvailable()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        Assert.Equal(0, desktop.Network.Requests);

        var renewal = await desktop.Services.GetRequiredService<ILicenseService>().RenewAsync();
        var updates = await desktop.Services.GetRequiredService<IUpdateService>().CheckForUpdatesAsync();

        Assert.True(renewal.IsFailure);
        Assert.True(updates.IsFailure);
        Assert.True(desktop.Network.Requests >= 2);                    // the cloud WAS tried, and refused
        Assert.Contains("Local operation is not affected", renewal.Error.Description);   // "cloud unavailable, local operation remains available"
        Assert.Contains("Local operation is not affected", updates.Error.Description);
        Assert.DoesNotContain("   at ", renewal.Error.Description);   // no stack trace, no internals
        Assert.DoesNotContain("   at ", updates.Error.Description);
        Assert.Equal(Platform.Core.Licensing.LicenseState.Active, desktop.Services.GetRequiredService<Platform.Application.Abstractions.Licensing.ILicenseEntitlementService>().State);   // an outage never revokes

        // ... and the shop keeps selling
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 1m);
        var checkout = await CheckoutAsync(desktop.Services, cartId);
        Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
        Assert.Equal(9m, await OnHandAsync(desktop.Host, shop.ProductId));
    }
}
