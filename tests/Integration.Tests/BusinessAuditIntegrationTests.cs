using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using CashManagement.Application.Commands;
using CashManagement.Contracts.Interfaces;
using CashManagement.Domain.Enums;
using Catalog.Application.Commands;
using Catalog.Application.Queries;
using Inventory.Application.Commands;
using Inventory.Contracts.Interfaces;
using Inventory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Commands;
using Platform.Application.Abstractions.Authorization;
using Pricing.Application.Commands;
using Purchasing.Application.Commands;
using Sales.Application.Commands;
using Sales.Contracts.Interfaces;
using Suppliers.Application.Commands;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// FIX-05 on the real host and the real SQLite file: every audited business action leaves exactly one audit entry under its module, naming
/// the signed-in user; a refused action leaves none; and an audit store that cannot be written never fails or undoes the action - its entry is
/// written once the store works again.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class BusinessAuditIntegrationTests
{
    private static async Task<IReadOnlyList<AuditEntryResult>> EntriesAsync(IServiceProvider services, string module, string action)
    {
        using var scope = services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IAuditReader>().QueryAsync(new AuditEntryFilter(Module: module, Action: action), pageSize: 50)).Items;
    }

    /// <summary>Business entries are written in the background after the commit: wait (bounded) until the expected number is there.</summary>
    private static async Task<AuditEntryResult> OneEntryAsync(IServiceProvider services, string module, string action)
    {
        IReadOnlyList<AuditEntryResult> found = [];
        for (var i = 0; i < 200 && found.Count == 0; i++)
        {
            found = await EntriesAsync(services, module, action);
            if (found.Count == 0) await Task.Delay(25);
        }

        await Task.Delay(100);   // a duplicate would have arrived by now
        return Assert.Single(await EntriesAsync(services, module, action));
    }

    private static async Task<T> InScopeAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    [Fact]
    public async Task Every_audited_business_action_leaves_one_entry_under_its_module_naming_the_signed_in_user()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var services = host.Services;
        var user = services.GetRequiredService<ICurrentUser>().UserName;
        var shop = await CreateShopAsync(services, salePrice: 2.5m, stock: 10m);   // also opens the MAIN drawer shift

        // completed sale (POS) and its payment voided (Payments)
        var (_, cartId) = await OpenCartAsync(services, shop, 2m);
        var sale = await CheckoutAsync(services, cartId, tendered: 10m);
        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<VoidPaymentCommandHandler>().HandleAsync(new VoidPaymentCommand(sale.PaymentId!.Value, "keyed twice")))).IsSuccess);

        // a sale cancelled before completion (Sales)
        var draft = await InScopeAsync(services, p => p.GetRequiredService<ISalesService>().CreateSaleAsync("manual-1"));
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<CancelSaleCommandHandler>().HandleAsync(new CancelSaleCommand(draft.SaleId, "customer left")))).IsSuccess);

        // stock adjustment (Inventory)
        var level = await InScopeAsync(services, p => p.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId));
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<AdjustStockCommandHandler>().HandleAsync(new AdjustStockCommand(level!.StockItemId, -1m, AdjustmentReason.DamageWrite, "dropped")))).IsSuccess);

        // price changes (Pricing: create, change, deactivate) and the product's own sale price (Catalog)
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<CreatePriceListCommandHandler>().HandleAsync(new CreatePriceListCommand("STD", "Standard", MakeDefault: true)))).IsSuccess);
        var price = await InScopeAsync(services, p => p.GetRequiredService<CreatePriceCommandHandler>().HandleAsync(new CreatePriceCommand(shop.Sku, 2.2m, new DateTime(2026, 1, 1))));
        Assert.True(price.IsSuccess, price.IsFailure ? price.Error.ToString() : null);
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<UpdatePriceCommandHandler>().HandleAsync(new UpdatePriceCommand(price.Value, 2.4m, new DateTime(2026, 1, 1))))).IsSuccess);
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<DeactivatePriceCommandHandler>().HandleAsync(new DeactivatePriceCommand(price.Value)))).IsSuccess);
        var product = (await InScopeAsync(services, p => p.GetRequiredService<GetProductByIdQueryHandler>().HandleAsync(new GetProductByIdQuery(shop.ProductId)))).Value;
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<UpdateProductCommandHandler>().HandleAsync(
            new UpdateProductCommand(product.Id, product.Name, product.CategoryId, product.UnitId, 2.75m, product.CostPrice)))).IsSuccess);
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<UpdateProductCommandHandler>().HandleAsync(
            new UpdateProductCommand(product.Id, "Cola 0.5 l", product.CategoryId, product.UnitId, 2.75m, product.CostPrice)))).IsSuccess);   // name only: no price entry

        // purchase receive (Purchasing)
        var order = await InScopeAsync(services, async p =>
        {
            var supplier = await p.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("SUP-1", "Acme"));
            var created = await p.GetRequiredService<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(supplier.Value));
            Assert.True((await p.GetRequiredService<AddPurchaseOrderLineCommandHandler>().HandleAsync(new AddPurchaseOrderLineCommand(created.Value, shop.Sku, 12m, 1.1m))).IsSuccess);
            Assert.True((await p.GetRequiredService<SubmitPurchaseOrderCommandHandler>().HandleAsync(new SubmitPurchaseOrderCommand(created.Value))).IsSuccess);
            return created.Value;
        });
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<ReceivePurchaseOrderCommandHandler>().HandleAsync(new ReceivePurchaseOrderCommand(order, shop.WarehouseId)))).IsSuccess);

        // cash: pay-out, then the shift is closed (the shift was opened by the shop setup)
        var shift = (await InScopeAsync(services, p => p.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync("MAIN")))!;
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<RecordCashMovementCommandHandler>().HandleAsync(
            new RecordCashMovementCommand(shift.SessionId, CashMovementKind.PayOut, 3m, "milk")))).IsSuccess);
        var balance = (await InScopeAsync(services, p => p.GetRequiredService<ICashSessionReader>().GetSessionAsync(shift.SessionId)))!.Balance;
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<CloseCashSessionCommandHandler>().HandleAsync(new CloseCashSessionCommand(shift.SessionId, balance - 1m, "x")))).IsSuccess);

        var expected = new (string Module, string Action, string EntityType, string Contains)[]
        {
            ("pos", "sale.completed", "sale", "total 5.00, paid by cash"),
            ("payments", "payment.voided", "payment", "keyed twice"),
            ("sales", "sale.cancelled", "sale", "customer left"),
            ("inventory", "stock.adjusted", "stock-item", "-1 (DamageWrite)"),
            ("pricing", "price.created", "price", "2.20"),
            ("pricing", "price.changed", "price", "from 2.20"),
            ("pricing", "price.deactivated", "price", "2.40"),
            ("catalog", "product.price-changed", "product", "from 2.50 to 2.75"),
            ("purchasing", "purchase-order.received", "purchase-order", "1 line(s)"),
            ("cash-management", "cash.shift-opened", "cash-session", "float of 50.00"),
            ("cash-management", "cash.pay-out", "cash-session", "pay-out of 3.00: milk"),
            ("cash-management", "cash.shift-closed", "cash-session", "difference -1.00"),
        };

        foreach (var (module, action, entityType, contains) in expected)
        {
            var entry = await OneEntryAsync(services, module, action);
            Assert.Equal(entityType, entry.EntityType);
            Assert.True(entry.Summary?.Contains(contains, StringComparison.Ordinal), $"{module} {action}: '{entry.Summary}' should contain '{contains}'");
            Assert.Equal(user, entry.ActorName);
        }

        Assert.Equal(sale.SaleId.ToString(), (await OneEntryAsync(services, "pos", "sale.completed")).EntityId);
    }

    [Fact]
    public async Task A_refused_or_failed_action_leaves_no_business_entry()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);

        await using (await FailInsertsAsync(host, "sal_Sales"))
            Assert.False((await CheckoutAsync(host.Services, cartId)).IsSuccess);

        var level = await InScopeAsync(host.Services, p => p.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId));
        Assert.False((await InScopeAsync(host.Services, p => p.GetRequiredService<AdjustStockCommandHandler>().HandleAsync(new AdjustStockCommand(level!.StockItemId, -50m, AdjustmentReason.DamageWrite)))).IsSuccess);

        await Task.Delay(300);
        Assert.Empty(await EntriesAsync(host.Services, "pos", "sale.completed"));
        Assert.Empty(await EntriesAsync(host.Services, "inventory", "stock.adjusted"));
    }

    [Fact]
    public async Task An_audit_store_that_cannot_be_written_never_fails_the_sale_and_its_entry_follows_once_it_works()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var (session, first) = await OpenCartAsync(host.Services, shop, 1m);

        POS.Contracts.Models.POSCheckoutResult sold;
        await using (await FailInsertsAsync(host, "aud_AuditEntries"))
        {
            sold = await CheckoutAsync(host.Services, first);
            Assert.True(sold.IsSuccess, sold.ErrorMessage);              // the sale stands
            await Task.Delay(300);
            Assert.Empty(await EntriesAsync(host.Services, "pos", "sale.completed"));
        }

        // the store works again: the next action writes the waiting entry first, then its own
        var second = await InScopeAsync(host.Services, async p =>
        {
            var pos = p.GetRequiredService<POS.Contracts.Interfaces.IPOSService>();
            var cart = await pos.StartCartAsync(session);
            Assert.True((await pos.AddProductAsync(cart.CartId, shop.Sku, 1m)).IsSuccess);
            return cart.CartId;
        });
        var next = await CheckoutAsync(host.Services, second);
        Assert.True(next.IsSuccess, next.ErrorMessage);
        for (var i = 0; i < 200 && (await EntriesAsync(host.Services, "pos", "sale.completed")).Count < 2; i++) await Task.Delay(25);

        var entries = await EntriesAsync(host.Services, "pos", "sale.completed");
        Assert.Equal([next.SaleId.ToString(), sold.SaleId.ToString()], entries.Select(e => e.EntityId));   // newest first: nothing lost, nothing doubled
        Assert.True(entries[1].OccurredAt <= entries[0].OccurredAt);
    }
}
