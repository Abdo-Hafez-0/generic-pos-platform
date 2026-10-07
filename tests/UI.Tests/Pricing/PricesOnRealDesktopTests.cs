using System.Globalization;
using System.Windows.Input;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using POS.UI.ViewModels;
using Pricing.UI.Resources;
using Pricing.UI.ViewModels;
using UI.Tests.Pos;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests.Pricing;

/// <summary>FIX-01d: the prices screen on the production-like offline desktop - a price set here is what the till charges.</summary>
[Collection(nameof(RealDesktop))]
public sealed class PricesOnRealDesktopTests
{
    private static UiActionRunner Runner(IServiceProvider services)
        => new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);

    private static async Task Run(ViewModelBase vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static string Money(decimal value) => value.ToString(CultureInfo.CurrentCulture);

    [Fact]
    public async Task A_price_list_price_set_on_the_screen_is_charged_at_the_till_and_deactivating_it_brings_back_the_catalog_price()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, salePrice: 2.5m, stock: 10m);
        var vm = new PricesViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();

        (vm.NewListCode, vm.NewListName, vm.NewListDefault) = ("retail", "Retail", true);
        await Run(vm, vm.AddListCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(PricingText.Yes, Assert.Single(vm.PriceLists).DefaultText);

        vm.ProductCode = shop.Sku;
        await Run(vm, vm.FindCommand);
        Assert.True(vm.HasProduct);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PricingText.CatalogApplies, 2.5m), vm.CurrentPriceText);

        (vm.NewAmount, vm.NewFrom) = (Money(2m), DateTime.Today.AddDays(-1));
        await Run(vm, vm.AddPriceCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PricingText.PriceNow, 2m, "RETAIL"), vm.CurrentPriceText);
        Assert.Equal("RETAIL", Assert.Single(vm.Prices).ListCode);

        var pos = new PosViewModel(Runner(desktop.Services), desktop.Services.GetRequiredService<ICurrentUser>());
        await pos.OnNavigatedToAsync();
        await Run(pos, pos.OpenSessionCommand);
        pos.ProductCode = shop.Sku;
        await Run(pos, pos.AddCommand);
        Assert.Equal(2m, pos.Total);

        vm.SelectedPrice = vm.Prices[0];
        await Run(vm, vm.DeactivatePriceCommand);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PricingText.CatalogApplies, 2.5m), vm.CurrentPriceText);
    }

    [Fact]
    public async Task An_overlapping_price_and_bad_input_are_refused_in_plain_words()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        var vm = new PricesViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();
        (vm.NewListCode, vm.NewListName, vm.NewListDefault) = ("RETAIL", "Retail", true);
        await Run(vm, vm.AddListCommand);
        vm.ProductCode = shop.Sku;
        await Run(vm, vm.FindCommand);

        (vm.NewAmount, vm.NewFrom, vm.NewUntil) = (Money(2m), DateTime.Today, DateTime.Today.AddDays(-1));
        await Run(vm, vm.AddPriceCommand);
        Assert.Equal(PricingText.UntilBeforeFrom, vm.ErrorMessage);

        (vm.NewAmount, vm.NewUntil) = (Money(2m), null);
        await Run(vm, vm.AddPriceCommand);
        Assert.Null(vm.ErrorMessage);

        vm.NewAmount = Money(1.8m);   // same list, same minimum quantity, overlapping period
        await Run(vm, vm.AddPriceCommand);
        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain("Exception", vm.ErrorMessage);
        Assert.Single(vm.Prices);

        (vm.NewAmount, vm.NewMinimumQuantity) = (Money(1.8m), "12");   // a quantity break is a different price
        await Run(vm, vm.AddPriceCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.Prices.Count);

        vm.ProductCode = "NO-SUCH";
        await Run(vm, vm.FindCommand);
        Assert.Contains("NO-SUCH", vm.ErrorMessage);
        Assert.False(vm.HasProduct);
    }
}
