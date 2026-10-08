using System.Globalization;
using System.Windows.Input;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Pricing.Contracts.Interfaces;
using Pricing.UI.Resources;
using Pricing.UI.ViewModels;
using UI.Tests.Pos;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests.Pricing;

/// <summary>FIX-08a: the Tax rates screen on the production-like offline desktop (real Pricing tables, real authorization, real audit).</summary>
[Collection(nameof(RealDesktop))]
public sealed class TaxRatesOnRealDesktopTests
{
    private static async Task Run(ViewModelBase vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static string Percent(decimal value) => value.ToString(CultureInfo.CurrentCulture);

    private static async Task<decimal> TillRateAsync(IServiceProvider services, Guid productId)
    {
        using var scope = services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ITaxRateResolver>().ResolveAsync(productId)).Rate;
    }

    [Fact]
    public async Task The_owner_sets_up_rates_and_a_product_rate_and_the_till_resolves_them()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        var vm = new TaxRatesViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));
        await vm.OnNavigatedToAsync();
        Assert.Empty(vm.Rates);

        // the first rate becomes the default
        (vm.NewCode, vm.NewName, vm.NewPercent) = ("std", "Standard VAT", Percent(14m));
        await Run(vm, vm.AddCommand);
        Assert.Null(vm.ErrorMessage);
        var standard = Assert.Single(vm.Rates);
        Assert.Equal(("STD", 0.14m, PricingText.Yes), (standard.Rate.Code, standard.Rate.Rate, standard.DefaultText));
        (vm.NewCode, vm.NewName, vm.NewPercent) = ("ZERO", "Basic food", Percent(0m));
        await Run(vm, vm.AddCommand);
        Assert.Equal(0.14m, await TillRateAsync(desktop.Services, shop.ProductId));

        // a product gets its own rate
        vm.ProductCode = shop.Sku;
        await Run(vm, vm.FindCommand);
        Assert.Contains("the default rate", vm.ProductTaxText);
        vm.ProductChoice = vm.Choices.Single(c => c.Rate?.Code == "ZERO");
        await Run(vm, vm.SaveProductCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains("chosen for this product", vm.ProductTaxText);
        Assert.Equal(0m, await TillRateAsync(desktop.Services, shop.ProductId));

        // a new law: the standard rate changes; the product goes back to the default
        vm.SelectedRate = vm.Rates.Single(r => r.Rate.Code == "STD");
        vm.EditPercent = Percent(15m);
        await Run(vm, vm.UpdateCommand);
        vm.ProductChoice = vm.Choices[0];
        await Run(vm, vm.SaveProductCommand);
        Assert.Equal(0.15m, await TillRateAsync(desktop.Services, shop.ProductId));

        // the default cannot be deactivated (the command is not even offered); another rate can
        vm.SelectedRate = vm.Rates.Single(r => r.Rate.Code == "STD");
        Assert.False(vm.DeactivateCommand.CanExecute(null));
        vm.SelectedRate = vm.Rates.Single(r => r.Rate.Code == "ZERO");
        await Run(vm, vm.DeactivateCommand);
        Assert.Equal(PricingText.Inactive, vm.Rates.Single(r => r.Rate.Code == "ZERO").StatusText);
        Assert.DoesNotContain(vm.Choices, c => c.Rate?.Code == "ZERO");
    }

    [Fact]
    public async Task Bad_percentages_are_refused_in_plain_words()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new TaxRatesViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));
        await vm.OnNavigatedToAsync();

        (vm.NewCode, vm.NewName, vm.NewPercent) = ("STD", "Standard", "fourteen");
        await Run(vm, vm.AddCommand);
        Assert.Equal(PricingText.TaxPercentInvalid, vm.ErrorMessage);

        vm.NewPercent = Percent(140m);
        await Run(vm, vm.AddCommand);
        Assert.Equal("A tax rate must be between 0% and 100%.", vm.ErrorMessage);
        Assert.Empty(vm.Rates);
    }
}
