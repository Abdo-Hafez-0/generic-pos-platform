using CashManagement.Contracts.Interfaces;
using Payments.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using POS.Contracts.Interfaces;
using Pricing.Application.Commands;
using Sales.Application.Queries;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// FIX-08b on the real host and the real SQLite file: prices include tax. The till snapshots each line's rate from Pricing; the cart, the
/// payment, the cash drawer and the recorded sale all agree to the cent; the sale keeps its tax after the rate changes.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class TaxIntegrationTests
{
    private static async Task<T> InScopeAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    [Fact]
    public async Task A_taxed_sale_agrees_everywhere_to_the_cent_and_keeps_its_tax_when_the_rate_changes()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var services = host.Services;
        var cola = await CreateShopAsync(services, "COLA-1", "Cola", salePrice: 2.5m, stock: 10m);
        var bread = await CreateShopAsync(services, "BREAD-1", "Bread", salePrice: 1.2m, stock: 10m, warehouseId: cola.WarehouseId);

        var standard = (await InScopeAsync(services, p => p.GetRequiredService<CreateTaxRateCommandHandler>().HandleAsync(new CreateTaxRateCommand("STD", "Standard VAT", 0.14m)))).Value;
        var zero = (await InScopeAsync(services, p => p.GetRequiredService<CreateTaxRateCommandHandler>().HandleAsync(new CreateTaxRateCommand("ZERO", "Basic food", 0m)))).Value;
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<SetProductTaxRateCommandHandler>().HandleAsync(new SetProductTaxRateCommand("BREAD-1", zero)))).IsSuccess);

        var (session, cartId) = await OpenCartAsync(services, cola, 3m);
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<IPOSService>().AddProductAsync(cartId, "BREAD-1", 2m))).IsSuccess);

        var cart = (await InScopeAsync(services, p => p.GetRequiredService<IPOSReader>().GetCartAsync(cartId)))!;
        Assert.Equal(9.9m, cart.Total);                       // 3 x 2.50 + 2 x 1.20, the shelf prices
        Assert.Equal(0.92m, cart.TaxTotal);                   // 7.50 x 0.14 / 1.14 = 0.921; the bread carries none

        var sale = await CheckoutAsync(services, cartId, tendered: 20m);
        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        Assert.Equal(10.1m, sale.ChangeDue);

        // the rate changes after the sale
        Assert.True((await InScopeAsync(services, p => p.GetRequiredService<UpdateTaxRateCommandHandler>().HandleAsync(new UpdateTaxRateCommand(standard, "Standard VAT", 0.20m)))).IsSuccess);

        var recorded = (await InScopeAsync(services, p => p.GetRequiredService<GetSaleByIdQueryHandler>().HandleAsync(new GetSaleByIdQuery(sale.SaleId))))!;
        Assert.Equal((9.9m, 0.92m, 8.98m), (recorded.GrandTotal, recorded.TaxTotal, recorded.SubTotal));
        Assert.Equal(0.14m, recorded.Items.Single(i => i.ProductSku == "COLA-1").TaxRate);   // the snapshot, not today's 20%
        Assert.Equal(0m, recorded.Items.Single(i => i.ProductSku == "BREAD-1").TaxRate);
        Assert.Equal(9.9m, Assert.Single(await InScopeAsync(services, p => p.GetRequiredService<IPaymentReader>().GetPaymentsForReferenceAsync("sale", sale.SaleId))).Amount);
        Assert.Equal(59.9m, (await InScopeAsync(services, p => p.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync("MAIN")))!.Balance);   // float 50 + 9.90

        // the next customer's cart gets the new rate
        var next = await InScopeAsync(services, async p =>
        {
            var pos = p.GetRequiredService<IPOSService>();
            var started = await pos.StartCartAsync(session);
            Assert.True((await pos.AddProductAsync(started.CartId, "COLA-1", 1m)).IsSuccess);
            return started.CartId;
        });
        Assert.Equal(0.42m, (await InScopeAsync(services, p => p.GetRequiredService<IPOSReader>().GetCartAsync(next)))!.TaxTotal);   // 2.50 x 0.2 / 1.2
    }

    [Fact]
    public async Task Without_any_tax_rate_set_up_a_sale_carries_no_tax()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 2m);

        var sale = await CheckoutAsync(host.Services, cartId);

        var recorded = (await InScopeAsync(host.Services, p => p.GetRequiredService<GetSaleByIdQueryHandler>().HandleAsync(new GetSaleByIdQuery(sale.SaleId))))!;
        Assert.Equal((5m, 0m), (recorded.GrandTotal, recorded.TaxTotal));
    }
}
