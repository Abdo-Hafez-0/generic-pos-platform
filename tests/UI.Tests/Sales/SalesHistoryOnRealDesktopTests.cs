using System.Globalization;
using System.Windows.Input;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using POS.UI.ViewModels;
using Sales.UI.Resources;
using Sales.UI.ViewModels;
using UI.Tests.Pos;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests.Sales;

/// <summary>FIX-01c: the sales history screen on the production-like offline desktop - what the till sold, with its lines and the day's takings.</summary>
[Collection(nameof(RealDesktop))]
public sealed class SalesHistoryOnRealDesktopTests
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

    private static async Task SellAsync(IServiceProvider services, PosViewModel pos, string sku, decimal quantity)
    {
        (pos.ProductCode, pos.QuantityText) = (sku, quantity.ToString(CultureInfo.CurrentCulture));
        await Run(pos, pos.AddCommand);
        await Run(pos, pos.CheckoutCommand);
        Assert.Null(pos.ErrorMessage);
    }

    [Fact]
    public async Task Todays_sales_from_the_till_are_listed_with_their_lines_and_the_takings()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, salePrice: 2.5m, stock: 20m);
        var pos = new PosViewModel(Runner(desktop.Services), desktop.Services.GetRequiredService<ICurrentUser>());
        await pos.OnNavigatedToAsync();
        await Run(pos, pos.OpenSessionCommand);
        await SellAsync(desktop.Services, pos, shop.Sku, 2m);
        await SellAsync(desktop.Services, pos, shop.Sku, 3m);

        var vm = new SalesHistoryViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();

        Assert.Equal(2, vm.Sales.Count);
        Assert.All(vm.Sales, s => Assert.Equal(SalesText.StatusCompleted, s.StatusText));
        Assert.Equal(7.5m, vm.Sales[0].Sale.GrandTotal);   // newest first
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, SalesText.Summary, 2, 12.5m), vm.SummaryText);
        Assert.Null(vm.ResultInfo);

        vm.Selected = vm.Sales[1];
        var line = Assert.Single(vm.Lines);
        Assert.Equal((shop.Sku, 2m, 5m), (line.ProductSku, line.Quantity, line.LineTotal));
    }

    [Fact]
    public async Task Another_period_shows_no_sales_and_a_reversed_period_is_refused_in_plain_words()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new SalesHistoryViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();
        Assert.Equal(SalesText.NoSales, vm.ResultInfo);

        (vm.FromDate, vm.ToDate) = (DateTime.Today, DateTime.Today.AddDays(-1));
        await Run(vm, vm.ShowCommand);

        Assert.Equal(SalesText.RangeInvalid, vm.ErrorMessage);
    }
}
