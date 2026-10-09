using System.Windows.Input;
using Integration.Tests;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using POS.Contracts.Interfaces;
using POS.UI.ViewModels;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests.Pos;

/// <summary>The real hosts set process environment variables (isolated folders): these tests run alone.</summary>
[CollectionDefinition(nameof(RealDesktop), DisableParallelization = true)]
public sealed class RealDesktop;

/// <summary>
/// FIX-01b: the cashier screen's view model on the production-like offline desktop (every module, real SQLite, a validly signed license, the
/// real sign-in and authorization, a network that refuses every request): what the cashier does on the screen really sells.
/// </summary>
[Collection(nameof(RealDesktop))]
public sealed class PosScreenOnRealDesktopTests
{
    private static PosViewModel Screen(IServiceProvider services)
        => new(new UiActionRunner(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance), services.GetRequiredService<ICurrentUser>());

    private static async Task Run(PosViewModel vm, ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter), "the command was not executable");
        command.Execute(parameter);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_cashier_sells_through_the_screen_offline_and_the_stock_goes_down()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, salePrice: 2.5m, stock: 10m);
        var vm = Screen(desktop.Services);

        await vm.OnNavigatedToAsync();
        Assert.Equal(shop.WarehouseId, vm.SelectedWarehouse?.WarehouseId);   // the only warehouse is preselected

        await Run(vm, vm.OpenSessionCommand);
        Assert.True(vm.HasOpenSession, vm.ErrorMessage);

        vm.ProductCode = shop.Sku;
        vm.QuantityText = "3";
        await Run(vm, vm.AddCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(7.5m, vm.Total);

        await Run(vm, vm.CheckoutCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Empty(vm.Items);

        using var scope = desktop.Services.CreateScope();
        var level = await scope.ServiceProvider.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId);
        Assert.Equal(7m, level!.OnHand);
        var session = await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetSessionAsync(vm.SessionId!.Value);
        Assert.Equal(desktop.Services.GetRequiredService<ICurrentUser>().UserName, session!.CashierReference);
        Assert.Equal(0, desktop.Network.Requests);
    }

    [Fact]
    public async Task A_split_payment_records_each_part_and_only_the_cash_kept_goes_into_the_drawer()
    {
        // FIX-10 on the real host: Payments and CashManagement installed, one transaction; the test kit opened the MAIN drawer with 50.00
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, salePrice: 2.5m, stock: 10m);
        var vm = Screen(desktop.Services);
        await vm.OnNavigatedToAsync();
        await Run(vm, vm.OpenSessionCommand);
        (vm.ProductCode, vm.QuantityText) = (shop.Sku, "4");
        await Run(vm, vm.AddCommand);
        Assert.Equal(10m, vm.Total);

        vm.PaymentMethod = vm.PaymentMethods.Single(m => m.Method == POS.Contracts.Models.POSPaymentMethod.Card);
        (vm.PaymentAmountText, vm.PaymentNote) = (6.5m.ToString(System.Globalization.CultureInfo.CurrentCulture), "approval 4711");
        await Run(vm, vm.AddPaymentCommand);
        vm.PaymentMethod = vm.PaymentMethods.Single(m => m.Method == POS.Contracts.Models.POSPaymentMethod.Cash);
        vm.PaymentAmountText = "5";   // 3.50 due, 5 handed over
        await Run(vm, vm.AddPaymentCommand);
        Assert.Equal(1.5m, vm.ChangeDue);

        await Run(vm, vm.CheckoutCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains(1.5m.ToString("N2", System.Globalization.CultureInfo.CurrentCulture), vm.StatusMessage);

        using var scope = desktop.Services.CreateScope();
        var saleId = (await scope.ServiceProvider.GetRequiredService<global::Sales.Contracts.Interfaces.ISalesReader>().GetRecentAsync(5)).Single().SaleId;
        var payments = await scope.ServiceProvider.GetRequiredService<global::Payments.Contracts.Interfaces.IPaymentReader>().GetPaymentsForReferenceAsync("sale", saleId);
        Assert.Equal([(global::Payments.Contracts.Models.PaymentMethodContract.Card, 6.5m, (decimal?)null, "approval 4711"), (global::Payments.Contracts.Models.PaymentMethodContract.Cash, 3.5m, 5m, null)],
            payments.OrderByDescending(p => p.Amount).Select(p => (p.Method, p.Amount, p.TenderedAmount, p.MethodDetail)).ToArray());
        var drawer = await scope.ServiceProvider.GetRequiredService<CashManagement.Contracts.Interfaces.ICashSessionReader>().GetOpenSessionAsync("MAIN");
        Assert.Equal(53.5m, drawer!.Balance);   // 50 float + the 3.50 cash kept (the 1.50 change went back)
        Assert.Equal(6m, (await scope.ServiceProvider.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId))!.OnHand);
    }

    [Fact]
    public async Task The_cashiers_open_till_and_cart_survive_leaving_the_screen_and_come_back_in_a_new_screen()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        var first = Screen(desktop.Services);
        await first.OnNavigatedToAsync();
        await Run(first, first.OpenSessionCommand);
        first.ProductCode = shop.Sku;
        await Run(first, first.AddCommand);

        // signing out and in again (or restarting) builds a new screen: it resumes the same till and cart
        var second = Screen(desktop.Services);
        await second.OnNavigatedToAsync();

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(first.CartId, second.CartId);
        Assert.Single(second.Items);
    }

    [Fact]
    public async Task A_code_that_is_not_a_product_is_refused_in_plain_words()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        await CreateShopAsync(desktop.Services);
        var vm = Screen(desktop.Services);
        await vm.OnNavigatedToAsync();
        await Run(vm, vm.OpenSessionCommand);

        vm.ProductCode = "NO-SUCH-CODE";
        await Run(vm, vm.AddCommand);

        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain("Exception", vm.ErrorMessage);
        Assert.Empty(vm.Items);
    }
}
