using System.Globalization;
using System.Windows.Input;
using CashManagement.UI.Resources;
using CashManagement.UI.ViewModels;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using UI.Tests.Pos;

namespace UI.Tests.Finance;

/// <summary>FIX-01d: the cash drawer screen on the production-like offline desktop.</summary>
[Collection(nameof(RealDesktop))]
public sealed class CashDrawerOnRealDesktopTests
{
    private static async Task Run(ViewModelBase vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static string Money(decimal value) => value.ToString(CultureInfo.CurrentCulture);

    [Fact]
    public async Task A_shift_is_opened_paid_in_and_out_counted_and_closed_with_the_difference_kept()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var user = desktop.Services.GetRequiredService<ICurrentUser>();
        var vm = new CashDrawerViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance), user);
        await vm.OnNavigatedToAsync();
        Assert.True(vm.HasNoOpenShift);

        vm.OpeningFloat = Money(100m);
        await Run(vm, vm.OpenCommand);
        Assert.True(vm.HasOpenShift);
        Assert.Equal(user.UserName, vm.Session!.OpenedBy);
        Assert.Equal(100m, vm.Session.Balance);

        (vm.Kind, vm.Amount, vm.Reason) = (vm.Kinds.Single(k => k.Name == CashText.PayOut), Money(20m), "milk for the staff room");
        await Run(vm, vm.RecordCommand);
        (vm.Kind, vm.Amount, vm.Reason) = (vm.Kinds.Single(k => k.Name == CashText.PayIn), Money(5m), "more change");
        await Run(vm, vm.RecordCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(85m, vm.Session!.Balance);
        Assert.Equal(2, vm.Movements.Count);
        Assert.All(vm.Movements, m => Assert.Equal(user.UserName, m.Movement.RecordedBy));

        vm.Counted = Money(84m);
        await Run(vm, vm.CloseCommand);
        Assert.True(vm.HasNoOpenShift);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, CashText.ShiftClosed, 85m, 84m, -1m), vm.StatusMessage);
        Assert.Equal(-1m, Assert.Single(vm.RecentShifts).Shift.Variance);
    }

    [Fact]
    public async Task Bad_amounts_are_refused_in_plain_words_and_nothing_is_recorded()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new CashDrawerViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance),
            desktop.Services.GetRequiredService<ICurrentUser>());
        await vm.OnNavigatedToAsync();

        vm.OpeningFloat = "-5";
        await Run(vm, vm.OpenCommand);
        Assert.Equal(CashText.FloatInvalid, vm.ErrorMessage);
        Assert.True(vm.HasNoOpenShift);

        vm.OpeningFloat = Money(10m);
        await Run(vm, vm.OpenCommand);
        (vm.Amount, vm.Reason) = ("0", "nothing");
        await Run(vm, vm.RecordCommand);
        Assert.Equal(CashText.AmountInvalid, vm.ErrorMessage);

        // taking out more than is in the drawer is refused by the domain, in its own words
        (vm.Kind, vm.Amount, vm.Reason) = (vm.Kinds.Single(k => k.Name == CashText.PayOut), Money(50m), "too much");
        await Run(vm, vm.RecordCommand);
        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain("Exception", vm.ErrorMessage);
        Assert.Empty(vm.Movements);
        Assert.Equal(10m, vm.Session!.Balance);
    }

    [Fact]
    public async Task A_sale_at_the_till_needs_an_open_drawer_shift_and_its_cash_shows_on_the_drawer_screen()
    {
        // FIX-04 through the screens: the cashier is told to open the drawer, the sale is not made; once the shift is open the cash goes in.
        await using var desktop = await OfflineDesktop.StartAsync();
        var services = desktop.Services;
        var user = services.GetRequiredService<ICurrentUser>();
        var runner = new UiActionRunner(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        var shop = await FailureTestKit.CreateShopAsync(services, salePrice: 2.5m, stock: 10m);

        var drawer = new CashDrawerViewModel(runner, user);
        await drawer.OnNavigatedToAsync();
        drawer.Counted = Money(50m);
        await Run(drawer, drawer.CloseCommand);                       // the kit opened MAIN: close it, as at the end of a day
        Assert.True(drawer.HasNoOpenShift);

        var till = new POS.UI.ViewModels.PosViewModel(runner, user);
        await till.OnNavigatedToAsync();
        await Run(till, till.OpenSessionCommand);
        (till.ProductCode, till.QuantityText) = (shop.Sku, "2");
        await Run(till, till.AddCommand);

        await Run(till, till.CheckoutCommand);
        Assert.Contains("Cash drawer screen", till.ErrorMessage);      // refused in plain words, nothing sold
        Assert.Equal(2m, till.Items.Single().Quantity);

        drawer.OpeningFloat = Money(100m);
        await Run(drawer, drawer.OpenCommand);
        await Run(till, till.CheckoutCommand);
        Assert.Null(till.ErrorMessage);
        Assert.Empty(till.Items);

        await drawer.OnNavigatedToAsync();
        Assert.Equal(105m, drawer.Session!.Balance);                  // float 100 + the 5.00 sale
        var sale = Assert.Single(drawer.Movements);
        Assert.Equal((CashText.CashSale, 5m, user.UserName), (sale.KindText, sale.Movement.Amount, sale.Movement.RecordedBy));
        Assert.Equal(0, desktop.Network.Requests);
    }
}
