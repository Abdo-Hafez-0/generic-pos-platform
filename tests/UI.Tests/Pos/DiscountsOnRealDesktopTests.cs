using System.Globalization;
using System.Windows.Input;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Pricing.Application.Commands;
using POS.Contracts.Models;
using POS.UI.Resources;
using POS.UI.ViewModels;
using Sales.Application.Queries;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests.Pos;

/// <summary>
/// FIX-08 through the cashier screen on the production-like offline desktop: tax from Pricing, a line discount and a cart discount given on
/// the screen, the sale recorded with the same amounts, every discount in the audit log.
/// </summary>
[Collection(nameof(RealDesktop))]
public sealed class DiscountsOnRealDesktopTests
{
    private static async Task Run(PosViewModel vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static string Number(decimal value) => value.ToString(CultureInfo.CurrentCulture);

    [Fact]
    public async Task The_cashier_gives_a_line_and_a_cart_discount_and_the_sale_records_the_same_amounts()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var services = desktop.Services;
        var shop = await CreateShopAsync(services, salePrice: 2.5m, stock: 10m);
        using (var scope = services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<CreateTaxRateCommandHandler>().HandleAsync(new CreateTaxRateCommand("STD", "Standard", 0.14m))).IsSuccess);

        var vm = new PosViewModel(new UiActionRunner(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance), services.GetRequiredService<ICurrentUser>());
        await vm.OnNavigatedToAsync();
        Assert.True(vm.CanGiveDiscounts);                 // the administrator holds pos.discount.give
        await Run(vm, vm.OpenSessionCommand);
        (vm.ProductCode, vm.QuantityText) = (shop.Sku, Number(4m));
        await Run(vm, vm.AddCommand);
        Assert.Equal((10m, 1.23m), (vm.Total, vm.TaxTotal));   // 10.00 x 0.14 / 1.14

        vm.SelectedItem = vm.Items.Single();
        (vm.DiscountText, vm.DiscountKind) = (Number(10m), vm.DiscountKinds.Single(k => k.Kind == POSDiscountKind.Percent));
        await Run(vm, vm.LineDiscountCommand);
        Assert.Equal(PosText.DiscountGiven, vm.StatusMessage);
        (vm.DiscountText, vm.DiscountKind) = (Number(0.5m), vm.DiscountKinds.Single(k => k.Kind == POSDiscountKind.Amount));
        await Run(vm, vm.CartDiscountCommand);

        Assert.Equal((10m, 1.5m, 8.5m, 1.04m), (vm.Subtotal, vm.DiscountTotal, vm.Total, vm.TaxTotal));   // 8.50 x 0.14 / 1.14 = 1.04
        Assert.Equal(1.5m, vm.Items.Single().Discount);

        var cartId = vm.CartId!.Value;
        await Run(vm, vm.CheckoutCommand);
        Assert.Null(vm.ErrorMessage);

        using var read = services.CreateScope();
        var saleId = (await read.ServiceProvider.GetRequiredService<POS.Contracts.Interfaces.IPOSReader>().GetCartAsync(cartId))!.SaleId!.Value;
        var sale = (await read.ServiceProvider.GetRequiredService<GetSaleByIdQueryHandler>().HandleAsync(new GetSaleByIdQuery(saleId)))!;
        Assert.Equal((8.5m, 1.04m, 7.46m), (sale.GrandTotal, sale.TaxTotal, sale.SubTotal));
        Assert.Equal((1.5m, 0.14m), (sale.Items.Single().Discount, sale.Items.Single().TaxRate));

        var audit = read.ServiceProvider.GetRequiredService<Audit.Contracts.Interfaces.IAuditReader>();
        for (var i = 0; i < 200 && (await audit.QueryAsync(new Audit.Contracts.Models.AuditEntryFilter(Module: "pos"))).Items.Count < 3; i++) await Task.Delay(25);
        var entries = (await audit.QueryAsync(new Audit.Contracts.Models.AuditEntryFilter(Module: "pos"))).Items;
        Assert.Contains(entries, e => e.Action == "discount.line-given" && e.Summary!.Contains("10%"));
        Assert.Contains(entries, e => e.Action == "discount.cart-given" && e.Summary!.Contains("0.50"));
        Assert.Contains(entries, e => e.Action == "sale.completed" && e.Summary!.Contains("after a discount of 1.50"));
    }
}
