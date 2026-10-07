using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Presentation.Actions;
using Platform.Presentation.Resources;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using POS.UI.Resources;
using POS.UI.ViewModels;

namespace UI.Tests.Pos;

/// <summary>The cashier screen's behaviour (FIX-01b) against an in-memory till, through the real per-action runner.</summary>
public sealed class PosViewModelTests
{
    private readonly FakeTill _till = new();
    private readonly FakeCurrentUser _user = new() { UserName = "cashier1", DisplayName = "First Cashier" };
    private readonly PosViewModel _vm;

    public PosViewModelTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_till);
        services.AddScoped<FakePosService>();
        services.AddScoped<IPOSService>(sp => sp.GetRequiredService<FakePosService>());
        services.AddScoped<IPOSReader>(sp => sp.GetRequiredService<FakePosService>());
        var runner = new UiActionRunner(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }).GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        _vm = new PosViewModel(runner, _user);
    }

    private async Task Run(ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter), "the command was not executable");
        command.Execute(parameter);
        for (var i = 0; i < 200 && _vm.IsBusy; i++) await Task.Delay(5);
        Assert.False(_vm.IsBusy);
    }

    private async Task OpenTillAsync()
    {
        _till.AddWarehouse("Main shop");
        await _vm.OnNavigatedToAsync();
        await Run(_vm.OpenSessionCommand);
    }

    [Fact]
    public async Task Without_an_open_till_the_screen_offers_the_warehouses_and_preselects_the_only_one()
    {
        var main = _till.AddWarehouse("Main shop");

        await _vm.OnNavigatedToAsync();

        Assert.False(_vm.HasOpenSession);
        Assert.True(_vm.NeedsSession);
        Assert.Equal(main, _vm.SelectedWarehouse?.WarehouseId);
        Assert.False(_vm.HasNoWarehouses);
        Assert.Equal(PosText.ChooseWarehouse, _vm.StatusMessage);
    }

    [Fact]
    public async Task With_several_warehouses_nothing_is_preselected_and_the_till_cannot_open_until_one_is_chosen()
    {
        _till.AddWarehouse("Main shop");
        _till.AddWarehouse("Kiosk");

        await _vm.OnNavigatedToAsync();

        Assert.Null(_vm.SelectedWarehouse);
        Assert.False(_vm.OpenSessionCommand.CanExecute(null));
        _vm.SelectedWarehouse = _vm.Warehouses[1];
        Assert.True(_vm.OpenSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_any_warehouse_the_screen_says_what_to_do()
    {
        await _vm.OnNavigatedToAsync();

        Assert.True(_vm.HasNoWarehouses);
        Assert.False(_vm.OpenSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task Opening_the_till_uses_the_signed_in_user_and_starts_a_cart()
    {
        await OpenTillAsync();

        Assert.True(_vm.HasOpenSession);
        Assert.NotNull(_vm.CartId);
        Assert.Contains(_till.Calls, c => c.StartsWith("open:cashier1:", StringComparison.Ordinal));
        Assert.Equal(PosText.SessionOpened, _vm.StatusMessage);
        Assert.Contains("First Cashier", _vm.CashierText);
    }

    [Fact]
    public async Task The_till_the_cashier_left_open_is_resumed_with_its_cart()
    {
        var warehouse = _till.AddWarehouse("Main shop");
        var session = _till.OpenSessionFor("cashier1", warehouse);
        var cart = _till.StartCart(session);
        _till.Products["COLA-1"] = (Guid.NewGuid(), "Cola", 2.5m);
        _till.Carts[cart].Add(new POSCartItemResult(Guid.NewGuid(), _till.Products["COLA-1"].ProductId, "COLA-1", "Cola", 2m, 2.5m, 5m));

        await _vm.OnNavigatedToAsync();

        Assert.Equal(session, _vm.SessionId);
        Assert.Equal(cart, _vm.CartId);
        Assert.Single(_vm.Items);
        Assert.Equal(5m, _vm.Total);
        Assert.Equal(PosText.SessionResumed, _vm.StatusMessage);
        Assert.DoesNotContain(_till.Calls, c => c.StartsWith("open:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Someone_elses_open_till_is_not_resumed()
    {
        var warehouse = _till.AddWarehouse("Main shop");
        _till.OpenSessionFor("cashier2", warehouse);

        await _vm.OnNavigatedToAsync();

        Assert.False(_vm.HasOpenSession);
    }

    [Fact]
    public async Task A_resumed_till_without_a_cart_gets_a_new_one()
    {
        var session = _till.OpenSessionFor("cashier1", _till.AddWarehouse("Main shop"));

        await _vm.OnNavigatedToAsync();

        Assert.Equal(session, _vm.SessionId);
        Assert.NotNull(_vm.CartId);
        Assert.Contains("start-cart", _till.Calls);
    }

    [Fact]
    public async Task Adding_a_product_shows_the_line_and_clears_the_input_for_the_next_scan()
    {
        _till.Products["COLA-1"] = (Guid.NewGuid(), "Cola", 2.5m);
        await OpenTillAsync();

        _vm.ProductCode = " COLA-1 ";
        _vm.QuantityText = 3m.ToString(CultureInfo.CurrentCulture);
        await Run(_vm.AddCommand);

        var line = Assert.Single(_vm.Items);
        Assert.Equal(("Cola", 3m, 7.5m), (line.ProductName, line.Quantity, line.LineTotal));
        Assert.Equal(7.5m, _vm.Total);
        Assert.Equal(string.Empty, _vm.ProductCode);
        Assert.Equal("1", _vm.QuantityText);
        Assert.Contains("add:COLA-1:3", _till.Calls);
    }

    [Fact]
    public async Task The_add_command_needs_a_code()
    {
        await OpenTillAsync();

        Assert.False(_vm.AddCommand.CanExecute(null));
        _vm.ProductCode = "X";
        Assert.True(_vm.AddCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task An_invalid_quantity_is_refused_in_plain_words_without_calling_the_till(string quantity)
    {
        await OpenTillAsync();
        _vm.ProductCode = "COLA-1";
        _vm.QuantityText = quantity;

        await Run(_vm.AddCommand);

        Assert.Equal(PosText.QuantityInvalid, _vm.ErrorMessage);
        Assert.DoesNotContain(_till.Calls, c => c.StartsWith("add:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_code_shows_the_tills_own_message_and_keeps_the_input()
    {
        await OpenTillAsync();
        _vm.ProductCode = "NOPE";

        await Run(_vm.AddCommand);

        Assert.Equal("No product with code 'NOPE'.", _vm.ErrorMessage);
        Assert.Equal("NOPE", _vm.ProductCode);
        Assert.Empty(_vm.Items);
    }

    [Fact]
    public async Task Removing_the_selected_line_updates_the_cart()
    {
        _till.Products["COLA-1"] = (Guid.NewGuid(), "Cola", 2.5m);
        await OpenTillAsync();
        _vm.ProductCode = "COLA-1";
        await Run(_vm.AddCommand);

        await Run(_vm.RemoveCommand, _vm.Items[0]);

        Assert.Empty(_vm.Items);
        Assert.Equal(PosText.ItemRemoved, _vm.StatusMessage);
        Assert.False(_vm.RemoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Checkout_completes_the_sale_and_starts_an_empty_cart_for_the_next_customer()
    {
        _till.Products["COLA-1"] = (Guid.NewGuid(), "Cola", 2.5m);
        await OpenTillAsync();
        _vm.ProductCode = "COLA-1";
        await Run(_vm.AddCommand);
        var soldCart = _vm.CartId;

        await Run(_vm.CheckoutCommand);

        Assert.Contains(soldCart!.Value, _till.CheckedOut);
        Assert.NotEqual(soldCart, _vm.CartId);
        Assert.Empty(_vm.Items);
        Assert.Equal(0m, _vm.Total);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.SaleCompleted, 2.5m), _vm.StatusMessage);
        Assert.Null(_vm.HardwareMessage);
        Assert.False(_vm.CheckoutCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_peripheral_problem_after_the_sale_is_shown_but_the_sale_stands()
    {
        _till.Products["COLA-1"] = (Guid.NewGuid(), "Cola", 2.5m);
        _till.HardwareNotices = [new POSHardwareNotice("ReceiptPrinter", "Hardware.Offline", "The receipt was not printed. Print it again later.")];
        await OpenTillAsync();
        _vm.ProductCode = "COLA-1";
        await Run(_vm.AddCommand);

        await Run(_vm.CheckoutCommand);

        Assert.Equal("The receipt was not printed. Print it again later.", _vm.HardwareMessage);
        Assert.Null(_vm.ErrorMessage);
        Assert.Single(_till.CheckedOut);
    }

    [Fact]
    public async Task A_refused_checkout_keeps_the_cart_and_says_why()
    {
        _till.Products["COLA-1"] = (Guid.NewGuid(), "Cola", 2.5m);
        _till.CheckoutRefusal = "Not enough stock for Cola.";
        await OpenTillAsync();
        _vm.ProductCode = "COLA-1";
        await Run(_vm.AddCommand);
        var cart = _vm.CartId;

        await Run(_vm.CheckoutCommand);

        Assert.Equal("Not enough stock for Cola.", _vm.ErrorMessage);
        Assert.Equal(cart, _vm.CartId);
        Assert.Single(_vm.Items);
    }

    [Fact]
    public async Task An_unexpected_failure_shows_the_plain_message_and_leaves_the_screen_usable()
    {
        await OpenTillAsync();
        _till.Throw = new InvalidOperationException("SQLite Error 5: database is locked");
        _vm.ProductCode = "COLA-1";

        await Run(_vm.AddCommand);

        Assert.Equal(PresentationText.OperationFailed, _vm.ErrorMessage);
        Assert.Equal("COLA-1", _vm.ProductCode);
        Assert.True(_vm.AddCommand.CanExecute(null));
    }

    [Fact]
    public async Task Closing_the_till_needs_an_empty_cart_and_then_offers_the_warehouses_again()
    {
        _till.Products["COLA-1"] = (Guid.NewGuid(), "Cola", 2.5m);
        await OpenTillAsync();
        _vm.ProductCode = "COLA-1";
        await Run(_vm.AddCommand);
        Assert.False(_vm.CloseSessionCommand.CanExecute(null));

        await Run(_vm.RemoveCommand, _vm.Items[0]);
        await Run(_vm.CloseSessionCommand);

        Assert.False(_vm.HasOpenSession);
        Assert.Null(_vm.CartId);
        Assert.Single(_vm.Warehouses);
        Assert.Equal(PosText.SessionClosed, _vm.StatusMessage);
    }

    [Fact]
    public async Task Every_user_action_runs_in_a_new_scope()
    {
        await OpenTillAsync();
        Assert.Equal(2, _till.Instances);   // showing the screen and opening the till: one scope each
        _vm.ProductCode = "UNKNOWN";

        await Run(_vm.AddCommand);
        await Run(_vm.AddCommand);

        Assert.Equal(4, _till.Instances);   // one more scope per click, none kept
    }
}
