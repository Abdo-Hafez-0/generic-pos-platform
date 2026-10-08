using Catalog.Application.Commands;
using Inventory.Application.Commands;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Payments.Contracts.Interfaces;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Pricing.Application.Commands;
using Purchasing.Application.Commands;
using Purchasing.Contracts.Interfaces;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;
using Suppliers.Application.Commands;

namespace Integration.Tests;

/// <summary>
/// The Stage 5 vertical slice, end to end through the REAL modules and the REAL database: create a product, add stock, open POS,
/// find the product, add it to the cart, check out, and see the sale created and stock reduced - with and without the Stage 8 modules.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class PosVerticalSliceTests
{
    private sealed record Shop(Guid ProductId, string Sku, Guid WarehouseId);

    private static async Task<Shop> CreateShopAsync(IServiceProvider services, decimal salePrice = 2.5m, decimal stock = 10m)
    {
        using var scope = services.CreateScope();
        var p = scope.ServiceProvider;

        var category = await p.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Drinks"));
        var unit = await p.GetRequiredService<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand("Piece", "pc"));
        Assert.True(category.IsSuccess && unit.IsSuccess);

        var product = await p.GetRequiredService<CreateProductCommandHandler>().HandleAsync(
            new CreateProductCommand("COLA-1", "Cola", category.Value.Value, unit.Value.Value, salePrice, 1m));
        Assert.True(product.IsSuccess, product.IsFailure ? product.Error.ToString() : null);

        var warehouse = await p.GetRequiredService<CreateWarehouseCommandHandler>().HandleAsync(new CreateWarehouseCommand("Main", "MAIN"));
        Assert.True(warehouse.IsSuccess, warehouse.IsFailure ? warehouse.Error.ToString() : null);

        var stocked = await p.GetRequiredService<AddStockCommandHandler>().HandleAsync(new AddStockCommand(product.Value.Value, warehouse.Value, stock));
        Assert.True(stocked.IsSuccess, stocked.IsFailure ? stocked.Error.ToString() : null);

        await FailureTestKit.OpenCashDrawerAsync(services);   // FIX-04: cash goes into an open drawer shift
        return new Shop(product.Value.Value, "COLA-1", warehouse.Value);
    }

    private static async Task<Guid> CartWithAsync(IServiceProvider services, Shop shop, decimal quantity)
    {
        using var scope = services.CreateScope();
        var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();

        var session = await pos.OpenSessionAsync("cashier-1", shop.WarehouseId);
        Assert.True(session.IsSuccess, session.ErrorMessage);
        var cart = await pos.StartCartAsync(session.SessionId);
        Assert.True(cart.IsSuccess, cart.ErrorMessage);
        var added = await pos.AddProductAsync(cart.CartId, shop.Sku, quantity);
        Assert.True(added.IsSuccess, added.ErrorMessage);
        return cart.CartId;
    }

    private static async Task<decimal> OnHandAsync(IServiceProvider services, Shop shop)
    {
        using var scope = services.CreateScope();
        var level = await scope.ServiceProvider.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId);
        return level!.OnHand;
    }

    // ------------------------------------------------------------------ Stage 8 modules absent

    [Fact]
    public async Task WithoutAnyStage8Module_ACheckoutCreatesTheSaleAndReducesStock()
    {
        await using var host = await IntegrationHost.StartAsync(IntegrationHost.CoreModules);
        var shop = await CreateShopAsync(host.Services);
        var cartId = await CartWithAsync(host.Services, shop, 3m);

        using var scope = host.Services.CreateScope();
        var checkout = await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cartId);

        Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
        Assert.Null(checkout.PaymentId);
        var sale = await scope.ServiceProvider.GetRequiredService<ISalesReader>().FindByIdAsync(checkout.SaleId);
        Assert.Equal(SaleStatusContract.Completed, sale!.Status);
        Assert.Equal(7.5m, sale.GrandTotal);
        Assert.Equal(7m, await OnHandAsync(host.Services, shop));
    }

    [Fact]
    public async Task WithoutThePaymentsModule_ARequestedPaymentIsRejectedAndNothingIsSoldOrIssued()
    {
        await using var host = await IntegrationHost.StartAsync(IntegrationHost.CoreModules);
        var shop = await CreateShopAsync(host.Services);
        var cartId = await CartWithAsync(host.Services, shop, 2m);

        using var scope = host.Services.CreateScope();
        var checkout = await scope.ServiceProvider.GetRequiredService<IPOSService>()
            .CheckoutAsync(cartId, payment: new POSPaymentRequest(POSPaymentMethod.Card));

        Assert.False(checkout.IsSuccess);
        Assert.Equal("POS.Checkout.PaymentsUnavailable", checkout.ErrorCode);
        Assert.Equal(10m, await OnHandAsync(host.Services, shop));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ISalesReader>().GetRecentAsync());
    }

    // ------------------------------------------------------------------ every module present

    [Fact]
    public async Task WithEveryModule_ACashCheckoutRecordsThePayment_ReducesStock_AndCompletesTheSale()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var cartId = await CartWithAsync(host.Services, shop, 3m);

        using var scope = host.Services.CreateScope();
        var checkout = await scope.ServiceProvider.GetRequiredService<IPOSService>()
            .CheckoutAsync(cartId, payment: new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 20m));

        Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
        Assert.NotNull(checkout.PaymentId);
        Assert.Equal(12.5m, checkout.ChangeDue);

        var sale = await scope.ServiceProvider.GetRequiredService<ISalesReader>().FindByIdAsync(checkout.SaleId);
        Assert.Equal(SaleStatusContract.Completed, sale!.Status);
        Assert.Equal(7.5m, sale.GrandTotal);

        var payments = await scope.ServiceProvider.GetRequiredService<IPaymentReader>().GetPaymentsForReferenceAsync("sale", checkout.SaleId);
        var payment = Assert.Single(payments);
        Assert.Equal(checkout.PaymentId, payment.PaymentId);
        Assert.Equal(7.5m, payment.Amount);
        Assert.Equal(7m, await OnHandAsync(host.Services, shop));
    }

    [Fact]
    public async Task WithPricing_APriceListPriceOverridesTheCatalogPrice_OnTheSale()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m);

        using (var scope = host.Services.CreateScope())
        {
            var list = await scope.ServiceProvider.GetRequiredService<CreatePriceListCommandHandler>().HandleAsync(new CreatePriceListCommand("RETAIL", "Retail"));
            Assert.True(list.IsSuccess, list.IsFailure ? list.Error.ToString() : null);
            var price = await scope.ServiceProvider.GetRequiredService<CreatePriceCommandHandler>()
                .HandleAsync(new CreatePriceCommand(shop.Sku, 2.0m, DateTime.UtcNow.AddDays(-1)));
            Assert.True(price.IsSuccess, price.IsFailure ? price.Error.ToString() : null);
        }

        var cartId = await CartWithAsync(host.Services, shop, 3m);
        using var checkoutScope = host.Services.CreateScope();
        var checkout = await checkoutScope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cartId);

        Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
        var sale = await checkoutScope.ServiceProvider.GetRequiredService<ISalesReader>().FindByIdAsync(checkout.SaleId);
        Assert.Equal(6.0m, sale!.GrandTotal);   // 3 x 2.00, not 3 x 2.50
    }

    [Fact]
    public async Task WithAnInsufficientTender_NothingIsSoldAndStockIsUntouched()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var cartId = await CartWithAsync(host.Services, shop, 2m);

        using var scope = host.Services.CreateScope();
        var checkout = await scope.ServiceProvider.GetRequiredService<IPOSService>()
            .CheckoutAsync(cartId, payment: new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 1m));

        Assert.Equal("POS.Checkout.TenderInsufficient", checkout.ErrorCode);
        Assert.Equal(10m, await OnHandAsync(host.Services, shop));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ISalesReader>().GetRecentAsync());
    }

    // ------------------------------------------------------------------ Purchasing through the real Inventory

    [Fact]
    public async Task ReceivingAPurchaseOrder_IncreasesStockInTheRealInventory()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, stock: 5m);

        using var scope = host.Services.CreateScope();
        var p = scope.ServiceProvider;
        var supplier = await p.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("SUP-1", "Acme Supplies"));
        Assert.True(supplier.IsSuccess, supplier.IsFailure ? supplier.Error.ToString() : null);
        var order = await p.GetRequiredService<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(supplier.Value));
        Assert.True(order.IsSuccess, order.IsFailure ? order.Error.ToString() : null);
        Assert.True((await p.GetRequiredService<AddPurchaseOrderLineCommandHandler>().HandleAsync(new AddPurchaseOrderLineCommand(order.Value, shop.Sku, 20m, 1.2m))).IsSuccess);
        Assert.True((await p.GetRequiredService<SubmitPurchaseOrderCommandHandler>().HandleAsync(new SubmitPurchaseOrderCommand(order.Value))).IsSuccess);

        var received = await p.GetRequiredService<ReceivePurchaseOrderCommandHandler>().HandleAsync(new ReceivePurchaseOrderCommand(order.Value, shop.WarehouseId));

        Assert.True(received.IsSuccess, received.IsFailure ? received.Error.ToString() : null);
        Assert.Equal(25m, await OnHandAsync(host.Services, shop));
        var summary = await p.GetRequiredService<IPurchaseOrderReader>().GetSummaryAsync();
        Assert.Equal(1, summary.Received);
        Assert.Equal(24m, summary.ReceivedValue);
    }
}
