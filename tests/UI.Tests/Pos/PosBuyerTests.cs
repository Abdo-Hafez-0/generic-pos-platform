using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Presentation.Actions;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using POS.UI.Resources;
using POS.UI.ViewModels;

namespace UI.Tests.Pos;

/// <summary>
/// MISS-11c (owner's choice: in the payment area, shown only when needed): a sale at or above the electronic receipt's threshold asks for the
/// buyer's name and national ID before Checkout; the chosen customer's name is filled in; the buyer goes with the sale and is cleared after.
/// </summary>
public sealed class PosBuyerTests
{
    private readonly FakeTill _till = new();
    private readonly PosViewModel _vm;

    public PosBuyerTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_till);
        services.AddScoped<FakePosService>();
        services.AddScoped<IPOSService>(sp => sp.GetRequiredService<FakePosService>());
        services.AddScoped<IPOSReader>(sp => sp.GetRequiredService<FakePosService>());
        var runner = new UiActionRunner(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }).GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        _vm = new PosViewModel(runner, new FakeCurrentUser(), new FakeScannerInput(_till));
    }

    private async Task Run(ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 200 && _vm.IsBusy; i++) await Task.Delay(5);
        Assert.False(_vm.IsBusy);
    }

    private async Task OpenTillAsync()
    {
        _till.AddWarehouse("Main shop");
        _till.Products["FRIDGE"] = (Guid.NewGuid(), "Fridge", 50_000m);
        await _vm.OnNavigatedToAsync();
        await Run(_vm.OpenSessionCommand);
    }

    private async Task AddAsync(string quantity)
    {
        (_vm.ProductCode, _vm.QuantityText) = ("FRIDGE", quantity);
        await Run(_vm.AddCommand);
    }

    [Fact]
    public async Task Below_the_threshold_or_without_the_rule_nothing_is_asked()
    {
        await OpenTillAsync();
        await AddAsync("3");                                                          // 150,000 but no rule (receipts off)
        Assert.False(_vm.NeedsBuyer);

        await Run(_vm.CheckoutCommand);
        Assert.Null(_vm.ErrorMessage);
        Assert.Null(_till.LastBuyer);
    }

    [Fact]
    public async Task At_the_threshold_the_till_asks_for_the_buyer_and_sends_them_with_the_sale()
    {
        _till.BuyerRule = new POSFiscalBuyerRule(150_000m, 14);
        await OpenTillAsync();
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.BuyerNeeded, 150_000m), _vm.BuyerNotice);

        await AddAsync("2");                                                          // 100,000
        Assert.False(_vm.NeedsBuyer);
        await AddAsync("1");                                                          // 150,000
        Assert.True(_vm.NeedsBuyer);

        await Run(_vm.CheckoutCommand);                                               // nothing given: the till says what is missing
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.BuyerMissing, 14), _vm.ErrorMessage);
        Assert.DoesNotContain("checkout", _till.Calls);

        (_vm.BuyerName, _vm.BuyerNationalId) = ("أحمد علي", "2990101123456");          // 13 digits
        await Run(_vm.CheckoutCommand);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.BuyerMissing, 14), _vm.ErrorMessage);

        _vm.BuyerNationalId = "29901011234567";
        await Run(_vm.CheckoutCommand);
        Assert.Contains("checkout", _till.Calls);
        Assert.Equal(new POSBuyer("أحمد علي", "29901011234567"), _till.LastBuyer);
        Assert.Equal((string.Empty, string.Empty, false), (_vm.BuyerName, _vm.BuyerNationalId, _vm.NeedsBuyer));   // ready for the next customer
    }

    [Fact]
    public async Task The_chosen_customers_name_is_filled_in()
    {
        _till.BuyerRule = new POSFiscalBuyerRule(150_000m, 14);
        var customer = new POSCustomerResult(Guid.NewGuid(), "C-7", "شركة النيل");
        _till.Customers.Add(customer);
        await OpenTillAsync();
        _vm.CustomerSearch = "C-7";
        await Run(_vm.FindCustomerCommand);
        await AddAsync("3");

        Assert.True(_vm.NeedsBuyer);
        Assert.Equal("شركة النيل", _vm.BuyerName);
    }
}
