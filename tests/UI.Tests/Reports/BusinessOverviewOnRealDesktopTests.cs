using System.Globalization;
using System.Windows.Input;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using POS.UI.ViewModels;
using Reporting.UI.Resources;
using Reporting.UI.ViewModels;
using UI.Tests.Pos;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests.Reports;

/// <summary>FIX-01d: the business overview on the production-like offline desktop - today's sale shows up in its figures.</summary>
[Collection(nameof(RealDesktop))]
public sealed class BusinessOverviewOnRealDesktopTests
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

    private static string Value(BusinessOverviewViewModel vm, string card, string label)
        => vm.Cards.Single(c => c.Title == card).Lines.Single(l => l.Label == label).Value;

    [Fact]
    public async Task A_sale_made_today_appears_in_the_sales_card_and_the_daily_list_with_the_other_sections()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, salePrice: 2.5m, stock: 10m);
        var pos = new PosViewModel(Runner(desktop.Services), desktop.Services.GetRequiredService<ICurrentUser>());
        await pos.OnNavigatedToAsync();
        await Run(pos, pos.OpenSessionCommand);
        (pos.ProductCode, pos.QuantityText) = (shop.Sku, "4");
        await Run(pos, pos.AddCommand);
        await Run(pos, pos.CheckoutCommand);
        Assert.Null(pos.ErrorMessage);

        var vm = new BusinessOverviewViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.Equal([ReportsText.SalesCard, ReportsText.InventoryCard, ReportsText.PurchasingCard, ReportsText.CustomersCard, ReportsText.SuppliersCard],
            vm.Cards.Select(c => c.Title));
        Assert.All(vm.Cards, c => Assert.True(c.IsAvailable, c.Message));
        Assert.Equal("1", Value(vm, ReportsText.SalesCard, ReportsText.SaleCount));
        Assert.Equal(10m.ToString("N2", CultureInfo.CurrentCulture), Value(vm, ReportsText.SalesCard, ReportsText.SalesTotal));
        Assert.Equal("6", Value(vm, ReportsText.InventoryCard, ReportsText.TotalOnHand));
        // FIX-12: every day of the period is listed, in the computer's local calendar; the sale is on today's local date
        Assert.Equal(Enumerable.Range(0, (vm.ToDate - vm.FromDate).Days + 1).Select(i => DateOnly.FromDateTime(vm.FromDate.AddDays(i))), vm.Days.Select(d => d.Date));
        Assert.Equal(10m, vm.Days.Single(d => d.Date == DateOnly.FromDateTime(DateTime.Now)).Total);
        Assert.Equal(10m, vm.Days.Sum(d => d.Total));
    }

    [Fact]
    public async Task A_reversed_period_is_refused_in_plain_words()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new BusinessOverviewViewModel(Runner(desktop.Services));
        (vm.FromDate, vm.ToDate) = (DateTime.Today, DateTime.Today.AddDays(-3));

        await Run(vm, vm.ShowCommand);

        Assert.Equal(ReportsText.RangeInvalid, vm.ErrorMessage);
        Assert.Empty(vm.Cards);
    }
}
