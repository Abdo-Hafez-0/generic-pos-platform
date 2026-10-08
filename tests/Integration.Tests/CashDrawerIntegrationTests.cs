using CashManagement.Application.Queries;
using CashManagement.Contracts.Interfaces;
using CashManagement.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// FIX-04 on the real host and the real SQLite file: a cash checkout records the cash in the open shift of the till's drawer inside the checkout
/// transaction; without an open shift a cash sale is refused before anything is written; other payments and payment-less checkouts never
/// touch the drawer. (Atomicity of a failing drawer write: AtomicityFailureTests.)
/// </summary>
[Collection(HostCollection.Name)]
public sealed class CashDrawerIntegrationTests
{
    private static async Task<decimal> DrawerBalanceAsync(IServiceProvider services, string drawer = "MAIN")
    {
        using var scope = services.CreateScope();
        var shift = await scope.ServiceProvider.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync(drawer);
        return shift!.Balance;
    }

    private static async Task<POSCheckoutResult> PayAsync(IServiceProvider services, Guid cartId, POSPaymentRequest? payment)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cartId, payment: payment);
    }

    [Fact]
    public async Task A_cash_sale_puts_the_total_into_the_open_drawer_shift_with_the_sale_as_reference()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m);     // opens MAIN with a float of 50
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 2m);

        var sale = await CheckoutAsync(host.Services, cartId, tendered: 20m);

        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        Assert.Equal(15m, sale.ChangeDue);
        Assert.Equal(55m, await DrawerBalanceAsync(host.Services));            // 50 + 5.00: the change went back to the customer
        using var scope = host.Services.CreateScope();
        var shiftId = (await scope.ServiceProvider.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync("MAIN"))!.SessionId;
        var shift = await scope.ServiceProvider.GetRequiredService<GetCashSessionQueryHandler>().HandleAsync(new GetCashSessionQuery(shiftId));
        var movement = Assert.Single(shift!.Movements);
        Assert.Equal((CashMovementKind.CashSale, 5m, "sale", sale.SaleId), (movement.Kind, movement.Amount, movement.ReferenceType, movement.ReferenceId!.Value));
    }

    [Fact]
    public async Task Without_an_open_shift_a_cash_sale_is_refused_and_nothing_is_written()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        await CloseCashDrawerAsync(host.Services);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);
        var before = await BusinessState.ReadAsync(host);

        var refused = await CheckoutAsync(host.Services, cartId);

        Assert.False(refused.IsSuccess);
        Assert.Equal("POS.Checkout.CashDrawerNotOpen", refused.ErrorCode);
        Assert.Contains("Cash drawer screen", refused.ErrorMessage);
        Assert.Equal(before, await BusinessState.ReadAsync(host));
        Assert.Equal(10m, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(0, await CountAsync(host, "cash_Movements"));

        // the shift is opened: the same cart now sells
        await OpenCashDrawerAsync(host.Services, openingFloat: 10m);
        Assert.True((await CheckoutAsync(host.Services, cartId)).IsSuccess);
        Assert.Equal(12.5m, await DrawerBalanceAsync(host.Services));
    }

    [Fact]
    public async Task The_cash_goes_into_the_configured_drawer_of_this_till()
    {
        Environment.SetEnvironmentVariable("GENERICPOS_PosCash__DrawerCode", "TILL-2");
        try
        {
            await using var host = await IntegrationHost.StartAllAsync();
            var shop = await CreateShopAsync(host.Services);                     // MAIN is open, TILL-2 is not
            var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);

            var refused = await CheckoutAsync(host.Services, cartId);
            Assert.Contains("'TILL-2'", refused.ErrorMessage);

            await OpenCashDrawerAsync(host.Services, drawer: "TILL-2", openingFloat: 20m);
            Assert.True((await CheckoutAsync(host.Services, cartId)).IsSuccess);
            Assert.Equal(22.5m, await DrawerBalanceAsync(host.Services, "TILL-2"));
            Assert.Equal(50m, await DrawerBalanceAsync(host.Services, "MAIN"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GENERICPOS_PosCash__DrawerCode", null);
        }
    }

    [Theory]
    [InlineData(POSPaymentMethod.Card)]
    [InlineData(POSPaymentMethod.Other)]
    [InlineData(null)]
    public async Task Card_other_and_payment_less_checkouts_never_touch_the_drawer_and_do_not_need_it_open(POSPaymentMethod? method)
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        await CloseCashDrawerAsync(host.Services);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);

        var sale = await PayAsync(host.Services, cartId, method is { } m ? new POSPaymentRequest(m, MethodDetail: m == POSPaymentMethod.Other ? "voucher" : null) : null);

        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        Assert.Equal(0, await CountAsync(host, "cash_Movements"));
    }
}
